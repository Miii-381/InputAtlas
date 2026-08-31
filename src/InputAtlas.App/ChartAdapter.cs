using InputAtlas.Core;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace InputAtlas.App;

public static class ChartAdapter
{
    public static PlotModel CreateCountsChart(
        IReadOnlyList<StatisticsPoint> points,
        string title,
        TimeZoneInfo displayTimeZone)
    {
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        var model = CreateModel(title);
        // 仪表盘卡片已经提供标题，图表画布保留给坐标与数据；参数仍用于兼容现有调用方。
        model.Title = string.Empty;
        var series = CreateLineSeries();
        model.Series.Add(series);
        ReplacePoints(series, points, displayTimeZone);
        return model;
    }

    public static PlotModel CreateActivityChart(
        IReadOnlyList<StatisticsPoint> points,
        TimeZoneInfo displayTimeZone)
    {
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        var model = CreateModel("活跃分数");
        model.Title = string.Empty;
        var series = CreateLineSeries();
        model.Series.Add(series);
        ReplacePoints(series, points, displayTimeZone, 0.1);
        model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
        {
            Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
            Y = 1000,
            Color = OxyColor.FromRgb(168, 132, 54),
            Text = "活跃阈值 1000",
            TextColor = OxyColor.FromRgb(117, 109, 100),
        });
        return model;
    }

    public static PlotModel CreateKeyDistributionChart(IReadOnlyList<InputRankingItem> items)
    {
        var model = CreatePieModel("按键占比");
        UpdateKeyDistributionChart(model, items);
        return model;
    }

    public static PlotModel CreateCategoryDistributionChart(IReadOnlyList<CategorySummaryItem> items)
    {
        var model = CreatePieModel("输入类型占比");
        UpdateCategoryDistributionChart(model, items);
        return model;
    }

    public static void UpdateCountsChart(
        PlotModel model,
        IReadOnlyList<StatisticsPoint> points,
        TimeZoneInfo displayTimeZone)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        var series = model.Series.OfType<LineSeries>().FirstOrDefault();
        if (series is null)
        {
            series = CreateLineSeries();
            model.Series.Add(series);
        }

        ReplacePoints(series, points, displayTimeZone);
        model.InvalidatePlot(updateData: true);
    }

    public static void UpdateActivityChart(
        PlotModel model,
        IReadOnlyList<StatisticsPoint> points,
        TimeZoneInfo displayTimeZone)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        var series = model.Series.OfType<LineSeries>().FirstOrDefault();
        if (series is null)
        {
            series = CreateLineSeries();
            model.Series.Add(series);
        }

        ReplacePoints(series, points, displayTimeZone, 0.1);
        model.InvalidatePlot(updateData: true);
    }

    public static void UpdateKeyDistributionChart(PlotModel model, IReadOnlyList<InputRankingItem> items)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(items);
        var series = model.Series.OfType<PieSeries>().FirstOrDefault();
        if (series is null)
        {
            series = CreatePieSeries();
            model.Series.Clear();
            model.Series.Add(series);
        }

        series.Slices.Clear();
        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            series.Slices.Add(new PieSlice(item.Label, Math.Max(0, item.Count))
            {
                Fill = DistributionColors[index % DistributionColors.Length],
                // 仪表盘饼图保持为完整圆形，避免最大扇区因数据差距被视觉上拆离主体。
                IsExploded = false,
            });
        }

        model.Title = items.Count == 0 ? "按键占比（暂无数据）" : string.Empty;
        model.InvalidatePlot(updateData: true);
    }

    public static void UpdateCategoryDistributionChart(PlotModel model, IReadOnlyList<CategorySummaryItem> items)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(items);
        var series = model.Series.OfType<PieSeries>().FirstOrDefault();
        if (series is null)
        {
            series = CreatePieSeries();
            model.Series.Clear();
            model.Series.Add(series);
        }

        series.Slices.Clear();
        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            series.Slices.Add(new PieSlice(item.Label, Math.Max(0, item.Count))
            {
                Fill = DistributionColors[index % DistributionColors.Length],
                // 输入类型只通过角度表达占比，不使用会改变图形边界的爆炸效果。
                IsExploded = false,
            });
        }

        model.Title = items.Count == 0 ? "输入类型占比（暂无数据）" : string.Empty;
        model.InvalidatePlot(updateData: true);
    }

    private static LineSeries CreateLineSeries() => new()
    {
        Color = OxyColor.FromRgb(127, 162, 162),
        StrokeThickness = 2.5,
        MarkerType = MarkerType.Circle,
        MarkerSize = 3,
        MarkerFill = OxyColor.FromRgb(255, 252, 246),
        MarkerStroke = OxyColor.FromRgb(127, 162, 162),
        MarkerStrokeThickness = 1.5,
        TrackerFormatString = "{2:yyyy-MM-dd HH:mm}\n次数：{4:N0}",
    };

    private static PlotModel CreatePieModel(string title)
    {
        var model = CreateModel(title);
        model.Axes.Clear();
        model.Legends.Add(new Legend
        {
            LegendPlacement = LegendPlacement.Outside,
            LegendPosition = LegendPosition.RightMiddle,
            LegendOrientation = LegendOrientation.Vertical,
            LegendSymbolLength = 16,
            LegendMargin = 10,
            LegendTextColor = model.TextColor,
        });
        model.Series.Add(CreatePieSeries());
        return model;
    }

    private static PieSeries CreatePieSeries() => new()
    {
        Stroke = OxyColor.FromRgb(255, 252, 246),
        StrokeThickness = 2,
        InsideLabelColor = OxyColor.FromRgb(49, 45, 40),
        // 仅保留外部标签，防止小扇区同时绘制内外两组文字并互相覆盖。
        InsideLabelFormat = string.Empty,
        OutsideLabelFormat = "{1} {2:0}%",
        TickDistance = 5,
        TickRadialLength = 5,
        TickHorizontalLength = 6,
        TickLabelDistance = 3,
        // 在完整矩形绘图区中适度放大圆环，同时给外部标签保留安全边距。
        Diameter = 0.72,
        InnerDiameter = 0.29,
        // OxyPlot 使用相对半径而非像素作为该值的单位；仪表盘固定为 0，确保任何量级都不越界。
        ExplodedDistance = 0,
        StartAngle = 90,
    };

    private static readonly OxyColor[] DistributionColors =
    [
        OxyColor.FromRgb(111, 159, 157),
        OxyColor.FromRgb(134, 169, 207),
        OxyColor.FromRgb(234, 193, 180),
        OxyColor.FromRgb(245, 223, 166),
        OxyColor.FromRgb(184, 214, 209),
        OxyColor.FromRgb(199, 154, 183),
        OxyColor.FromRgb(216, 154, 114),
        OxyColor.FromRgb(158, 189, 142),
    ];

    private static void ReplacePoints(
        LineSeries series,
        IReadOnlyList<StatisticsPoint> points,
        TimeZoneInfo displayTimeZone,
        double valueScale = 1)
    {
        series.Points.Clear();
        foreach (var point in points)
        {
            if (point.Coverage != CoverageState.Missing)
            {
                var displayTime = TimeZoneInfo.ConvertTime(
                    DateTimeOffset.FromUnixTimeSeconds(point.StartUtc),
                    displayTimeZone);
                series.Points.Add(new DataPoint(
                    DateTimeAxis.ToDouble(displayTime.DateTime),
                    point.Count * valueScale));
            }
        }
    }

    private static PlotModel CreateModel(string title)
    {
        var model = new PlotModel
        {
            Title = title,
            DefaultFontSize = 13,
            TitleColor = OxyColor.FromRgb(49, 45, 40),
            TextColor = OxyColor.FromRgb(117, 109, 100),
            PlotAreaBorderColor = OxyColor.FromRgb(220, 209, 193),
            PlotAreaBackground = OxyColor.FromRgb(255, 252, 246),
            Background = OxyColors.Transparent,
            Padding = new OxyThickness(16, 12, 16, 10),
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd\nHH:mm",
            TextColor = model.TextColor,
            TicklineColor = model.PlotAreaBorderColor,
            AxislineColor = model.PlotAreaBorderColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(239, 232, 221),
            MinorGridlineStyle = LineStyle.None,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            TextColor = model.TextColor,
            TicklineColor = model.PlotAreaBorderColor,
            AxislineColor = model.PlotAreaBorderColor,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(239, 232, 221),
            MinorGridlineStyle = LineStyle.None,
        });
        return model;
    }
}
