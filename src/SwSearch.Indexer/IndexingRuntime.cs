using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Threading.Channels;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

/// <summary>
/// Resolves the progress denominator for the file discovery bar.  A provider
/// published estimate always wins; without one the last committed store
/// snapshot is the fixed baseline.  The baseline is deliberately not adjusted
/// when a live scan climbs past it, so the visible percentage can rise and cap
/// at 99.99 but can never regress or fake a stable 100 by keeping numerator
/// and denominator equal.
/// </summary>
internal static class FileDiscoveryBaselineEstimate
{
    public static long? ResolveEstimatedTotal(
        long indexedFileCount,
        long? providerEstimate,
        long presentFileCount,
        RuntimeServiceState discoveryState)
    {
        if (providerEstimate is > 0)
        {
            return providerEstimate;
        }

        var baseline = ResolveBaseline(presentFileCount, discoveryState);
        return baseline is > 0
            ? Math.Max(baseline.Value, Math.Max(0, indexedFileCount))
            : null;
    }

    public static long? ResolveBaseline(
        long presentFileCount,
        RuntimeServiceState discoveryState) =>
        presentFileCount > 0 || discoveryState == RuntimeServiceState.Idle
            ? Math.Max(0, presentFileCount)
            : null;
}

public sealed record IndexingRuntimeOptions(
    TimeSpan DiscoveryRefreshInterval,
    TimeSpan IdleDelay,
    int MetadataBatchSize,
    double MaximumFilesPerSecond = 12,
    long MaximumReadBytesPerSecond = 32 * 1024 * 1024,
    long MaximumWriteBytesPerSecond = 8 * 1024 * 1024,
    int SoftFileTimeoutMilliseconds = 2_000,
    int HardFileTimeoutMilliseconds = 5_000,
    int WorkerFailureTripCount = 3,
    int WorkerCircuitCooldownSeconds = 300,
    int MaximumConcurrentExtractions = 2,
    int PipelineCapacity = 16,
    long MaximumEstimatedReadBytesPerFile = 4 * 1024 * 1024,
    double InteractiveThrottleFactor = 0.25,
    int InteractiveQuietSeconds = 10,
    int PressureCooldownSeconds = 5,
    int SlowExtractionMilliseconds = 750)
{
    public static IndexingRuntimeOptions Default { get; } = new(
        TimeSpan.FromHours(6),
        TimeSpan.FromMilliseconds(250),
        64,
        MaximumFilesPerSecond: 12,
        MaximumReadBytesPerSecond: 32 * 1024 * 1024,
        MaximumWriteBytesPerSecond: 8 * 1024 * 1024);
}

internal sealed class RuntimeCapabilityProjection
{
    private readonly object _gate = new();
    private RuntimeCapability _discovery;
    private RuntimeCapability _metadata;
    private RuntimeCapability? _storagePressure;
    private bool _storagePressureActive;
    private RuntimeCapability _current;

    public RuntimeCapabilityProjection(
        RuntimeCapability discovery,
        RuntimeCapability metadata)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _current = Combine(discovery, metadata);
    }

    public RuntimeCapability Current => Volatile.Read(ref _current);

    public RuntimeCapability Metadata
    {
        get
        {
            lock (_gate)
            {
                return _metadata;
            }
        }
    }

    public void UpdateDiscovery(RuntimeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
        {
            _discovery = capability;
            ReprojectLocked();
        }
    }

    public void UpdateMetadata(RuntimeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
        {
            _metadata = capability;
            ReprojectLocked();
        }
    }

    public void UpdateComponents(
        RuntimeCapability discovery,
        RuntimeCapability metadata)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_gate)
        {
            _discovery = discovery;
            _metadata = metadata;
            ReprojectLocked();
        }
    }

    public bool PublishStoragePressure(RuntimeCapability pressure)
    {
        ArgumentNullException.ThrowIfNull(pressure);
        if (!string.Equals(pressure.Code, "INDEX_STORAGE_PRESSURE", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Storage pressure overlays must use INDEX_STORAGE_PRESSURE.",
                nameof(pressure));
        }

        lock (_gate)
        {
            var changed = !_storagePressureActive ||
                !string.Equals(
                    _storagePressure?.Details?.GetValueOrDefault("reason"),
                    pressure.Details?.GetValueOrDefault("reason"),
                    StringComparison.Ordinal);
            _storagePressure = pressure;
            _storagePressureActive = true;
            ReprojectLocked();
            return changed;
        }
    }

    public bool PublishStorageReady()
    {
        lock (_gate)
        {
            if (!_storagePressureActive)
            {
                return false;
            }

            _storagePressure = null;
            _storagePressureActive = false;
            ReprojectLocked();
            return true;
        }
    }

    internal static RuntimeCapability Combine(
        RuntimeCapability discovery,
        RuntimeCapability metadata)
    {
        if (discovery.State != CapabilityState.Available)
        {
            return discovery;
        }

        if (metadata.State != CapabilityState.Available)
        {
            return new RuntimeCapability(
                CapabilityState.Degraded,
                metadata.Code,
                $"文件发现正常；{metadata.ChineseMessage}",
                metadata.Details);
        }

        return new RuntimeCapability(
            CapabilityState.Available,
            "INDEX_READY",
            "文件发现、持久索引和元数据服务均已就绪。");
    }

    private void ReprojectLocked()
    {
        var projected = _storagePressureActive
            ? _storagePressure!
            : Combine(_discovery, _metadata);
        Volatile.Write(ref _current, projected);
    }
}

public sealed class IndexingRuntime : ISearchService, IAsyncDisposable
{
    // Background work may amortize a SQLite commit across this many files, but
    // it must never turn into an unbounded queue reservation.  This is a
    // batching limit, not an extraction-worker setting.
    private const int BackgroundMetadataLeaseLimit = 32;
    // A cloud-only placeholder has no local bytes to parse. It remains indexed
    // by file name/path, but is terminal immediately instead of holding the
    // global progress below 100% behind a multi-hour retry ladder. A later
    // discovery change or explicit read requeues it after the file hydrates.
    internal const int MaximumCloudPlaceholderDeferrals = 1;
    internal const string CloudPlaceholderDeferredErrorCode = "CLOUD_PLACEHOLDER_DEFERRED";
    internal const string CloudPlaceholderRetryExhaustedErrorCode =
        "CLOUD_PLACEHOLDER_RETRY_EXHAUSTED";
    private const int BackgroundLeaseBaseMilliseconds = 5_000;
    // A hosted parser can remain effectively serial even when the runtime has
    // multiple client slots.  Keep a complete 32-item background batch leased
    // long enough to accumulate it before the single SQLite commit.  Derive
    // the allowance from the window so future tuning cannot silently make a
    // valid batch expire while its final commit is waiting for the write gate.
    private const int BackgroundCommitMaximumAgeMilliseconds = 45_000;
    private const int BackgroundLeaseCommitSafetyMarginMilliseconds = 15_000;
    private const int BackgroundLeaseCommitAllowanceMilliseconds =
        BackgroundCommitMaximumAgeMilliseconds + BackgroundLeaseCommitSafetyMarginMilliseconds;
    private const int BackgroundMaximumLeaseMilliseconds = 14 * 60 * 1_000;
    private static readonly TimeSpan ConservativeCommitMaximumAge = TimeSpan.FromSeconds(1);
    // This applies only to the background consumer.  Interactive/manual work
    // retains ConservativeCommitMaximumAge so a user-triggered result never
    // waits behind the amortisation window.
    private static readonly TimeSpan BackgroundCommitMaximumAge =
        TimeSpan.FromMilliseconds(BackgroundCommitMaximumAgeMilliseconds);
    // A fresh committed snapshot is still blind to mutations which occurred
    // while the Broker was stopped. Give the first metadata settlement a short
    // head start, then perform one normal reconciliation instead of inheriting
    // the provider's multi-day cadence on every restart.
    private static readonly TimeSpan FreshReadyStartupCatchUpGracePeriod =
        TimeSpan.FromSeconds(30);

    private readonly SqliteIndexStore _store;
    private readonly IFileDiscoveryProvider _discovery;
    private readonly IMetadataExtractionClient _metadata;
    private readonly IndexingCoordinator _coordinator;
    private readonly IndexingRuntimeOptions _options;
    private readonly RuntimeFileLog _log;
    private readonly ResourceGovernor _governor;
    private readonly WorkerCircuitBreaker _circuitBreaker;
    private readonly SearchCandidatePromotionQueue _searchPromotions;
    private readonly RuntimePhaseMetrics _runtimeMetrics = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _backgroundPreparationGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly SemaphoreSlim _progressGate = new(1, 1);
    // Serializes the tiny admission decision for live discovery mutations. A
    // full snapshot never holds this while enumerating: once it marks itself
    // active, watcher events append to a bounded in-memory coalescer instead
    // of competing with the final publication transaction.
    private readonly SemaphoreSlim _discoveryMutationGate = new(1, 1);
    private readonly object _backgroundLoopGate = new();
    private readonly HashSet<Task> _lingeringBackgroundLoops = [];
    private readonly string _leaseOwner = $"runtime-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private bool _initialized;
    private bool _backgroundPrepared;
    private bool _disposed;
    private int _manualPause;
    private int _powerSuspended;
    private int _networkConstrained;
    private long _hintReconciliationDueUtcTicks;
    private long _progressCacheExpiresUtcTicks;
    private long _progressCacheMetadataEpoch = -1;
    private long _nextStorageRecoveryUtcTicks;
    private long _nextDiscoveryHintWriteUtcTicks;
    private long _nextDeferredDiscoveryFlushCheckUtcTicks;
    private long _nextDurableDeferredDiscoveryFlushCheckUtcTicks;
    private long _publishedMetadataProgressEpoch;
    private long _revision;
    private long _publishedSearchContentVersion;
    private long _lastUpdatedUtcTicks;
    private int _backgroundUpdateCount;
    private int _discoveryServiceState = (int)RuntimeServiceState.Idle;
    private int _metadataServiceState = (int)RuntimeServiceState.Idle;
    private long _discoveryHeartbeatUtcTicks;
    private long _metadataHeartbeatUtcTicks;
    private string _discoveryServiceCode = "DISCOVERY_IDLE";
    private string _metadataServiceCode = "METADATA_IDLE";
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private MetadataProgressSnapshot? _progressCache;
    private long _lastIdleSelfTrimUtcTicks;
    private readonly SystemLoadMonitor _systemLoad = new();
    private static readonly TimeSpan IdleSelfTrimInterval = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, DiscoveredFile> _deferredDiscoveryHints =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _fullDiscoverySnapshotInProgress;
    private bool _metadataProgressBatchInProgress;
    private const int MaximumDeferredDiscoveryHints = 4_096;
    private readonly RuntimeCapabilityProjection _capabilities = new(
        new RuntimeCapability(
            CapabilityState.Degraded,
            "INDEX_INITIALIZING",
            "索引服务正在初始化；现有缓存仍可查询。"),
        new RuntimeCapability(
            CapabilityState.Degraded,
            "METADATA_INITIALIZING",
            "Metadata service is initializing; the existing cache remains searchable."));

    public IndexingRuntime(
        SqliteIndexStore store,
        IFileDiscoveryProvider discovery,
        IMetadataExtractionClient metadata,
        IndexingRuntimeOptions? options = null,
        RuntimeFileLog? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _coordinator = new IndexingCoordinator(discovery, store);
        _options = options ?? IndexingRuntimeOptions.Default;
        _log = log ?? new RuntimeFileLog();
        ValidateOptions(_options);
        _governor = new ResourceGovernor(_options);
        _circuitBreaker = new WorkerCircuitBreaker(
            _options.WorkerFailureTripCount,
            TimeSpan.FromSeconds(_options.WorkerCircuitCooldownSeconds));
        _searchPromotions = new SearchCandidatePromotionQueue(
            capacity: 64,
            (query, cancellationToken) => _store.EnqueueSearchCandidatesAsync(
                query,
                cancellationToken: cancellationToken),
            (exception, cancellationToken) => _log.WriteAsync(
                "WARN",
                "SEARCH_PROMOTION_FAILED",
                exception.GetType().Name,
                cancellationToken));
    }

    public RuntimeCapability CurrentCapability => _capabilities.Current;

    public SqliteIndexStore Store => _store;

    internal SearchPromotionQueueSnapshot SearchPromotionSnapshot => _searchPromotions.Snapshot;

    internal Func<string, FileAttributes>? BackgroundFileAttributesReaderForTesting { get; set; }

    internal Func<string, StableFileEvidence>? BackgroundEvidenceReaderForTesting { get; set; }

    internal Func<string, StableFileEvidence>? InteractiveEvidenceReaderForTesting { get; set; }

    internal Action? MetadataLeaseClaimedForTesting { get; set; }

    // This is test-only observability for the bounded background batching
    // policy.  It intentionally reports the plan before a lease is claimed so
    // tests can prove the public/manual path stays conservative without
    // reaching into the SQLite queue implementation.
    internal Action<MetadataBatchPlan>? MetadataBatchPlannedForTesting { get; set; }

    internal Action? BackgroundMetadataLeaseKeepaliveStartedForTesting { get; set; }

    internal TimeSpan? MetadataFirstPassTimeoutForTesting { get; set; }

    internal TimeSpan? FreshReadyStartupCatchUpGracePeriodForTesting { get; set; }

    internal Func<CancellationToken, ValueTask>? MetadataFirstPassTimeoutLogForTesting { get; set; }

    internal TimeSpan? BackgroundLoopDrainTimeoutForTesting { get; set; }

    internal int TrackedBackgroundLoopCountForTesting
    {
        get
        {
            lock (_backgroundLoopGate)
            {
                return _lingeringBackgroundLoops.Count;
            }
        }
    }

    internal TimeSpan MetadataStallThresholdForTesting { get; set; } = TimeSpan.FromMinutes(2);

    internal IDiscoveryProgressSource? DiscoveryProgressSourceForTesting { get; set; }

    internal RuntimePhaseMetricsSnapshot RuntimePhaseMetricsForTesting =>
        _runtimeMetrics.Snapshot();

    internal TimeSpan RuntimeMetricsLogIntervalForTesting
    {
        set => _runtimeMetrics.LogInterval = value;
    }

    public bool BackgroundWorkPaused =>
        Volatile.Read(ref _manualPause) != 0 ||
        Volatile.Read(ref _powerSuspended) != 0 ||
        Volatile.Read(ref _networkConstrained) != 0;

    public void SetManualPause(bool paused) => Volatile.Write(ref _manualPause, paused ? 1 : 0);

    public void NotifyInteractiveActivity() => _governor.NotifyInteractiveActivity();

    public void RequestRefresh() => RequestReconciliation(TimeSpan.Zero);

