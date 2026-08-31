namespace InputAtlas.App.Tests;

public sealed class ThemeColorServiceTests
{
    [Fact]
    public void DefaultPaletteUsesTheWarmMutedPrimary()
    {
        Assert.Equal("#F3D48D", ThemeColorService.DefaultAccentColor);
        Assert.Equal("#F9EBC8", ThemeColorService.DefaultAccentContainerColor);
    }

    [Theory]
    [InlineData("#00796b", "#00796B")]
    [InlineData("635BFF", "#635BFF")]
    [InlineData("  #c62828  ", "#C62828")]
    public void ValidHexColorIsNormalized(string input, string expected)
    {
        Assert.True(ThemeColorService.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("not-a-color")]
    public void InvalidHexColorIsRejected(string input)
    {
        Assert.False(ThemeColorService.TryNormalize(input, out _));
    }
}
