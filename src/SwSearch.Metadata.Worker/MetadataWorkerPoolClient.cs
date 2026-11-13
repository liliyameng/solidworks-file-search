using System.Collections.Concurrent;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

public sealed record MetadataWorkerPoolOptions(
    MetadataWorkerClientOptions Worker,
    int WorkerCount = 2);

internal sealed record MetadataWorkerPoolMember(
    IMetadataExtractionClient Client,
    IMetadataGenerationSupervisor? Supervisor,
    IAsyncDisposable? Lifetime);

/// <summary>
/// A low-resource pool of independently supervised worker generations.
/// Each member owns its own pipe, process, job and generation nonce. Routing
/// never merges generation identity: retirement is sent only to the member
/// that returned the matching nonce.
/// </summary>
public sealed class MetadataWorkerPoolClient :
    IMetadataExtractionClient,
    IMetadataGenerationSupervisor,
    IBrokerHostedMetadataExtractionClient,
    IAsyncDisposable
{
    private const int MinimumWorkerCount = 1;
    private const int MaximumWorkerCount = 3;
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(7);
    private readonly PoolSlot[] _slots;
    private readonly ConcurrentDictionary<Guid, int> _generationOwners = new();
    private readonly object _routingGate = new();
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly TimeSpan _disposeTimeout;
    private int _nextSlot = -1;
    private int _disposed;
    private bool _disposeCompleted;

    public MetadataWorkerPoolClient(MetadataWorkerPoolOptions options)
        : this(CreateMembers(options), DefaultDisposeTimeout)
    {
    }

    internal MetadataWorkerPoolClient(
        IReadOnlyList<MetadataWorkerPoolMember> members,
        TimeSpan? disposeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < MinimumWorkerCount or > MaximumWorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(members));
        }

        _slots = members
            .Select(static (member, index) =>
                new PoolSlot(
                    index,
                    member.Client ?? throw new ArgumentException("Pool member client is required."),
                    member.Supervisor,
                    member.Lifetime))
            .ToArray();
        _disposeTimeout = disposeTimeout ?? DefaultDisposeTimeout;
        if (_disposeTimeout <= TimeSpan.Zero || _disposeTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(disposeTimeout));
        }
    }

    public int WorkerCount => _slots.Length;

    internal IReadOnlyList<int> ActiveRequestsForTesting =>
        _slots.Select(static slot => slot.ActiveRequests).ToArray();

    public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var probes = _slots
            .Select(slot => ProbeMemberAsync(slot, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var healthy = results.Count(static capability =>
            capability.State == CapabilityState.Available);
        var degraded = results.Count(static capability =>
            capability.State == CapabilityState.Degraded);
        var unavailable = results.Count(static capability =>
            capability.State == CapabilityState.Unavailable);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workerCount"] = _slots.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["healthyWorkers"] = healthy.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["parallelism"] = Math.Max(1, healthy).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };

        if (healthy == _slots.Length)
        {
            return new RuntimeCapability(
                CapabilityState.Available,
                "METADATA_POOL_READY",
                $"属性处理池已就绪（{healthy}/{_slots.Length}）。",
                details);
        }

        if (healthy > 0)
        {
            return new RuntimeCapability(
                CapabilityState.Degraded,
                "METADATA_POOL_DEGRADED",
                $"属性处理池正在自动恢复（{healthy}/{_slots.Length} 可用）。",
                details);
        }

        if (degraded > 0)
        {
            details["degradedWorkers"] = degraded.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return new RuntimeCapability(
                CapabilityState.Degraded,
                "METADATA_POOL_RECOVERING",
                $"属性处理池正在自动恢复（{degraded}/{_slots.Length} 可连接）。",
                details);
        }

        var first = results.FirstOrDefault(static capability =>
            capability.State == CapabilityState.Unavailable);
        details["unavailableWorkers"] = unavailable.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return new RuntimeCapability(
            CapabilityState.Unavailable,
            "METADATA_WORKER_UNAVAILABLE",
            first?.ChineseMessage ?? "属性处理 Worker 暂不可用，现有缓存仍可搜索。",
            details);
    }

    public async ValueTask<MetadataResponse> ExtractAsync(
        MetadataRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        PoolSlot slot;
        lock (_routingGate)
        {
            slot = SelectSlot();
            slot.Reserve();
        }
        var turnAcquired = false;
        try
        {
            await slot.WaitForTurnAsync(cancellationToken).ConfigureAwait(false);
            turnAcquired = true;
            var response = await slot.Client.ExtractAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.WorkerGenerationNonce != Guid.Empty)
            {
                _generationOwners[response.WorkerGenerationNonce] = slot.Index;
            }

            return response;
        }
        finally
        {
            slot.Exit(turnAcquired);
        }
    }

    public async ValueTask RetireGenerationAsync(
        Guid workerGenerationNonce,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (workerGenerationNonce == Guid.Empty)
        {
            return;
        }

        ThrowIfDisposed();
        if (_generationOwners.TryGetValue(workerGenerationNonce, out var ownerIndex))
        {
            var owner = _slots[ownerIndex];
            if (owner.Supervisor is not null)
            {
                await owner.Supervisor.RetireGenerationAsync(
                        workerGenerationNonce,
                        reason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        // Unknown nonces can occur when a timeout response races pool startup.
        // Each production member already performs exact-nonce validation and
        // no-ops for a newer generation, so this cannot retire the wrong one.
        foreach (var slot in _slots)
        {
            if (slot.Supervisor is not null)
            {
                await slot.Supervisor.RetireGenerationAsync(
                        workerGenerationNonce,
                        reason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        if (!await _disposeGate.WaitAsync(_disposeTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out waiting for metadata worker pool cleanup ownership.");
        }
        try
        {
            if (_disposeCompleted)
            {
                return;
            }

            // Start cleanup for every owned member before waiting. A slow
            // generation can never prevent the other owned generations from
            // exercising their existing bounded termination capability.
            var cleanupTasks = _slots
                .Select(static slot => slot.BeginDispose())
                .ToArray();
            try
            {
                await Task.WhenAll(cleanupTasks)
                    .WaitAsync(_disposeTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Incomplete member tasks remain strongly tracked by their
                // slots. A subsequent DisposeAsync retries the bounded wait
                // (and retries a member whose previous disposal faulted).
                throw new TimeoutException(
                    $"Metadata worker pool cleanup exceeded {_disposeTimeout.TotalSeconds:F1}s; cleanup remains tracked and retryable.");
            }

            foreach (var slot in _slots)
            {
                slot.CompleteDispose();
            }
            _disposeCompleted = true;
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private static IReadOnlyList<MetadataWorkerPoolMember> CreateMembers(
        MetadataWorkerPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Worker);
        if (options.WorkerCount is < MinimumWorkerCount or > MaximumWorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options.WorkerCount));
        }

        var basePipeName = options.Worker.PipeName ?? MetadataWorkerDefaults.PipeName;
        var members = new List<MetadataWorkerPoolMember>(options.WorkerCount);
        for (var index = 0; index < options.WorkerCount; index++)
        {
            var worker = new MetadataWorkerClient(options.Worker with
            {
                PipeName = $"{basePipeName}.pool.{index + 1}",
            });
            members.Add(new MetadataWorkerPoolMember(worker, worker, worker));
        }

        return members;
    }

    private static async Task<RuntimeCapability> ProbeMemberAsync(
        PoolSlot slot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await slot.Client.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RuntimeCapability(
                CapabilityState.Unavailable,
                "METADATA_WORKER_UNAVAILABLE",
                "属性处理 Worker 暂不可用，现有缓存仍可搜索。",
                new Dictionary<string, string>
                {
                    ["exception"] = exception.GetType().Name,
                    ["slot"] = slot.Index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }

    private PoolSlot SelectSlot()
    {
        var start = (int)((uint)Interlocked.Increment(ref _nextSlot) % (uint)_slots.Length);
        var selected = _slots[start];
        var minimum = selected.ActiveRequests;
        for (var offset = 1; offset < _slots.Length; offset++)
        {
            var candidate = _slots[(start + offset) % _slots.Length];
            var active = candidate.ActiveRequests;
            if (active < minimum)
            {
                selected = candidate;
                minimum = active;
            }
        }

        return selected;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class PoolSlot(
        int index,
        IMetadataExtractionClient client,
        IMetadataGenerationSupervisor? supervisor,
        IAsyncDisposable? lifetime)
    {
        private int _activeRequests;
        private readonly SemaphoreSlim _singleFlight = new(1, 1);
        private readonly object _disposeStateGate = new();
        private Task? _disposeTask;
        private bool _disposeCompleted;
        private int _slotDisposed;

        internal int Index { get; } = index;

        internal IMetadataExtractionClient Client { get; } = client;

        internal IMetadataGenerationSupervisor? Supervisor { get; } = supervisor;

        internal IAsyncDisposable? Lifetime { get; } = lifetime;

        internal int ActiveRequests => Math.Max(0, Volatile.Read(ref _activeRequests));

        internal void Reserve() => Interlocked.Increment(ref _activeRequests);

        internal ValueTask WaitForTurnAsync(CancellationToken cancellationToken) =>
            new(_singleFlight.WaitAsync(cancellationToken));

        internal void Exit(bool turnAcquired)
        {
            if (turnAcquired)
            {
                _singleFlight.Release();
            }
            Interlocked.Decrement(ref _activeRequests);
        }

        internal Task BeginDispose()
        {
            lock (_disposeStateGate)
            {
                if (_disposeCompleted || Lifetime is null)
                {
                    _disposeCompleted = true;
                    return Task.CompletedTask;
                }

                if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
                {
                    _disposeTask = DisposeLifetimeAsync();
                }

                return _disposeTask;
            }
        }

        internal void CompleteDispose()
        {
            lock (_disposeStateGate)
            {
                _disposeCompleted = true;
            }
            if (Interlocked.Exchange(ref _slotDisposed, 1) == 0)
            {
                _singleFlight.Dispose();
            }
        }

        private async Task DisposeLifetimeAsync()
        {
            await Lifetime!.DisposeAsync().ConfigureAwait(false);
        }
    }
}
