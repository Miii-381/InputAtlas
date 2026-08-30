using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace InputAtlas.Windows;

public sealed class SingleInstanceCoordinator : IAsyncDisposable, IDisposable
{
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var identityText = $"{identity.User?.Value ?? Environment.UserName}:{Environment.ProcessId / Math.Max(Environment.ProcessId, 1)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityText)))[..16];
        _pipeName = $"InputAtlas.{hash}";
        _mutex = new Mutex(false, $"Local\\InputAtlas.{hash}");
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public void StartServer(Func<string, Task> commandHandler)
    {
        ArgumentNullException.ThrowIfNull(commandHandler);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("仅主实例可以启动 IPC 服务。");
        }

        _serverTask ??= Task.Run(() => ServerLoopAsync(commandHandler, _shutdown.Token));
    }

    public async ValueTask<bool> SendCommandAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 256, true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(command.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    private async Task ServerLoopAsync(Func<string, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 256, true);
            var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(command))
            {
                await handler(command).ConfigureAwait(false);
            }
        }
    }

    private void ReleaseResources()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }
}

