using SwSearch.Contracts;

namespace SwSearch.Indexer;

internal sealed record SearchPromotionQueueSnapshot(
    int PendingCount,
    long AcceptedCount,
    long DeduplicatedCount,
    long DroppedCount,
    long CompletedCount,
    bool ConsumerCompleted);

/// <summary>
/// Best-effort promotion of cached search matches into the metadata queue.
/// Search producers never wait for storage: one bounded, deduplicating consumer
/// owns all writes and is cancelled with the indexing runtime.
/// </summary>
internal sealed class SearchCandidatePromotionQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<PromotionRequest> _pending = new();
    private readonly HashSet<string> _knownKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<SearchQuery, CancellationToken, ValueTask<int>> _promoteAsync;
    private readonly Func<Exception, CancellationToken, ValueTask> _reportFailureAsync;
    private readonly int _capacity;
    private readonly Task _consumer;
    private long _accepted;
    private long _deduplicated;
    private long _dropped;
    private long _completed;
    private int _disposed;

    internal SearchCandidatePromotionQueue(
        int capacity,
        Func<SearchQuery, CancellationToken, ValueTask<int>> promoteAsync,
        Func<Exception, CancellationToken, ValueTask>? reportFailureAsync = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _promoteAsync = promoteAsync ?? throw new ArgumentNullException(nameof(promoteAsync));
        _reportFailureAsync = reportFailureAsync ?? ((_, _) => ValueTask.CompletedTask);
        _consumer = Task.Run(ConsumeAsync);
    }

    internal bool TryEnqueue(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(query.Text))
        {
            return false;
        }

        var request = PromotionRequest.Create(query);
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return false;
            }

            if (!_knownKeys.Add(request.Key))
            {
                Interlocked.Increment(ref _deduplicated);
                return true;
            }

            if (_pending.Count >= _capacity)
            {
                _knownKeys.Remove(request.Key);
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _pending.Enqueue(request);
            Interlocked.Increment(ref _accepted);
        }

        _available.Release();
        return true;
    }

    internal SearchPromotionQueueSnapshot Snapshot
    {
        get
        {
            int pending;
            lock (_gate)
            {
                pending = _pending.Count;
            }

            return new SearchPromotionQueueSnapshot(
                pending,
                Interlocked.Read(ref _accepted),
                Interlocked.Read(ref _deduplicated),
                Interlocked.Read(ref _dropped),
                Interlocked.Read(ref _completed),
                _consumer.IsCompleted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _available.Release();
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        lock (_gate)
        {
            _pending.Clear();
            _knownKeys.Clear();
        }

        _available.Dispose();
        _lifetime.Dispose();
    }

    private async Task ConsumeAsync()
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            while (true)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (TryTake(out var request))
                {
                    try
                    {
                        // Scheduling mutations are intentionally not search-result
                        // mutations and therefore never publish a search revision.
                        await _promoteAsync(request.Query, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            await _reportFailureAsync(exception, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            // Diagnostics must never terminate the sole consumer.
                        }
                    }
                    finally
                    {
                        Complete(request.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool TryTake(out PromotionRequest request)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                request = default!;
                return false;
            }

            request = _pending.Dequeue();
            return true;
        }
    }

    private void Complete(string key)
    {
        lock (_gate)
        {
            _knownKeys.Remove(key);
        }

        Interlocked.Increment(ref _completed);
    }

    private sealed record PromotionRequest(string Key, SearchQuery Query)
    {
        internal static PromotionRequest Create(SearchQuery query)
        {
            var scope = query.EffectiveScope;
            var kinds = query.Kinds?
                .Distinct()
                .OrderBy(static kind => kind)
                .ToArray();
            var copy = query with
            {
                Kinds = kinds,
                Scope = scope,
            };
            var kindKey = kinds is null ? "*" : string.Join(',', kinds.Select(static kind => (int)kind));
            var key = string.Join(
                "\u001f",
                query.Text.Trim().ToUpperInvariant(),
                ((int)query.Mode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                query.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kindKey,
                ((int)scope.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
                scope.RootPath?.ToUpperInvariant() ?? string.Empty,
                scope.Recursive ? "1" : "0");
            return new PromotionRequest(key, copy);
        }
    }
}
