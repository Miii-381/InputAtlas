using InputAtlas.Core;

namespace InputAtlas.Core.Tests;

public sealed class MetricsAndTimeTests
{
    [Fact]
    public void ActivityBoundaryIsStrictlyGreaterThanOneThousand()
    {
        var exactly = BucketSnapshot.Create(0, 300, [new(InputId.MouseLeft, 1000)], 0);
        var above = BucketSnapshot.Create(0, 300, [new(InputId.MouseLeft, 1000), new(InputId.WheelUp, 1)], 0);

        Assert.False(MetricsCalculator.Calculate([exactly]).IsActiveDay);
        Assert.True(MetricsCalculator.Calculate([above]).IsActiveDay);
        Assert.Equal(1000.1m, MetricsCalculator.Calculate([above]).ActivityScore);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(299, 0)]
    [InlineData(300, 300)]
    [InlineData(601, 600)]
    public void FiveMinuteAlignmentUsesUtcBoundaries(long input, long expected)
    {
        Assert.Equal(expected, TimeBuckets.AlignFiveMinutes(input));
    }

    [Fact]
    public void MainMetricDoesNotIncludeMouseOrWheel()
    {
        var snapshot = BucketSnapshot.Create(
            0,
            300,
            [
                new(new InputId(0x04), 10),
                new(InputId.MouseLeft, 20),
                new(InputId.WheelUp, 30),
            ],
            0);

        var metrics = MetricsCalculator.Calculate([snapshot]);
        Assert.Equal(10, metrics.KeyboardCount);
        Assert.Equal(20, metrics.MouseButtonCount);
        Assert.Equal(30, metrics.WheelSteps);
        Assert.Equal(33m, metrics.ActivityScore);
    }
}
