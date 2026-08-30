using System.Globalization;
using System.Windows;
using System.Windows.Media;
using InputAtlas.Core;

namespace InputAtlas.App;

public sealed class MouseHeatmapControl : FrameworkElement
{
    public static readonly DependencyProperty CountsProperty = DependencyProperty.Register(
        nameof(Counts),
        typeof(IReadOnlyDictionary<InputId, long>),
        typeof(MouseHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyDictionary<InputId, long>? Counts
    {
        get => (IReadOnlyDictionary<InputId, long>?)GetValue(CountsProperty);
        set => SetValue(CountsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var body = new Rect(RenderSize.Width * 0.22, 8, RenderSize.Width * 0.56, RenderSize.Height - 16);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            new Pen(new SolidColorBrush(Color.FromRgb(71, 85, 105)), 1.5),
            body,
            body.Width * 0.42,
            body.Width * 0.42);

        var half = body.Width / 2;
        DrawRegion(drawingContext, new Rect(body.X + 3, body.Y + 3, half - 5, body.Height * 0.42), InputId.MouseLeft, "左键", dpi);
        DrawRegion(drawingContext, new Rect(body.X + half + 2, body.Y + 3, half - 5, body.Height * 0.42), InputId.MouseRight, "右键", dpi);
        DrawRegion(drawingContext, new Rect(body.X + half - 12, body.Y + 20, 24, 52), InputId.MouseMiddle, "中", dpi);
        DrawRegion(drawingContext, new Rect(body.X - 9, body.Y + body.Height * 0.42, 23, 38), InputId.MouseBack, "后", dpi);
        DrawRegion(drawingContext, new Rect(body.X - 9, body.Y + body.Height * 0.62, 23, 38), InputId.MouseForward, "前", dpi);
    }

    private void DrawRegion(DrawingContext context, Rect rect, InputId input, string label, double dpi)
    {
        long count = 0;
        Counts?.TryGetValue(input, out count);
        var fill = count > 0 ? Color.FromRgb(14, 165, 233) : Color.FromRgb(51, 65, 85);
        context.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, 6, 6);
        var text = new FormattedText(
            $"{label}\n{count:N0}",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            10,
            Brushes.White,
            dpi)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(1, rect.Width),
        };
        context.DrawText(text, new Point(rect.X, rect.Y + Math.Max(1, (rect.Height - text.Height) / 2)));
    }
}
