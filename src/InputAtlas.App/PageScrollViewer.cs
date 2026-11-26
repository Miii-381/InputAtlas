using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InputAtlas.App;

/// <summary>按滚轮增量累计页面位移，支持高精度滚轮。</summary>
public sealed class PageScrollViewer : ScrollViewer
{
    // WPF 像素滚动模式的一行是 16 个设备无关像素。
    private const double LineHeight = 16;
    private double? _pendingOffset;

    // 布局完成之前，后续消息从已经请求的位置继续累计。
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled || ScrollInfo is null)
        {
            return;
        }

        var lines = SystemParameters.WheelScrollLines;
        var step = lines < 0 ? ViewportHeight : lines * LineHeight;
        var distance = (double)e.Delta / Mouse.MouseWheelDeltaForOneLine * step;
        var target = Math.Clamp((_pendingOffset ?? VerticalOffset) - distance, 0, ScrollableHeight);
        _pendingOffset = target;
        ScrollInfo.SetVerticalOffset(target);
        e.Handled = true;
    }

    // 布局、键盘或滚动条改变实际位置后，下一次滚轮从新位置起算。
    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        _pendingOffset = null;
        base.OnScrollChanged(e);
    }
}
