namespace InputAtlas.Core;

public interface IInputCaptureController : IAsyncDisposable
{
    CaptureStatus Status { get; }

    event EventHandler<CaptureStatus>? StatusChanged;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask PauseAsync(CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    BucketSnapshot GetCurrentSnapshot();
}

public interface IBucketRepository : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertFiveMinuteAsync(BucketSnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<BucketSnapshot>> ReadRangeAsync(
        long startUtc,
        long endUtc,
        CancellationToken cancellationToken = default);

    ValueTask<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default);

    ValueTask SetMetadataAsync(string key, string value, CancellationToken cancellationToken = default);

    ValueTask<bool> IntegrityCheckAsync(CancellationToken cancellationToken = default);
}

public interface IStatisticsQueryService
{
    ValueTask<IReadOnlyList<StatisticsPoint>> QueryAsync(
        StatisticsQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<InputMetrics> GetMetricsAsync(
        long startUtc,
        long endUtc,
        CancellationToken cancellationToken = default);
}

public interface IApplicationLog : IAsyncDisposable
{
    void Debug(string eventName, string message);

    void Information(string eventName, string message);

    void Warning(string eventName, string message);

    void LogError(string eventName, string message, Exception? exception = null);
}
