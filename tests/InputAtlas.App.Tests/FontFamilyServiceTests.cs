namespace InputAtlas.App.Tests;

public sealed class FontFamilyServiceTests
{
    [Fact]
    public void OptionsContainModernWindowsAndChineseFallbacks()
    {
        var values = FontFamilyService.Options.Select(option => option.Value).ToArray();

        Assert.Contains("Segoe UI Variable Text", values);
        Assert.Contains("Segoe UI", values);
        Assert.Contains("等线", values);
        Assert.Contains("Microsoft YaHei UI", values);
    }

    [Theory]
    [InlineData("Segoe UI Variable Text", "Segoe UI Variable Text")]
    [InlineData(" 等线 ", "等线")]
    [InlineData("missing-font", "Segoe UI Variable Text")]
    [InlineData(null, "Segoe UI Variable Text")]
    public void NormalizeReturnsAStableSupportedFamily(string? input, string expected)
    {
        Assert.Equal(expected, FontFamilyService.Normalize(input));
    }

    [Fact]
    public void ApplyBuildsACompositeFamilyStartingWithTheSelection()
    {
        var applied = FontFamilyService.Apply("等线");

        Assert.StartsWith("等线", applied.Source);
    }
}
