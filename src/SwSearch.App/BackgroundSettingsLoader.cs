using System.Windows.Threading;

namespace SwSearch.App;

/// <summary>
/// Loads the small local settings document away from the UI thread.  Cancellation and
/// generation checks bound the visible lifecycle even if an operating-system file call
/// is slow; a late worker is observed but can never apply stale settings.
/// </summary>
internal sealed class BackgroundSettingsLoader : IDisposable
{
    private readonly Func<AppSettings> _load;
    private readonly Dispatcher _dispatcher;
    private readonly Action<AppSettings> _apply;
    private readonly Func<bool> _canApply;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCancellation;
    private Task _completion = Task.CompletedTask;
    private int _generation;
    private bool _disposed;

    internal BackgroundSettingsLoader(
        Func<AppSettings> load,
        Dispatcher dispatcher,
        Action<AppSettings> apply,
        Func<bool> canApply,
        TimeSpan? timeout = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _canApply = canApply ?? throw new ArgumentNullException(nameof(canApply));
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _completion;
            }
        }
    }

    internal void Start()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource cancellation;
        Task<AppSettings> worker;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _activeCancellation;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _activeCancellation = cancellation;
            generation = ++_generation;

            // The delegate is the explicit asynchronous boundary inspected by the
            // candidate IL gate.  No settings store method runs before Task.Run returns.
            worker = Task.Run(_load, cancellation.Token);
        }
        CancelBestEffort(previous);

        var completion = CompleteAsync(worker, cancellation, generation);
        lock (_gate)
        {
            if (!_disposed && generation == _generation)
            {
                _completion = completion;
            }
        }

        _ = worker.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CompleteAsync(
        Task<AppSettings> worker,
        CancellationTokenSource cancellation,
        int generation)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            bounded.CancelAfter(_timeout);
            var settings = await worker.WaitAsync(bounded.Token).ConfigureAwait(false);
            if (bounded.IsCancellationRequested || !CanApply(generation))
            {
                return;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (CanApply(generation))
                    {
                        _apply(settings);
                    }
                },
                DispatcherPriority.Background,
                bounded.Token).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Settings are optional.  Defaults remain active on every load, parse,
            // quarantine, timeout or dispatcher failure.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, cancellation))
                {
                    _activeCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private bool CanApply(int generation)
    {
        lock (_gate)
        {
            return !_disposed && generation == _generation && _canApply();
        }
    }

    private static void CancelBestEffort(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion owns disposal of a generation source.  A concurrent restart
            // or loader disposal may observe it immediately after that disposal.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _generation++;
            active = _activeCancellation;
        }
        CancelBestEffort(active);
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
