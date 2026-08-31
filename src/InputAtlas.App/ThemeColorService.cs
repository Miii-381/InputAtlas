using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace InputAtlas.App;

public static class ThemeColorService
{
    public const string DefaultAccentColor = "#F3D48D";
    public const string DefaultAccentContainerColor = "#F9EBC8";

    private static readonly Color WarmSurfaceColor = Color.FromRgb(252, 250, 245);
    private static readonly Color WarmTextColor = Color.FromRgb(49, 45, 40);

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = DefaultAccentColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        normalized = $"#{rgb:X6}";
        return true;
    }

    public static void Apply(string normalizedHex)
    {
        if (!TryNormalize(normalizedHex, out var normalized))
        {
            normalized = DefaultAccentColor;
        }

        var accent = (Color)ColorConverter.ConvertFromString(normalized);
        var container = string.Equals(normalized, DefaultAccentColor, StringComparison.OrdinalIgnoreCase)
            ? (Color)ColorConverter.ConvertFromString(DefaultAccentContainerColor)
            : Blend(accent, WarmSurfaceColor, 0.78);
        var foreground = GetContrastingForeground(accent);
        var accentText = Blend(accent, WarmTextColor, RelativeLuminance(accent) > 0.25 ? 0.48 : 0.18);
        UpdateBrush("AccentBrush", accent);
        UpdateBrush("AccentContainerBrush", container);
        UpdateBrush("AccentTextBrush", accentText);
        UpdateBrush("AccentForegroundBrush", foreground);
        Application.Current.Resources["AccentColor"] = accent;
        Application.Current.Resources["AccentContainerColor"] = container;
    }

    public static Color GetContrastingForeground(Color background) =>
        RelativeLuminance(background) > 0.24 ? WarmTextColor : Colors.White;

    private static void UpdateBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Blend(Color start, Color end, double amount) => Color.FromRgb(
        (byte)(start.R + (end.R - start.R) * amount),
        (byte)(start.G + (end.G - start.G) * amount),
        (byte)(start.B + (end.B - start.B) * amount));

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
}
