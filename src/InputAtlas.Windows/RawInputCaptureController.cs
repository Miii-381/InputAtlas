using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using InputAtlas.Core;

namespace InputAtlas.Windows;

public sealed class RawInputCaptureController : IInputCaptureController
{
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RimhMouse = 0;
    private const uint RimhKeyboard = 1;
    private const uint WmInput = 0x00FF;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmAppPause = 0x8001;
    private const uint WmAppResume = 0x8002;
    private const uint WmAppStop = 0x8003;
    private const uint WmAppRefreshRegistration = 0x8004;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private static readonly ConcurrentDictionary<nint, RawInputCaptureController> Windows = new();
    private static readonly WndProcDelegate WindowProcedure = StaticWindowProcedure;
    private static long _windowClassSequence;
    private readonly IApplicationLog _log;
    private readonly InputCounterEngine _engine;
    private readonly object _lifecycleSync = new();
    private readonly SemaphoreSlim _registrationRefreshGate = new(1, 1);
    private Thread? _thread;
    private TaskCompletionSource? _started;
    private TaskCompletionSource? _registrationRefreshCompletion;
    private string? _registrationRefreshReason;
    private nint _window;
    private uint _threadId;
    private bool _sessionAvailable = true;
    private bool _manuallyPaused;
    private bool _keyboardInputObserved;
    private bool _mouseInputObserved;
    private int _registrationGeneration;
    private string _registrationReason = "not_registered";
    private CaptureStatus _status = CaptureStatus.Stopped;
    private bool _disposed;

    public RawInputCaptureController(IApplicationLog log, TimeProvider? timeProvider = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        TimeProvider = timeProvider ?? TimeProvider.System;
        _engine = new InputCounterEngine(TimeProvider.GetUtcNow().ToUnixTimeSeconds());
        _engine.Counted += input => InputCounted?.Invoke(input);
        _engine.StateChanged += (input, isPressed) => InputStateChanged?.Invoke(input, isPressed);
        _engine.BucketCompleted += snapshot => BucketCompleted?.Invoke(snapshot);
    }

    public event EventHandler<CaptureStatus>? StatusChanged;

    public event Action<InputId>? InputCounted;

    public event Action<InputId, bool>? InputStateChanged;

    public event Action<BucketSnapshot>? BucketCompleted;

    public CaptureStatus Status => _status;

    public bool IsCoverageActive => _status == CaptureStatus.Recording && _sessionAvailable && !_manuallyPaused;

    internal nint MessageWindowHandle => _window;

