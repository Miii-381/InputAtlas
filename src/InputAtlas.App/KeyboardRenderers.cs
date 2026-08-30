using System.Globalization;
using System.Windows;
using System.Windows.Media;
using InputAtlas.Core;

namespace InputAtlas.App;

public interface IKeyboardRenderer
{
    void Render(
        DrawingContext context,
        Size size,
        KeyboardLayoutDefinition layout,
        IReadOnlyDictionary<InputId, long> counts,
        InputId? selected,
        double pixelsPerDip);

    InputId? HitTest(Point point, Size size, KeyboardLayoutDefinition layout);
}

public sealed class KeyboardRenderer2D : IKeyboardRenderer
{
    private const double Gap = 3;

    public void Render(
        DrawingContext context,
        Size size,
        KeyboardLayoutDefinition layout,
        IReadOnlyDictionary<InputId, long> counts,
        InputId? selected,
        double pixelsPerDip)
    {
        var scale = CalculateScale(size, layout);
        var offset = CalculateOffset(size, layout, scale);
        var maximum = Math.Max(1, counts.Where(pair => pair.Key.Value < 900).Select(static pair => pair.Value).DefaultIfEmpty().Max());
        foreach (var key in layout.Keys)
        {
            var rect = ToRect(key, offset, scale);
            rect.Inflate(-Gap / 2, -Gap / 2);
            counts.TryGetValue(key.Input, out var count);
            var fill = key.Observable ? HeatBrush(count, maximum) : new SolidColorBrush(Color.FromRgb(51, 65, 85));
            var border = selected == key.Input
                ? new Pen(new SolidColorBrush(Color.FromRgb(248, 250, 252)), 2.2)
                : new Pen(new SolidColorBrush(Color.FromRgb(71, 85, 105)), 1);
            context.DrawRoundedRectangle(fill, border, rect, 5, 5);

            var labelSize = Math.Clamp(scale * 0.18, 8, 12);
            var label = new FormattedText(
                key.Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                labelSize,
                Brushes.White,
                pixelsPerDip);
            context.DrawText(label, new Point(rect.X + 6, rect.Y + 4));

            var valueText = key.Observable ? count.ToString("N0", CultureInfo.CurrentCulture) : "不可统计";
            var value = new FormattedText(
                valueText,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                Math.Clamp(scale * 0.19, 8, 13),
                key.Observable ? Brushes.White : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 10),
                TextAlignment = TextAlignment.Right,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            if (rect.Height >= 33 && rect.Width >= 32)
            {
                context.DrawText(value, new Point(rect.X + 5, rect.Bottom - value.Height - 4));
            }
        }
    }

    public InputId? HitTest(Point point, Size size, KeyboardLayoutDefinition layout)
    {
        var scale = CalculateScale(size, layout);
        var offset = CalculateOffset(size, layout, scale);
        foreach (var key in layout.Keys)
        {
            if (ToRect(key, offset, scale).Contains(point))
            {
                return key.Input;
            }
        }

        return null;
    }

    private static Rect ToRect(KeyDefinition key, Vector offset, double scale) => new(
        offset.X + key.X * scale,
        offset.Y + key.Y * scale,
        key.Width * scale,
        key.Height * scale);

    private static double CalculateScale(Size size, KeyboardLayoutDefinition layout) =>
        Math.Max(1, Math.Min(size.Width / layout.WidthUnits, size.Height / layout.HeightUnits));

    private static Vector CalculateOffset(Size size, KeyboardLayoutDefinition layout, double scale) => new(
        Math.Max(0, (size.Width - layout.WidthUnits * scale) / 2),
        Math.Max(0, (size.Height - layout.HeightUnits * scale) / 2));

    private static SolidColorBrush HeatBrush(long count, long maximum)
    {
        if (count <= 0)
        {
            return new SolidColorBrush(Color.FromRgb(30, 41, 59));
        }

        var intensity = Math.Log(1 + count) / Math.Log(1 + maximum);
        return intensity switch
        {
            < 0.45 => Blend(Color.FromRgb(30, 64, 175), Color.FromRgb(14, 165, 233), intensity / 0.45),
            < 0.75 => Blend(Color.FromRgb(14, 165, 233), Color.FromRgb(250, 204, 21), (intensity - 0.45) / 0.30),
            _ => Blend(Color.FromRgb(250, 204, 21), Color.FromRgb(249, 115, 22), (intensity - 0.75) / 0.25),
        };
    }

    private static SolidColorBrush Blend(Color start, Color end, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new SolidColorBrush(Color.FromRgb(
            (byte)(start.R + (end.R - start.R) * amount),
            (byte)(start.G + (end.G - start.G) * amount),
            (byte)(start.B + (end.B - start.B) * amount)));
    }
}

public sealed class KeyboardRenderer25D : IKeyboardRenderer
{
    private readonly KeyboardRenderer2D _baseline = new();

    public void Render(
        DrawingContext context,
        Size size,
        KeyboardLayoutDefinition layout,
        IReadOnlyDictionary<InputId, long> counts,
        InputId? selected,
        double pixelsPerDip)
    {
        context.PushTransform(new SkewTransform(-2.2, 0, size.Width / 2, size.Height / 2));
        context.PushTransform(new TranslateTransform(0, -2));
        _baseline.Render(context, size, layout, counts, selected, pixelsPerDip);
        context.Pop();
        context.Pop();
    }

    public InputId? HitTest(Point point, Size size, KeyboardLayoutDefinition layout) =>
        _baseline.HitTest(point, size, layout);
}
