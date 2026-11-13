using System.Threading.Channels;

namespace SwSearch.App;

internal sealed class SettingsIntegrationOperationAdapter : IDisposable
{
    private sealed record WorkItem(
        Func<CancellationToken, Task<object?>> Operation,
        CancellationToken CancellationToken,
        TaskCompletionSource<object?> Completion);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly Task _worker;
    private int _activeCount;
    private bool _disposed;

    internal SettingsIntegrationOperationAdapter()
    {
        _worker = Task.Run(DrainAsync);
    }

    internal int ActiveCount => Volatile.Read(ref _activeCount);
    internal Task Completion => _worker;

    internal Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new WorkItem(
            async token => await operation(token).ConfigureAwait(false),
            cancellationToken,
            completion);
        if (!_queue.Writer.TryWrite(work))
        {
            return Task.FromException<T>(new InvalidOperationException("集成设置后台队列已满。"));
        }
        return AwaitTypedAsync<T>(completion.Task);
    }

    private static async Task<T> AwaitTypedAsync<T>(Task<object?> completion) =>
        (T)(await completion.ConfigureAwait(false))!;

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.CancellationToken);
                    continue;
                }

                using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token,
                    work.CancellationToken);
                Interlocked.Increment(ref _activeCount);
                try
                {
                    var result = await work.Operation(operationCancellation.Token).ConfigureAwait(false);
                    operationCancellation.Token.ThrowIfCancellationRequested();
                    work.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    work.Completion.TrySetCanceled(operationCancellation.Token);
                }
                catch (Exception exception)
                {
                    work.Completion.TrySetException(exception);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeCount);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            while (_queue.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetCanceled(new CancellationToken(canceled: true));
            }
        }
    }

    internal async Task<bool> StopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Dispose();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await _worker.WaitAsync(deadline.Token).ConfigureAwait(false);
            return ActiveCount == 0;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
    }
}