    private TimeProvider TimeProvider { get; }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleSync)
        {
            if (_thread is not null)
            {
                return;
            }

            SetStatus(CaptureStatus.Starting);
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "InputAtlas Raw Input",
            };
            _thread.Start();
        }

        await _started!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmAppPause, 0, 0);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmAppResume, 0, 0);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask RefreshRegistrationAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _registrationRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_threadId == 0 || _window == 0)
            {
                throw new InvalidOperationException("Raw Input 消息窗口尚未就绪。");
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrationRefreshReason = reason;
            _registrationRefreshCompletion = completion;
            if (!PostThreadMessage(_threadId, WmAppRefreshRegistration, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法请求刷新 Raw Input 注册。");
            }

            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registrationRefreshReason = null;
            _registrationRefreshCompletion = null;
            _registrationRefreshGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Thread? thread;
        lock (_lifecycleSync)
        {
            thread = _thread;
        }

        if (thread is null)
        {
            return;
        }

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmAppStop, 0, 0);
        }

        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), cancellationToken).ConfigureAwait(false);
        lock (_lifecycleSync)
        {
            _thread = null;
        }
    }

    public BucketSnapshot GetCurrentSnapshot() =>
        _engine.Snapshot(TimeProvider.GetUtcNow().ToUnixTimeSeconds());

    public void AddCoverageSecond()
    {
        if (IsCoverageActive)
        {
            var now = TimeProvider.GetUtcNow().ToUnixTimeSeconds();
            _engine.AddCoverage(1, now);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            var className =
                $"InputAtlas.RawInput.{Environment.ProcessId}.{Interlocked.Increment(ref _windowClassSequence)}";
            RegisterWindowClass(className);
            _window = CreateWindowEx(
                0,
                className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                new nint(-3),
                0,
                0,
                0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Raw Input 消息窗口。");
            }

            Windows[_window] = this;

            if (!WTSRegisterSessionNotification(_window, 0))
            {
                _log.Warning("capture_session_notifications", $"会话通知注册失败 error={Marshal.GetLastWin32Error()}");
            }

            RegisterDevices("initial_start");
            _engine.ResetPressedStates();
            SetStatus(CaptureStatus.Recording);
            _log.Information(
                "capture_started",
                $"Raw Input 专用线程已开始记录 thread_id={_threadId} window=0x{_window:X} " +
                $"generation={_registrationGeneration} reason={_registrationReason}");
            _started?.TrySetResult();

            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == WmAppPause)
                {
                    PauseOnInputThread();
                    continue;
                }

                if (message.Id == WmAppResume)
                {
                    ResumeOnInputThread();
                    continue;
                }

                if (message.Id == WmAppStop)
                {
                    break;
                }

                if (message.Id == WmAppRefreshRegistration)
                {
                    RefreshRegistrationOnInputThread();
                    continue;
                }

                TranslateMessage(in message);
                DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            SetStatus(CaptureStatus.Unavailable);
            _log.LogError("capture_failed", "Raw Input 启动或消息循环失败", exception);
            _started?.TrySetException(exception);
        }
        finally
        {
            CleanupWindow();
            SetStatus(CaptureStatus.Stopped);
            _threadId = 0;
            _log.Information("capture_stopped", "Raw Input 专用线程已停止");
        }
    }

    private static void RegisterWindowClass(string className)
    {
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            Instance = GetModuleHandle(null),
            ClassName = className,
        };
        if (RegisterClassEx(in windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册 Raw Input 窗口类。");
        }
    }

    private unsafe void RegisterDevices(string reason)
    {
        var devices = stackalloc RawInputDevice[2];
        devices[0] = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink, Target = _window };
        devices[1] = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevInputSink, Target = _window };
        if (!RegisterRawInputDevices(devices, 2, (uint)sizeof(RawInputDevice)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw Input 设备注册失败。");
        }

        var state = GetRegistrationState();
        if (!state.KeyboardOwned || !state.MouseOwned)
        {
            throw new InvalidOperationException(
                $"Raw Input 注册目标校验失败：keyboard_owned={state.KeyboardOwned} mouse_owned={state.MouseOwned}。");
        }

        _registrationGeneration++;
        _registrationReason = reason;
        _keyboardInputObserved = false;
        _mouseInputObserved = false;
        _log.Information(
            "capture_devices_registered",
            $"reason={reason} generation={_registrationGeneration} keyboard=true mouse=true input_sink=true target=0x{_window:X}");
    }

    private unsafe void RemoveDevices()
    {
        var devices = stackalloc RawInputDevice[2];
        devices[0] = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevRemove, Target = 0 };
        devices[1] = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevRemove, Target = 0 };
        RegisterRawInputDevices(devices, 2, (uint)sizeof(RawInputDevice));
    }

    private void PauseOnInputThread()
    {
        if (_manuallyPaused)
        {
            return;
        }

        _manuallyPaused = true;
        RemoveDevices();
        _engine.ResetPressedStates();
        SetStatus(CaptureStatus.Paused);
        _log.Information("capture_paused", "用户已暂停记录");
    }

    private void ResumeOnInputThread()
    {
        if (!_manuallyPaused)
        {
            return;
        }

        try
        {
            RegisterDevices("manual_resume");
            _engine.ResetPressedStates();
            _manuallyPaused = false;
            SetStatus(CaptureStatus.Recording);
            _log.Information("capture_resumed", "用户已恢复记录");
        }
        catch (Exception exception)
        {
            SetStatus(CaptureStatus.Unavailable);
            _log.LogError("capture_resume_failed", "恢复 Raw Input 注册失败", exception);
        }
    }

    private void RefreshRegistrationOnInputThread()
    {
        var completion = _registrationRefreshCompletion;
        var reason = _registrationRefreshReason ?? "unspecified";
        var exception = TryRefreshRegistrationOnInputThread(reason);
        if (exception is null)
        {
            completion?.TrySetResult();
        }
        else
        {
            completion?.TrySetException(exception);
        }
    }

    private Exception? TryRefreshRegistrationOnInputThread(string reason)
    {
        if (_manuallyPaused)
        {
            _log.Information("capture_registration_refresh_skipped", $"reason={reason} capture_paused=true");
            return null;
        }

        try
        {
            var before = GetRegistrationState();
            RegisterDevices(reason);
            _engine.ResetPressedStates();
            if (_status == CaptureStatus.Unavailable)
            {
                SetStatus(CaptureStatus.Recording);
            }

            _log.Information(
                "capture_registration_refreshed",
                $"reason={reason} generation={_registrationGeneration} before_keyboard_owned={before.KeyboardOwned} " +
                $"before_mouse_owned={before.MouseOwned} target=0x{_window:X}");
            return null;
        }
        catch (Exception exception)
        {
            SetStatus(CaptureStatus.Unavailable);
            _log.LogError(
                "capture_registration_refresh_failed",
                $"刷新 Raw Input 注册失败 reason={reason}",
                exception);
            return exception;
        }
    }

    internal bool HasExpectedRegistration()
    {
        if (_window == 0)
        {
            return false;
        }

        var state = GetRegistrationState();
        return state.KeyboardOwned && state.MouseOwned;
    }

    private unsafe RegistrationState GetRegistrationState()
    {
        const int maximumRegistrations = 64;
        var devices = stackalloc RawInputDevice[maximumRegistrations];
        uint count = maximumRegistrations;
        var result = GetRegisteredRawInputDevices(devices, ref count, (uint)sizeof(RawInputDevice));
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前 Raw Input 注册目标。");
        }

        var keyboardOwned = false;
        var mouseOwned = false;
        for (var index = 0; index < result; index++)
        {
            var device = devices[index];
            var ownedByCaptureWindow =
                device.Target == _window &&
                (device.Flags & RidevInputSink) == RidevInputSink;
            if (device.UsagePage == 0x01 && device.Usage == 0x06)
            {
                keyboardOwned = ownedByCaptureWindow;
            }
            else if (device.UsagePage == 0x01 && device.Usage == 0x02)
            {
                mouseOwned = ownedByCaptureWindow;
            }
        }

        return new RegistrationState(keyboardOwned, mouseOwned);
    }

    private nint WindowProcedureInstance(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInput:
                if (IsCoverageActive)
                {
                    ProcessRawInput(lParam);
                }

                break;
            case WmPowerBroadcast:
                if ((int)wParam == PbtApmSuspend)
                {
                    _sessionAvailable = false;
                    _engine.ResetPressedStates();
                    _log.Information("power_suspend", "系统进入睡眠或休眠");
                }
                else if ((int)wParam == PbtApmResumeAutomatic)
                {
                    _sessionAvailable = true;
                    _engine.ResetPressedStates();
                    _log.Information("power_resume", "系统已从睡眠或休眠恢复");
                    TryRefreshRegistrationOnInputThread("power_resume");
                }

                break;
            case WmWtsSessionChange:
                if ((int)wParam == WtsSessionLock)
                {
                    _sessionAvailable = false;
                    _engine.ResetPressedStates();
                    _log.Information("session_locked", "当前用户会话已锁定");
                }
                else if ((int)wParam == WtsSessionUnlock)
                {
                    _sessionAvailable = true;
                    _engine.ResetPressedStates();
                    _log.Information("session_unlocked", "当前用户会话已解锁");
                    TryRefreshRegistrationOnInputThread("session_unlock");
                }

                break;
        }

        return DefWindowProc(_window, message, wParam, lParam);
    }

    private unsafe void ProcessRawInput(nint rawHandle)
    {
        uint size = 0;
        var headerSize = (uint)sizeof(RawInputHeader);
        if (GetRawInputData(rawHandle, RidInput, null, ref size, headerSize) != 0 || size == 0 || size > 128)
        {
            return;
        }

        byte* buffer = stackalloc byte[(int)size];
        if (GetRawInputData(rawHandle, RidInput, buffer, ref size, headerSize) != size)
        {
            return;
        }

        var header = (RawInputHeader*)buffer;
        var now = TimeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (header->Type == RimhKeyboard)
        {
            if (!_keyboardInputObserved)
            {
                _keyboardInputObserved = true;
                _log.Information(
                    "capture_first_keyboard_input",
                    $"已收到本次注册后的首个键盘 Raw Input 事件 generation={_registrationGeneration} reason={_registrationReason}");
            }

            var keyboard = (RawKeyboard*)(buffer + sizeof(RawInputHeader));
            var flags = keyboard->Flags;
            var sample = new RawKeyboardSample(
                keyboard->MakeCode,
                keyboard->VirtualKey,
                (flags & 0x02) != 0,
                (flags & 0x04) != 0,
                (flags & 0x01) != 0);
            _engine.HandleKeyboard(sample, now);
        }
        else if (header->Type == RimhMouse)
        {
            if (!_mouseInputObserved)
            {
                _mouseInputObserved = true;
                _log.Information(
                    "capture_first_mouse_input",
                    $"已收到本次注册后的首个鼠标 Raw Input 事件 generation={_registrationGeneration} reason={_registrationReason}");
            }

            var mouse = (RawMouse*)(buffer + sizeof(RawInputHeader));
            if (mouse->ButtonFlags == 0)
            {
                return;
            }

            _engine.HandleMouse(
                new RawMouseSample((RawMouseButtons)mouse->ButtonFlags, unchecked((short)mouse->ButtonData)),
                now);
        }
    }

    private void CleanupWindow()
    {
        if (_window == 0)
        {
            return;
        }

        RemoveDevices();
        WTSUnRegisterSessionNotification(_window);
        Windows.TryRemove(_window, out _);

        DestroyWindow(_window);
        _window = 0;
    }

    private void SetStatus(CaptureStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static nint StaticWindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        return Windows.TryGetValue(window, out var instance)
            ? instance.WindowProcedureInstance(message, wParam, lParam)
            : DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    private readonly record struct RegistrationState(bool KeyboardOwned, bool MouseOwned);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public uint Buttons;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    private delegate nint WndProcDelegate(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool RegisterRawInputDevices(RawInputDevice* devices, uint deviceCount, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint GetRegisteredRawInputDevices(
        RawInputDevice* devices,
        ref uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint GetRawInputData(nint rawInput, uint command, void* data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(in WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
}
