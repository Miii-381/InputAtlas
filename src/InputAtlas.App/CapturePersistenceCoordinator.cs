using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using InputAtlas.Core;
using InputAtlas.Storage;
using InputAtlas.Windows;

namespace InputAtlas.App;

public sealed class CapturePersistenceCoordinator : IAsyncDisposable
{
    private readonly RawInputCaptureController _capture;
    private readonly IBucketRepository _repository;
    private readonly IApplicationLog _log;
    private readonly Channel<BucketSnapshot> _snapshots;
    private readonly ConcurrentDictionary<long, BucketSnapshot> _faultCache = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _timerTask;
    private Task? _writerTask;
    private int _secondsSinceCheckpoint;
    private DateTimeOffset? _lastSavedUtc;

    public CapturePersistenceCoordinator(
        RawInputCaptureController capture,
        IBucketRepository repository,
        IApplicationLog log)
    {
        _capture = capture;
        _repository = repository;
        _log = log;
        _snapshots = Channel.CreateBounded<BucketSnapshot>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _capture.BucketCompleted += OnBucketCompleted;
    }

    public DateTimeOffset? LastSavedUtc => _lastSavedUtc;

    public event Action? SnapshotChanged;

    public void Start()
    {
        _writerTask ??= Task.Run(WriterLoopAsync);
        _timerTask ??= Task.Run(TimerLoopAsync);
    }

    public async ValueTask SaveNowAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _capture.GetCurrentSnapshot();
        await _snapshots.Writer.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var finalSnapshot = _capture.GetCurrentSnapshot();
        await _repository.UpsertFiveMinuteAsync(finalSnapshot, cancellationToken).ConfigureAwait(false);
        _lastSavedUtc = DateTimeOffset.UtcNow;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _snapshots.Writer.TryComplete();
        if (_timerTask is not null)
        {
            await IgnoreCancellationAsync(_timerTask).ConfigureAwait(false);
        }

        if (_writerTask is not null)
        {
            await IgnoreCancellationAsync(_writerTask).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _capture.BucketCompleted -= OnBucketCompleted;
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task TimerLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
        {
            _capture.AddCoverageSecond();
            SnapshotChanged?.Invoke();
            if (Interlocked.Increment(ref _secondsSinceCheckpoint) >= 30)
            {
                Interlocked.Exchange(ref _secondsSinceCheckpoint, 0);
                await SaveNowAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var snapshot in _snapshots.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _faultCache[snapshot.BucketStartUtc] = snapshot;
            foreach (var pending in _faultCache.OrderBy(static item => item.Key).ToArray())
            {
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    await _repository.UpsertFiveMinuteAsync(pending.Value, _shutdown.Token).ConfigureAwait(false);
                    _faultCache.TryRemove(pending.Key, out _);
                    _lastSavedUtc = DateTimeOffset.UtcNow;
                    _log.Information(
                        "checkpoint_completed",
                        $"buckets=1 duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} cache={_faultCache.Count}");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _log.LogError("checkpoint_failed", $"缓存桶数={_faultCache.Count}", exception);
                    break;
                }
            }
        }
    }

    private void OnBucketCompleted(BucketSnapshot snapshot)
    {
        if (!_snapshots.Writer.TryWrite(snapshot))
        {
            _faultCache[snapshot.BucketStartUtc] = snapshot;
            _log.Warning("checkpoint_queue_full", $"持久化队列已满 cache={_faultCache.Count}");
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
