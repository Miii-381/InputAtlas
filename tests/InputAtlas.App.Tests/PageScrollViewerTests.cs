using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using InputAtlas.App;

namespace InputAtlas.App.Tests;

public sealed class PageScrollViewerTests
{
    [Fact]
    public async Task PageAccumulatesWheelMessagesBeforeLayoutAndFollowsScrollbar()
    {
        // 使用独立 STA 和不可见窗口，只向测试控件发送 WPF 路由事件。
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                VerifyScrolling();
                finished.SetResult();
            }
            catch (Exception exception)
            {
                finished.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void VerifyScrolling()
    {
        var panel = new StackPanel();
        panel.Children.Add(new Border { Height = 10000 });
        var page = new PageScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        using var host = new HwndSource(new HwndSourceParameters("ScrollTest", 400, 300) { WindowStyle = 0 });
        host.RootVisual = page;
        page.Measure(new Size(400, 300));
        page.Arrange(new Rect(0, 0, 400, 300));
        page.UpdateLayout();
        var lines = SystemParameters.WheelScrollLines;
        var step = lines < 0 ? page.ViewportHeight : 16d * lines;

        foreach (var deltas in new[] { new[] { -360 }, [-120, -120, -120], [-40, -40, -40, -240], [-480, 120] })
        {
            // 模拟一次滚动条拖动，将外部定位和连续滚轮消息放在同一轮验证。
            page.ScrollToVerticalOffset(500);
            page.UpdateLayout();
            foreach (var delta in deltas)
            {
                panel.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
                {
                    RoutedEvent = Mouse.MouseWheelEvent,
                });
            }

            page.UpdateLayout();
            Assert.Equal(Math.Min(page.ScrollableHeight, 500 + 3 * step), page.VerticalOffset, 5);
        }
    }
}
