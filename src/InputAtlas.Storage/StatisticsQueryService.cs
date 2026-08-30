using InputAtlas.Core;

namespace InputAtlas.Storage;

public sealed class StatisticsQueryService(IBucketRepository repository) : IStatisticsQueryService
{
    public async ValueTask<IReadOnlyList<StatisticsPoint>> QueryAsync(
        StatisticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.EndUtc <= query.StartUtc)
        {
            return [];
        }

        var snapshots = await repository.ReadRangeAsync(
            query.StartUtc,
            query.EndUtc,
            cancellationToken).ConfigureAwait(false);
        var seconds = GranularitySeconds(query.Granularity);
        var groups = new SortedDictionary<long, Aggregate>();

        foreach (var snapshot in snapshots)
        {
            var start = TimeBuckets.Align(snapshot.BucketStartUtc, seconds);
            if (!groups.TryGetValue(start, out var aggregate))
            {
                aggregate = new Aggregate();
                groups[start] = aggregate;
            }

            aggregate.Coverage = Math.Min((int)seconds, aggregate.Coverage + snapshot.CoverageSeconds);
            foreach (var pair in snapshot.Counts)
            {
                if (query.Input is { } selected && pair.Key != selected)
                {
                    continue;
                }

                if (query.Category is { } category && MetricsCalculator.GetCategory(pair.Key) != category)
                {
                    continue;
                }

                aggregate.Count = SaturatingAdd(aggregate.Count, pair.Value);
            }
        }

        return groups.Select(pair => new StatisticsPoint(
            pair.Key,
            pair.Key + seconds,
            pair.Value.Count,
            pair.Value.Coverage,
            pair.Value.Coverage <= 0
                ? CoverageState.Missing
                : pair.Value.Coverage >= seconds
                    ? CoverageState.Complete
                    : CoverageState.Partial)).ToArray();
    }

    public async ValueTask<InputMetrics> GetMetricsAsync(
        long startUtc,
        long endUtc,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await repository.ReadRangeAsync(startUtc, endUtc, cancellationToken).ConfigureAwait(false);
        return MetricsCalculator.Calculate(snapshots);
    }

    private static long GranularitySeconds(StatisticsGranularity granularity) => granularity switch
    {
        StatisticsGranularity.FiveMinutes => 300,
        StatisticsGranularity.FifteenMinutes => 900,
        StatisticsGranularity.ThirtyMinutes => 1800,
        StatisticsGranularity.OneHour => 3600,
        StatisticsGranularity.SixHours => 21600,
        StatisticsGranularity.OneDay => 86400,
        StatisticsGranularity.OneWeek => 604800,
        StatisticsGranularity.OneMonth => 2678400,
        _ => throw new ArgumentOutOfRangeException(nameof(granularity)),
    };

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class Aggregate
    {
        public long Count { get; set; }

        public int Coverage { get; set; }
    }
}

