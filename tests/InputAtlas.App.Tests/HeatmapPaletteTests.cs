using System.Windows.Media;
using InputAtlas.Core;

namespace InputAtlas.App.Tests;

public sealed class HeatmapPaletteTests
{
    [Fact]
    public void FirstInputStartsFromIdleSurfaceInsteadOfJumpingToBlue()
    {
        var idle = Colors.White;

        var color = HeatmapPalette.GetColor(1, idle, HeatmapThresholds.Default);

        Assert.InRange(Math.Abs(color.R - idle.R), 0, 2);
        Assert.InRange(Math.Abs(color.G - idle.G), 0, 1);
        Assert.InRange(Math.Abs(color.B - idle.B), 0, 1);
    }

    [Fact]
    public void TwoHundredInputsRemainLowSaturationAndFarFromHotRed()
    {
        var color = HeatmapPalette.GetColor(200, Colors.White, HeatmapThresholds.Default);

        Assert.True(color.B > color.R);
        Assert.True(color.G > 170);
        Assert.True(color.R < 190);
        Assert.True(color.B > 175);
    }

    [Fact]
    public void RedHeatRequiresHotThreshold()
    {
        var thresholds = HeatmapThresholds.Default;
        var beforeHot = HeatmapPalette.GetColor(thresholds.Hot - 1, Colors.White, thresholds);
        var hot = HeatmapPalette.GetColor(thresholds.Hot, Colors.White, thresholds);

        Assert.NotEqual(hot, beforeHot);
        Assert.Equal(Color.FromRgb(207, 122, 134), hot);
    }

    [Fact]
    public void CustomThresholdsDelayWarmColors()
    {
        var defaults = HeatmapPalette.GetColor(3000, Colors.White, HeatmapThresholds.Default);
        var delayed = HeatmapPalette.GetColor(3000, Colors.White, new HeatmapThresholds(2000, 6000, 12000));

        Assert.True(delayed.B > defaults.B);
        Assert.True(delayed.R < defaults.R);
    }

    [Fact]
    public void ScoreThresholdsAreTenTimesSingleInputThresholds()
    {
        var scoreThresholds = HeatmapThresholds.Default.Scale(10);

        Assert.Equal(new HeatmapThresholds(1000, 5000, 20000), scoreThresholds);
    }

    [Fact]
    public void ThresholdScaleRejectsNonPositiveFactor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapThresholds.Default.Scale(0));
    }

    [Theory]
    [InlineData(HeatmapThresholdMode.FixedCount)]
    [InlineData(HeatmapThresholdMode.RelativeToMaximum)]
    [InlineData(HeatmapThresholdMode.Percentile)]
    [InlineData(HeatmapThresholdMode.SquareRootScale)]
    public void EveryThresholdModeProducesOrderedThresholds(HeatmapThresholdMode mode)
    {
        var thresholds = HeatmapPalette.ResolveThresholds([1, 12, 45, 120, 900], HeatmapThresholds.Default, mode);

        Assert.True(thresholds.IsValid);
        Assert.True(thresholds.Cool < thresholds.Warm);
        Assert.True(thresholds.Warm < thresholds.Hot);
    }

    [Fact]
    public void RelativeThresholdsFollowCurrentMaximumInsteadOfFixedSettings()
    {
        var thresholds = HeatmapPalette.ResolveThresholds(
            [10, 100, 1000],
            HeatmapThresholds.Default,
            HeatmapThresholdMode.RelativeToMaximum);

        Assert.Equal(new HeatmapThresholds(200, 500, 800), thresholds);
    }

    [Fact]
    public void SquareRootScaleMakesLowFrequencyTransitionVisible()
    {
        var linear = HeatmapPalette.GetColor(25, Colors.White, HeatmapThresholds.Default);
        var squareRoot = HeatmapPalette.GetColor(25, Colors.White, HeatmapThresholds.Default, HeatmapThresholdMode.SquareRootScale);

        Assert.True(squareRoot.R < linear.R);
        Assert.True(squareRoot.B < linear.B);
    }
}
