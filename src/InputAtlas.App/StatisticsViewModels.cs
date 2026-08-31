using InputAtlas.Core;

namespace InputAtlas.App;

public sealed record InputRankingItem(
    int Rank,
    string Label,
    long Count,
    double Share,
    string Category);

public sealed record CategorySummaryItem(
    string Label,
    long Count,
    double Share,
    InputCategory Category);

public sealed record HeatmapThresholdModeOption(
    HeatmapThresholdMode Value,
    string Label,
    string Description)
{
    public override string ToString() => Label;
}
