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
        HeatmapThresholds thresholds,
        HeatmapThresholdMode thresholdMode,
        IReadOnlyDictionary<InputId, double> animationProgress,
        Color accentColor,
        FontFamily fontFamily,
        double pixelsPerDip);

    InputId? HitTest(Point point, Size size, KeyboardLayoutDefinition layout);
}

public sealed class KeyboardRenderer2D : IKeyboardRenderer
{
    private const double Gap = 4;
    private const double CanvasPadding = 14;

    public void Render(
        DrawingContext context,
        Size size,
        KeyboardLayoutDefinition layout,
        IReadOnlyDictionary<InputId, long> counts,
        InputId? selected,
        HeatmapThresholds thresholds,
        HeatmapThresholdMode thresholdMode,
        IReadOnlyDictionary<InputId, double> animationProgress,
        Color accentColor,
        FontFamily fontFamily,
        double pixelsPerDip)
    {
        var scale = CalculateScale(size, layout);
        var offset = CalculateOffset(size, layout, scale);
        DrawKeyboardDeck(context, layout, offset, scale, accentColor);

        foreach (var key in layout.Keys)
        {
            var rect = ToRect(key, offset, scale);
            rect.Inflate(-Gap / 2, -Gap / 2);
            animationProgress.TryGetValue(key.Input, out var pressed);
            if (pressed > 0)
            {
                rect.Inflate(-rect.Width * 0.015 * pressed, -rect.Height * 0.015 * pressed);
                rect.Offset(0, 2.2 * pressed);
            }

            counts.TryGetValue(key.Input, out var count);
            var baseColor = key.Observable
                ? HeatmapPalette.GetColor(count, Color.FromRgb(255, 252, 246), thresholds, thresholdMode)
                : Color.FromRgb(241, 233, 222);
            var fillColor = pressed > 0
                ? Blend(baseColor, accentColor, pressed * 0.44)
                : baseColor;
            var shadowRect = rect;
            shadowRect.Offset(0, Math.Max(0.7, 2.3 - pressed * 1.6));
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(35, 29, 27, 32)),
                null,
                shadowRect,
                7,
                7);

            var border = selected == key.Input
                ? new Pen(new SolidColorBrush(accentColor), 2.3)
                : new Pen(new SolidColorBrush(pressed > 0 ? accentColor : Color.FromRgb(220, 209, 193)), pressed > 0 ? 1.6 : 1.1);
            context.DrawRoundedRectangle(new SolidColorBrush(fillColor), border, rect, 7, 7);

            var useLightText = RelativeLuminance(fillColor) < 0.47;
            var primaryText = useLightText ? Color.FromRgb(255, 255, 255) : Color.FromRgb(49, 45, 40);
            var secondaryText = useLightText ? Color.FromArgb(220, 255, 255, 255) : Color.FromRgb(117, 109, 100);
            var label = new FormattedText(
                key.Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                Math.Clamp(scale * 0.18, 8.5, 12.5),
                new SolidColorBrush(primaryText),
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 8),
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(label, new Point(rect.X + 4.5, rect.Y + 3));

            var value = new FormattedText(
                key.Observable ? count.ToString("N0", CultureInfo.CurrentCulture) : "—",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                Math.Clamp(scale * 0.19, 8.5, 13.5),
                key.Observable ? new SolidColorBrush(primaryText) : new SolidColorBrush(secondaryText),
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 8),
                TextAlignment = TextAlignment.Right,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            if (rect.Height >= 24 && rect.Width >= 23)
            {
                context.DrawText(value, new Point(rect.X + 4, rect.Bottom - value.Height - 2.5));
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
        Math.Max(1, Math.Min(
            Math.Max(1, size.Width - CanvasPadding * 2) / layout.WidthUnits,
            Math.Max(1, size.Height - CanvasPadding * 2) / layout.HeightUnits));

    private static Vector CalculateOffset(Size size, KeyboardLayoutDefinition layout, double scale) => new(
        Math.Max(0, (size.Width - layout.WidthUnits * scale) / 2),
        Math.Max(0, (size.Height - layout.HeightUnits * scale) / 2));

    private static void DrawKeyboardDeck(
        DrawingContext context,
        KeyboardLayoutDefinition layout,
        Vector offset,
        double scale,
        Color accentColor)
    {
        var deck = new Rect(
            offset.X - 7,
            offset.Y - 7,
            layout.WidthUnits * scale + 14,
            layout.HeightUnits * scale + 14);
        var ambientShadow = deck;
        ambientShadow.Inflate(5, 5);
        ambientShadow.Offset(0, 4);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(22, accentColor.R, accentColor.G, accentColor.B)),
            null,
            ambientShadow,
            18,
            18);

        var contactShadow = deck;
        contactShadow.Offset(0, 3);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(28, 29, 27, 32)), null, contactShadow, 15, 15);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(255, 252, 246)),
            new Pen(new SolidColorBrush(Color.FromRgb(220, 209, 193)), 1),
            deck,
            15,
            15);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return Channel(color.R) * 0.2126 + Channel(color.G) * 0.7152 + Channel(color.B) * 0.0722;
    }

    private static Color Blend(Color start, Color end, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(start.R + (end.R - start.R) * amount),
            (byte)(start.G + (end.G - start.G) * amount),
            (byte)(start.B + (end.B - start.B) * amount));
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
        HeatmapThresholds thresholds,
        HeatmapThresholdMode thresholdMode,
        IReadOnlyDictionary<InputId, double> animationProgress,
        Color accentColor,
        FontFamily fontFamily,
        double pixelsPerDip)
    {
        context.PushTransform(new SkewTransform(-2.2, 0, size.Width / 2, size.Height / 2));
        context.PushTransform(new TranslateTransform(0, -2));
        _baseline.Render(context, size, layout, counts, selected, thresholds, thresholdMode, animationProgress, accentColor, fontFamily, pixelsPerDip);
        context.Pop();
        context.Pop();
    }

    public InputId? HitTest(Point point, Size size, KeyboardLayoutDefinition layout) =>
        _baseline.HitTest(point, size, layout);
}
