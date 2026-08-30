using System.Collections.ObjectModel;

namespace InputAtlas.Core;

public enum InputCategory
{
    Keyboard,
    MouseButton,
    Wheel,
    Other,
}

public enum CaptureStatus
{
    Starting,
    Recording,
    Paused,
    Unavailable,
    FaultBuffering,
    Stopped,
}

public enum CoverageState
{
    Missing,
    Partial,
    Complete,
}

public enum StatisticsGranularity
{
    FiveMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    SixHours,
    OneDay,
    OneWeek,
    OneMonth,
}

public sealed record BucketSnapshot(
    long BucketStartUtc,
    int CoverageSeconds,
    IReadOnlyDictionary<InputId, long> Counts,
    long UpdatedUtc)
{
    public static BucketSnapshot Create(
        long bucketStartUtc,
        int coverageSeconds,
        IEnumerable<KeyValuePair<InputId, long>> counts,
        long updatedUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bucketStartUtc);
        ArgumentOutOfRangeException.ThrowIfNegative(coverageSeconds);

        var ordered = new SortedDictionary<InputId, long>();
        foreach (var item in counts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(item.Value);
            if (item.Value > 0)
            {
                ordered[item.Key] = item.Value;
            }
        }

        return new BucketSnapshot(
            bucketStartUtc,
            coverageSeconds,
            new ReadOnlyDictionary<InputId, long>(ordered),
            updatedUtc);
    }
}

public sealed record StatisticsQuery(
    long StartUtc,
    long EndUtc,
    StatisticsGranularity Granularity,
    InputCategory? Category = null,
    InputId? Input = null);

public sealed record StatisticsPoint(
    long StartUtc,
    long EndUtc,
    long Count,
    int CoverageSeconds,
    CoverageState Coverage);

public sealed record InputMetrics(
    long KeyboardCount,
    long MouseButtonCount,
    long WheelSteps,
    long ActivityUnits)
{
    public decimal ActivityScore => ActivityUnits / 5m;

    public bool IsActiveDay => ActivityUnits > 5000;
}

