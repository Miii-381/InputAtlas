using System.Text;
using System.Threading.Channels;
using InputAtlas.Core;

namespace InputAtlas.Storage;

public sealed class AsyncRollingLog : IApplicationLog
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 5;
    private readonly string _directory;
    private readonly Channel<Entry> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    private long _dropped;

    public AsyncRollingLog(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public void Debug(string eventName, string message) => Enqueue("DBG", eventName, message, null);

    public void Information(string eventName, string message) => Enqueue("INF", eventName, message, null);

    public void Warning(string eventName, string message) => Enqueue("WRN", eventName, message, null);

    public void LogError(string eventName, string message, Exception? exception = null) =>
        Enqueue("ERR", eventName, message, exception?.GetType().Name);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void Enqueue(string level, string eventName, string message, string? exceptionType)
    {
        var safeEvent = Sanitize(eventName);
        var safeMessage = Sanitize(message);
        if (!_channel.Writer.TryWrite(new Entry(DateTimeOffset.UtcNow, level, safeEvent, safeMessage, exceptionType)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            RotateIfNeeded();
            var path = Path.Combine(_directory, "inputatlas.log");
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            var prefix = dropped > 0
                ? $"{DateTimeOffset.UtcNow:O} WRN log_queue dropped={dropped}{Environment.NewLine}"
                : string.Empty;
            var line = $"{entry.Time:O} {entry.Level} {entry.EventName} {entry.Message}";
            if (entry.ExceptionType is not null)
            {
                line += $" exception={entry.ExceptionType}";
            }

            await File.AppendAllTextAsync(
                path,
                prefix + line + Environment.NewLine,
                new UTF8Encoding(false),
                _shutdown.Token).ConfigureAwait(false);
        }
    }

    private void RotateIfNeeded()
    {
        var current = Path.Combine(_directory, "inputatlas.log");
        if (!File.Exists(current) || new FileInfo(current).Length < MaxFileBytes)
        {
            return;
        }

        var oldest = Path.Combine(_directory, $"inputatlas.{MaxFiles - 1}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = MaxFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"inputatlas.{index}.log");
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(_directory, $"inputatlas.{index + 1}.log"), true);
            }
        }

        File.Move(current, Path.Combine(_directory, "inputatlas.1.log"), true);
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private sealed record Entry(
        DateTimeOffset Time,
        string Level,
        string EventName,
        string Message,
        string? ExceptionType);
}
