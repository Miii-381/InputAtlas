using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using InputAtlas.Core;
using InputAtlas.Storage;
using InputAtlas.Windows;
using Microsoft.Win32;

namespace InputAtlas.App.Pet;

public sealed class PetHost : IAsyncDisposable
{
    private readonly RawInputCaptureController _capture;
    private readonly Dispatcher _dispatcher;
    private readonly IApplicationLog _log;
    private readonly Func<PetWindowPlacement, Task> _savePlacementAsync;
    private readonly PetAnimationController _controller;
    private readonly object _controllerSync = new();
    private readonly DispatcherTimer _transitionTimer;
    private AppSettings _settings;
    private PetWindow? _window;
    private int _renderScheduled;
    private long _renderVersion;
    private long _controllerTransitionsSinceLog;
    private long _renderedFramesSinceLog;
    private long _actionPulsesSinceLog;
    private long _lastPerformanceLogTimestamp = Stopwatch.GetTimestamp();
    private int _pulseRequested;
    private bool _runtimeHidden;
    private bool _disposed;

    public PetHost(
        RawInputCaptureController capture,
        Dispatcher dispatcher,
        AppSettings settings,
        IApplicationLog log,
        Func<PetWindowPlacement, Task> savePlacementAsync)
    {
        _capture = capture;
        _dispatcher = dispatcher;
        _settings = settings;
        _log = log;
        _savePlacementAsync = savePlacementAsync;
        _controller = new PetAnimationController(DateTimeOffset.UtcNow);
        _controller.SetPaused(capture.Status != CaptureStatus.Recording, DateTimeOffset.UtcNow);
        _transitionTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(34),
        };
        _transitionTimer.Tick += TransitionTimerTick;
        _capture.InputStateChanged += CaptureInputStateChanged;
        _capture.StatusChanged += CaptureStatusChanged;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        ApplySettings(settings);
        _log.Information(
            "pet_host_initialized",
            $"enabled={settings.PetEnabled} topmost={settings.PetTopmost} click_through={settings.PetClickThrough} scale={settings.PetScale:F2}");
    }

    public bool IsVisible => _window?.IsVisible == true;

    public void ApplySettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = settings;
        if (!settings.PetEnabled)
        {
            _runtimeHidden = false;
            Hide("disabled");
            return;
        }

        var window = EnsureWindow();
        window.ApplyPresentation(settings.PetTopmost, settings.PetClickThrough, settings.PetScale);
        PetWindowPlacementService.PlaceAndClamp(window, settings);
        if (!_runtimeHidden)
        {
            Show("settings_enabled");
        }

        _log.Information(
            "pet_settings_applied",
            $"enabled={settings.PetEnabled} topmost={settings.PetTopmost} click_through={settings.PetClickThrough} scale={settings.PetScale:F2}");
    }

    public void ToggleVisibility()
    {
        if (!_settings.PetEnabled)
        {
            _log.Information("pet_visibility_toggle_ignored", "reason=feature_disabled");
            return;
        }

        if (IsVisible)
        {
            _runtimeHidden = true;
            Hide("tray_toggle");
        }
        else
        {
            _runtimeHidden = false;
            Show("tray_toggle");
        }
    }

    public void BeginAdjustment()
    {
        if (!_settings.PetEnabled)
        {
            _log.Information("pet_adjustment_ignored", "reason=feature_disabled");
            return;
        }

        _runtimeHidden = false;
        var window = EnsureWindow();
        Show("adjustment_requested");
        window.BeginAdjustment();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capture.InputStateChanged -= CaptureInputStateChanged;
        _capture.StatusChanged -= CaptureStatusChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _transitionTimer.Stop();
        _transitionTimer.Tick -= TransitionTimerTick;
        if (_window is not null)
        {
            _window.PlacementCommitted -= WindowPlacementCommitted;
            _window.PlacementRefreshRequested -= WindowPlacementRefreshRequested;
            _window.Close();
            _window = null;
        }

        _log.Information("pet_host_disposed", "桌宠计时器、窗口和事件订阅已释放");
        await Task.CompletedTask;
    }

    private PetWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        var started = Stopwatch.GetTimestamp();
        var frames = PetFrameSet.Load();
        _window = new PetWindow(frames, _log);
        _window.SetFrame(GetCurrentState());
        _window.PlacementCommitted += WindowPlacementCommitted;
        _window.PlacementRefreshRequested += WindowPlacementRefreshRequested;
        _log.Information(
            "pet_frames_preloaded",
            $"frame_count=10 duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        return _window;
    }

    private void Show(string reason)
    {
        var window = EnsureWindow();
        lock (_controllerSync)
        {
            _controller.Advance(DateTimeOffset.UtcNow);
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.SetFrame(GetCurrentState());
        PetWindowPlacementService.PlaceAndClamp(window, _settings);
        if (HasPendingTransition())
        {
            _transitionTimer.Start();
        }

        _log.Information("pet_window_shown", $"reason={reason} adjusting={window.IsAdjusting}");
    }

    private void Hide(string reason)
    {
        _transitionTimer.Stop();
        Interlocked.Exchange(ref _pulseRequested, 0);
        if (_window?.IsVisible == true)
        {
            _window.Hide();
            _log.Information("pet_window_hidden", $"reason={reason}");
        }
    }

    private void CaptureInputStateChanged(InputId input, bool isPressed)
    {
        bool stateChanged;
        bool pulseRequested;
        lock (_controllerSync)
        {
            (stateChanged, pulseRequested) = _controller.ApplyInput(input, isPressed, DateTimeOffset.UtcNow);
        }

        var animatePulse = pulseRequested &&
            _settings.Animation != AnimationKind.Off &&
            SystemParameters.ClientAreaAnimation;
        if ((stateChanged || animatePulse || HasPendingTransition()) && ShouldRender())
        {
            if (stateChanged)
            {
                Interlocked.Increment(ref _controllerTransitionsSinceLog);
            }

            if (animatePulse)
            {
                Interlocked.Exchange(ref _pulseRequested, 1);
            }

            Interlocked.Increment(ref _renderVersion);
            ScheduleRender();
        }
    }

    private void CaptureStatusChanged(object? sender, CaptureStatus status)
    {
        bool stateChanged;
        lock (_controllerSync)
        {
            stateChanged = _controller.SetPaused(status != CaptureStatus.Recording, DateTimeOffset.UtcNow);
        }

        if (stateChanged)
        {
            Interlocked.Increment(ref _controllerTransitionsSinceLog);
        }

        _log.Information("pet_capture_state_changed", $"capture_status={status}");
        if (ShouldRender())
        {
            Interlocked.Increment(ref _renderVersion);
            ScheduleRender();
        }
    }

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderScheduled, 1) != 0 || _disposed)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(RenderLatestState, DispatcherPriority.Render);
    }

    private void RenderLatestState()
    {
        var version = Volatile.Read(ref _renderVersion);
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (_window?.IsVisible == true)
            {
                _window.SetFrame(GetCurrentState());
                Interlocked.Increment(ref _renderedFramesSinceLog);
                if (Interlocked.Exchange(ref _pulseRequested, 0) != 0)
                {
                    _window.PlayActionPulse(_settings.Animation == AnimationKind.Reduced);
                    Interlocked.Increment(ref _actionPulsesSinceLog);
                }
            }

            if (HasPendingTransition() && _window?.IsVisible == true)
            {
                _transitionTimer.Start();
            }
            else
            {
                _transitionTimer.Stop();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _renderScheduled, 0);
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > TimeSpan.FromMilliseconds(16))
            {
                _log.Warning("pet_frame_slow", $"duration_ms={elapsed.TotalMilliseconds:F1}");
            }

            if (version != Volatile.Read(ref _renderVersion) && ShouldRender())
            {
                ScheduleRender();
            }

            LogPerformanceIfDue();
        }
    }

    private void TransitionTimerTick(object? sender, EventArgs e)
    {
        bool changed;
        lock (_controllerSync)
        {
            changed = _controller.Advance(DateTimeOffset.UtcNow);
        }

        if (changed && _window?.IsVisible == true)
        {
            Interlocked.Increment(ref _controllerTransitionsSinceLog);
            _window.SetFrame(GetCurrentState());
            Interlocked.Increment(ref _renderedFramesSinceLog);
        }

        if (!HasPendingTransition() || _window?.IsVisible != true)
        {
            _transitionTimer.Stop();
        }

        LogPerformanceIfDue();
    }

    private PetVisualState GetCurrentState()
    {
        lock (_controllerSync)
        {
            return _controller.CurrentState;
        }
    }

    private bool HasPendingTransition()
    {
        lock (_controllerSync)
        {
            return _controller.NextTransitionAt is not null;
        }
    }

    private bool ShouldRender() =>
        !_disposed &&
        _settings.PetEnabled &&
        !_runtimeHidden &&
        _window?.IsVisible == true;

    private void LogPerformanceIfDue()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastPerformanceLogTimestamp, now) < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastPerformanceLogTimestamp = now;
        var transitions = Interlocked.Exchange(ref _controllerTransitionsSinceLog, 0);
        var renderedFrames = Interlocked.Exchange(ref _renderedFramesSinceLog, 0);
        var actionPulses = Interlocked.Exchange(ref _actionPulsesSinceLog, 0);
        if (transitions == 0 && renderedFrames == 0 && actionPulses == 0)
        {
            return;
        }

        _log.Information(
            "pet_render_summary",
            $"window_seconds=30 controller_transitions={transitions} rendered_frames={renderedFrames} action_pulses={actionPulses} timer_running={_transitionTimer.IsEnabled}");
    }

    private async void WindowPlacementCommitted(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        var placement = PetWindowPlacementService.Capture(_window);
        try
        {
            await _savePlacementAsync(placement);
        }
        catch (Exception exception)
        {
            _log.LogError("pet_placement_save_failed", "桌宠位置保存失败", exception);
        }
    }

    private void WindowPlacementRefreshRequested(object? sender, EventArgs e) =>
        ClampToVisibleArea("dpi_changed");

    private void DisplaySettingsChanged(object? sender, EventArgs e) =>
        _ = _dispatcher.InvokeAsync(() => ClampToVisibleArea("display_settings_changed"));

    private void ClampToVisibleArea(string reason)
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsAdjusting)
        {
            _log.Information("pet_placement_clamp_deferred", $"reason={reason} adjustment_active=true");
            return;
        }

        PetWindowPlacementService.PlaceAndClamp(_window, _settings);
        _log.Information("pet_placement_clamped", $"reason={reason} visible={_window.IsVisible}");
    }
}
