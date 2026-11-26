using System.Collections.Concurrent;
using System.Diagnostics;
using InputAtlas.Core;
using InputAtlas.Windows;

namespace InputAtlas.App;

public sealed class CapturePersistenceCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(10);
    private readonly RawInputCaptureController _capture;
    private readonly IBucketRepository _repository;
    private readonly IApplicationLog _log;
    // 已结束的桶直到写入成功才移除。当前桶一直由采集器持有。
    private readonly ConcurrentDictionary<long, BucketSnapshot> _completed = new();
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loops;
    private long _lastSavedUnixSeconds;

    public CapturePersistenceCoordinator(
        RawInputCaptureController capture,
        IBucketRepository repository,
        IApplicationLog log)
    {
        _capture = capture;
        _repository = repository;
        _log = log;
        _capture.BucketCompleted += OnBucketCompleted;
    }

    public DateTimeOffset? LastSavedUtc
    {
        get
        {
            var saved = Interlocked.Read(ref _lastSavedUnixSeconds);
            return saved == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(saved);
        }
    }

    public event Action? SnapshotChanged;

    // 覆盖秒数和 SQLite 同步使用独立时钟，慢查询不影响覆盖率。
    public void Start()
    {
        if (_loops is not null)
        {
            return;
        }

        _loops = Task.WhenAll(Task.Run(CoverageLoopAsync), Task.Run(SyncLoopAsync));
        _log.Information("statistics_sync_started", $"interval_seconds={SyncInterval.TotalSeconds}");
    }

    private async Task CoverageLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
        {
            _capture.AddCoverageSecond();
        }
    }

    private async Task SyncLoopAsync()
    {
        using var timer = new PeriodicTimer(SyncInterval);
        while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
        {
            try
            {
                await SaveNowAsync(_shutdown.Token).ConfigureAwait(false);
                SnapshotChanged?.Invoke();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.LogError("checkpoint_failed", $"pending_buckets={_completed.Count}", exception);
            }
        }
    }

    // SQLite 的异步 API 也可能同步执行，因此显式在工作线程写入。
    public Task SaveNowAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await _storageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var active = _capture.GetCurrentSnapshot();
            var buckets = _completed.ToArray();
            // 取快照后如果恰好切桶，同一桶只写封桶后的最终值。
            if (!buckets.Any(pair => pair.Key == active.BucketStartUtc) &&
                (active.CoverageSeconds > 0 || active.Counts.Count > 0))
            {
                await _repository.UpsertFiveMinuteAsync(active, cancellationToken).ConfigureAwait(false);
            }

            foreach (var pair in buckets)
            {
                await _repository.UpsertFiveMinuteAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                _completed.TryRemove(pair);
            }

            Interlocked.Exchange(ref _lastSavedUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _log.Information("checkpoint_completed",
                $"closed_buckets={buckets.Length} duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }
        finally
        {
            _storageGate.Release();
        }
    }, cancellationToken);

    // 输入回调只保留最终快照，不等待数据库。
    private void OnBucketCompleted(BucketSnapshot snapshot) => _completed[snapshot.BucketStartUtc] = snapshot;

    // 同步期间读取稳定的数据库视图，未落盘的桶按起始时间覆盖旧检查点。
    public async Task<IReadOnlyList<BucketSnapshot>> ReadRangeAsync(
        long start, long end, CancellationToken cancellationToken)
    {
        await _storageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _repository.ReadRangeAsync(start, end, cancellationToken).ConfigureAwait(false);
            var result = stored.ToDictionary(static item => item.BucketStartUtc);
            var active = _capture.GetCurrentSnapshot();
            foreach (var bucket in _completed.Values.Prepend(active))
            {
                if (bucket.BucketStartUtc < end && bucket.BucketStartUtc + 300 > start &&
                    (bucket.CoverageSeconds > 0 || bucket.Counts.Count > 0))
                {
                    result[bucket.BucketStartUtc] = bucket;
                }
            }

            return result.Values.OrderBy(static item => item.BucketStartUtc).ToArray();
        }
        finally
        {
            _storageGate.Release();
        }
    }

    // 采集停止或暂停时，从数据库恢复本时间桶，避免旧进程的计数被覆盖。
    public async Task ReloadCurrentBucketAsync(CancellationToken cancellationToken = default)
    {
        var start = TimeBuckets.AlignFiveMinutes(_capture.CurrentUnixSeconds);
        var stored = await _repository.ReadRangeAsync(start, start + 300, cancellationToken).ConfigureAwait(false);
        _capture.RestoreBucket(start, stored.FirstOrDefault(item => item.BucketStartUtc == start));
        _log.Information("capture_bucket_loaded", $"bucket_start={start} stored={stored.Count}");
    }

    // 历史删除和备份恢复与后台写盘互斥，恢复后的桶成为下一次写入的起点。
    public Task ChangeStoredDataAsync(Func<Task> change) => Task.Run(async () =>
    {
        await _storageGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await change().ConfigureAwait(false);
            await ReloadCurrentBucketAsync().ConfigureAwait(false);
        }
        finally
        {
            _storageGate.Release();
        }
    });

    // 先结束定时器，再完成最后一次写盘；调用方已停止输入采集。
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loops is not null)
            {
                await _loops.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }

        try
        {
            await SaveNowAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogError("shutdown_checkpoint_failed", "退出前保存统计失败", exception);
            throw;
        }
        finally
        {
            _capture.BucketCompleted -= OnBucketCompleted;
            _storageGate.Dispose();
            _shutdown.Dispose();
        }
    }
}
