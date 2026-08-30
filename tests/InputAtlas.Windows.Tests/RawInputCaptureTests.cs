using InputAtlas.Core;
using InputAtlas.Windows;

namespace InputAtlas.Windows.Tests;

public sealed class RawInputCaptureTests
{
    [Fact]
    public async Task DedicatedWindowCanRegisterPauseResumeAndStop()
    {
        await using var capture = new RawInputCaptureController(new NullLog());

        await capture.StartAsync();
        Assert.Equal(CaptureStatus.Recording, capture.Status);
        await capture.PauseAsync();
        await WaitForStatusAsync(capture, CaptureStatus.Paused);
        await capture.ResumeAsync();
        await WaitForStatusAsync(capture, CaptureStatus.Recording);
        await capture.StopAsync();
        Assert.Equal(CaptureStatus.Stopped, capture.Status);
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

    private sealed class NullLog : IApplicationLog
    {
        public void Debug(string eventName, string message) { }
        public void Information(string eventName, string message) { }
        public void Warning(string eventName, string message) { }
        public void LogError(string eventName, string message, Exception? exception = null) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

