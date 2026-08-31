using System.Globalization;
using System.Windows.Data;

namespace InputAtlas.App;

/// <summary>
/// 为概览统计值选择离散字号，避免 Viewbox 的连续缩放让字形落在分数像素上。
/// </summary>
public sealed class MetricFontSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var length = value?.ToString()?.Length ?? 0;
        return length switch
        {
            <= 7 => 29d,
            <= 10 => 26d,
            <= 13 => 23d,
            <= 16 => 20d,
            _ => 18d,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
