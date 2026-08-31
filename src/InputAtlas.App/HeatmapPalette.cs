using System.Windows.Media;
using InputAtlas.Core;

namespace InputAtlas.App;

public readonly record struct HeatmapThresholds(long Cool, long Warm, long Hot)
{
    public static HeatmapThresholds Default { get; } = new(100, 500, 2000);

    public bool IsValid => Cool > 0 && Cool < Warm && Warm < Hot;

    public HeatmapThresholds Scale(long factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        return new HeatmapThresholds(
            SaturatingMultiply(Cool, factor),
            SaturatingMultiply(Warm, factor),
            SaturatingMultiply(Hot, factor));
    }

    private static long SaturatingMultiply(long value, long factor) =>
        value > long.MaxValue / factor ? long.MaxValue : value * factor;
}

public static class HeatmapPalette
{
    public static readonly Color CoolEnd = Color.FromRgb(143, 184, 207);
    public static readonly Color WarmEnd = Color.FromRgb(214, 182, 108);
    public static readonly Color HotEnd = Color.FromRgb(207, 122, 134);

    /// <summary>
    /// 根据当前图中数据计算实际使用的三个阈值。
    /// </summary>
    public static HeatmapThresholds ResolveThresholds(
        IEnumerable<long>? counts,
        HeatmapThresholds configured,
        HeatmapThresholdMode mode)
    {
        if (!configured.IsValid)
        {
            configured = HeatmapThresholds.Default;
        }

        if (mode == HeatmapThresholdMode.FixedCount)
        {
            return configured;
        }

        var values = counts?
            .Where(static count => count > 0)
            .OrderBy(static count => count)
            .ToArray() ?? [];
        if (values.Length == 0)
        {
            return configured;
        }

        var maximum = values[^1];
        return mode switch
        {
            HeatmapThresholdMode.RelativeToMaximum => EnsureOrdered(
                Scale(maximum, 0.20),
                Scale(maximum, 0.50),
                Scale(maximum, 0.80)),
            HeatmapThresholdMode.Percentile => EnsureOrdered(
                Percentile(values, 0.50),
                Percentile(values, 0.75),
                Percentile(values, 0.90)),
            _ => configured,
        };
    }

    public static Color GetColor(long count, Color idleColor, HeatmapThresholds thresholds) =>
        GetColor(count, idleColor, thresholds, HeatmapThresholdMode.FixedCount);

    public static Color GetColor(
        long count,
        Color idleColor,
        HeatmapThresholds thresholds,
        HeatmapThresholdMode mode)
    {
        if (!thresholds.IsValid)
        {
            thresholds = HeatmapThresholds.Default;
        }

        if (count <= 0)
        {
            return idleColor;
        }

        if (count < thresholds.Cool)
        {
            return Blend(idleColor, CoolEnd, AdjustProgress(count / (double)thresholds.Cool, mode));
        }

        if (count < thresholds.Warm)
        {
            return Blend(CoolEnd, WarmEnd, AdjustProgress(
                (count - thresholds.Cool) / (double)(thresholds.Warm - thresholds.Cool),
                mode));
        }

        if (count < thresholds.Hot)
        {
            return Blend(WarmEnd, HotEnd, AdjustProgress(
                (count - thresholds.Warm) / (double)(thresholds.Hot - thresholds.Warm),
                mode));
        }

        return HotEnd;
    }

    private static double AdjustProgress(double progress, HeatmapThresholdMode mode) =>
        mode == HeatmapThresholdMode.SquareRootScale
            ? Math.Sqrt(Math.Clamp(progress, 0, 1))
            : Math.Clamp(progress, 0, 1);

    private static long Scale(long value, double ratio)
    {
        if (value <= 0)
        {
            return 1;
        }

        var scaled = value * ratio;
        if (scaled >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return Math.Max(1, (long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private static long Percentile(long[] values, double percentile)
    {
        var index = (int)Math.Clamp(
            Math.Ceiling((values.Length - 1) * percentile),
            0,
            values.Length - 1);
        return Math.Max(1, values[index]);
    }

    private static HeatmapThresholds EnsureOrdered(long cool, long warm, long hot)
    {
        cool = Math.Max(1, cool);
        warm = Math.Max(cool + (cool == long.MaxValue ? 0 : 1), warm);
        if (warm == long.MaxValue && cool == long.MaxValue)
        {
            cool = long.MaxValue - 2;
            warm = long.MaxValue - 1;
        }

        hot = Math.Max(warm + (warm == long.MaxValue ? 0 : 1), hot);
        if (hot <= warm)
        {
            hot = long.MaxValue;
        }

        return new HeatmapThresholds(cool, warm, hot);
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
