using InputAtlas.Core;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace InputAtlas.App;

public static class ChartAdapter
{
    public static PlotModel CreateCountsChart(IReadOnlyList<StatisticsPoint> points, string title)
    {
        var model = CreateModel(title);
        var series = new LineSeries
        {
            Color = OxyColor.FromRgb(56, 189, 248),
            StrokeThickness = 2,
            MarkerType = MarkerType.None,
            TrackerFormatString = "{2:yyyy-MM-dd HH:mm}\n次数：{4:N0}",
        };
        foreach (var point in points)
        {
            if (point.Coverage != CoverageState.Missing)
            {
                series.Points.Add(new DataPoint(
                    DateTimeAxis.ToDouble(DateTimeOffset.FromUnixTimeSeconds(point.StartUtc).UtcDateTime),
                    point.Count));
            }
        }

        model.Series.Add(series);
        return model;
    }

    public static PlotModel CreateActivityChart(IReadOnlyList<StatisticsPoint> points)
    {
        var model = CreateCountsChart(points, "活跃分数");
        model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
        {
            Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
            Y = 1000,
            Color = OxyColor.FromRgb(249, 115, 22),
            Text = "活跃阈值 1000",
        });
        return model;
    }

    private static PlotModel CreateModel(string title)
    {
        var model = new PlotModel
        {
            Title = title,
            TextColor = OxyColor.FromRgb(226, 232, 240),
            PlotAreaBorderColor = OxyColor.FromRgb(71, 85, 105),
            Background = OxyColors.Transparent,
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd\nHH:mm",
            TextColor = model.TextColor,
            TicklineColor = model.PlotAreaBorderColor,
            AxislineColor = model.PlotAreaBorderColor,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            TextColor = model.TextColor,
            TicklineColor = model.PlotAreaBorderColor,
            AxislineColor = model.PlotAreaBorderColor,
        });
        return model;
    }
}

