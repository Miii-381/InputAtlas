using InputAtlas.App;
using InputAtlas.Core;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace InputAtlas.App.Tests;

public sealed class ChartAdapterTests
{
    private static readonly TimeZoneInfo UtcPlusEight = TimeZoneInfo.CreateCustomTimeZone(
        "Test UTC+08",
        TimeSpan.FromHours(8),
        "Test UTC+08",
        "Test UTC+08");

    [Fact]
    public void UpdatingCountsPreservesTheUsersAxisZoom()
    {
        var initial = new[]
        {
            new StatisticsPoint(100, 200, 10, 100, CoverageState.Complete),
            new StatisticsPoint(200, 300, 20, 100, CoverageState.Complete),
        };
        var model = ChartAdapter.CreateCountsChart(initial, "测试趋势", UtcPlusEight);
        var horizontalAxis = Assert.IsType<DateTimeAxis>(model.Axes[0]);
        horizontalAxis.Zoom(15, 25);

        ChartAdapter.UpdateCountsChart(
            model,
            [new StatisticsPoint(300, 400, 30, 100, CoverageState.Complete)],
            UtcPlusEight);

        Assert.Equal(15, horizontalAxis.ActualMinimum);
        Assert.Equal(25, horizontalAxis.ActualMaximum);
        var series = Assert.IsType<LineSeries>(Assert.Single(model.Series));
        Assert.Single(series.Points);
    }

    [Fact]
    public void CountsChartConvertsUtcTimestampsToTheConfiguredDisplayTimeZone()
    {
        var model = ChartAdapter.CreateCountsChart(
            [new StatisticsPoint(0, 300, 10, 300, CoverageState.Complete)],
            "测试趋势",
            UtcPlusEight);

        var horizontalAxis = Assert.IsType<DateTimeAxis>(model.Axes[0]);
        var series = Assert.IsType<LineSeries>(Assert.Single(model.Series));
        var displayed = horizontalAxis.ConvertToDateTime(Assert.Single(series.Points).X);

        Assert.Equal(new DateTime(1970, 1, 1, 8, 0, 0), displayed);
    }

    [Fact]
    public void ActivityChartConvertsUtcTimestampsToTheConfiguredDisplayTimeZone()
    {
        var model = ChartAdapter.CreateActivityChart(
            [new StatisticsPoint(0, 300, 10, 300, CoverageState.Complete)],
            UtcPlusEight);

        var horizontalAxis = Assert.IsType<DateTimeAxis>(model.Axes[0]);
        var series = Assert.IsType<LineSeries>(Assert.Single(model.Series));
        var displayed = horizontalAxis.ConvertToDateTime(Assert.Single(series.Points).X);

        Assert.Equal(new DateTime(1970, 1, 1, 8, 0, 0), displayed);
    }

    [Fact]
    public void KeyDistributionChartUsesTheProvidedRankingItems()
    {
        var model = ChartAdapter.CreateKeyDistributionChart(
        [
            new InputRankingItem(1, "空格", 80, 0.8, "键盘"),
            new InputRankingItem(2, "E", 20, 0.2, "键盘"),
        ]);

        var series = Assert.IsType<PieSeries>(Assert.Single(model.Series));
        Assert.Equal(2, series.Slices.Count);
        Assert.Equal("空格", series.Slices[0].Label);
        Assert.Equal(80, series.Slices[0].Value);
        Assert.Equal(0, series.ExplodedDistance);
        Assert.Equal(0.72, series.Diameter);
        Assert.Equal(0.29, series.InnerDiameter);
        Assert.All(series.Slices, slice => Assert.False(slice.IsExploded));
    }

    [Fact]
    public void EmptyDistributionChartShowsAnExplicitEmptyTitle()
    {
        var model = ChartAdapter.CreateCategoryDistributionChart([]);

        Assert.Contains("暂无数据", model.Title, StringComparison.Ordinal);
    }
}