    public async ValueTask<BrokerStateSnapshot> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            return new BrokerStateSnapshot(
                _epoch,
                Interlocked.Read(ref _revision),
                BrokerRuntimePhase.Starting,
                CacheAvailable: false,
                BackgroundUpdating: Volatile.Read(ref _backgroundUpdateCount) > 0,
                FileCount: 0,
                LastUpdatedUtc: null,
                CurrentCapability);
        }

        var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var cacheAvailable = status.DiscoveryGeneration > 0 || status.PresentFileCount > 0;
        var updating = Volatile.Read(ref _backgroundUpdateCount) > 0;
        var phase = !cacheAvailable
            ? BrokerRuntimePhase.FirstIndex
            : updating
                ? BrokerRuntimePhase.Updating
                : CurrentCapability.State == CapabilityState.Available
                    ? BrokerRuntimePhase.Ready
                    : BrokerRuntimePhase.Degraded;
        var updatedTicks = Interlocked.Read(ref _lastUpdatedUtcTicks);
        return new BrokerStateSnapshot(
            _epoch,
            Interlocked.Read(ref _revision),
            phase,
            cacheAvailable,
            updating,
            status.PresentFileCount,
            updatedTicks == 0
                ? status.LastSuccessfulDiscoveryUtc
                : new DateTimeOffset(updatedTicks, TimeSpan.Zero),
            CurrentCapability);
    }

    // These hooks are driven only by host OS notifications.  They deliberately
    // do not inspect foreground windows, keyboard state or mouse activity.
    public void SetPowerSuspended(bool suspended) => Volatile.Write(ref _powerSuspended, suspended ? 1 : 0);

    public void SetNetworkConstrained(bool constrained) => Volatile.Write(ref _networkConstrained, constrained ? 1 : 0);

    public async ValueTask<RuntimeProgressSnapshot> GetProgressAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        // A discovery finalization publishes its queue comparison atomically and
        // then advances the store epoch. Retry once if it races this composite
        // read so a DTO never pairs pre-publish metadata with post-publish
        // status (or the converse).
        for (var attempt = 0; attempt != 2; attempt++)
        {
            var epochBefore = _store.MetadataProgressEpoch;
            var metadata = await GetCachedMetadataProgressAsync(cancellationToken).ConfigureAwait(false);
            var storage = await _store.GetStorageStatusAsync(cancellationToken).ConfigureAwait(false);
            var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var epochAfter = _store.MetadataProgressEpoch;
            if (epochBefore != epochAfter && attempt == 0)
            {
                Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
                continue;
            }

            var (source, code) = DiscoverySource();
            var now = DateTimeOffset.UtcNow;
            var providerDiscovery =
                (DiscoveryProgressSourceForTesting ?? _discovery as IDiscoveryProgressSource)
                ?.DiscoveryProgress;
            var indexedFileCount = Math.Max(
                0,
                providerDiscovery?.IndexedFileCount ?? status.PresentFileCount);
            var discoveryHeartbeat = providerDiscovery?.LastHeartbeatUtc.UtcTicks
                ?? Interlocked.Read(ref _discoveryHeartbeatUtcTicks);
            var discoveryState = BackgroundWorkPaused
                ? RuntimeServiceState.Paused
                : EffectiveServiceState(
                    providerDiscovery?.State ??
                    (RuntimeServiceState)Volatile.Read(ref _discoveryServiceState),
                    discoveryHeartbeat,
                    now,
                    TimeSpan.FromMinutes(2));
            var metadataState = BackgroundWorkPaused
                ? RuntimeServiceState.Paused
                : EffectiveServiceState(
                    (RuntimeServiceState)Volatile.Read(ref _metadataServiceState),
                    Interlocked.Read(ref _metadataHeartbeatUtcTicks),
                    now,
                    MetadataStallThresholdForTesting);
            return new RuntimeProgressSnapshot(
                metadata,
                source,
                code,
                BackgroundWorkPaused,
                _governor.CurrentFilesPerSecond,
                _governor.CurrentReadBytesPerSecond,
                EstimatedRemaining: null,
                storage.MetadataWritesPaused,
                storage.PauseReason,
                storage.WalBytes,
                new FileDiscoveryProgressSnapshot(
                    indexedFileCount,
                    FileDiscoveryBaselineEstimate.ResolveEstimatedTotal(
                        indexedFileCount,
                        providerDiscovery?.EstimatedTotalCount,
                        status.PresentFileCount,
                        discoveryState),
                    discoveryState,
                    providerDiscovery?.LastHeartbeatUtc ?? ReadHeartbeat(ref _discoveryHeartbeatUtcTicks, now),
                    providerDiscovery?.Source ?? source,
                    providerDiscovery?.Code ?? Volatile.Read(ref _discoveryServiceCode),
                    DiscoveryGeneration: status.DiscoveryGeneration,
                    LastSuccessfulDiscoveryUtc: status.LastSuccessfulDiscoveryUtc,
                    SourceState: status.SourceState,
                    BaselineFileCount: FileDiscoveryBaselineEstimate.ResolveBaseline(
                        status.PresentFileCount,
                        discoveryState)),
                new MetadataServiceProgressSnapshot(
                    metadata.CompletedCount,
                    metadata.PartialCount,
                    metadata.PendingCount,
                    metadata.FailedCount,
                    metadataState,
                    ReadHeartbeat(ref _metadataHeartbeatUtcTicks, now),
                    Volatile.Read(ref _metadataServiceCode)),
                DatabaseBytes: storage.DatabaseBytes,
                LogicalDatabaseBytes: storage.LogicalDatabaseBytes,
                ReusableDatabaseBytes: storage.ReusableDatabaseBytes,
                StorageMaintenancePhase: storage.MaintenancePhase,
                MetadataProgressEpoch: Interlocked.Read(ref _publishedMetadataProgressEpoch),
                MetadataProgressEpochSession: _epoch);
        }

        throw new InvalidOperationException("Unreachable progress read retry state.");
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            SwSearchPaths.EnsureDirectories();
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var recoveredCloudPlaceholders = await _store.FinalizeExhaustedDeferredMetadataAsync(
                CloudPlaceholderDeferredErrorCode,
                MaximumCloudPlaceholderDeferrals,
                CloudPlaceholderRetryExhaustedErrorCode,
                cancellationToken).ConfigureAwait(false);
            if (recoveredCloudPlaceholders > 0)
            {
                Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
                await _log.WriteAsync(
                    "WARN",
                    CloudPlaceholderRetryExhaustedErrorCode,
                    $"finalized={recoveredCloudPlaceholders};attempt_limit={MaximumCloudPlaceholderDeferrals};" +
                    "action=retry-after-cooldown-or-file-change-or-interactive-read",
                    cancellationToken).ConfigureAwait(false);
            }
            if (_discovery is IPersistedDiscoveryRootSink rootSink)
            {
                rootSink.RememberPreviouslyIndexedRoots(
                    await _store.GetIndexedRootsAsync(cancellationToken).ConfigureAwait(false));
            }

            var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var initialCapability = (status.DiscoveryGeneration > 0
                ? new RuntimeCapability(
                    status.CacheMayBeStale ? CapabilityState.Degraded : CapabilityState.Available,
                    status.CacheMayBeStale ? "INDEX_CACHE_STALE" : "INDEX_CACHE_READY",
                    status.CacheMayBeStale ? "正在后台刷新；当前显示最后一次持久缓存。" : "持久索引已就绪。")
                : new RuntimeCapability(
                    CapabilityState.Degraded,
                    "INDEX_EMPTY",
                    "索引尚未建立，正在等待后台文件发现。"));
            SetCapability(initialCapability);
            Interlocked.Exchange(ref _revision, status.DiscoveryGeneration);
            Interlocked.Exchange(ref _publishedSearchContentVersion, _store.SearchContentVersion);
            if (status.LastSuccessfulDiscoveryUtc is { } lastUpdated)
            {
                Interlocked.Exchange(ref _lastUpdatedUtcTicks, lastUpdated.UtcTicks);
            }
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async ValueTask<RuntimeCapability> RefreshAndProcessAsync(
        int? maximumMetadataItems = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _backgroundUpdateCount);
        try
        {
            var totalClock = Stopwatch.StartNew();
            var discoveryClock = Stopwatch.StartNew();
            var discoveryCapability = await RefreshFullDiscoverySnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            discoveryClock.Stop();
            RuntimeCapability metadataCapability;
            var probeClock = Stopwatch.StartNew();
            try
            {
                metadataCapability = await _metadata.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                metadataCapability = new RuntimeCapability(
                    CapabilityState.Degraded,
                    BrokerHostedMetadata
                        ? "METADATA_WORKER_RECOVERING"
                        : "METADATA_WORKER_UNAVAILABLE",
                    BrokerHostedMetadata
                        ? "Broker 内部属性处理暂时异常，正在自动恢复；文件名与现有缓存仍可搜索。"
                        : "元数据工作进程不可用；文件名与现有缓存仍可搜索。",
                    new Dictionary<string, string> { ["exception"] = exception.GetType().Name });
            }
            if (BrokerHostedMetadata && metadataCapability.State == CapabilityState.Unavailable)
            {
                metadataCapability = metadataCapability with { Code = "METADATA_WORKER_UNAVAILABLE" };
            }
            else if (BrokerHostedMetadata && metadataCapability.State == CapabilityState.Degraded)
            {
                metadataCapability = metadataCapability with { Code = "METADATA_WORKER_RECOVERING" };
            }
            probeClock.Stop();

            var processed = 0;
            var metadataClock = Stopwatch.StartNew();
            if (!BackgroundWorkPaused)
            {
                processed = await ProcessPendingMetadataCoreAsync(
                    maximumMetadataItems ?? _options.MetadataBatchSize,
                    cancellationToken).ConfigureAwait(false);
                await TryPublishDeferredDiscoveryHintsAsync(cancellationToken).ConfigureAwait(false);
                await TryPublishDurableDeferredDiscoveryChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            metadataClock.Stop();

            var combined = Combine(discoveryCapability, metadataCapability);
            var storage = await _store.GetStorageStatusAsync(cancellationToken).ConfigureAwait(false);
            _capabilities.UpdateComponents(discoveryCapability, metadataCapability);
            var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            PublishSearchRevisionIfChanged();
            totalClock.Stop();
            await _log.WriteAsync(
                "INFO",
                combined.Code,
                $"files={status.PresentFileCount};pending={status.PendingMetadataCount};generation={status.DiscoveryGeneration};" +
                $"processed={processed};discovery_ms={discoveryClock.Elapsed.TotalMilliseconds:F1};" +
                $"probe_ms={probeClock.Elapsed.TotalMilliseconds:F1};metadata_ms={metadataClock.Elapsed.TotalMilliseconds:F1};" +
                $"total_ms={totalClock.Elapsed.TotalMilliseconds:F1};wal_bytes={storage.WalBytes}",
                cancellationToken).ConfigureAwait(false);
            return combined;
        }
        finally
        {
            Interlocked.Decrement(ref _backgroundUpdateCount);
            _refreshGate.Release();
        }
    }

    public async ValueTask<int> ProcessPendingMetadataAsync(
        int maximumItems,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var processed = BackgroundWorkPaused
            ? 0
            : await ProcessPendingMetadataCoreAsync(maximumItems, cancellationToken).ConfigureAwait(false);
        if (!BackgroundWorkPaused)
        {
            await TryPublishDeferredDiscoveryHintsAsync(cancellationToken).ConfigureAwait(false);
            await TryPublishDurableDeferredDiscoveryChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        PublishSearchRevisionIfChanged();
        return processed;
    }

    public async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await PrepareBackgroundAsync(cancellationToken).ConfigureAwait(false);
        var incremental = _discovery as IIncrementalFileDiscoveryProvider;
        var refreshInterval = incremental?.RecommendedReconciliationInterval
            ?? _options.DiscoveryRefreshInterval;
        using var backgroundLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var backgroundToken = backgroundLifetime.Token;
        var tasks = new List<Task>(3);
        var firstMetadataPass = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataTask = StartTrackedBackgroundLoop(
            token => RunMetadataConsumerLoopAsync(token, firstMetadataPass),
            backgroundToken);
        tasks.Add(metadataTask);

        // Every loop is tracked from the instant the first loop starts. This
        // outer try/finally also covers cancellation and diagnostic failures
        // while waiting for first-pass admission, before discovery exists.
        try
        {
            // An existing metadata queue must receive an execution opportunity
            // before a potentially synchronous discovery provider can occupy its
            // loop thread for minutes or hours. The signal is completed after the
            // one-file conservative pass has durably settled its work, or once
            // the bounded pass proves that no claim is currently possible.
            try
            {
                await firstMetadataPass.Task.WaitAsync(
                        MetadataFirstPassTimeoutForTesting ??
                        TimeSpan.FromMilliseconds(
                            Math.Max(5_000, _options.HardFileTimeoutMilliseconds + 1_000)),
                        backgroundToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var timeoutLog = MetadataFirstPassTimeoutLogForTesting;
                if (timeoutLog is not null)
                {
                    await timeoutLog(backgroundToken).ConfigureAwait(false);
                }
                else
                {
                    await _log.WriteAsync(
                            "WARN",
                            "METADATA_FIRST_SETTLEMENT_TIMEOUT",
                            "Metadata first-settlement exceeded its bounded startup window.",
                            backgroundToken)
                        .ConfigureAwait(false);
                }
            }

            tasks.Add(StartTrackedBackgroundLoop(
                token => RunDiscoveryLoopAsync(refreshInterval, token),
                backgroundToken));
            if (incremental is not null)
            {
                tasks.Add(StartTrackedBackgroundLoop(
                    token => RunDiscoveryHintsAsync(incremental, token),
                    backgroundToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal background-service shutdown path.
        }
        finally
        {
            try
            {
                backgroundLifetime.Cancel();
            }
            catch (AggregateException)
            {
                // Cancellation callbacks cannot prevent loop observation.
            }

            var allStartedLoops = Task.WhenAll(tasks);
            try
            {
                await allStartedLoops.WaitAsync(
                        BackgroundLoopDrainTimeoutForTesting ??
                        TimeSpan.FromMilliseconds(
                            Math.Max(5_000, _options.HardFileTimeoutMilliseconds + 1_000)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (allStartedLoops.IsCanceled)
            {
                // All started loops observed normal coordinated cancellation.
            }
            catch (TimeoutException)
            {
                // A third-party provider violated its cancellation contract.
                // Keep a strong owner-visible reference until it actually ends;
                // DisposeAsync performs another bounded drain rather than
                // turning this into an untracked fire-and-forget loop.
                TrackLingeringBackgroundLoop(allStartedLoops);
            }
            catch
            {
                // The primary path already observes and propagates loop faults.
                // During an earlier startup failure this second observation must
                // not replace the original exception.
            }
        }
    }

    private void TrackLingeringBackgroundLoop(Task loop)
    {
        lock (_backgroundLoopGate)
        {
            _lingeringBackgroundLoops.Add(loop);
        }

        loop.GetAwaiter().OnCompleted(() =>
        {
            try
            {
                loop.GetAwaiter().GetResult();
            }
            catch
            {
                // Observe the terminal exception before releasing the strong
                // tracking reference.
            }

            lock (_backgroundLoopGate)
            {
                _lingeringBackgroundLoops.Remove(loop);
            }
        });
    }

    private async ValueTask DrainLingeringBackgroundLoopsAsync()
    {
        Task[] snapshot;
        lock (_backgroundLoopGate)
        {
            snapshot = [.. _lingeringBackgroundLoops];
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        var drain = Task.WhenAll(snapshot);
        try
        {
            await drain.WaitAsync(
                    BackgroundLoopDrainTimeoutForTesting ??
                    TimeSpan.FromMilliseconds(
                        Math.Max(5_000, _options.HardFileTimeoutMilliseconds + 1_000)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (drain.IsCanceled)
        {
            // Cancellation is a completed, observed terminal state.
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"{snapshot.Length} tracked background loop(s) did not stop within the bounded drain.");
        }
        catch
        {
            // Faults are observed here and by the completion callback.
        }

        lock (_backgroundLoopGate)
        {
            foreach (var completed in snapshot)
            {
                if (completed.IsCompleted)
                {
                    _lingeringBackgroundLoops.Remove(completed);
                }
            }
        }
    }

    private static Task StartTrackedBackgroundLoop(
        Func<CancellationToken, Task> loop,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                // Force loop admission to yield to the scheduler even when a
                // provider returns synchronously-completed ValueTasks.
                await Task.Yield();
                await loop(cancellationToken).ConfigureAwait(false);
            },
            CancellationToken.None);

    private static DateTimeOffset InitialDiscoveryDeadline(
        IndexStatus status,
        TimeSpan refreshInterval,
        DateTimeOffset now,
        TimeSpan freshReadyGrace)
    {
        if (freshReadyGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshReadyGrace));
        }

        if (status.DiscoveryGeneration <= 0 ||
            !string.Equals(status.SourceState, "ready", StringComparison.OrdinalIgnoreCase) ||
            status.LastSuccessfulDiscoveryUtc is not { } lastSuccessful)
        {
            return DateTimeOffset.MinValue;
        }

        var normalDeadline = lastSuccessful.Add(refreshInterval);
        if (normalDeadline <= now)
        {
            return DateTimeOffset.MinValue;
        }

        var catchUpDeadline = now.Add(freshReadyGrace);
        return normalDeadline < catchUpDeadline ? normalDeadline : catchUpDeadline;
    }

    private async Task RunLegacyBackgroundAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await PrepareBackgroundAsync(cancellationToken).ConfigureAwait(false);
        var incremental = _discovery as IIncrementalFileDiscoveryProvider;
        var refreshInterval = incremental is not null
            ? incremental.RecommendedReconciliationInterval
            : _options.DiscoveryRefreshInterval;
        var watchTask = incremental is null
            ? null
            : RunDiscoveryHintsAsync(incremental, cancellationToken);
        var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var nextDiscovery = InitialDiscoveryDeadline(
            status,
            refreshInterval,
            DateTimeOffset.UtcNow,
            FreshReadyStartupCatchUpGracePeriodForTesting ?? FreshReadyStartupCatchUpGracePeriod);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (BackgroundWorkPaused)
                    {
                        await Task.Delay(_options.IdleDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var requestedTicks = Interlocked.Read(ref _hintReconciliationDueUtcTicks);
                    if (requestedTicks != 0 && requestedTicks <= DateTimeOffset.UtcNow.UtcTicks)
                    {
                        Interlocked.Exchange(ref _hintReconciliationDueUtcTicks, 0);
                        nextDiscovery = DateTimeOffset.MinValue;
                    }

                    var utcNow = DateTimeOffset.UtcNow;
                    // System-busy avoidance for the ROUTINE refresh pass: the
                    // discovery scan is the product's heaviest read burst (an
                    // MFT/directory sweep has no byte-rate governor). While the
                    // machine is visibly busy (level >= 2), postpone the
                    // scheduled pass by one dynamic step; pending queue work
                    // still runs this iteration. Hint-driven catch-ups and the
                    // startup catch-up keep their semantics.
                    if (utcNow >= nextDiscovery &&
                        nextDiscovery != DateTimeOffset.MinValue &&
                        _systemLoad.CurrentLevel >= 2)
                    {
                        nextDiscovery = utcNow.Add(
                            SystemLoadMonitor.LevelDelays[_systemLoad.CurrentLevel]);
                    }

                    if (utcNow >= nextDiscovery)
                    {
                        await RefreshAndProcessAsync(_options.MetadataBatchSize, cancellationToken).ConfigureAwait(false);
                        nextDiscovery = DateTimeOffset.UtcNow.Add(refreshInterval);
                    }
                    else
                    {
                        await ProcessPendingMetadataAsync(_options.MetadataBatchSize, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SetCapability(new RuntimeCapability(
                        CapabilityState.Degraded,
                        "INDEX_BACKGROUND_FAILED",
                        "后台索引暂时失败；现有缓存仍可查询。",
                        new Dictionary<string, string> { ["exception"] = exception.GetType().Name }));
                    await _log.WriteAsync(
                        "ERROR",
                        "INDEX_BACKGROUND_FAILED",
                        exception.GetType().Name,
                        cancellationToken).ConfigureAwait(false);
                }

                // Resident-but-lean cadence: the Broker must keep listening
                // (watcher/USN hints, new queue rows), so it never exits —
                // instead, once the queue is fully drained and no full
                // discovery pass is running, the poll interval adapts to the
                // sampled system load (1-60 s) and the working set is
                // periodically returned. Any new work is simply noticed
                // within the stretched window; with pending work the loop
                // keeps its responsive base cadence.
                var loopDelay = _options.IdleDelay;
                var idleProgress = Volatile.Read(ref _progressCache);
                if (idleProgress is { PendingCount: 0 } && !_fullDiscoverySnapshotInProgress)
                {
                    loopDelay = SystemLoadMonitor.LevelDelays[_systemLoad.Sample()];
                    var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                    if (nowTicks - Volatile.Read(ref _lastIdleSelfTrimUtcTicks) >= IdleSelfTrimInterval.Ticks)
                    {
                        Volatile.Write(ref _lastIdleSelfTrimUtcTicks, nowTicks);
                        TryTrimWorkingSet();
                    }
                }

                await Task.Delay(loopDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (watchTask is not null)
            {
                try
                {
                    await watchTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal shutdown.
                }
            }
        }
    }

    private async Task RunDiscoveryLoopAsync(
        TimeSpan refreshInterval,
        CancellationToken cancellationToken)
    {
        var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var nextDiscovery = InitialDiscoveryDeadline(
            status,
            refreshInterval,
            DateTimeOffset.UtcNow,
            FreshReadyStartupCatchUpGracePeriodForTesting ?? FreshReadyStartupCatchUpGracePeriod);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (BackgroundWorkPaused)
                {
                    SetDiscoveryService(RuntimeServiceState.Paused, "DISCOVERY_PAUSED");
                    await Task.Delay(_options.IdleDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var requestedTicks = Interlocked.Read(ref _hintReconciliationDueUtcTicks);
                if (requestedTicks != 0 && requestedTicks <= DateTimeOffset.UtcNow.UtcTicks)
                {
                    Interlocked.Exchange(ref _hintReconciliationDueUtcTicks, 0);
                    nextDiscovery = DateTimeOffset.MinValue;
                }

                if (DateTimeOffset.UtcNow < nextDiscovery)
                {
                    SetDiscoveryService(RuntimeServiceState.Idle, "DISCOVERY_IDLE");
                    await Task.Delay(_options.IdleDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RefreshDiscoveryOnlyAsync(cancellationToken).ConfigureAwait(false);
                nextDiscovery = DateTimeOffset.UtcNow.Add(refreshInterval);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SetDiscoveryService(RuntimeServiceState.Unavailable, "DISCOVERY_LOOP_FAILED");
                SetCapability(new RuntimeCapability(
                    CapabilityState.Degraded,
                    "DISCOVERY_LOOP_FAILED",
                    "File discovery is temporarily unavailable; the existing cache remains searchable.",
                    new Dictionary<string, string> { ["exception"] = exception.GetType().Name }));
                await _log.WriteAsync(
                    "ERROR",
                    "DISCOVERY_LOOP_FAILED",
                    exception.GetType().Name,
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunMetadataConsumerLoopAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? firstPass = null)
    {
        await Task.Yield();
        try
        {
            await ProbeMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            firstPass?.TrySetResult(false);
            throw;
        }

        var firstPassPending = firstPass is not null;
        var recoveryDelay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (BackgroundWorkPaused)
                {
                    if (firstPassPending)
                    {
                        firstPass!.TrySetResult(false);
                        firstPassPending = false;
                    }
                    SetMetadataService(RuntimeServiceState.Paused, "METADATA_PAUSED");
                    await DelayMetadataIdleAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_capabilities.Metadata.State == CapabilityState.Unavailable)
                {
                    await ProbeMetadataAsync(cancellationToken).ConfigureAwait(false);
                    if (_capabilities.Metadata.State == CapabilityState.Unavailable)
                    {
                        if (firstPassPending)
                        {
                            firstPass!.TrySetResult(false);
                            firstPassPending = false;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                var circuitBeforeWork = _circuitBreaker.Snapshot(DateTimeOffset.UtcNow);
                if (!_circuitBreaker.HasExecutionOpportunity(DateTimeOffset.UtcNow))
                {
                    var code = BrokerHostedMetadata
                        ? "METADATA_WORKER_RECOVERING"
                        : circuitBeforeWork.State == WorkerCircuitState.HalfOpen
                            ? "METADATA_CIRCUIT_HALF_OPEN"
                            : "METADATA_CIRCUIT_OPEN";
                    if ((RuntimeServiceState)Volatile.Read(ref _metadataServiceState) !=
                            RuntimeServiceState.Unavailable ||
                        !string.Equals(Volatile.Read(ref _metadataServiceCode), code, StringComparison.Ordinal))
                    {
                        SetMetadataService(RuntimeServiceState.Unavailable, code);
                    }
                    if (firstPassPending)
                    {
                        firstPass!.TrySetResult(false);
                        firstPassPending = false;
                    }
                    await DelayMetadataIdleAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                SetMetadataService(
                    RuntimeServiceState.Running,
                    BrokerHostedMetadata ? "METADATA_UPDATING" : "METADATA_RUNNING");
                var processed = 0;
                try
                {
                    processed = await ProcessPendingMetadataCoreAsync(
                            firstPassPending ? 1 : _options.MetadataBatchSize,
                            cancellationToken,
                            firstPassPending
                                ? () => firstPass!.TrySetResult(true)
                                : null,
                            firstPassPending
                                ? MetadataBatchExecutionMode.Conservative
                                : MetadataBatchExecutionMode.Background)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (firstPassPending)
                    {
                        firstPass!.TrySetResult(false);
                        firstPassPending = false;
                    }
                }
                await TryPublishDeferredDiscoveryHintsAsync(cancellationToken).ConfigureAwait(false);
                await TryPublishDurableDeferredDiscoveryChangesAsync(cancellationToken).ConfigureAwait(false);
                PublishSearchRevisionIfChanged();
                var circuit = _circuitBreaker.Snapshot(DateTimeOffset.UtcNow);
                if (circuit.State != WorkerCircuitState.Closed)
                {
                    SetMetadataService(
                        RuntimeServiceState.Unavailable,
                        BrokerHostedMetadata
                            ? "METADATA_WORKER_RECOVERING"
                            : circuit.State == WorkerCircuitState.HalfOpen
                                ? "METADATA_CIRCUIT_HALF_OPEN"
                                : "METADATA_CIRCUIT_OPEN");
                }
                else
                {
                    var progress = processed == 0
                        ? await GetCachedMetadataProgressAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                    var hasPending = progress is { PendingCount: > 0 };
                    var freshStorage = await _store.GetStorageStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var storageBlocked = hasPending &&
                        freshStorage.MetadataWritesPaused &&
                        IsCapacityPauseReason(freshStorage.PauseReason);
                    SetMetadataService(
                        storageBlocked
                            ? RuntimeServiceState.Unavailable
                            : processed == 0 && !hasPending
                                ? RuntimeServiceState.Idle
                                : RuntimeServiceState.Running,
                        storageBlocked
                            ? "METADATA_STORAGE_BLOCKED"
                            : BrokerHostedMetadata
                                ? processed == 0 && !hasPending
                                    ? "METADATA_COMPLETE"
                                    : "METADATA_UPDATING"
                                : processed == 0 && !hasPending
                                    ? "METADATA_IDLE"
                                    : "METADATA_RUNNING");
                }
                recoveryDelay = TimeSpan.FromSeconds(1);
                if (ShouldDelayAfterMetadataBatch(processed))
                {
                    await DelayMetadataIdleAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (firstPassPending)
                {
                    firstPass!.TrySetResult(false);
                    firstPassPending = false;
                }
                // Worker/pipe failures are converted to per-lease outcomes and
                // reflected by the circuit breaker above.  A catch here covers
                // scheduler, storage, and queue infrastructure as well, so do
                // not misreport those as a worker recovery.
                const string code = "METADATA_LOOP_FAILED";
                SetMetadataService(RuntimeServiceState.Unavailable, code);
                await _log.WriteAsync(
                    "ERROR",
                    code,
                    exception.GetType().Name,
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(recoveryDelay, cancellationToken).ConfigureAwait(false);
                recoveryDelay = TimeSpan.FromSeconds(Math.Min(30, recoveryDelay.TotalSeconds * 2));
            }

        }

        firstPass?.TrySetResult(false);
    }

    private async ValueTask<RuntimeCapability> RefreshDiscoveryOnlyAsync(
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _backgroundUpdateCount);
        SetDiscoveryService(RuntimeServiceState.Running, "DISCOVERY_RUNNING");
        var clock = Stopwatch.StartNew();
        try
        {
            RuntimeCapability capability;
            var usedIncrementalJournal = false;
            if (_discovery is IJournalIncrementalDiscoveryProvider journal &&
                await _store.LoadLatestDiscoveryCheckpointAsync(cancellationToken).ConfigureAwait(false) is { } checkpoint)
            {
                var incremental = await ApplyJournalDeltasAsync(journal, checkpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (incremental is not null)
                {
                    capability = incremental;
                    usedIncrementalJournal = true;
                }
                else
                {
                    capability = await RefreshFullDiscoverySnapshotAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                capability = await RefreshFullDiscoverySnapshotAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!usedIncrementalJournal &&
                _discovery is IJournalIncrementalDiscoveryProvider completedJournal &&
                completedJournal.SnapshotCheckpoint is { } completedCheckpoint &&
                capability.State != CapabilityState.Unavailable)
            {
                // The provider publishes SnapshotCheckpoint only after its full
                // snapshot has committed. Persisting it afterwards means a
                // crash can replay changes, but can never skip an unapplied USN.
                await _store.SaveDiscoveryCheckpointAsync(completedCheckpoint, cancellationToken)
                    .ConfigureAwait(false);
            }

            var status = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            PublishSearchRevisionIfChanged();
            SetDiscoveryService(
                capability.State == CapabilityState.Unavailable
                    ? RuntimeServiceState.Unavailable
                    : RuntimeServiceState.Idle,
                capability.Code);
            _capabilities.UpdateDiscovery(capability);
            await _log.WriteAsync(
                "INFO",
                capability.Code,
                FormatDiscoveryLogMessage(capability, status, clock.Elapsed),
                cancellationToken).ConfigureAwait(false);
            return capability;
        }
        finally
        {
            clock.Stop();
            TouchDiscoveryHeartbeat();
            Interlocked.Decrement(ref _backgroundUpdateCount);
            _refreshGate.Release();
        }
    }

    private async ValueTask<RuntimeCapability?> ApplyJournalDeltasAsync(
        IJournalIncrementalDiscoveryProvider journal,
        DiscoveryJournalCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var current = checkpoint;
        var applied = 0L;
        while (true)
        {
            var batch = await journal.ReadDeltaBatchAsync(current, 4_096, cancellationToken)
                .ConfigureAwait(false);
            if (batch.RequiresFullReconciliation)
            {
                await _log.WriteAsync(
                    "WARN",
                    batch.Code,
                    "USN checkpoint requires a full reconciliation.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            var result = await _store.ApplyDiscoveryDeltaBatchAsync(batch.Changes, cancellationToken)
                .ConfigureAwait(false);
            if (result.Changed)
            {
                await BeginMetadataProgressBatchAsync(cancellationToken).ConfigureAwait(false);
            }
            if (result.DeferredForActiveLease)
            {
                // The store persisted every protected upsert/tombstone in its
                // own transaction before returning this result, so advancing
                // the USN cursor is safe. Ask the background cadence to check
                // for the terminal lease transition soon rather than relying
                // on a process-local watcher buffer.
                RequestReconciliation(TimeSpan.FromSeconds(30));
            }
            applied += batch.Changes.Count;

            // Each mutation above is idempotent. The checkpoint advances only
            // after every mutation in this page committed; a crash before this
            // atomic save simply replays the page on the next start.
            await _store.SaveDiscoveryCheckpointAsync(batch.NextCheckpoint, cancellationToken)
                .ConfigureAwait(false);
            current = batch.NextCheckpoint;
            if (!batch.HasMore)
            {
                return new RuntimeCapability(
                    CapabilityState.Available,
                    "DISCOVERY_USN_INCREMENTAL",
                    "文件增量索引已更新。",
                    new Dictionary<string, string>
                    {
                        ["changes"] = applied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["source"] = current.Source,
                    });
            }
        }
    }

    private async ValueTask ProbeMetadataAsync(CancellationToken cancellationToken)
    {
        RuntimeCapability capability;
        try
        {
            capability = await _metadata.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            capability = new RuntimeCapability(
                CapabilityState.Unavailable,
                BrokerHostedMetadata
                    ? "METADATA_WORKER_RECOVERING"
                    : "METADATA_WORKER_UNAVAILABLE",
                BrokerHostedMetadata
                    ? "Broker 内部属性处理暂时异常，正在自动恢复；文件名与现有缓存仍可搜索。"
                    : "Metadata worker is unavailable; file names and existing cached properties remain searchable.",
                new Dictionary<string, string> { ["exception"] = exception.GetType().Name });
        }

        if (BrokerHostedMetadata && capability.State == CapabilityState.Unavailable)
        {
            capability = capability with { Code = "METADATA_WORKER_UNAVAILABLE" };
        }
        else if (BrokerHostedMetadata && capability.State == CapabilityState.Degraded)
        {
            capability = capability with { Code = "METADATA_WORKER_RECOVERING" };
        }

        SetMetadataService(
            capability.State == CapabilityState.Unavailable
                ? RuntimeServiceState.Unavailable
                : RuntimeServiceState.Idle,
            capability.Code);
        _capabilities.UpdateMetadata(capability);
    }

    private async Task RunDiscoveryHintsAsync(
        IIncrementalFileDiscoveryProvider provider,
        CancellationToken cancellationToken)
    {
        var recentPaths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var hint in provider.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                if (BackgroundWorkPaused)
                {
                    RequestReconciliation(TimeSpan.FromMinutes(5));
                    continue;
                }

                if (hint.Kind is DiscoveryChangeKind.CreatedOrChanged or DiscoveryChangeKind.Renamed &&
                    !string.IsNullOrWhiteSpace(hint.FullPath))
                {
                    var now = DateTimeOffset.UtcNow.UtcTicks;
                    if (recentPaths.TryGetValue(hint.FullPath, out var previous) &&
                        now - previous < TimeSpan.FromMilliseconds(500).Ticks)
                    {
                        continue;
                    }
                    recentPaths[hint.FullPath] = now;
                    if (recentPaths.Count > 2_048)
                    {
                        var cutoff = now - TimeSpan.FromSeconds(10).Ticks;
                        foreach (var path in recentPaths
                                     .Where(pair => pair.Value < cutoff)
                                     .Select(static pair => pair.Key)
                                     .ToArray())
                        {
                            recentPaths.Remove(path);
                        }
                    }

                    await ThrottleDiscoveryHintWriteAsync(cancellationToken).ConfigureAwait(false);
                    var entry = await provider.TryInspectFileAsync(hint.FullPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (entry is not null)
                    {
                        await ApplyOrDeferDiscoveryHintAsync(entry, cancellationToken).ConfigureAwait(false);
                        PublishSearchRevisionIfChanged();
                    }
                    else
                    {
                        RequestReconciliation(TimeSpan.FromMinutes(5));
                    }
                }
                else if (hint.Kind == DiscoveryChangeKind.ReconciliationRequired)
                {
                    RequestReconciliation(TimeSpan.FromMinutes(1));
                }
                else if (hint.Kind == DiscoveryChangeKind.Deleted)
                {
                    // Watcher deletion is only a hint. Never delete from it; a
                    // complete root reconciliation is the authority.
                    RequestReconciliation(TimeSpan.FromMinutes(5));
                }
                else if (hint.Kind == DiscoveryChangeKind.Renamed)
                {
                    RequestReconciliation(TimeSpan.FromSeconds(30));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RequestReconciliation(TimeSpan.FromMinutes(1));
            await _log.WriteAsync(
                "WARN",
                "DISCOVERY_WATCH_FAILED",
                exception.GetType().Name,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<RuntimeCapability> RefreshFullDiscoverySnapshotAsync(
        CancellationToken cancellationToken)
    {
        var generationBefore = (await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false))
            .DiscoveryGeneration;
        var admissionStarted = false;
        try
        {
            await BeginFullDiscoverySnapshotAsync(cancellationToken).ConfigureAwait(false);
            admissionStarted = true;
            return await _coordinator.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (admissionStarted)
            {
                // A confirmed partial merge also advances generation. A generic
                // enumeration/cancellation failure only deletes its stage, so it
                // cannot accidentally publish deferred watcher work here.
                var published = false;
                try
                {
                    published = (await _store.GetStatusAsync(CancellationToken.None).ConfigureAwait(false))
                        .DiscoveryGeneration > generationBefore;
                }
                catch
                {
                    // The store's normal error path will surface this failure;
                    // leave buffered live hints untouched for a later retry.
                }

                await CompleteFullDiscoverySnapshotAsync(published).ConfigureAwait(false);
                if (_store.LastDiscoveryFinalizeTiming is { } mergeTiming)
                {
                    try
                    {
                        await _log.WriteAsync(
                                "INFO",
                                "DISCOVERY_MERGE_TIMING",
                                mergeTiming,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Timing diagnostics must never affect discovery.
                    }
                }
            }
        }
    }

    private async ValueTask BeginFullDiscoverySnapshotAsync(CancellationToken cancellationToken)
    {
        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _fullDiscoverySnapshotInProgress = true;
        }
        finally
        {
            _discoveryMutationGate.Release();
        }
    }

    private async ValueTask CompleteFullDiscoverySnapshotAsync(bool published)
    {
        var scheduleRetry = false;
        await _discoveryMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _fullDiscoverySnapshotInProgress = false;
            if (published)
            {
                // A finalized discovery snapshot is an explicit, atomic
                // work-set publication even when a previous metadata batch is
                // still draining. It therefore gets its own visible boundary;
                // only uncommitted watcher/USN writes are coalesced into the
                // current boundary.
                PublishMetadataProgressBatchLocked(forceNewEpoch: true);
            }
            else
            {
                // Keep the coalesced evidence in memory. The next authoritative
                // scan re-observes it, while an immediate reconciliation avoids
                // silently turning a generic snapshot failure into a publish.
                scheduleRetry = _deferredDiscoveryHints.Count > 0;
            }
        }
        finally
        {
            _discoveryMutationGate.Release();
        }

        if (published)
        {
            Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
        }
        else if (scheduleRetry)
        {
            RequestReconciliation(TimeSpan.Zero);
        }
    }

    private async ValueTask BeginMetadataProgressBatchAsync(
        CancellationToken cancellationToken,
        bool forceNewEpoch = false)
    {
        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PublishMetadataProgressBatchLocked(forceNewEpoch);
        }
        finally
        {
            _discoveryMutationGate.Release();
        }
    }

    private void PublishMetadataProgressBatchLocked(bool forceNewEpoch = false)
    {
        if (_metadataProgressBatchInProgress && !forceNewEpoch)
        {
            return;
        }

        _metadataProgressBatchInProgress = true;
        Interlocked.Increment(ref _publishedMetadataProgressEpoch);
        Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
    }

    private async ValueTask ApplyOrDeferDiscoveryHintAsync(
        DiscoveredFile entry,
        CancellationToken cancellationToken)
    {
        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fullDiscoverySnapshotInProgress ||
                _metadataProgressBatchInProgress ||
                _deferredDiscoveryHints.Count > 0)
            {
                BufferDeferredDiscoveryHintLocked(entry);
                return;
            }

            var result = await _store.ApplyDiscoveryHintBatchAsync([entry], cancellationToken)
                .ConfigureAwait(false);
            if (result.Changed)
            {
                PublishMetadataProgressBatchLocked();
            }
            else if (result.DeferredForActiveLease)
            {
                await DeferDiscoveryHintUntilActiveLeaseCompletesLockedAsync(entry)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _discoveryMutationGate.Release();
        }
    }

    private async ValueTask DeferDiscoveryHintUntilActiveLeaseCompletesAsync(
        DiscoveredFile entry,
        CancellationToken cancellationToken)
    {
        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeferDiscoveryHintUntilActiveLeaseCompletesLockedAsync(entry).ConfigureAwait(false);
        }
        finally
        {
            _discoveryMutationGate.Release();
        }
    }

    private ValueTask DeferDiscoveryHintUntilActiveLeaseCompletesLockedAsync(DiscoveredFile entry)
    {
        // Keep the inspected evidence until the valid owner resolves. Its
        // existing lease is already counted as pending work, so do not create a
        // visible epoch yet; the flush below publishes one atomic follow-up
        // batch after that owner reaches terminal state.
        BufferDeferredDiscoveryHintLocked(entry);
        _metadataProgressBatchInProgress = true;
        RequestReconciliation(TimeSpan.FromSeconds(30));
        return ValueTask.CompletedTask;
    }

    private void BufferDeferredDiscoveryHintLocked(DiscoveredFile entry)
    {
        var key = IndexNormalization.PathKey(entry.Candidate.FullPath);
        if (_deferredDiscoveryHints.TryGetValue(key, out var existing) &&
            entry.Identity is null && existing.Identity is not null)
        {
            entry = entry with { Identity = existing.Identity };
        }

        if (!_deferredDiscoveryHints.ContainsKey(key) &&
            _deferredDiscoveryHints.Count >= MaximumDeferredDiscoveryHints)
        {
            // The complete reconciliation is authoritative. Dropping only the
            // overflow hint is safe and avoids retaining an unbounded watcher
            // storm while the current metadata epoch is being drained.
            RequestReconciliation(TimeSpan.Zero);
            return;
        }

        _deferredDiscoveryHints[key] = entry;
    }

    private async ValueTask TryPublishDurableDeferredDiscoveryChangesAsync(
        CancellationToken cancellationToken)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        while (true)
        {
            var scheduled = Interlocked.Read(ref _nextDurableDeferredDiscoveryFlushCheckUtcTicks);
            if (scheduled > nowTicks)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _nextDurableDeferredDiscoveryFlushCheckUtcTicks,
                    nowTicks + TimeSpan.FromMilliseconds(500).Ticks,
                    scheduled) == scheduled)
            {
                break;
            }
        }

        // Preserve one steady recovery curve for the already-published batch.
        // A deferred mutation becomes a fresh visible work set only after the
        // old queue has drained; then its atomic replay earns one new epoch.
        if ((await _store.GetMetadataProgressAsync(cancellationToken).ConfigureAwait(false)).PendingCount > 0)
        {
            return;
        }

        var result = await _store.ApplyDeferredDiscoveryChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.Changed)
        {
            await BeginMetadataProgressBatchAsync(cancellationToken, forceNewEpoch: true)
                .ConfigureAwait(false);
        }
        else if (result.DeferredForActiveLease)
        {
            RequestReconciliation(TimeSpan.FromSeconds(30));
        }
    }

    private async ValueTask TryPublishDeferredDiscoveryHintsAsync(CancellationToken cancellationToken)
    {
        var shouldCheck = false;
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metadataProgressBatchInProgress &&
                !_fullDiscoverySnapshotInProgress &&
                Interlocked.Read(ref _nextDeferredDiscoveryFlushCheckUtcTicks) <= nowTicks)
            {
                Interlocked.Exchange(
                    ref _nextDeferredDiscoveryFlushCheckUtcTicks,
                    nowTicks + TimeSpan.FromMilliseconds(500).Ticks);
                shouldCheck = true;
            }
        }
        finally
        {
            _discoveryMutationGate.Release();
        }

        if (!shouldCheck)
        {
            return;
        }

        var progress = await _store.GetMetadataProgressAsync(cancellationToken).ConfigureAwait(false);
        if (progress.PendingCount > 0)
        {
            return;
        }

        DiscoveredFile[] deferred;
        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_metadataProgressBatchInProgress || _fullDiscoverySnapshotInProgress)
            {
                return;
            }

            deferred = [.. _deferredDiscoveryHints.Values];
            _deferredDiscoveryHints.Clear();
            if (deferred.Length == 0)
            {
                _metadataProgressBatchInProgress = false;
                return;
            }
        }
        finally
        {
            _discoveryMutationGate.Release();
        }

        SqliteIndexStore.DiscoveryHintBatchResult result;
        try
        {
            result = await _store.ApplyDiscoveryHintBatchAsync(deferred, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _discoveryMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                foreach (var entry in deferred)
                {
                    // Newer watcher evidence already buffered while this commit
                    // was attempted wins over an older retry item.
                    _deferredDiscoveryHints.TryAdd(IndexNormalization.PathKey(entry.Candidate.FullPath), entry);
                }
            }
            finally
            {
                _discoveryMutationGate.Release();
            }

            throw;
        }

        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.Changed)
            {
                // The preceding epoch recovered to terminal state. This is a
                // fresh, atomic live-hint batch and may intentionally retreat
                // once on the next progress DTO.
                _metadataProgressBatchInProgress = false;
                PublishMetadataProgressBatchLocked();
            }
            else if (_deferredDiscoveryHints.Count == 0)
            {
                _metadataProgressBatchInProgress = false;
            }
        }
        finally
        {
            _discoveryMutationGate.Release();
        }

        if (result.DeferredForActiveLease)
        {
            RequestReconciliation(TimeSpan.FromSeconds(30));
        }
    }

    private async ValueTask ThrottleDiscoveryHintWriteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = DateTimeOffset.UtcNow.UtcTicks;
            var scheduled = Interlocked.Read(ref _nextDiscoveryHintWriteUtcTicks);
            var due = Math.Max(now, scheduled);
            var next = due + TimeSpan.FromMilliseconds(50).Ticks;
            if (Interlocked.CompareExchange(ref _nextDiscoveryHintWriteUtcTicks, next, scheduled) != scheduled)
            {
                continue;
            }

            if (due > now)
            {
                await Task.Delay(TimeSpan.FromTicks(due - now), cancellationToken).ConfigureAwait(false);
            }
            return;
        }
    }

    private void RequestReconciliation(TimeSpan delay)
    {
        var requested = DateTimeOffset.UtcNow.Add(delay).UtcTicks;
        while (true)
        {
            var existing = Interlocked.Read(ref _hintReconciliationDueUtcTicks);
            if (existing != 0 && existing <= requested)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _hintReconciliationDueUtcTicks, requested, existing) == existing)
            {
                return;
            }
        }
    }

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (ShouldNotifyInteractiveActivity(query))
        {
            NotifyInteractiveActivity();
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var hits = await _store.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        // Best-effort metadata promotion is deliberately outside the response
        // path. TryEnqueue is bounded and never waits for SQLite's write gate.
        _searchPromotions.TryEnqueue(query);
        return hits;
    }

    public async ValueTask<long> CountSearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (ShouldNotifyInteractiveActivity(query))
        {
            NotifyInteractiveActivity();
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.CountSearchAsync(query, cancellationToken).ConfigureAwait(false);
    }

    internal static bool ShouldNotifyInteractiveActivity(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.EffectiveIsInteractive && !string.IsNullOrWhiteSpace(query.Text);
    }

    internal static bool ShouldDelayAfterMetadataBatch(int processed) => processed == 0;

    public async ValueTask PrioritizePropertyViewerAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        NotifyInteractiveActivity();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _store.EnqueueMetadataAsync(
            fullPath,
            "PropertyViewer",
            MetadataQueuePriority.PropertyViewer,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<DocumentMetadata?> RefreshMetadataAsync(
        string fullPath,
        CancellationToken cancellationToken = default) =>
        RefreshMetadataNowAsync(fullPath, cancellationToken);

    public async ValueTask<DocumentMetadata?> RefreshMetadataNowAsync(
        string fullPath,
        CancellationToken cancellationToken = default) =>
        (await RefreshMetadataNowWithStatusAsync(fullPath, cancellationToken).ConfigureAwait(false)).Metadata;

    public async ValueTask<MetadataRefreshResult> RefreshMetadataNowWithStatusAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        NotifyInteractiveActivity();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var normalized = Path.GetFullPath(fullPath);
        var cached = await _store.TryGetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false);
        InteractiveMetadataCommitToken? attempt = null;
        try
        {
            var pipelineLeaseId = Guid.NewGuid();
            attempt = await _store.BeginInteractiveMetadataAttemptAsync(
                    normalized,
                    pipelineLeaseId,
                    cancellationToken)
                .ConfigureAwait(false);
            var request = new MetadataRequest(
                ContractVersions.MetadataPipe,
                Guid.NewGuid(),
                normalized,
                ForceRefresh: true,
                TimeoutMilliseconds: _options.HardFileTimeoutMilliseconds,
                WorkClass: MetadataWorkClass.Interactive,
                PipelineLeaseId: pipelineLeaseId,
                // Force refresh must be able to advance a stale DB baseline
                // (V1) to the stable file version read by the Worker (V2).
                ExpectedFingerprint: null,
                ExtractorVersion: ExtractorVersions.Current);
            var response = await _metadata.ExtractAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.ProtocolVersion != ContractVersions.MetadataPipe ||
                response.RequestId != request.RequestId ||
                (_metadata is IBrokerHostedMetadataExtractionClient &&
                (response.PipelineLeaseId != pipelineLeaseId ||
                 response.WorkerGenerationNonce == Guid.Empty))
               )
            {
                return StaleRefresh(
                    cached,
                    "INTERACTIVE_STALE_WORKER_RESPONSE",
                    "属性 Worker 返回了过期代际结果，已拒绝写入缓存，请重试。",
                    retryable: true);
            }

            if (response.Metadata is not { } metadata)
            {
                await _log.WriteAsync(
                    "WARN",
                    response.ErrorCode ?? "INTERACTIVE_METADATA_FAILED",
                    response.ChineseErrorMessage ?? "interactive metadata response missing",
                    cancellationToken).ConfigureAwait(false);
                return StaleRefresh(
                    cached,
                    response.ErrorCode ?? "INTERACTIVE_METADATA_FAILED",
                    response.ChineseErrorMessage ?? "无法从文件重新读取属性，已保留原缓存。",
                    response.Retryable);
            }

            if (response.Disposition is MetadataFileDisposition.CloudPlaceholder or
                MetadataFileDisposition.ReparsePoint ||
                response.Outcome is MetadataPipelineOutcome.FileChanged or
                MetadataPipelineOutcome.FileGone or
                MetadataPipelineOutcome.FileUnreadable or
                MetadataPipelineOutcome.Deferred or
                MetadataPipelineOutcome.Unsupported or
                MetadataPipelineOutcome.Failed ||
                response.BeforeEvidence is not { } beforeContract ||
                response.AfterEvidence is not { } afterContract)
            {
                return StaleRefresh(
                    cached,
                    response.ErrorCode ?? "INTERACTIVE_METADATA_EVIDENCE_MISSING",
                    response.ChineseErrorMessage ?? "属性读取未能取得稳定文件证据，已保留原缓存。",
                    response.Retryable);
            }

            var before = ToStableEvidence(beforeContract);
            var after = ToStableEvidence(afterContract);
            if (!before.RequestedPathMatchesFinal ||
                !after.RequestedPathMatchesFinal ||
                !before.SameObjectAndVersion(after) ||
                !after.Matches(metadata.File))
            {
                await _log.WriteAsync(
                    "WARN",
                    "INTERACTIVE_FILE_CHANGED",
                    normalized,
                    cancellationToken).ConfigureAwait(false);
                return StaleRefresh(
                    cached,
                    "INTERACTIVE_FILE_CHANGED",
                    "读取属性期间文件已被修改或替换，未覆盖原缓存，请重试。",
                    retryable: true);
            }

            if (!await _store.TryCommitInteractiveMetadataAsync(
                    metadata,
                    before,
                    after,
                    attempt,
                    cancellationToken).ConfigureAwait(false))
            {
                await _log.WriteAsync(
                    "WARN",
                    "INTERACTIVE_METADATA_CAS_REJECTED",
                    normalized,
                    cancellationToken).ConfigureAwait(false);
                return StaleRefresh(
                    await _store.TryGetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false) ?? cached,
                    "INTERACTIVE_METADATA_CAS_REJECTED",
                    "文件或缓存版本已变化，本次鲜读结果未写入，请重试。",
                    retryable: true);
            }

            PublishSearchRevisionIfChanged();
            Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
            var committed = await _store.TryGetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false)
                ?? metadata;
            return new MetadataRefreshResult(
                committed,
                new MetadataRefreshStatus(
                    Fresh: true,
                    CacheStale: false,
                    ErrorCode: null,
                    ChineseMessage: "属性已从文件重新读取并更新缓存。",
                    Retryable: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(
                "WARN",
                "INTERACTIVE_METADATA_FAILED",
                exception.GetType().Name,
                cancellationToken).ConfigureAwait(false);
            return StaleRefresh(
                cached,
                "INTERACTIVE_METADATA_FAILED",
                "无法从文件重新读取属性，已保留原缓存。",
                retryable: true);
        }
        finally
        {
            if (attempt is not null)
            {
                try
                {
                    await _store.EndInteractiveMetadataAttemptAsync(attempt, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A newer attempt replaces this row atomically. Cleanup is
                    // best-effort and never touches the target file.
                }
            }
        }
    }

    private static MetadataRefreshResult StaleRefresh(
        DocumentMetadata? cached,
        string errorCode,
        string chineseMessage,
        bool retryable) =>
        new(
            cached,
            new MetadataRefreshStatus(
                Fresh: false,
                CacheStale: cached is not null,
                ErrorCode: errorCode,
                ChineseMessage: chineseMessage,
                Retryable: retryable));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // RunBackgroundAsync can return after its bounded shutdown window only
        // when a provider ignored cancellation. Keep runtime dependencies alive
        // and give those strongly tracked loops one final bounded owner drain.
        // A timeout leaves the runtime retryable instead of disposing resources
        // underneath a still-running loop.
        await DrainLingeringBackgroundLoopsAsync().ConfigureAwait(false);
        _disposed = true;
        await _searchPromotions.DisposeAsync().ConfigureAwait(false);
        if (_metadata is IAsyncDisposable asyncMetadata)
        {
            await asyncMetadata.DisposeAsync().ConfigureAwait(false);
        }

        if (_discovery is IAsyncDisposable asyncDiscovery)
        {
            await asyncDiscovery.DisposeAsync().ConfigureAwait(false);
        }

        await _store.DisposeAsync().ConfigureAwait(false);
        _discoveryMutationGate.Dispose();
        _progressGate.Dispose();
        _processingGate.Dispose();
        _refreshGate.Dispose();
        _backgroundPreparationGate.Dispose();
        _initializeGate.Dispose();
    }

    private async ValueTask PrepareBackgroundAsync(CancellationToken cancellationToken)
    {
        if (_backgroundPrepared)
        {
            return;
        }

        await _backgroundPreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backgroundPrepared)
            {
                return;
            }

            Interlocked.Increment(ref _backgroundUpdateCount);
            try
            {
                var adopted = await _store.AdoptCompletedCompatibleExtractorCacheAsync(
                        ExtractorVersions.Rc85CompatibleCacheVersion,
                        ExtractorVersions.Current,
                        cancellationToken)
                    .ConfigureAwait(false);
                var requeued = await _store.RequeueForExtractorVersionAsync(
                        ExtractorVersions.Current,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (requeued > 0 || adopted > 0)
                {
                    Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
                    await BeginMetadataProgressBatchAsync(cancellationToken).ConfigureAwait(false);
                }
                _backgroundPrepared = true;
            }
            finally
            {
                Interlocked.Decrement(ref _backgroundUpdateCount);
            }
        }
        finally
        {
            _backgroundPreparationGate.Release();
        }
    }

    private void BumpRevision()
    {
        Interlocked.Increment(ref _revision);
        Interlocked.Exchange(ref _lastUpdatedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
    }

    private void PublishSearchRevisionIfChanged()
    {
        var current = _store.SearchContentVersion;
        while (true)
        {
            var published = Interlocked.Read(ref _publishedSearchContentVersion);
            if (current <= published)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _publishedSearchContentVersion,
                    current,
                    published) == published)
            {
                BumpRevision();
                return;
            }
        }
    }

    private async ValueTask DelayMetadataIdleAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await Task.Delay(_options.IdleDelay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            clock.Stop();
            _runtimeMetrics.RecordIdleDelay(clock.Elapsed);
        }

        await MaybeWriteRuntimeMetricsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask MaybeWriteRuntimeMetricsAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeMetrics.TryDrainForLog(DateTimeOffset.UtcNow, out var metrics))
        {
            return;
        }

        await _log.WriteAsync(
            "INFO",
            "METADATA_PIPELINE_METRICS",
            metrics.ToLogMessage(),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ProcessPendingMetadataCoreAsync(
        int maximumItems,
        CancellationToken cancellationToken,
        Action? firstSettlementCompleted = null,
        MetadataBatchExecutionMode executionMode = MetadataBatchExecutionMode.Conservative)
    {
        if (maximumItems is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        var storageFullProbeStart = _store.StorageStatusFullProbeCount;
        var storageCacheHitStart = _store.StorageStatusCacheHitCount;
        var storageSnapshotElapsed = TimeSpan.Zero;
        var storageSnapshotChecks = 0;
        await _processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storageStatusCycle = _store.BeginStorageStatusSnapshotCycle();
            var storageClock = Stopwatch.StartNew();
            var storageReady = await EnsureStorageReadyAsync(cancellationToken).ConfigureAwait(false);
            storageClock.Stop();
            storageSnapshotElapsed = storageClock.Elapsed;
            storageSnapshotChecks = 1;
            if (!storageReady || !_circuitBreaker.HasExecutionOpportunity(DateTimeOffset.UtcNow))
            {
                return 0;
            }

            var concurrency = _governor.RecommendedConcurrency(_options.MaximumConcurrentExtractions);
            var batchPlan = CreateMetadataBatchPlan(
                maximumItems,
                concurrency,
                executionMode == MetadataBatchExecutionMode.Background);
            MetadataBatchPlannedForTesting?.Invoke(batchPlan);
            var batchLeaseOwner = $"{_leaseOwner}-{Guid.NewGuid():N}";
            var claimClock = Stopwatch.StartNew();
            var leases = await _store.LeaseMetadataAsync(
                batchPlan.LeaseCount,
                batchPlan.LeaseDuration,
                batchLeaseOwner,
                cancellationToken).ConfigureAwait(false);
            claimClock.Stop();
            _runtimeMetrics.RecordClaim(claimClock.Elapsed, leases.Count);
            if (leases.Count == 0)
            {
                return 0;
            }
            MetadataLeaseClaimedForTesting?.Invoke();

            using var batchLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            BackgroundMetadataLeaseKeepalive? leaseKeepalive = null;
            try
            {
                if (batchPlan.IsBackground)
                {
                    // A background batch is allowed to wait behind the read or
                    // write governor.  Keep its exact queue leases alive while
                    // it does so, but never beyond the independently bounded
                    // background reservation lifetime.
                    var claimedAtUtc = leases.Min(lease => lease.LeaseUntilUtc).Subtract(batchPlan.LeaseDuration);
                    leaseKeepalive = new BackgroundMetadataLeaseKeepalive(
                        _store,
                        leases,
                        new MetadataLeaseKeepaliveSettings(
                            batchPlan.LeaseDuration,
                            TimeSpan.FromMilliseconds(BackgroundMaximumLeaseMilliseconds),
                            TimeSpan.FromSeconds(15),
                            TimeSpan.FromMilliseconds(BackgroundLeaseCommitSafetyMarginMilliseconds)),
                        claimedAtUtc,
                        batchLifetime);
                    leaseKeepalive.Start();
                    BackgroundMetadataLeaseKeepaliveStartedForTesting?.Invoke();
                }

                var processed = await ProcessLeasesPipelineAsync(
                    leases,
                    concurrency,
                    batchPlan.PipelineCapacity,
                    batchPlan.CommitMaximumAge,
                    leaseKeepalive,
                    batchLifetime.Token).ConfigureAwait(false);
                firstSettlementCompleted?.Invoke();
                return processed;
            }
            finally
            {
                // Stop and observe the heartbeat before releasing its owner.
                // Releasing first would allow a late renewal to recreate an
                // otherwise finished reservation.
                if (leaseKeepalive is not null)
                {
                    await leaseKeepalive.StopAsync().ConfigureAwait(false);
                }

                // Pause/cancel never strands the rest of a long-lived leased batch.
                // Rows already completed or failed no longer match this owner.
                await _store.ReleaseMetadataLeasesAsync(batchLeaseOwner, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _runtimeMetrics.RecordStorageSnapshot(
                storageSnapshotElapsed,
                storageSnapshotChecks,
                _store.StorageStatusFullProbeCount - storageFullProbeStart,
                _store.StorageStatusCacheHitCount - storageCacheHitStart);
            _processingGate.Release();
            await MaybeWriteRuntimeMetricsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private MetadataBatchPlan CreateMetadataBatchPlan(
        int maximumItems,
        int extractionConcurrency,
        bool isBackground)
    {
        // Public/interactive callers keep the historical conservative plan.
        // The background consumer is the only caller that may reserve a 32
        // item batch.  MetadataPipeHost still decides worker isolation, and
        // ProcessLeasesPipelineAsync below still creates exactly
        // extractionConcurrency readers.
        if (!isBackground)
        {
            var leaseCount = Math.Min(maximumItems, extractionConcurrency * 4);
            var leaseDuration = TimeSpan.FromMilliseconds(
                Math.Min(
                    30_000,
                    5_000 + (long)_options.HardFileTimeoutMilliseconds * leaseCount / extractionConcurrency));
            return new MetadataBatchPlan(
                IsBackground: false,
                LeaseCount: leaseCount,
                PipelineCapacity: Math.Max(1, Math.Min(_options.PipelineCapacity, leaseCount)),
                ExtractionConcurrency: extractionConcurrency,
                LeaseDuration: leaseDuration,
                CommitMaximumAge: ConservativeCommitMaximumAge);
        }

        // Lease validity must cover the conservative case where the hosted
        // metadata service serializes all background requests.  Do not assume
        // the runtime's two client requests translate to two parser workers.
        // A bounded 14-minute reservation leaves the store's 15-minute safety
        // ceiling intact; on unusually large per-file hard timeouts we take a
        // smaller batch rather than letting any lease quietly expire.
        var maximumBackgroundItems = Math.Max(
            1,
            (BackgroundMaximumLeaseMilliseconds -
                BackgroundLeaseBaseMilliseconds -
                BackgroundLeaseCommitAllowanceMilliseconds) /
            Math.Max(1, _options.HardFileTimeoutMilliseconds));
        var backgroundLeaseCount = Math.Min(
            Math.Min(maximumItems, BackgroundMetadataLeaseLimit),
            maximumBackgroundItems);
        var requestedLeaseMilliseconds =
            BackgroundLeaseBaseMilliseconds +
            (long)_options.HardFileTimeoutMilliseconds * backgroundLeaseCount +
            BackgroundLeaseCommitAllowanceMilliseconds;
        var leaseDurationMilliseconds = Math.Clamp(
            requestedLeaseMilliseconds,
            60_000L,
            (long)BackgroundMaximumLeaseMilliseconds);
        return new MetadataBatchPlan(
            IsBackground: true,
            LeaseCount: backgroundLeaseCount,
            PipelineCapacity: backgroundLeaseCount,
            ExtractionConcurrency: extractionConcurrency,
            LeaseDuration: TimeSpan.FromMilliseconds(leaseDurationMilliseconds),
            CommitMaximumAge: BackgroundCommitMaximumAge);
    }

    private enum MetadataBatchExecutionMode
    {
        Conservative,
        Background,
    }

    private async ValueTask<int> ProcessLeasesPipelineAsync(
        IReadOnlyList<MetadataWorkLease> leases,
        int concurrency,
        int pipelineCapacity,
        TimeSpan commitMaximumAge,
        BackgroundMetadataLeaseKeepalive? leaseKeepalive,
        CancellationToken cancellationToken)
    {
        var capacity = Math.Max(1, Math.Min(pipelineCapacity, leases.Count));
        var work = Channel.CreateBounded<MetadataWorkLease>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var results = Channel.CreateBounded<MetadataExtractionOutcome>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = ProduceLeasesAsync(leases, work.Writer, cancellationToken);
        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => ExtractLeasesAsync(work.Reader, results.Writer, leaseKeepalive, cancellationToken))
            .ToArray();
        var completion = CompleteResultsAsync(workers, results.Writer);

        var pendingWrites = new List<MetadataCommitItem>(32);
        var estimatedWriteBytes = 0L;
        var succeeded = 0;
        var transactionClock = Stopwatch.StartNew();
        await foreach (var outcome in results.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            TouchMetadataHeartbeat();
            if (outcome.Metadata is { } metadata && outcome.Evidence is { } evidence)
            {
                pendingWrites.Add(new MetadataCommitItem(outcome.Lease, metadata, evidence));
                estimatedWriteBytes += EstimateMetadataBytes(metadata);
                if (pendingWrites.Count >= 32 || estimatedWriteBytes >= 2 * 1024 * 1024 ||
                    transactionClock.Elapsed >= commitMaximumAge)
                {
                    succeeded += await FlushMetadataBatchAsync(
                        pendingWrites,
                        estimatedWriteBytes,
                        leaseKeepalive,
                        cancellationToken).ConfigureAwait(false);
                    pendingWrites.Clear();
                    estimatedWriteBytes = 0;
                    transactionClock.Restart();
                }
            }
            else if (outcome.ChangedFile is { } changed)
            {
                leaseKeepalive?.BeginFinalizing(outcome.Lease);
                _ = await _store.TryRequeueChangedFileAsync(outcome.Lease, changed, cancellationToken)
                    .ConfigureAwait(false);
                leaseKeepalive?.Complete(outcome.Lease);
            }
            else if (outcome.TerminalSkipped)
            {
                leaseKeepalive?.BeginFinalizing(outcome.Lease);
                await _store.SkipMetadataLeaseAsync(
                    outcome.Lease.FileId,
                    outcome.Lease.LeaseOwner,
                    outcome.ErrorCode ?? "METADATA_SKIPPED_REPARSE_POINT",
                    cancellationToken).ConfigureAwait(false);
                leaseKeepalive?.Complete(outcome.Lease);
            }
            else if (outcome.TerminalDeferred)
            {
                leaseKeepalive?.BeginFinalizing(outcome.Lease);
                await _store.FinalizeDeferredMetadataLeaseAsync(
                    outcome.Lease.FileId,
                    outcome.Lease.LeaseOwner,
                    outcome.ErrorCode ?? CloudPlaceholderRetryExhaustedErrorCode,
                    cancellationToken).ConfigureAwait(false);
                leaseKeepalive?.Complete(outcome.Lease);
                Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
                await _log.WriteAsync(
                    "WARN",
                    CloudPlaceholderRetryExhaustedErrorCode,
                    $"file_id={outcome.Lease.FileId};attempt={outcome.Lease.Attempt};" +
                    "action=retry-after-cooldown-or-file-change-or-interactive-read",
                    cancellationToken).ConfigureAwait(false);
            }
            else if (outcome.DeferredUntilUtc is { } deferredUntil)
            {
                leaseKeepalive?.BeginFinalizing(outcome.Lease);
                await _store.DeferMetadataLeaseAsync(
                    outcome.Lease.FileId,
                    outcome.Lease.LeaseOwner,
                    deferredUntil,
                    outcome.ErrorCode ?? CloudPlaceholderDeferredErrorCode,
                    cancellationToken).ConfigureAwait(false);
                leaseKeepalive?.Complete(outcome.Lease);
            }
            else if (!outcome.Deferred)
            {
                await FailLeaseAsync(
                    outcome.Lease,
                    outcome.ErrorCode ?? "METADATA_INVALID_RESPONSE",
                    outcome.ErrorMessage ?? "metadata response missing",
                    leaseKeepalive,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await producer.ConfigureAwait(false);
        await completion.ConfigureAwait(false);
        if (pendingWrites.Count > 0)
        {
            succeeded += await FlushMetadataBatchAsync(
                pendingWrites,
                estimatedWriteBytes,
                leaseKeepalive,
                cancellationToken).ConfigureAwait(false);
        }

        if (succeeded > 0)
        {
            Interlocked.Exchange(ref _progressCacheExpiresUtcTicks, 0);
        }

        return succeeded;
    }

    private static async Task ProduceLeasesAsync(
        IReadOnlyList<MetadataWorkLease> leases,
        ChannelWriter<MetadataWorkLease> writer,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            foreach (var lease in leases)
            {
                await writer.WriteAsync(lease, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(error);
        }
    }

    private async Task ExtractLeasesAsync(
        ChannelReader<MetadataWorkLease> reader,
        ChannelWriter<MetadataExtractionOutcome> writer,
        BackgroundMetadataLeaseKeepalive? leaseKeepalive,
        CancellationToken cancellationToken)
    {
        await foreach (var lease in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            TouchMetadataHeartbeat();
            var extractionClock = Stopwatch.StartNew();
            try
            {
                var outcome = await ExtractLeaseAsync(lease, leaseKeepalive, cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteAsync(outcome, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                extractionClock.Stop();
                _runtimeMetrics.RecordExtraction(extractionClock.Elapsed);
                TouchMetadataHeartbeat();
            }
        }
    }

    private static async Task CompleteResultsAsync(
        IReadOnlyCollection<Task> workers,
        ChannelWriter<MetadataExtractionOutcome> writer)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private async ValueTask<MetadataExtractionOutcome> ExtractLeaseAsync(
        MetadataWorkLease lease,
        BackgroundMetadataLeaseKeepalive? leaseKeepalive,
        CancellationToken cancellationToken)
    {
        if (BackgroundWorkPaused)
        {
            return MetadataExtractionOutcome.Defer(lease);
        }

        var estimatedRead = Math.Min(
            Math.Max(0, lease.EstimatedReadBytes),
            _options.MaximumEstimatedReadBytesPerFile);
        await EnsureBackgroundLeaseCoverageAsync(leaseKeepalive, cancellationToken).ConfigureAwait(false);
        await _governor.WaitForReadAsync(estimatedRead, cancellationToken).ConfigureAwait(false);
        await EnsureBackgroundLeaseCoverageAsync(leaseKeepalive, cancellationToken).ConfigureAwait(false);
        if (BackgroundWorkPaused)
        {
            return MetadataExtractionOutcome.Defer(lease);
        }

        var clock = Stopwatch.StartNew();
        var transportCompleted = false;
        WorkerCircuitPermit? circuitPermit = null;
        try
        {
            if (!_circuitBreaker.TryAcquire(DateTimeOffset.UtcNow, out circuitPermit))
            {
                return MetadataExtractionOutcome.Defer(lease);
            }

            var pipelineLeaseId = Guid.NewGuid();
            var request = new MetadataRequest(
                ContractVersions.MetadataPipe,
                Guid.NewGuid(),
                lease.FullPath,
                ForceRefresh: true,
                TimeoutMilliseconds: _options.HardFileTimeoutMilliseconds,
                WorkClass: MetadataWorkClass.Background,
                PipelineLeaseId: pipelineLeaseId,
                ExpectedFingerprint: lease.Fingerprint,
                ExtractorVersion: ExtractorVersions.Current);
            var response = await _metadata.ExtractAsync(request, cancellationToken)
                .ConfigureAwait(false);
            transportCompleted = true;

            if (response.ProtocolVersion != ContractVersions.MetadataPipe ||
                response.RequestId != request.RequestId ||
                (_metadata is IBrokerHostedMetadataExtractionClient &&
                (response.PipelineLeaseId != pipelineLeaseId ||
                 response.WorkerGenerationNonce == Guid.Empty))
               )
            {
                circuitPermit!.Failure(DateTimeOffset.UtcNow, "METADATA_STALE_WORKER_RESPONSE");
                if (response.WorkerGenerationNonce != Guid.Empty &&
                    _metadata is IMetadataGenerationSupervisor supervisor)
                {
                    await supervisor.RetireGenerationAsync(
                        response.WorkerGenerationNonce,
                        "stale-pipeline-response",
                        cancellationToken).ConfigureAwait(false);
                }
                return MetadataExtractionOutcome.Failure(
                    lease,
                    "METADATA_STALE_WORKER_RESPONSE",
                    "stale worker generation or pipeline lease");
            }

            if (IsInfrastructureFailure(response.ErrorCode))
            {
                circuitPermit!.Failure(DateTimeOffset.UtcNow, response.ErrorCode);
            }
            else
            {
                // A protocol response proves that the worker transport is live,
                // even when this individual file cannot be parsed.
                circuitPermit!.Success(DateTimeOffset.UtcNow);
            }

            if (response.Disposition == MetadataFileDisposition.CloudPlaceholder)
            {
                if (IsCloudPlaceholderRetryExhausted(lease.Attempt))
                {
                    return MetadataExtractionOutcome.ExhaustedDeferred(
                        lease,
                        CloudPlaceholderRetryExhaustedErrorCode);
                }

                return MetadataExtractionOutcome.DeferUntil(
                    lease,
                    DateTimeOffset.UtcNow.Add(CloudPlaceholderRetryDelay(lease.Attempt)),
                    response.ErrorCode ?? CloudPlaceholderDeferredErrorCode);
            }

            if (response.Outcome == MetadataPipelineOutcome.Deferred)
            {
                return MetadataExtractionOutcome.DeferUntil(
                    lease,
                    DateTimeOffset.UtcNow.Add(CloudPlaceholderRetryDelay(lease.Attempt)),
                    response.ErrorCode ?? "METADATA_DEFERRED");
            }

            if (response.Disposition == MetadataFileDisposition.ReparsePoint ||
                response.Outcome == MetadataPipelineOutcome.Unsupported)
            {
                return MetadataExtractionOutcome.Skip(
                    lease,
                    response.ErrorCode ?? "METADATA_SKIPPED_REPARSE_POINT");
            }

            if (response.Outcome == MetadataPipelineOutcome.FileGone)
            {
                return MetadataExtractionOutcome.Skip(
                    lease,
                    response.ErrorCode ?? "FILE_GONE");
            }

            if (response.Outcome == MetadataPipelineOutcome.FileUnreadable)
            {
                return MetadataExtractionOutcome.Failure(
                    lease,
                    response.ErrorCode ?? "FILE_UNREADABLE",
                    response.ChineseErrorMessage ?? "file unreadable");
            }

            var before = response.BeforeEvidence is { } beforeContract
                ? ToStableEvidence(beforeContract)
                : null;
            var after = response.AfterEvidence is { } afterContract
                ? ToStableEvidence(afterContract)
                : null;
            if (response.Outcome == MetadataPipelineOutcome.FileChanged)
            {
                return after is not null || before is not null
                    ? MetadataExtractionOutcome.Changed(lease, after ?? before!)
                    : MetadataExtractionOutcome.Failure(
                        lease,
                        response.ErrorCode ?? "FILE_CHANGED_DURING_READ",
                        "file changed without stable evidence");
            }

            if (response.Metadata is { } metadata &&
                before is not null &&
                after is not null &&
                before.RequestedPathMatchesFinal &&
                after.RequestedPathMatchesFinal &&
                FingerprintMatches(lease.Fingerprint, before) &&
                before.SameObjectAndVersion(after) &&
                after.Matches(metadata.File))
            {
                return MetadataExtractionOutcome.Success(lease, metadata, after);
            }

            return MetadataExtractionOutcome.Failure(
                lease,
                response.ErrorCode ?? "METADATA_INVALID_RESPONSE",
                response.ChineseErrorMessage ?? "metadata response missing");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            circuitPermit?.Failure(DateTimeOffset.UtcNow, "METADATA_HARD_TIMEOUT");
            return MetadataExtractionOutcome.Failure(lease, "METADATA_HARD_TIMEOUT", "hard timeout");
        }
        catch (Exception exception)
        {
            if (!transportCompleted)
            {
                circuitPermit?.Failure(DateTimeOffset.UtcNow, "METADATA_CLIENT_FAILED");
            }
            return MetadataExtractionOutcome.Failure(
                lease,
                "METADATA_CLIENT_FAILED",
                exception.GetType().Name);
        }
        finally
        {
            circuitPermit?.Dispose();
            clock.Stop();
            _governor.RecordExtraction(clock.Elapsed);
        }
    }

    internal static bool IsCloudPlaceholderAttributes(FileAttributes attributes)
        => ClassifyBackgroundFileAttributes(attributes) == BackgroundFileDisposition.CloudPlaceholder;

    internal static BackgroundFileDisposition ClassifyBackgroundFileAttributes(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x0004_0000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x0040_0000;
        if ((attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0)
        {
            return BackgroundFileDisposition.CloudPlaceholder;
        }

        return (attributes & FileAttributes.ReparsePoint) != 0
            ? BackgroundFileDisposition.ReparsePoint
            : BackgroundFileDisposition.Normal;
    }

    private sealed record MetadataExtractionOutcome(
        MetadataWorkLease Lease,
        DocumentMetadata? Metadata,
        StableFileEvidence? Evidence,
        StableFileEvidence? ChangedFile,
        string? ErrorCode,
        string? ErrorMessage,
        bool Deferred,
        DateTimeOffset? DeferredUntilUtc,
        bool TerminalSkipped,
        bool TerminalDeferred = false)
    {
        internal static MetadataExtractionOutcome Success(
            MetadataWorkLease lease,
            DocumentMetadata metadata,
            StableFileEvidence evidence) =>
            new(lease, metadata, evidence, null, null, null, false, null, false);

        internal static MetadataExtractionOutcome Changed(MetadataWorkLease lease, StableFileEvidence file) =>
            new(lease, null, null, file, null, null, false, null, false);

        internal static MetadataExtractionOutcome Failure(MetadataWorkLease lease, string code, string message) =>
            new(lease, null, null, null, code, message, false, null, false);

        internal static MetadataExtractionOutcome Defer(MetadataWorkLease lease) =>
            new(lease, null, null, null, null, null, true, null, false);

        internal static MetadataExtractionOutcome DeferUntil(
            MetadataWorkLease lease,
            DateTimeOffset dueUtc,
            string code) =>
            new(lease, null, null, null, code, null, true, dueUtc, false);

        internal static MetadataExtractionOutcome Skip(MetadataWorkLease lease, string code) =>
            new(lease, null, null, null, code, null, false, null, true);

        internal static MetadataExtractionOutcome ExhaustedDeferred(
            MetadataWorkLease lease,
            string code) =>
            new(lease, null, null, null, code, null, false, null, false, true);
    }

    internal enum BackgroundFileDisposition
    {
        Normal,
        CloudPlaceholder,
        ReparsePoint,
    }

    private async ValueTask<int> FlushMetadataBatchAsync(
        IReadOnlyList<MetadataCommitItem> batch,
        long estimatedWriteBytes,
        BackgroundMetadataLeaseKeepalive? leaseKeepalive,
        CancellationToken cancellationToken)
    {
        var commitClock = Stopwatch.StartNew();
        var committedCount = 0;
        try
        {
            await EnsureBackgroundLeaseCoverageAsync(leaseKeepalive, cancellationToken).ConfigureAwait(false);
            await _governor.WaitForWriteAsync(estimatedWriteBytes, cancellationToken).ConfigureAwait(false);
            await EnsureBackgroundLeaseCoverageAsync(leaseKeepalive, cancellationToken).ConfigureAwait(false);
            leaseKeepalive?.BeginFinalizing(batch.Select(item => item.Lease));
            var committed = await _store.TryCommitMetadataLeaseBatchAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in batch)
            {
                if (committed.Contains(item.Lease.FileId))
                {
                    leaseKeepalive?.Complete(item.Lease);
                }
                else
                {
                    _ = await _store.TryRequeueChangedFileAsync(
                        item.Lease,
                        item.Evidence,
                        cancellationToken).ConfigureAwait(false);
                    leaseKeepalive?.Complete(item.Lease);
                }
            }
            committedCount = committed.Count;
            return committedCount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            foreach (var item in batch)
            {
                await FailLeaseAsync(
                    item.Lease,
                    "METADATA_BATCH_WRITE_FAILED",
                    exception.GetType().Name,
                    leaseKeepalive,
                    cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            commitClock.Stop();
            _runtimeMetrics.RecordCommit(commitClock.Elapsed, batch.Count, committedCount);
        }
    }

    private async ValueTask FailLeaseAsync(
        MetadataWorkLease lease,
        string code,
        string logMessage,
        BackgroundMetadataLeaseKeepalive? leaseKeepalive,
        CancellationToken cancellationToken)
    {
        var seconds = Math.Min(3_600, 15 * Math.Pow(2, Math.Min(lease.Attempt, 8)));
        leaseKeepalive?.BeginFinalizing(lease);
        await _store.CompleteMetadataLeaseAsync(
            lease.FileId,
            lease.LeaseOwner,
            succeeded: false,
            errorCode: code,
            retryDelay: TimeSpan.FromSeconds(seconds),
            cancellationToken).ConfigureAwait(false);
        leaseKeepalive?.Complete(lease);
        await _log.WriteAsync("WARN", code, logMessage, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask EnsureBackgroundLeaseCoverageAsync(
        BackgroundMetadataLeaseKeepalive? leaseKeepalive,
        CancellationToken cancellationToken) =>
        leaseKeepalive is null
            ? ValueTask.CompletedTask
            : leaseKeepalive.EnsureCoverageAsync(cancellationToken);

    // Cloud placeholders are not parse failures.  Preserve their lease-attempt
    // count and use a bounded delay so the worker does not repeatedly inspect
    // a file that the sync provider still reports as unavailable.  Discovery or
    // an interactive demand can still promote the same queue entry sooner.
    internal static TimeSpan CloudPlaceholderRetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 4);
        return TimeSpan.FromMinutes(Math.Min(60, 5 * (1 << exponent)));
    }

    internal static bool IsCloudPlaceholderRetryExhausted(int attempt) =>
        attempt >= MaximumCloudPlaceholderDeferrals;

    private static StableFileEvidence ToStableEvidence(MetadataFileEvidence evidence) =>
        new(
            Path.GetFullPath(evidence.RequestedPath),
            Path.GetFullPath(evidence.FinalPath),
            evidence.Kind,
            evidence.Size,
            evidence.LastWriteTimeUtc,
            evidence.FileIdentity);

    private static bool FingerprintMatches(string fingerprint, StableFileEvidence file)
    {
        return string.Equals(fingerprint, file.Fingerprint(), StringComparison.Ordinal) ||
               string.Equals(
                   fingerprint,
                   $"{file.Size}:{file.LastWriteTimeUtc.ToUniversalTime().Ticks}::{ExtractorVersions.Current}",
                   StringComparison.Ordinal);
    }

    private static long EstimateMetadataBytes(DocumentMetadata metadata)
    {
        var characters = metadata.File.FullPath.Length;
        foreach (var property in metadata.Properties)
        {
            characters += property.Name.Length + property.RawValue.Length + property.ResolvedValue.Length +
                          (property.DisplayValue?.Length ?? 0) + (property.CachedValue?.Length ?? 0) +
                          (property.ConfigurationName?.Length ?? 0);
        }

        return Math.Max(512, characters * 2L);
    }

    /// <summary>
    /// Best-effort working-set trim of THIS Broker process after a long idle
    /// stretch. Trimming never affects correctness; touched pages repage
    /// through soft faults on the next extraction batch.
    /// </summary>
    private static void TryTrimWorkingSet()
    {
        try
        {
            // (IntPtr)(-1) is the current-process pseudo-handle; (-1, -1)
            // asks the memory manager to trim the working set now.
            _ = SetProcessWorkingSetSize((IntPtr)(-1), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // Optional hygiene; never propagate.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    /// <summary>
    /// Conservative idle signal for the Broker's on-demand exit: true only when
    /// the metadata queue is fully drained on an initialized index, no full
    /// discovery pass is running, and no live filesystem hints are waiting for
    /// reconciliation. Any doubt must read as busy so the host never exits
    /// mid-work.
    /// </summary>
    public async ValueTask<bool> IsIdleForBackgroundExitAsync(CancellationToken cancellationToken)
    {
        var progress = await GetCachedMetadataProgressAsync(cancellationToken).ConfigureAwait(false);
        if (progress.PendingCount > 0 || progress.DiscoveredFileCount == 0)
        {
            return false;
        }

        await _discoveryMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return !_fullDiscoverySnapshotInProgress && _deferredDiscoveryHints.Count == 0;
        }
        finally
        {
            _discoveryMutationGate.Release();
        }
    }

    private async ValueTask<MetadataProgressSnapshot> GetCachedMetadataProgressAsync(
        CancellationToken cancellationToken)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var metadataEpoch = _store.MetadataProgressEpoch;
        var cached = Volatile.Read(ref _progressCache);
        if (cached is not null &&
            Interlocked.Read(ref _progressCacheMetadataEpoch) == metadataEpoch &&
            Interlocked.Read(ref _progressCacheExpiresUtcTicks) > nowTicks)
        {
            return cached;
        }

        await _progressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            metadataEpoch = _store.MetadataProgressEpoch;
            cached = Volatile.Read(ref _progressCache);
            if (cached is not null &&
                Interlocked.Read(ref _progressCacheMetadataEpoch) == metadataEpoch &&
                Interlocked.Read(ref _progressCacheExpiresUtcTicks) > nowTicks)
            {
                return cached;
            }

            cached = await _store.GetMetadataProgressAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _progressCache, cached);
            // Bind this cached value to the version observed before its SQL
            // read.  If a discovery batch commits between that observation and
            // this assignment, callers must reject this value and retry; using
            // the post-read version here could otherwise label old counts as a
            // newly published work set.
            Interlocked.Exchange(ref _progressCacheMetadataEpoch, metadataEpoch);
            Interlocked.Exchange(
                ref _progressCacheExpiresUtcTicks,
                DateTimeOffset.UtcNow.AddSeconds(2).UtcTicks);
            return cached;
        }
        finally
        {
            _progressGate.Release();
        }
    }

    private async ValueTask<bool> EnsureStorageReadyAsync(CancellationToken cancellationToken)
    {
        var storage = await _store.GetStorageStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!storage.MetadataWritesPaused)
        {
            await PublishStorageReadyAsync(storage, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var retryAt = Interlocked.Read(ref _nextStorageRecoveryUtcTicks);
        if (retryAt == 0 || retryAt <= now.UtcTicks)
        {
            Interlocked.Exchange(
                ref _nextStorageRecoveryUtcTicks,
                now.AddSeconds(_options.PressureCooldownSeconds).UtcTicks);
            storage = await _store.MaintainStorageAsync(cancellationToken).ConfigureAwait(false);
            if (!storage.MetadataWritesPaused)
            {
                Interlocked.Exchange(ref _nextStorageRecoveryUtcTicks, 0);
                await PublishStorageReadyAsync(storage, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        var pressureReason = storage.PauseReason ?? "UNKNOWN_CAPACITY_PRESSURE";
        var pressureChanged = _capabilities.PublishStoragePressure(new RuntimeCapability(
            CapabilityState.Degraded,
            "INDEX_STORAGE_PRESSURE",
            "索引 WAL 正在等待安全检查点；搜索仍可用，属性写入已自动退避并会重试。",
            new Dictionary<string, string>
            {
                ["reason"] = pressureReason,
                ["walBytes"] = storage.WalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
        if (pressureChanged)
        {
            await _log.WriteAsync(
                "WARN",
                "INDEX_STORAGE_PRESSURE",
                FormatStorageReason("blocked", storage),
                cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async ValueTask PublishStorageReadyAsync(
        IndexStorageStatus storage,
        CancellationToken cancellationToken)
    {
        if (!_capabilities.PublishStorageReady())
        {
            return;
        }

        await _log.WriteAsync(
            "INFO",
            "INDEX_STORAGE_RECOVERED",
            FormatStorageReason("recovered", storage),
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsCapacityPauseReason(string? reason) =>
        reason is "WAL_PRESSURE" or
            "DATABASE_HARD_LIMIT" or
            "DATABASE_PHYSICAL_SAFETY_LIMIT";

    private static string FormatStorageReason(string state, IndexStorageStatus storage) =>
        $"state={state};reason={storage.PauseReason ?? "NONE"};" +
        $"database_bytes={storage.DatabaseBytes};logical_bytes={storage.LogicalDatabaseBytes};" +
        $"reusable_bytes={storage.ReusableDatabaseBytes};wal_bytes={storage.WalBytes};" +
        $"page_size={storage.PageSizeBytes};page_count={storage.PageCount};" +
        $"free_page_count={storage.FreePageCount};maintenance={storage.MaintenancePhase}";

    private static bool IsInfrastructureFailure(string? code) =>
        code is not null &&
        (code.Contains("WORKER", StringComparison.OrdinalIgnoreCase) ||
         code.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
         code.Contains("TRANSPORT", StringComparison.OrdinalIgnoreCase) ||
         code.Contains("BROKER_INTERNAL", StringComparison.OrdinalIgnoreCase));

    private bool BrokerHostedMetadata =>
        _metadata is IBrokerHostedMetadataExtractionClient;

    private void SetDiscoveryService(RuntimeServiceState state, string code)
    {
        Volatile.Write(ref _discoveryServiceState, (int)state);
        Volatile.Write(ref _discoveryServiceCode, code);
        TouchDiscoveryHeartbeat();
    }

    private void SetMetadataService(RuntimeServiceState state, string code)
    {
        Volatile.Write(ref _metadataServiceState, (int)state);
        Volatile.Write(ref _metadataServiceCode, code);
        TouchMetadataHeartbeat();
    }

    private void TouchMetadataHeartbeat() =>
        Interlocked.Exchange(ref _metadataHeartbeatUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

    private void TouchDiscoveryHeartbeat() =>
        Interlocked.Exchange(ref _discoveryHeartbeatUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

    private static RuntimeServiceState EffectiveServiceState(
        RuntimeServiceState state,
        long heartbeatTicks,
        DateTimeOffset now,
        TimeSpan stallThreshold)
    {
        if (state != RuntimeServiceState.Running || heartbeatTicks == 0)
        {
            return state;
        }

        return now.UtcTicks - heartbeatTicks > stallThreshold.Ticks
            ? RuntimeServiceState.Stalled
            : state;
    }

    private static DateTimeOffset ReadHeartbeat(
        ref long heartbeatTicks,
        DateTimeOffset fallback)
    {
        var ticks = Interlocked.Read(ref heartbeatTicks);
        return ticks == 0 ? fallback : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private void SetCapability(RuntimeCapability capability)
    {
        _capabilities.UpdateDiscovery(capability);
    }

    private (string Source, string Code) DiscoverySource()
    {
        if (_discovery is FallbackDiscoveryProvider fallback)
        {
            var code = fallback.LastCapability.Code;
            var source = code.StartsWith("EVERYTHING_", StringComparison.OrdinalIgnoreCase)
                ? "Everything 加速"
                : code.StartsWith("BUILTIN_", StringComparison.OrdinalIgnoreCase)
                    ? "内置扫描"
                    : "正在判定";
            return (source, code);
        }

        return _discovery is BuiltInFileDiscoveryProvider
            ? ("内置扫描", "BUILTIN_DISCOVERY")
            : ("可选发现源", _discovery.GetType().Name);
    }

    internal static string FormatDiscoveryLogMessage(
        RuntimeCapability capability,
        IndexStatus status,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(status);
        var details = capability.Details is null
            ? null
            : capability.Details
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            eventType = "discovery-refresh",
            capabilityState = capability.State.ToString(),
            capabilityCode = capability.Code,
            files = status.PresentFileCount,
            pending = status.PendingMetadataCount,
            generation = status.DiscoveryGeneration,
            discoveryMilliseconds = Math.Round(elapsed.TotalMilliseconds, 1),
            details,
        });
    }

    private static RuntimeCapability Combine(RuntimeCapability discovery, RuntimeCapability metadata)
        => RuntimeCapabilityProjection.Combine(discovery, metadata);

    private static void ValidateOptions(IndexingRuntimeOptions options)
    {
        if (options.DiscoveryRefreshInterval <= TimeSpan.Zero || options.IdleDelay <= TimeSpan.Zero ||
            options.MetadataBatchSize is < 1 or > 100 || options.MaximumFilesPerSecond <= 0 ||
            options.MaximumReadBytesPerSecond <= 0 || options.MaximumWriteBytesPerSecond <= 0 ||
            options.SoftFileTimeoutMilliseconds is < 100 or > 5_000 ||
            options.HardFileTimeoutMilliseconds < options.SoftFileTimeoutMilliseconds ||
            options.HardFileTimeoutMilliseconds > 30_000 || options.WorkerFailureTripCount is < 1 or > 20 ||
            options.WorkerCircuitCooldownSeconds is < 1 or > 86_400 ||
            options.MaximumConcurrentExtractions is < 1 or > 4 ||
            options.PipelineCapacity is < 1 or > 100 ||
            options.MaximumEstimatedReadBytesPerFile is < 64 * 1024 or > 64 * 1024 * 1024 ||
            options.InteractiveThrottleFactor is <= 0 or > 1 ||
            options.InteractiveQuietSeconds is < 1 or > 300 ||
            options.PressureCooldownSeconds is < 1 or > 300 ||
            options.SlowExtractionMilliseconds is < 100 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}

internal readonly record struct MetadataBatchPlan(
    bool IsBackground,
    int LeaseCount,
    int PipelineCapacity,
    int ExtractionConcurrency,
    TimeSpan LeaseDuration,
    TimeSpan CommitMaximumAge);

// The queue lease is a safety boundary, not a parser timeout.  This settings
// object keeps the renewal policy explicit and independently bounded so a
// throttled background batch cannot turn into an unbounded reservation.
internal readonly record struct MetadataLeaseKeepaliveSettings(
    TimeSpan LeaseDuration,
    TimeSpan MaximumLifetime,
    TimeSpan RenewalInterval,
    TimeSpan DeadlineSafetyMargin)
{
    internal void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero || MaximumLifetime <= TimeSpan.Zero ||
            LeaseDuration > MaximumLifetime || MaximumLifetime > TimeSpan.FromMinutes(14) ||
            RenewalInterval <= TimeSpan.Zero || DeadlineSafetyMargin <= TimeSpan.Zero ||
            DeadlineSafetyMargin >= LeaseDuration ||
            DeadlineSafetyMargin >= MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(MetadataLeaseKeepaliveSettings));
        }
    }
}

// The heartbeat owns no parser work and creates no additional extraction
// concurrency.  It only extends rows that are still demonstrably ours while a
// background batch waits for governor admission or a SQLite write.  All state
// transitions are fail-closed: an active row that cannot be renewed cancels the
// batch; a finalizing row may have been atomically removed by its terminal
// write, so it is simply observed until that write reports completion.
internal sealed class BackgroundMetadataLeaseKeepalive
{
    private enum LeaseState
    {
        Active,
        Finalizing,
    }

    private sealed record TrackedLease(MetadataWorkLease Lease, LeaseState State);

    private readonly SqliteIndexStore _store;
    private readonly object _stateGate = new();
    private readonly Dictionary<long, TrackedLease> _leases;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private readonly MetadataLeaseKeepaliveSettings _settings;
    private readonly DateTimeOffset _absoluteDeadlineUtc;
    private readonly DateTimeOffset _renewalDeadlineUtc;
    private readonly CancellationTokenSource _batchLifetime;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _loopLifetime;
    private Task? _loop;
    private Exception? _failure;
    private long _nextRenewalUtcTicks;
    private long _knownLeaseUntilUtcTicks;
    private int _started;
    private int _stopped;

    internal BackgroundMetadataLeaseKeepalive(
        SqliteIndexStore store,
        IReadOnlyList<MetadataWorkLease> leases,
        MetadataLeaseKeepaliveSettings settings,
        DateTimeOffset claimedAtUtc,
        CancellationTokenSource batchLifetime)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(batchLifetime);
        if (leases.Count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(leases));
        }

        settings.Validate();
        _store = store;
        _settings = settings;
        _batchLifetime = batchLifetime;
        _absoluteDeadlineUtc = claimedAtUtc.Add(settings.MaximumLifetime);
        _renewalDeadlineUtc = _absoluteDeadlineUtc.Subtract(settings.DeadlineSafetyMargin);
        _leases = new Dictionary<long, TrackedLease>(leases.Count);
        foreach (var lease in leases)
        {
            ArgumentNullException.ThrowIfNull(lease);
            if (!_leases.TryAdd(lease.FileId, new TrackedLease(lease, LeaseState.Active)))
            {
                throw new ArgumentException("Lease keepalive requires distinct file IDs.", nameof(leases));
            }
        }

        var initialUntil = leases.Min(lease => lease.LeaseUntilUtc);
        if (initialUntil <= claimedAtUtc || initialUntil > _absoluteDeadlineUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leases));
        }

        Interlocked.Exchange(ref _knownLeaseUntilUtcTicks, initialUntil.UtcTicks);
        Interlocked.Exchange(
            ref _nextRenewalUtcTicks,
            Math.Min(
                initialUntil.UtcTicks - settings.DeadlineSafetyMargin.Ticks,
                claimedAtUtc.Add(settings.RenewalInterval).UtcTicks));
    }

    internal void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The metadata lease keepalive has already started.");
        }

        _loopLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            _stop.Token,
            _batchLifetime.Token);
        _loop = RunAsync(_loopLifetime.Token);
    }

    internal async ValueTask EnsureCoverageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailed();
        await RenewIfNeededAsync(force: false, cancellationToken).ConfigureAwait(false);
    }

    internal void BeginFinalizing(MetadataWorkLease lease) =>
        BeginFinalizing([lease]);

    internal void BeginFinalizing(IEnumerable<MetadataWorkLease> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ThrowIfFailed();
        lock (_stateGate)
        {
            foreach (var lease in leases)
            {
                if (!_leases.TryGetValue(lease.FileId, out var tracked))
                {
                    throw new InvalidOperationException("The metadata lease is no longer tracked.");
                }

                EnsureSameLease(tracked.Lease, lease);
                _leases[lease.FileId] = tracked with { State = LeaseState.Finalizing };
            }
        }
    }

    internal void Complete(MetadataWorkLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_stateGate)
        {
            if (_leases.TryGetValue(lease.FileId, out var tracked))
            {
                EnsureSameLease(tracked.Lease, lease);
                _leases.Remove(lease.FileId);
            }
        }
    }

    internal async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _stop.Cancel();
        }

        var loop = Volatile.Read(ref _loop);
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        _loopLifetime?.Dispose();
    }

    internal int ActiveLeaseCountForTesting
    {
        get
        {
            lock (_stateGate)
            {
                return _leases.Count;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                ThrowIfRenewalDeadlineExceeded(now);
                var next = new DateTimeOffset(
                    Interlocked.Read(ref _nextRenewalUtcTicks),
                    TimeSpan.Zero);
                if (next > now)
                {
                    await Task.Delay(next - now, cancellationToken).ConfigureAwait(false);
                }

                await RenewIfNeededAsync(force: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop/caller cancellation is a normal lifecycle transition.  The
            // owner releases the leases only after this task has been observed.
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    private async ValueTask RenewIfNeededAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailed();
        var now = DateTimeOffset.UtcNow;
        ThrowIfRenewalDeadlineExceeded(now);
        var knownUntil = new DateTimeOffset(
            Interlocked.Read(ref _knownLeaseUntilUtcTicks),
            TimeSpan.Zero);
        var next = new DateTimeOffset(
            Interlocked.Read(ref _nextRenewalUtcTicks),
            TimeSpan.Zero);
        if (!force && next > now && knownUntil - now > _settings.DeadlineSafetyMargin)
        {
            return;
        }

        await _renewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed();
            now = DateTimeOffset.UtcNow;
            ThrowIfRenewalDeadlineExceeded(now);
            knownUntil = new DateTimeOffset(
                Interlocked.Read(ref _knownLeaseUntilUtcTicks),
                TimeSpan.Zero);
            next = new DateTimeOffset(
                Interlocked.Read(ref _nextRenewalUtcTicks),
                TimeSpan.Zero);
            if (!force && next > now && knownUntil - now > _settings.DeadlineSafetyMargin)
            {
                return;
            }

            MetadataWorkLease[] snapshot;
            lock (_stateGate)
            {
                snapshot = _leases.Values.Select(tracked => tracked.Lease).ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            var requestedUntilUtc = now.Add(_settings.LeaseDuration);
            var renewUntilUtc = requestedUntilUtc <= _absoluteDeadlineUtc
                ? requestedUntilUtc
                : _absoluteDeadlineUtc;
            if (renewUntilUtc <= now)
            {
                ThrowIfRenewalDeadlineExceeded(now);
                throw new InvalidOperationException("The metadata lease renewal deadline has elapsed.");
            }

            var renewed = await _store.RenewMetadataLeasesAsync(
                    snapshot,
                    renewUntilUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            List<long>? missingActive = null;
            lock (_stateGate)
            {
                foreach (var lease in snapshot)
                {
                    if (renewed.Contains(lease.FileId) ||
                        !_leases.TryGetValue(lease.FileId, out var tracked) ||
                        tracked.State == LeaseState.Finalizing)
                    {
                        continue;
                    }

                    (missingActive ??= []).Add(lease.FileId);
                }
            }

            if (missingActive is { Count: > 0 })
            {
                var exception = new InvalidOperationException(
                    $"Metadata lease renewal lost active file IDs: {string.Join(',', missingActive)}.");
                RecordFailure(exception);
                throw exception;
            }

            var updatedKnownUntilTicks = Math.Max(knownUntil.UtcTicks, renewUntilUtc.UtcTicks);
            Interlocked.Exchange(ref _knownLeaseUntilUtcTicks, updatedKnownUntilTicks);
            var updatedKnownUntilUtc = new DateTimeOffset(updatedKnownUntilTicks, TimeSpan.Zero);
            Interlocked.Exchange(
                ref _nextRenewalUtcTicks,
                Math.Min(
                    Math.Min(
                        now.Add(_settings.RenewalInterval).UtcTicks,
                        updatedKnownUntilUtc.Subtract(_settings.DeadlineSafetyMargin).UtcTicks),
                    _renewalDeadlineUtc.UtcTicks));
        }
        finally
        {
            _renewGate.Release();
        }
    }

    private void ThrowIfRenewalDeadlineExceeded(DateTimeOffset now)
    {
        if (now < _renewalDeadlineUtc)
        {
            return;
        }

        var exception = new TimeoutException("The bounded metadata lease renewal lifetime has elapsed.");
        RecordFailure(exception);
        throw exception;
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new InvalidOperationException("The metadata lease keepalive failed.", failure);
        }
    }

    private void RecordFailure(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _failure, exception, null) is null)
        {
            _batchLifetime.Cancel();
        }
    }

    private static void EnsureSameLease(MetadataWorkLease expected, MetadataWorkLease actual)
    {
        if (expected.FileId != actual.FileId || expected.Attempt != actual.Attempt ||
            !string.Equals(expected.LeaseOwner, actual.LeaseOwner, StringComparison.Ordinal) ||
            !string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The metadata lease identity changed while it was tracked.");
        }
    }
}

internal readonly record struct RuntimePhaseMetricsSnapshot(
    long ClaimOperations,
    long ClaimedItems,
    long ClaimTicks,
    long ExtractionOperations,
    long ExtractionTicks,
    long CommitOperations,
    long CommitItems,
    long CommittedItems,
    long CommitTicks,
    long StorageSnapshotChecks,
    long StorageFullProbes,
    long StorageCacheHits,
    long StorageSnapshotTicks,
    long IdleDelayOperations,
    long IdleDelayTicks)
{
    internal bool HasData =>
        ClaimOperations != 0 ||
        ExtractionOperations != 0 ||
        CommitOperations != 0 ||
        StorageSnapshotChecks != 0 ||
        IdleDelayOperations != 0;

    internal string ToLogMessage()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(
            ';',
            $"claim_ops={ClaimOperations}",
            $"claimed={ClaimedItems}",
            $"claim_ms={TimeSpan.FromTicks(ClaimTicks).TotalMilliseconds.ToString("F1", culture)}",
            $"extract_ops={ExtractionOperations}",
            $"extract_ms={TimeSpan.FromTicks(ExtractionTicks).TotalMilliseconds.ToString("F1", culture)}",
            $"commit_ops={CommitOperations}",
            $"commit_items={CommitItems}",
            $"committed={CommittedItems}",
            $"commit_ms={TimeSpan.FromTicks(CommitTicks).TotalMilliseconds.ToString("F1", culture)}",
            $"storage_checks={StorageSnapshotChecks}",
            $"storage_full_probes={StorageFullProbes}",
            $"storage_cache_hits={StorageCacheHits}",
            $"storage_snapshot_ms={TimeSpan.FromTicks(StorageSnapshotTicks).TotalMilliseconds.ToString("F1", culture)}",
            $"idle_delays={IdleDelayOperations}",
            $"idle_delay_ms={TimeSpan.FromTicks(IdleDelayTicks).TotalMilliseconds.ToString("F1", culture)}");
    }
}

internal sealed class RuntimePhaseMetrics
{
    private long _claimOperations;
    private long _claimedItems;
    private long _claimTicks;
    private long _extractionOperations;
    private long _extractionTicks;
    private long _commitOperations;
    private long _commitItems;
    private long _committedItems;
    private long _commitTicks;
    private long _storageSnapshotChecks;
    private long _storageFullProbes;
    private long _storageCacheHits;
    private long _storageSnapshotTicks;
    private long _idleDelayOperations;
    private long _idleDelayTicks;
    private long _logIntervalTicks = TimeSpan.FromSeconds(30).Ticks;
    private long _nextLogUtcTicks = DateTimeOffset.UtcNow.AddSeconds(30).UtcTicks;

    internal TimeSpan LogInterval
    {
        set
        {
            if (value < TimeSpan.Zero || value > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var intervalTicks = Math.Max(1, value.Ticks);
            Interlocked.Exchange(ref _logIntervalTicks, intervalTicks);
            Interlocked.Exchange(
                ref _nextLogUtcTicks,
                DateTimeOffset.UtcNow.UtcTicks + intervalTicks);
        }
    }

    internal void RecordClaim(TimeSpan elapsed, int claimedItems)
    {
        Interlocked.Increment(ref _claimOperations);
        Interlocked.Add(ref _claimedItems, Math.Max(0, claimedItems));
        Interlocked.Add(ref _claimTicks, Math.Max(0, elapsed.Ticks));
    }

    internal void RecordExtraction(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _extractionOperations);
        Interlocked.Add(ref _extractionTicks, Math.Max(0, elapsed.Ticks));
    }

    internal void RecordCommit(TimeSpan elapsed, int attemptedItems, int committedItems)
    {
        Interlocked.Increment(ref _commitOperations);
        Interlocked.Add(ref _commitItems, Math.Max(0, attemptedItems));
        Interlocked.Add(ref _committedItems, Math.Max(0, committedItems));
        Interlocked.Add(ref _commitTicks, Math.Max(0, elapsed.Ticks));
    }

    internal void RecordStorageSnapshot(
        TimeSpan elapsed,
        int checks,
        long fullProbes,
        long cacheHits)
    {
        Interlocked.Add(ref _storageSnapshotChecks, Math.Max(0, checks));
        Interlocked.Add(ref _storageFullProbes, Math.Max(0, fullProbes));
        Interlocked.Add(ref _storageCacheHits, Math.Max(0, cacheHits));
        Interlocked.Add(ref _storageSnapshotTicks, Math.Max(0, elapsed.Ticks));
    }

    internal void RecordIdleDelay(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _idleDelayOperations);
        Interlocked.Add(ref _idleDelayTicks, Math.Max(0, elapsed.Ticks));
    }

    internal RuntimePhaseMetricsSnapshot Snapshot() =>
        new(
            Interlocked.Read(ref _claimOperations),
            Interlocked.Read(ref _claimedItems),
            Interlocked.Read(ref _claimTicks),
            Interlocked.Read(ref _extractionOperations),
            Interlocked.Read(ref _extractionTicks),
            Interlocked.Read(ref _commitOperations),
            Interlocked.Read(ref _commitItems),
            Interlocked.Read(ref _committedItems),
            Interlocked.Read(ref _commitTicks),
            Interlocked.Read(ref _storageSnapshotChecks),
            Interlocked.Read(ref _storageFullProbes),
            Interlocked.Read(ref _storageCacheHits),
            Interlocked.Read(ref _storageSnapshotTicks),
            Interlocked.Read(ref _idleDelayOperations),
            Interlocked.Read(ref _idleDelayTicks));

    internal bool TryDrainForLog(
        DateTimeOffset now,
        out RuntimePhaseMetricsSnapshot snapshot)
    {
        var nowTicks = now.UtcTicks;
        var next = Interlocked.Read(ref _nextLogUtcTicks);
        if (next > nowTicks ||
            Interlocked.CompareExchange(
                ref _nextLogUtcTicks,
                nowTicks + Interlocked.Read(ref _logIntervalTicks),
                next) != next)
        {
            snapshot = default;
            return false;
        }

        snapshot = new RuntimePhaseMetricsSnapshot(
            Interlocked.Exchange(ref _claimOperations, 0),
            Interlocked.Exchange(ref _claimedItems, 0),
            Interlocked.Exchange(ref _claimTicks, 0),
            Interlocked.Exchange(ref _extractionOperations, 0),
            Interlocked.Exchange(ref _extractionTicks, 0),
            Interlocked.Exchange(ref _commitOperations, 0),
            Interlocked.Exchange(ref _commitItems, 0),
            Interlocked.Exchange(ref _committedItems, 0),
            Interlocked.Exchange(ref _commitTicks, 0),
            Interlocked.Exchange(ref _storageSnapshotChecks, 0),
            Interlocked.Exchange(ref _storageFullProbes, 0),
            Interlocked.Exchange(ref _storageCacheHits, 0),
            Interlocked.Exchange(ref _storageSnapshotTicks, 0),
            Interlocked.Exchange(ref _idleDelayOperations, 0),
            Interlocked.Exchange(ref _idleDelayTicks, 0));
        return snapshot.HasData;
    }
}

internal sealed class ResourceGovernor
{
    private readonly double _maximumFilesPerSecond;
    private readonly long _maximumReadBytesPerSecond;
    private readonly long _maximumWriteBytesPerSecond;
    private readonly double _interactiveThrottleFactor;
    private readonly TimeSpan _interactiveQuietPeriod;
    private readonly TimeSpan _pressureCooldown;
    private readonly TimeSpan _slowExtraction;
    private readonly SystemPressureSampler _systemPressure = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;
    private DateTimeOffset _nextWrite = DateTimeOffset.MinValue;
    private long _interactiveUntilUtcTicks;
    private long _pressureUntilUtcTicks;

    internal ResourceGovernor(IndexingRuntimeOptions options)
    {
        _maximumFilesPerSecond = options.MaximumFilesPerSecond;
        _maximumReadBytesPerSecond = options.MaximumReadBytesPerSecond;
        _maximumWriteBytesPerSecond = options.MaximumWriteBytesPerSecond;
        _interactiveThrottleFactor = options.InteractiveThrottleFactor;
        _interactiveQuietPeriod = TimeSpan.FromSeconds(options.InteractiveQuietSeconds);
        _pressureCooldown = TimeSpan.FromSeconds(options.PressureCooldownSeconds);
        _slowExtraction = TimeSpan.FromMilliseconds(options.SlowExtractionMilliseconds);
    }

    internal double CurrentFilesPerSecond => _maximumFilesPerSecond * CurrentScale();

    internal long CurrentReadBytesPerSecond =>
        Math.Max(1, (long)(_maximumReadBytesPerSecond * CurrentScale()));

    internal void NotifyInteractiveActivity() =>
        ExtendUntil(ref _interactiveUntilUtcTicks, DateTimeOffset.UtcNow.Add(_interactiveQuietPeriod).UtcTicks);

    internal void RecordExtraction(TimeSpan elapsed)
    {
        if (elapsed >= _slowExtraction || _systemPressure.IsBusy())
        {
            ExtendUntil(ref _pressureUntilUtcTicks, DateTimeOffset.UtcNow.Add(_pressureCooldown).UtcTicks);
        }
    }

    internal int RecommendedConcurrency(int maximum)
    {
        if (_systemPressure.IsBusy())
        {
            ExtendUntil(ref _pressureUntilUtcTicks, DateTimeOffset.UtcNow.Add(_pressureCooldown).UtcTicks);
        }

        return CurrentScale() < 1 ? 1 : maximum;
    }

    internal ValueTask WaitForReadAsync(long estimatedBytes, CancellationToken cancellationToken)
    {
        var scale = CurrentScale();
        var filesPerSecond = _maximumFilesPerSecond * scale;
        var bytesPerSecond = _maximumReadBytesPerSecond * scale;
        return WaitAsync(
            isRead: true,
            TimeSpan.FromTicks(Math.Max(
                TimeSpan.FromSeconds(1d / filesPerSecond).Ticks,
                TimeSpan.FromSeconds(Math.Max(0, estimatedBytes) / bytesPerSecond).Ticks)),
            cancellationToken);
    }

    internal ValueTask WaitForWriteAsync(long estimatedBytes, CancellationToken cancellationToken)
    {
        var bytesPerSecond = _maximumWriteBytesPerSecond * CurrentScale();
        return WaitAsync(
            isRead: false,
            TimeSpan.FromSeconds(Math.Max(0, estimatedBytes) / bytesPerSecond),
            cancellationToken);
    }

    private async ValueTask WaitAsync(bool isRead, TimeSpan reservation, CancellationToken cancellationToken)
    {
        var gate = isRead ? _readGate : _writeGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var next = isRead ? _nextRead : _nextWrite;
            if (next > now)
            {
                await Task.Delay(next - now, cancellationToken).ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
            }

            if (isRead)
            {
                _nextRead = now.Add(reservation);
            }
            else
            {
                _nextWrite = now.Add(reservation);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private double CurrentScale()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (Interlocked.Read(ref _interactiveUntilUtcTicks) > now)
        {
            return _interactiveThrottleFactor;
        }

        return Interlocked.Read(ref _pressureUntilUtcTicks) > now ? 0.5 : 1;
    }

    private static void ExtendUntil(ref long target, long requested)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (current >= requested || Interlocked.CompareExchange(ref target, requested, current) == current)
            {
                return;
            }
        }
    }
}

internal sealed class SystemPressureSampler
{
    private readonly object _gate = new();
    private long _lastSampleTimestamp;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _lastBusy;

    internal bool IsBusy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (_gate)
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (_lastSampleTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastSampleTimestamp, timestamp) < TimeSpan.FromSeconds(1))
            {
                return _lastBusy;
            }

            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                return false;
            }

            var idle = idleTime.Value;
            var kernel = kernelTime.Value;
            var user = userTime.Value;
            if (_lastSampleTimestamp != 0)
            {
                var total = (kernel - _lastKernel) + (user - _lastUser);
                var idleDelta = idle - _lastIdle;
                _lastBusy = total > 0 && 1d - Math.Min(1d, idleDelta / (double)total) >= 0.85;
            }

            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _lastSampleTimestamp = timestamp;
            return _lastBusy;
        }
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        internal ulong Value => ((ulong)_high << 32) | _low;
    }
}

internal enum WorkerCircuitState
{
    Closed,
    Open,
    HalfOpen,
}

internal sealed record WorkerCircuitSnapshot(
    WorkerCircuitState State,
    DateTimeOffset? RetryAtUtc);

internal sealed class WorkerCircuitBreaker(int tripCount, TimeSpan cooldown)
{
    private static readonly TimeSpan StartupFailureCoalescingWindow = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private WorkerCircuitState _state = WorkerCircuitState.Closed;
    private long _epoch;
    private long _nextPermitId;
    private long _activeHalfOpenPermitId;
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil;
    private DateTimeOffset _lastStartupUnavailableFailure;

    internal bool HasExecutionOpportunity(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _state == WorkerCircuitState.Closed ||
                   (_state == WorkerCircuitState.Open && _openUntil <= now);
        }
    }

    internal bool TryAcquire(DateTimeOffset now, out WorkerCircuitPermit permit)
    {
        lock (_gate)
        {
            if (_state == WorkerCircuitState.Closed)
            {
                permit = NewPermit(WorkerCircuitPermitKind.Closed);
                return true;
            }

            if (_state != WorkerCircuitState.Open || _openUntil > now)
            {
                permit = null!;
                return false;
            }

            // Admit exactly one half-open probe. Other leased work is released
            // and remains recoverable until this probe proves the worker live.
            _state = WorkerCircuitState.HalfOpen;
            _epoch++;
            permit = NewPermit(WorkerCircuitPermitKind.HalfOpen);
            _activeHalfOpenPermitId = permit.PermitId;
            return true;
        }
    }

    internal WorkerCircuitSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return new WorkerCircuitSnapshot(
                _state,
                _state == WorkerCircuitState.Closed
                    ? null
                    : _openUntil > now
                        ? _openUntil
                        : now);
        }
    }

    internal void Complete(
        long epoch,
        long permitId,
        WorkerCircuitPermitKind kind,
        WorkerCircuitCompletion completion,
        DateTimeOffset now,
        string? code)
    {
        lock (_gate)
        {
            if (epoch != _epoch)
            {
                return;
            }

            if (kind == WorkerCircuitPermitKind.HalfOpen)
            {
                if (_state != WorkerCircuitState.HalfOpen || permitId != _activeHalfOpenPermitId)
                {
                    return;
                }

                _activeHalfOpenPermitId = 0;
                _epoch++;
                if (completion == WorkerCircuitCompletion.Success)
                {
                    _state = WorkerCircuitState.Closed;
                    _consecutiveFailures = 0;
                    _openUntil = default;
                    _lastStartupUnavailableFailure = default;
                }
                else
                {
                    // Failure and abandonment both reopen. A canceled half-open
                    // request can never wedge the breaker indefinitely.
                    _state = WorkerCircuitState.Open;
                    _openUntil = now.Add(cooldown);
                }
                return;
            }

            if (_state != WorkerCircuitState.Closed || completion == WorkerCircuitCompletion.Abandoned)
            {
                return;
            }

            if (completion == WorkerCircuitCompletion.Success)
            {
                _consecutiveFailures = 0;
                _lastStartupUnavailableFailure = default;
                return;
            }

            // A single failed startup is observed by every concurrent lease.
            // Count that bounded recovery incident once, while still counting
            // independent hard timeouts and crashes individually.
            if (string.Equals(code, "METADATA_WORKER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            {
                if (_lastStartupUnavailableFailure != default &&
                    now - _lastStartupUnavailableFailure < StartupFailureCoalescingWindow)
                {
                    return;
                }

                _lastStartupUnavailableFailure = now;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= tripCount)
            {
                _state = WorkerCircuitState.Open;
                _openUntil = now.Add(cooldown);
                _epoch++;
            }
        }
    }

    private WorkerCircuitPermit NewPermit(WorkerCircuitPermitKind kind) =>
        new(this, _epoch, ++_nextPermitId, kind);
}

internal enum WorkerCircuitPermitKind
{
    Closed,
    HalfOpen,
}

internal enum WorkerCircuitCompletion
{
    Success,
    Failure,
    Abandoned,
}

internal sealed class WorkerCircuitPermit : IDisposable
{
    private readonly WorkerCircuitBreaker _owner;
    private readonly long _epoch;
    private readonly WorkerCircuitPermitKind _kind;
    private int _completed;

    internal WorkerCircuitPermit(
        WorkerCircuitBreaker owner,
        long epoch,
        long permitId,
        WorkerCircuitPermitKind kind)
    {
        _owner = owner;
        _epoch = epoch;
        PermitId = permitId;
        _kind = kind;
    }

    internal long PermitId { get; }

    internal void Success(DateTimeOffset now) =>
        Complete(WorkerCircuitCompletion.Success, now, code: null);

    internal void Failure(DateTimeOffset now, string? code) =>
        Complete(WorkerCircuitCompletion.Failure, now, code);

    internal void Abandon(DateTimeOffset now) =>
        Complete(WorkerCircuitCompletion.Abandoned, now, code: null);

    public void Dispose() => Abandon(DateTimeOffset.UtcNow);

    private void Complete(WorkerCircuitCompletion completion, DateTimeOffset now, string? code)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.Complete(_epoch, PermitId, _kind, completion, now, code);
        }
    }
}
