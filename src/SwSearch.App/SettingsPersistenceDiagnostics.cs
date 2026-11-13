using System.IO;
using System.Text;
using System.Threading.Channels;

namespace SwSearch.App;

internal interface ISettingsPersistenceDiagnosticSink : IDisposable
{
    bool TryWrite(string code, string exceptionType);
    Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

internal sealed class FileSettingsPersistenceDiagnosticSink : ISettingsPersistenceDiagnosticSink
{
    private readonly string _logPath;
    private readonly Channel<string> _queue;
    private readonly Task _worker;
    private int _stopping;
    private int _failureCount;
    private int _droppedCount;

    internal FileSettingsPersistenceDiagnosticSink(string? logPath = null)
    {
        _logPath = Path.GetFullPath(
            logPath ?? Path.Combine(SwSearchDataPaths.DataDirectory, "Logs", "app-settings.log"));
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(DrainAsync);
    }

    public bool TryWrite(string code, string exceptionType)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Diagnostic code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(exceptionType)) throw new ArgumentException("Exception type is required.", nameof(exceptionType));
        if (Volatile.Read(ref _stopping) != 0) return false;
        var accepted = _queue.Writer.TryWrite($"{DateTime.UtcNow:O}|{code}|{exceptionType}{Environment.NewLine}");
        if (!accepted) Interlocked.Increment(ref _droppedCount);
        return accepted;
    }

    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (Interlocked.Exchange(ref _stopping, 1) == 0) _queue.Writer.TryComplete();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await _worker.WaitAsync(deadline.Token).ConfigureAwait(false);
            return Volatile.Read(ref _failureCount) == 0 && Volatile.Read(ref _droppedCount) == 0;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var directory = Path.GetDirectoryName(_logPath)
                    ?? throw new InvalidOperationException("Diagnostic directory is invalid.");
                Directory.CreateDirectory(directory);
                var bytes = Encoding.UTF8.GetBytes(line);
                await using var stream = new FileStream(
                    _logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _failureCount);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) _queue.Writer.TryComplete();
    }
}
