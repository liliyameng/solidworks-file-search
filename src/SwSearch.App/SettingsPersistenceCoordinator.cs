namespace SwSearch.App;

internal readonly record struct SettingsPersistenceResult(
    bool Saved,
    bool Superseded,
    Exception? Error);

internal readonly record struct SettingsPersistenceShutdownResult(
    SettingsPersistenceResult Persistence,
    bool WorkerStopped);

internal sealed class SettingsPersistenceCoordinator : IDisposable
{
    private sealed class WorkItem(
        AppSettings settings,
        CancellationTokenSource cancellation) : IDisposable
    {
        private int _invalidated;
        private CancellationTokenRegistration _registration;

        internal AppSettings Settings { get; } = settings;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal CancellationToken Token => Cancellation.Token;
        internal long Generation { get; set; }
        internal bool Invalidated => Volatile.Read(ref _invalidated) != 0;
        internal TaskCompletionSource<SettingsPersistenceResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void RegisterCancellation(SettingsPersistenceCoordinator owner) =>
            _registration = Token.Register(
                static state =>
                {
                    var tuple = ((SettingsPersistenceCoordinator Owner, WorkItem Work))state!;
                    tuple.Owner.Invalidate(tuple.Work);
                },
                (owner, this));

        internal void MarkInvalid()
        {
            if (Interlocked.Exchange(ref _invalidated, 1) != 0) return;
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _registration.Dispose();
            Cancellation.Dispose();
        }
    }

    private readonly object _sync = new();
    private readonly Func<AppSettings, CancellationToken, ValueTask<IPreparedAppSettingsSave>> _prepareAsync;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private WorkItem? _pending;
    private Task _completion = Task.CompletedTask;
    private bool _workerRunning;
    private bool _disposed;
    private long _generation;
    private int _activeCount;
    private WorkItem? _active;

    internal SettingsPersistenceCoordinator(
        Func<AppSettings, CancellationToken, ValueTask<IPreparedAppSettingsSave>> prepareAsync,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(prepareAsync);
        _prepareAsync = prepareAsync;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(6);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    internal Task Completion
    {
        get { lock (_sync) return _completion; }
    }

    internal int ActiveCount => Volatile.Read(ref _activeCount);

    internal void Queue(AppSettings settings) => _ = QueueAsync(settings, CancellationToken.None);

    internal Task<SettingsPersistenceResult> QueueAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new SettingsPersistenceResult(false, true, null));
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        linkedCancellation.CancelAfter(_operationTimeout);
        var work = new WorkItem(settings, linkedCancellation);
        work.RegisterCancellation(this);
        WorkItem? replaced;
        WorkItem? activeToInvalidate = null;
        var rejected = false;
        lock (_sync)
        {
            if (_disposed || work.Invalidated)
            {
                rejected = true;
                replaced = null;
            }
            else
            {
                work.Generation = ++_generation;
                replaced = _pending;
                replaced?.MarkInvalid();
                activeToInvalidate = _active;
                activeToInvalidate?.MarkInvalid();
                _pending = work;
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    _completion = Task.Run(DrainAsync);
                }
            }
        }

        if (rejected)
        {
            work.Dispose();
            return Task.FromResult(new SettingsPersistenceResult(false, true, null));
        }

        activeToInvalidate?.MarkInvalid();

        if (replaced is not null)
        {
            replaced.Completion.TrySetResult(new SettingsPersistenceResult(false, true, null));
            replaced.Dispose();
        }
        return work.Completion.Task;
    }

    internal async Task<SettingsPersistenceResult> FlushLatestAsync(
        AppSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var operation = QueueAsync(settings, deadline.Token);
        try
        {
            return await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new SettingsPersistenceResult(
                false,
                true,
                new TimeoutException("设置保存未在关闭期限内完成。"));
        }
    }

    internal async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var completion = BeginStop();
        var cancellation = _lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(completion, cancellation).WaitAsync(deadline.Token).ConfigureAwait(false);
            return ActiveCount == 0;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    internal async Task<SettingsPersistenceShutdownResult> FlushLatestAndStopAsync(
        AppSettings settings,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken = default)
    {
        if (totalTimeout <= TimeSpan.FromMilliseconds(500))
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        using var hardDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardDeadline.CancelAfter(totalTimeout);
        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(hardDeadline.Token);
        operationDeadline.CancelAfter(totalTimeout - TimeSpan.FromMilliseconds(500));
        SettingsPersistenceResult result;
        try
        {
            result = await QueueAsync(settings, operationDeadline.Token)
                .WaitAsync(operationDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationDeadline.IsCancellationRequested)
        {
            result = new SettingsPersistenceResult(
                false,
                true,
                new TimeoutException("设置保存未在关闭期限内完成。"));
        }

        var completion = BeginStop();
        var cancellation = _lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(completion, cancellation).WaitAsync(hardDeadline.Token).ConfigureAwait(false);
            return new SettingsPersistenceShutdownResult(result, ActiveCount == 0);
        }
        catch (OperationCanceledException) when (hardDeadline.IsCancellationRequested)
        {
            return new SettingsPersistenceShutdownResult(result, false);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            WorkItem? next;
            lock (_sync)
            {
                next = _pending;
                _pending = null;
                if (next is null)
                {
                    _workerRunning = false;
                    return;
                }
            }

            if (next.Invalidated)
            {
                next.Completion.TrySetResult(new SettingsPersistenceResult(false, true, null));
                next.Dispose();
                continue;
            }

            Exception? error = null;
            var committed = false;
            Interlocked.Increment(ref _activeCount);
            try
            {
                using var prepared = await _prepareAsync(next.Settings, next.Token).ConfigureAwait(false);
                var ownsCommitLease = false;
                lock (_sync)
                {
                    if (IsCurrentLocked(next))
                    {
                        _active = next;
                        ownsCommitLease = true;
                    }
                }
                if (ownsCommitLease)
                {
                    await prepared.CommitAsync(next.Token).ConfigureAwait(false);
                    next.Token.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        committed = IsCurrentLocked(next);
                        if (ReferenceEquals(_active, next)) _active = null;
                    }
                }
            }
            catch (OperationCanceledException) when (next.Invalidated || next.Token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_active, next)) _active = null;
                }
                Interlocked.Decrement(ref _activeCount);
            }

            bool isCurrent;
            lock (_sync) isCurrent = IsCurrentLocked(next);
            next.Completion.TrySetResult(
                isCurrent
                    ? new SettingsPersistenceResult(committed && error is null, !committed && error is null, error)
                    : new SettingsPersistenceResult(false, true, error));
            next.Dispose();
        }
    }

    private bool IsCurrentLocked(WorkItem item) =>
        !_disposed &&
        !item.Invalidated &&
        !item.Token.IsCancellationRequested &&
        item.Generation == _generation;

    private void Invalidate(WorkItem item)
    {
        item.MarkInvalid();
    }

    private Task BeginStop()
    {
        WorkItem? pending;
        WorkItem? active;
        Task completion;
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _generation++;
                pending = _pending;
                pending?.MarkInvalid();
                _pending = null;
                active = _active;
                active?.MarkInvalid();
            }
            else
            {
                pending = null;
                active = _active;
            }
            completion = _completion;
        }
        pending?.Completion.TrySetResult(new SettingsPersistenceResult(false, true, null));
        pending?.Dispose();
        active?.MarkInvalid();
        return completion;
    }

    public void Dispose()
    {
        _ = BeginStop();
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
    }
}
