using System.Globalization;

namespace InputAtlas.App.Tests;

public sealed class MetricFontSizeConverterTests
{
    private readonly MetricFontSizeConverter _converter = new();

    [Theory]
    [InlineData("123", 29d)]
    [InlineData("123,456", 29d)]
    [InlineData("1,234,567", 26d)]
    [InlineData("123,456,789", 23d)]
    [InlineData("12,345,678,901", 20d)]
    [InlineData("123,456,789,012,345", 18d)]
    public void ConvertSelectsStableDiscreteFontSize(string value, double expected)
    {
        var actual = _converter.Convert(value, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, actual);
    }
}
