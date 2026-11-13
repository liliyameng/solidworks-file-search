using System.Windows.Threading;
using SwSearch.Integration;

namespace SwSearch.App;

internal enum SettingsIntegrationTarget
{
    Taskpane,
    FolderMenu,
    Startup,
}

internal sealed class SettingsIntegrationProbeCoordinator : IDisposable
{
    private static readonly SettingsIntegrationTarget[] Targets =
        [SettingsIntegrationTarget.Taskpane, SettingsIntegrationTarget.FolderMenu, SettingsIntegrationTarget.Startup];

    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<SettingsIntegrationTarget, CancellationToken, Task<IntegrationStatus>> _probe;
    private readonly Action<SettingsIntegrationTarget, IntegrationStatus> _apply;
    private readonly Action _completed;
    private readonly TimeSpan _timeout;
    private readonly Action<Exception>? _failure;
    private CancellationTokenSource? _generationCancellation;
    private long _generation;
    private bool _disposed;

    internal SettingsIntegrationProbeCoordinator(
        Dispatcher dispatcher,
        Func<SettingsIntegrationTarget, CancellationToken, Task<IntegrationStatus>> probe,
        Action<SettingsIntegrationTarget, IntegrationStatus> apply,
        Action completed,
        TimeSpan timeout,
        Action<Exception>? failure = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(completed);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _dispatcher = dispatcher;
        _probe = probe;
        _apply = apply;
        _completed = completed;
        _timeout = timeout;
        _failure = failure;
    }

    internal Task Start()
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _generationCancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            _generationCancellation = cancellation;
            generation = ++_generation;
        }
        return RunGenerationAsync(generation, cancellation);
    }

    private async Task RunGenerationAsync(long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(Targets.Select(target => RunProbeAsync(target, generation, cancellation.Token)))
                .ConfigureAwait(false);
            await ApplyOnDispatcherAsync(generation, cancellation.Token, _completed).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_generationCancellation, cancellation))
                {
                    _generationCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task RunProbeAsync(
        SettingsIntegrationTarget target,
        long generation,
        CancellationToken generationToken)
    {
        IntegrationStatus status;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
        timeout.CancelAfter(_timeout);
        Task<IntegrationStatus>? operation = null;
        try
        {
            operation = _probe(target, timeout.Token);
            status = await operation.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            ObserveLateFault(operation);
            return;
        }
        catch (OperationCanceledException)
        {
            ObserveLateFault(operation);
            status = new IntegrationStatus(false, false, "检测超时；暂不可用");
        }
        catch (Exception exception)
        {
            try { _failure?.Invoke(exception); }
            catch { }
            status = new IntegrationStatus(
                false,
                false,
                $"检测失败（{exception.GetType().Name}）；暂不可用");
        }

        await ApplyOnDispatcherAsync(
                generation,
                generationToken,
                () => _apply(target, status))
            .ConfigureAwait(false);
    }

    private async Task ApplyOnDispatcherAsync(long generation, CancellationToken token, Action action)
    {
        if (!IsCurrent(generation, token)) return;
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (!IsCurrent(generation, token)) return;
                    try { action(); }
                    catch { }
                },
                DispatcherPriority.Background,
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private bool IsCurrent(long generation, CancellationToken token)
    {
        lock (_sync)
        {
            return !_disposed && !token.IsCancellationRequested && generation == _generation;
        }
    }

    private static void ObserveLateFault(Task? operation)
    {
        if (operation is null) return;
        _ = operation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _generationCancellation?.Cancel();
            _generationCancellation = null;
        }
    }
}
