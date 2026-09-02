using System.Runtime.InteropServices;
using InputAtlas.Core;
using InputAtlas.Windows;

namespace InputAtlas.Windows.Tests;

public sealed class RawInputCaptureTests
{
    private const uint WmWtsSessionChange = 0x02B1;
    private const nuint WtsSessionUnlock = 0x8;

    [Fact]
    public void WindowsStartupCommandIncludesBackgroundStartupArgument()
    {
        var executablePath = Path.Combine("relative folder", "InputAtlas.exe");

        var command = StartupRegistration.BuildLaunchCommand(executablePath);

        Assert.Equal($"\"{Path.GetFullPath(executablePath)}\" --startup", command);
    }

    [Fact]
    public async Task DedicatedWindowCanRegisterPauseResumeAndStop()
    {
        await using var capture = new RawInputCaptureController(new NullLog());

        await capture.StartAsync();
        Assert.Equal(CaptureStatus.Recording, capture.Status);
        Assert.True(capture.HasExpectedRegistration());
        await capture.PauseAsync();
        await WaitForStatusAsync(capture, CaptureStatus.Paused);
        await capture.ResumeAsync();
        await WaitForStatusAsync(capture, CaptureStatus.Recording);
        await capture.StopAsync();
        Assert.Equal(CaptureStatus.Stopped, capture.Status);
    }

    [Fact]
    public async Task RefreshRegistrationRestoresKeyboardTargetAfterAnotherRegistrationOverridesIt()
    {
        await using var capture = new RawInputCaptureController(new NullLog());
        await capture.StartAsync();
        Assert.True(capture.HasExpectedRegistration());

        OverrideKeyboardRegistrationForCurrentProcess();
        Assert.False(capture.HasExpectedRegistration());

        await capture.RefreshRegistrationAsync("test_registration_override");

        Assert.True(capture.HasExpectedRegistration());
    }

    [Fact]
    public async Task SessionUnlockNotificationReRegistersOverriddenKeyboardTarget()
    {
        await using var capture = new RawInputCaptureController(new NullLog());
        await capture.StartAsync();
        OverrideKeyboardRegistrationForCurrentProcess();
        Assert.False(capture.HasExpectedRegistration());

        SendMessage(capture.MessageWindowHandle, WmWtsSessionChange, WtsSessionUnlock, 0);

        Assert.True(capture.HasExpectedRegistration());
        Assert.Equal(CaptureStatus.Recording, capture.Status);
    }

    private static async Task WaitForStatusAsync(RawInputCaptureController capture, CaptureStatus expected)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (capture.Status != expected && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.Equal(expected, capture.Status);
    }

    private static void OverrideKeyboardRegistrationForCurrentProcess()
    {
        var foregroundOnlyKeyboard = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = 0,
                Target = 0,
            },
        };
        Assert.True(RegisterRawInputDevices(
            foregroundOnlyKeyboard,
            (uint)foregroundOnlyKeyboard.Length,
            (uint)Marshal.SizeOf<RawInputDevice>()));
    }

    private sealed class NullLog : IApplicationLog
    {
        public void Debug(string eventName, string message) { }
        public void Information(string eventName, string message) { }
        public void Warning(string eventName, string message) { }
        public void LogError(string eventName, string message, Exception? exception = null) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}
