using System.Runtime.CompilerServices;
using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Indexer;

/// <summary>
/// Prefers Everything as the authoritative file-discovery source. A successful
/// paged Everything snapshot is committed as a complete snapshot and never
/// triggers an MFT or built-in full reconciliation. MFT and built-in discovery
/// are used only after the Everything probe reports unavailability or degradation.
/// When Everything is available per probe but a query subsequently fails, the
/// exception propagates so the caller preserves the old committed cache.
/// </summary>
public sealed class FallbackDiscoveryProvider :
    IIncrementalFileDiscoveryProvider,
    IPersistedDiscoveryRootSink,
    IJournalIncrementalDiscoveryProvider,
    IDiscoveryProgressSource,
    IAsyncDisposable
{
    private readonly IFileDiscoveryProvider? _accelerator;
    private readonly IFileDiscoveryProvider _builtIn;
    private readonly IIncrementalFileDiscoveryProvider? _incrementalBuiltIn;
    private readonly IIncrementalFileDiscoveryProvider? _mftProvider;
    private readonly IJournalIncrementalDiscoveryProvider? _journalProvider;
    private readonly string? _acceleratorConstructionError;
    private RuntimeCapability _lastCapability = new(
        CapabilityState.Degraded,
        "DISCOVERY_INITIALIZING",
        "文件发现正在初始化。");
    private DiscoveryScanReport _lastReport = DiscoveryScanReport.Empty;
    private object? _activeProgressSource;
    private DiscoveryJournalCheckpoint? _snapshotCheckpoint;
    private bool _disposed;

    public FallbackDiscoveryProvider(
        IFileDiscoveryProvider? accelerator,
        IFileDiscoveryProvider builtIn,
        string? acceleratorConstructionError = null,
        IIncrementalFileDiscoveryProvider? mftProvider = null)
    {
        _accelerator = accelerator;
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _incrementalBuiltIn = builtIn as IIncrementalFileDiscoveryProvider;
        _mftProvider = mftProvider;
        _journalProvider = mftProvider as IJournalIncrementalDiscoveryProvider;
        _acceleratorConstructionError = acceleratorConstructionError;
    }

    public RuntimeCapability LastCapability => Volatile.Read(ref _lastCapability);

    public DiscoveryScanReport LastReport => Volatile.Read(ref _lastReport);

    public DiscoveryJournalCheckpoint? SnapshotCheckpoint => Volatile.Read(ref _snapshotCheckpoint);

    public FileDiscoveryProgressSnapshot DiscoveryProgress => GetDiscoveryProgress();

    public TimeSpan RecommendedReconciliationInterval =>
        _mftProvider?.RecommendedReconciliationInterval ??
        _incrementalBuiltIn?.RecommendedReconciliationInterval ??
        TimeSpan.FromHours(6);

    public void RememberPreviouslyIndexedRoots(IEnumerable<string> roots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(roots);
        if (_builtIn is IPersistedDiscoveryRootSink sink)
        {
            sink.RememberPreviouslyIndexedRoots(roots);
        }
    }

    /// <summary>
    /// Creates the normal production chain. Failure to construct the optional
    /// IPC accelerator is converted into built-in-only operation.
    /// </summary>
    public static FallbackDiscoveryProvider CreateDefault(BuiltInDiscoveryOptions? options = null)
    {
        IFileDiscoveryProvider? accelerator = null;
        string? constructionError = null;
        try
        {
            accelerator = new EverythingDiscoveryProvider();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            constructionError = exception.GetType().Name;
        }

        return new FallbackDiscoveryProvider(
            accelerator,
            new BuiltInFileDiscoveryProvider(options),
            constructionError,
            new NtfsMftUsnDiscoveryProvider(new WindowsNtfsChangeSource()));
    }

    public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var acceleratorCapability = await SafeProbeAsync(
            _accelerator,
            "EVERYTHING_ACCELERATOR_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);

        // Everything is the preferred authority. Avoid probing built-in and
        // MFT sources while it is available; those probes can enumerate drives
        // or read volume state and must stay off the Everything fast path.
        if (acceleratorCapability.State == CapabilityState.Available)
        {
            SetLastCapability(acceleratorCapability);
            return LastCapability;
        }

        var builtInCapability = await SafeProbeAsync(
            _builtIn,
            "BUILTIN_DISCOVERY_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);
        var mftCapability = await SafeProbeAsync(
            _mftProvider,
            "NTFS_MFT_USN_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);

        // An already indexed root being offline is a deletion-safety condition,
        // not a reason to trust a fallback snapshot which also omits it.
        if (builtInCapability.Code == "BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE")
        {
            SetLastCapability(builtInCapability);
            return LastCapability;
        }

        if (mftCapability.State == CapabilityState.Available &&
            CanUseAcceleratorForAllRoots(builtInCapability))
        {
            var mftSelected = mftCapability with
            {
                Details = MergeDetails(
                    mftCapability.Details,
                    new Dictionary<string, string>
                    {
                        ["acceleratorCode"] = acceleratorCapability.Code,
                        ["fallbackReason"] = acceleratorCapability.Code,
                    }),
            };
            SetLastCapability(mftSelected);
            return LastCapability;
        }

        if (builtInCapability.State == CapabilityState.Available)
        {
            var details = new Dictionary<string, string>
            {
                ["acceleratorCode"] = acceleratorCapability.Code,
                ["mftCode"] = mftCapability.Code,
                ["fallbackReason"] = acceleratorCapability.Code,
            };
            if (_acceleratorConstructionError is not null)
            {
                details["acceleratorConstructionError"] = _acceleratorConstructionError;
            }

            var fallbackReady = new RuntimeCapability(
                CapabilityState.Available,
                "BUILTIN_DISCOVERY_READY",
                "未使用 Everything；内置文件发现已就绪。",
                details);
            SetLastCapability(fallbackReady);
            return LastCapability;
        }

        var unavailable = new RuntimeCapability(
            CapabilityState.Degraded,
            "ALL_DISCOVERY_SOURCES_UNAVAILABLE",
            "Everything 与内置文件发现当前均不可用；保留已有缓存。",
            new Dictionary<string, string>
            {
                ["acceleratorCode"] = acceleratorCapability.Code,
                ["mftCode"] = mftCapability.Code,
                ["builtInCode"] = builtInCapability.Code,
                ["fallbackReason"] = acceleratorCapability.Code,
            });
        SetLastCapability(unavailable);
        return LastCapability;
    }

    public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in DiscoverEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry.Candidate;
        }
    }

    public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _snapshotCheckpoint, null);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acceleratedReport = new ScanReportBuilder(startedAtUtc);
        Exception? acceleratorFailure = null;
        var acceleratorCompleted = false;
        DiscoveryScanReport? mftReport = null;
        IReadOnlyList<DiscoveryRootState>? rootsMissingMftCoverage = null;
        IReadOnlyList<DiscoveryRootState>? requiredRoots = null;
        DiscoveryJournalCheckpoint? preSnapshotCheckpoint = null;
        var acceleratorCapability = await SafeProbeAsync(
            _accelerator,
            "EVERYTHING_ACCELERATOR_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);

        if (acceleratorCapability.State == CapabilityState.Available)
        {
            Volatile.Write(ref _activeProgressSource, _accelerator);
            if (_builtIn is BuiltInFileDiscoveryProvider concreteBuiltIn)
            {
                foreach (var root in concreteBuiltIn.GetRootStatesForAccelerator(cancellationToken)
                             .Where(static root => root.IsOnline))
                {
                    acceleratedReport.AddRoot(root.RootPath, root.Kind);
                }
            }

            var enumerator = _accelerator!.DiscoverAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    FileCandidate candidate;
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        candidate = moved ? enumerator.Current : null!;
                        if (moved && !DiscoveryPath.IsExcludedSystemPath(candidate.FullPath))
                        {
                            ValidateAcceleratedCandidate(candidate);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsAcceleratorFailure(exception))
                    {
                        if (acceleratorCapability.State == CapabilityState.Available)
                        {
                            SetLastCapability(new RuntimeCapability(
                                CapabilityState.Available,
                                "EVERYTHING_QUERY_FAILED",
                                "Everything query failed while available; preserving cache."));
                            throw;
                        }

                        acceleratorFailure = exception;
                        break;
                    }

                    if (!moved)
                    {
                        // Everything completed its paged snapshot. The
                        // successful result is authoritative and must not
                        // trigger a built-in or MFT full reconciliation.
                        acceleratorCompleted = true;
                        break;
                    }

                    if (DiscoveryPath.IsExcludedSystemPath(candidate.FullPath))
                    {
                        continue;
                    }

                    var enriched = CreatePortableEntry(candidate);
                    if (!seen.Add(enriched.NormalizedPath))
                    {
                        continue;
                    }

                    acceleratedReport.Add(enriched);
                    yield return enriched;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (acceleratorCompleted)
            {
                // The successful Everything snapshot is committed directly.
                // No MFT checkpoint is captured or published on this path, so
                // a later refresh with Everything available still returns to
                // Everything instead of continuing USN deltas.
                Volatile.Write(ref _lastReport, acceleratedReport.Build());
                Volatile.Write(ref _snapshotCheckpoint, null);
                SetLastCapability(acceleratorCapability);
                yield break;
            }
        }

        // Everything did not produce an authoritative snapshot. Built-in and
        // MFT probing happens only on this fallback path.
        var builtInCapability = await SafeProbeAsync(
            _builtIn,
            "BUILTIN_DISCOVERY_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);
        requiredRoots ??= GetRequiredRootSnapshot(cancellationToken);
        var mftCapability = await SafeProbeAsync(
            _mftProvider,
            "NTFS_MFT_USN_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);
        preSnapshotCheckpoint ??= _journalProvider is null
            ? null
            : await _journalProvider.CaptureCheckpointAsync(cancellationToken).ConfigureAwait(false);

        if (builtInCapability.Code == "BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE")
        {
            // Let the built-in provider produce its detailed incomplete report
            // below. Accelerated items already streamed remain a merge-only
            // partial pass; the incomplete report disables deletion.
            acceleratorFailure = new IOException("known discovery root offline");
        }

        // A root can disappear between the initial root snapshot and this
        // selection point.  MFT coverage for C:/D: is never authority to
        // publish a complete snapshot while a previously indexed E: (or other
        // root) is offline, so force the built-in partial-report path instead.
        else if (_mftProvider is not null &&
            mftCapability.State == CapabilityState.Available &&
            CanUseMftForKnownRoots(requiredRoots, builtInCapability))
        {
            Volatile.Write(ref _activeProgressSource, _mftProvider);
            var mftEnumerator = _mftProvider.DiscoverEntriesAsync(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var mftCompleted = false;
            try
            {
                while (true)
                {
                    DiscoveredFile entry;
                    bool moved;
                    try
                    {
                        moved = await mftEnumerator.MoveNextAsync().ConfigureAwait(false);
                        entry = moved ? mftEnumerator.Current : null!;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsAcceleratorFailure(exception))
                    {
                        acceleratorFailure = exception;
                        break;
                    }

                    if (!moved)
                    {
                        mftCompleted = true;
                        break;
                    }

                    if (seen.Add(entry.NormalizedPath))
                    {
                        yield return entry;
                    }
                }
            }
            finally
            {
                await mftEnumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (mftCompleted)
            {
                mftReport = _mftProvider.LastReport;
                rootsMissingMftCoverage = GetRootsMissingMftCoverage(requiredRoots, mftReport);
                if (requiredRoots is null || rootsMissingMftCoverage.Count == 0)
                {
                    Volatile.Write(ref _lastReport, mftReport);
                    Volatile.Write(
                        ref _snapshotCheckpoint,
                        preSnapshotCheckpoint ?? _journalProvider?.SnapshotCheckpoint);
                    SetLastCapability(mftCapability);
                    yield break;
                }

                // One readable MFT (for example C:) is not proof that D:, a
                // non-NTFS fixed volume, or another configured root was seen.
                // The missing roots are scanned below before this pass can be
                // reported as complete.
                acceleratorFailure ??= new IOException("MFT root coverage incomplete");
            }
        }

        if (builtInCapability.State != CapabilityState.Available &&
            builtInCapability.Code != "BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE")
        {
            var unavailable = new RuntimeCapability(
                CapabilityState.Degraded,
                "ALL_DISCOVERY_SOURCES_UNAVAILABLE",
                "Everything 失败且内置文件发现当前不可用；保留已有缓存。",
                new Dictionary<string, string>
                {
                    ["acceleratorCode"] = acceleratorCapability.Code,
                    ["builtInCode"] = builtInCapability.Code,
                    ["fallbackReason"] = GetFallbackReason(
                        acceleratorFailure,
                        acceleratorCompleted,
                        acceleratorCapability.Code),
                });
            SetLastCapability(unavailable);
            throw new DiscoveryUnavailableException(LastCapability, acceleratorFailure);
        }

        var reason = GetFallbackReason(
            acceleratorFailure,
            acceleratorCompleted,
            acceleratorCapability.Code);
        var fallbackDetails = new Dictionary<string, string>
        {
            ["fallbackReason"] = reason,
        };
        if (rootsMissingMftCoverage is { Count: > 0 })
        {
            fallbackDetails["mftMissingRoots"] = string.Join(
                ";",
                rootsMissingMftCoverage.Select(static root => root.RootPath));
        }
        SetLastCapability(new RuntimeCapability(
            CapabilityState.Available,
            "BUILTIN_DISCOVERY_FALLBACK",
            "本轮未采用 Everything，已自动切换到内置文件发现。",
            fallbackDetails));

        if (_incrementalBuiltIn is not null)
        {
            Volatile.Write(ref _activeProgressSource, _incrementalBuiltIn);
            DiscoverySnapshotIncompleteException? builtInPartial = null;
            try
            {
                var builtInEntries = _incrementalBuiltIn is BuiltInFileDiscoveryProvider concreteBuiltIn &&
                                     rootsMissingMftCoverage is { Count: > 0 }
                    ? concreteBuiltIn.DiscoverEntriesForRootsAsync(
                        rootsMissingMftCoverage.Select(static root => root.RootPath).ToArray(),
                        cancellationToken)
                    : _incrementalBuiltIn.DiscoverEntriesAsync(cancellationToken);
                var builtInEnumerator = builtInEntries.GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        DiscoveredFile entry;
                        bool moved;
                        try
                        {
                            moved = await builtInEnumerator.MoveNextAsync().ConfigureAwait(false);
                            entry = moved ? builtInEnumerator.Current : null!;
                        }
                        catch (DiscoverySnapshotIncompleteException exception)
                        {
                            builtInPartial = exception;
                            break;
                        }

                        if (!moved)
                        {
                            break;
                        }

                        if (seen.Add(entry.NormalizedPath))
                        {
                            yield return entry;
                        }
                    }
                }
                finally
                {
                    await builtInEnumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(
                    ref _lastReport,
                    MergeCoverageReports(
                        startedAtUtc,
                        requiredRoots,
                        mftReport,
                        _incrementalBuiltIn.LastReport));
            }

            if (builtInPartial is not null)
            {
                // The built-in provider owns its report, but this wrapper may
                // merge it with MFT coverage before the store observes the
                // failure. Rebind the typed partial after the finally block so
                // the exception and LastReport describe one coherent snapshot.
                throw new DiscoverySnapshotIncompleteException(LastReport);
            }

            // An MFT checkpoint which did not cover every snapshot root cannot
            // safely drive later USN-only updates. Watchers still emit hints;
            // the next reconciliation remains a complete scan.
            Volatile.Write(ref _snapshotCheckpoint,
                rootsMissingMftCoverage is { Count: 0 }
                    ? preSnapshotCheckpoint
                    : null);
            yield break;
        }

        var fallbackReport = new ScanReportBuilder(startedAtUtc);
        Volatile.Write(ref _activeProgressSource, _builtIn);
        await foreach (var candidate in _builtIn.DiscoverAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = CreatePortableEntry(candidate);
            if (seen.Add(entry.NormalizedPath))
            {
                fallbackReport.Add(entry);
                yield return entry;
            }
        }

        Volatile.Write(
            ref _lastReport,
            MergeCoverageReports(startedAtUtc, requiredRoots, mftReport, fallbackReport.Build()));
        Volatile.Write(ref _snapshotCheckpoint,
            rootsMissingMftCoverage is { Count: 0 }
                ? preSnapshotCheckpoint
                : null);
    }

    public IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(CancellationToken cancellationToken) =>
        _incrementalBuiltIn is not null
            ? _incrementalBuiltIn.WatchAsync(cancellationToken)
            : EmptyWatchAsync(cancellationToken);

    public async ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_journalProvider is null)
        {
            return null;
        }

        var checkpoint = await _journalProvider.CaptureCheckpointAsync(cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _snapshotCheckpoint, checkpoint);
        return checkpoint;
    }

    public async ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
        DiscoveryJournalCheckpoint checkpoint,
        int maximumChanges,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var acceleratorCapability = await SafeProbeAsync(
            _accelerator,
            "EVERYTHING_ACCELERATOR_UNAVAILABLE",
            cancellationToken).ConfigureAwait(false);
        if (acceleratorCapability.State == CapabilityState.Available)
        {
            // A saved USN cursor must not bypass the preferred source. When
            // Everything is available, route this refresh back to a direct
            // Everything snapshot instead of continuing MFT deltas.
            return new DiscoveryDeltaBatch(
                checkpoint,
                checkpoint,
                Array.Empty<DiscoveryDelta>(),
                HasMore: false,
                RequiresFullReconciliation: true,
                "EVERYTHING_AVAILABLE_FULL_SNAPSHOT");
        }

        if (_journalProvider is not null)
        {
            Volatile.Write(ref _activeProgressSource, _mftProvider);
            return await _journalProvider.ReadDeltaBatchAsync(
                checkpoint,
                maximumChanges,
                cancellationToken).ConfigureAwait(false);
        }

        return new DiscoveryDeltaBatch(
            checkpoint,
            checkpoint,
            Array.Empty<DiscoveryDelta>(),
            HasMore: false,
            RequiresFullReconciliation: true,
            "JOURNAL_DISCOVERY_UNAVAILABLE");
    }

    public async ValueTask<DiscoveredFile?> TryInspectFileAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_incrementalBuiltIn is not null)
        {
            return await _incrementalBuiltIn.TryInspectFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!DiscoveryPath.TryGetDocumentKind(fullPath, out var kind))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            {
                return null;
            }

            return CreatePortableEntry(new FileCandidate(
                file.FullName,
                kind,
                file.Length,
                file.LastWriteTimeUtc));
        }
        catch (Exception exception) when (BuiltInFileDiscoveryProvider.IsRecoverable(exception))
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_accelerator is IAsyncDisposable asyncAccelerator)
        {
            await asyncAccelerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!ReferenceEquals(_builtIn, _accelerator) && _builtIn is IAsyncDisposable asyncBuiltIn)
        {
            await asyncBuiltIn.DisposeAsync().ConfigureAwait(false);
        }

        if (_mftProvider is IAsyncDisposable asyncMft &&
            !ReferenceEquals(_mftProvider, _builtIn) &&
            !ReferenceEquals(_mftProvider, _accelerator))
        {
            await asyncMft.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static DiscoveredFile CreatePortableEntry(FileCandidate candidate)
    {
        var normalized = DiscoveryPath.Normalize(candidate.FullPath);
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetDirectoryName(normalized) ?? normalized;
        }

        return new DiscoveredFile(
            candidate with { FullPath = normalized },
            DiscoveryPath.NormalizeKey(normalized),
            DiscoveryPath.Normalize(root),
            Identity: null);
    }

    private sealed class ScanReportBuilder(DateTimeOffset startedAtUtc)
    {
        private readonly Dictionary<string, (long Count, DiscoveryRootKind Kind)> _roots =
            new(StringComparer.OrdinalIgnoreCase);

        internal void AddRoot(string rootPath, DiscoveryRootKind kind)
        {
            if (!_roots.ContainsKey(rootPath))
            {
                _roots[rootPath] = (0, kind);
            }
        }

        internal void Add(DiscoveredFile entry)
        {
            _roots.TryGetValue(entry.RootPath, out var current);
            var kind = current == default
                ? entry.RootPath.StartsWith("\\\\", StringComparison.Ordinal)
                    ? DiscoveryRootKind.ExplicitNetwork
                    : DiscoveryRootKind.Fixed
                : current.Kind;
            _roots[entry.RootPath] = (current.Count + 1, kind);
        }

        internal DiscoveryScanReport Build()
        {
            var roots = _roots.Select(static pair => new DiscoveryRootReport(
                    pair.Key,
                    pair.Value.Kind,
                    WasOnline: true,
                    FullyEnumerated: true,
                    VisitedEntries: pair.Value.Count,
                    SolidWorksFiles: pair.Value.Count,
                    ReparsePointsSkipped: 0,
                    OfflineFilesSkipped: 0,
                    new Dictionary<string, int>()))
                .ToArray();
            return new DiscoveryScanReport(
                startedAtUtc,
                DateTimeOffset.UtcNow,
                IsCompleteForDeletion: true,
                roots);
        }
    }

    private IReadOnlyList<DiscoveryRootState>? GetRequiredRootSnapshot(
        CancellationToken cancellationToken)
    {
        if (_builtIn is not BuiltInFileDiscoveryProvider concreteBuiltIn)
        {
            // Keep the extension point backward-compatible. Production always
            // supplies the concrete provider, which gives us the authoritative
            // root list required for coverage validation.
            return null;
        }

        return concreteBuiltIn.GetRootStatesForAccelerator(cancellationToken)
            .Where(static root => root.IsOnline && !root.IsReparsePoint)
            .ToArray();
    }

    private static bool CanUseMftForKnownRoots(
        IReadOnlyList<DiscoveryRootState>? requiredRoots,
        RuntimeCapability builtInCapability)
    {
        if (requiredRoots is null)
        {
            // Custom providers cannot expose a root map. Preserve the legacy
            // extension behavior while production follows the strict branch.
            return CanUseAcceleratorForAllRoots(builtInCapability);
        }

        // MFT only describes whole fixed NTFS volumes. It must not be used to
        // expand an explicit subdirectory-only configuration into its parent
        // volume, but it can still accelerate the fixed roots while the
        // built-in scanner later fills removable/network roots.
        return requiredRoots.Any(static root => root.Kind == DiscoveryRootKind.Fixed);
    }

    private static IReadOnlyList<DiscoveryRootState> GetRootsMissingMftCoverage(
        IReadOnlyList<DiscoveryRootState>? requiredRoots,
        DiscoveryScanReport report)
    {
        if (requiredRoots is null)
        {
            return Array.Empty<DiscoveryRootState>();
        }

        if (!report.IsCompleteForDeletion)
        {
            return requiredRoots;
        }

        return requiredRoots
            .Where(root => root.Kind != DiscoveryRootKind.Fixed ||
                           !report.Roots.Any(mftRoot =>
                               mftRoot.Kind == DiscoveryRootKind.Fixed &&
                               mftRoot.WasOnline &&
                               mftRoot.FullyEnumerated &&
                               DiscoveryPath.IsWithinRoot(root.RootPath, mftRoot.RootPath)))
            .ToArray();
    }

    private static DiscoveryScanReport MergeCoverageReports(
        DateTimeOffset startedAtUtc,
        IReadOnlyList<DiscoveryRootState>? requiredRoots,
        DiscoveryScanReport? mftReport,
        DiscoveryScanReport builtInReport)
    {
        if (mftReport is null && requiredRoots is null)
        {
            return builtInReport;
        }

        var reports = (mftReport?.Roots ?? Array.Empty<DiscoveryRootReport>())
            .Concat(builtInReport.Roots)
            .GroupBy(static root => DiscoveryPath.NormalizeKey(root.RootPath), StringComparer.OrdinalIgnoreCase)
            // A targeted built-in report is newer and is authoritative for the
            // roots it rescanned after an incomplete MFT pass.
            .Select(static group => group.Last())
            .OrderBy(static root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The required-root snapshot intentionally excludes roots that became
        // offline. Do not let that filtering turn an incomplete built-in pass
        // into a deletion-authoritative merged report for the remaining roots.
        var completeForDeletion = requiredRoots is null
            ? (mftReport?.IsCompleteForDeletion ?? false) && builtInReport.IsCompleteForDeletion
            : builtInReport.IsCompleteForDeletion && requiredRoots.All(root =>
                root.IsOnline &&
                !root.IsReparsePoint &&
                reports.Any(report =>
                    report.WasOnline &&
                    report.FullyEnumerated &&
                    DiscoveryPath.IsWithinRoot(root.RootPath, report.RootPath)));

        return new DiscoveryScanReport(
            mftReport?.StartedAtUtc < builtInReport.StartedAtUtc
                ? mftReport.StartedAtUtc
                : builtInReport.StartedAtUtc == DateTimeOffset.MinValue
                    ? startedAtUtc
                    : builtInReport.StartedAtUtc,
            DateTimeOffset.UtcNow,
            completeForDeletion,
            reports);
    }

    private static bool CanUseAcceleratorForAllRoots(RuntimeCapability builtInCapability)
    {
        if (builtInCapability.Details is null ||
            !builtInCapability.Details.TryGetValue("nonFixedOnlineRoots", out var value))
        {
            // Custom providers used by tests or integrators remain eligible.
            return true;
        }

        return int.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var count) && count == 0;
    }

    private static async IAsyncEnumerable<DiscoveryChangeHint> EmptyWatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private static async ValueTask<RuntimeCapability> SafeProbeAsync(
        IFileDiscoveryProvider? provider,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return new RuntimeCapability(
                CapabilityState.Degraded,
                failureCode,
                "发现源未配置。");
        }

        try
        {
            return await provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new RuntimeCapability(
                CapabilityState.Degraded,
                failureCode,
                "发现源探测失败。",
                new Dictionary<string, string> { ["exception"] = exception.GetType().Name });
        }
    }

    private static void ValidateAcceleratedCandidate(FileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Size < 0 || candidate.LastWriteTimeUtc == DateTimeOffset.MinValue ||
            !DiscoveryPath.TryGetDocumentKind(candidate.FullPath, out var inferredKind) ||
            inferredKind != candidate.Kind)
        {
            throw new InvalidDataException("Everything 返回了无效的 SOLIDWORKS 文件记录。");
        }

        _ = DiscoveryPath.Normalize(candidate.FullPath);
    }

    private static bool IsAcceleratorFailure(Exception exception) => exception is not
        OutOfMemoryException and not
        StackOverflowException;

    private static IReadOnlyDictionary<string, string> MergeDetails(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyDictionary<string, string> additions)
    {
        var details = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var pair in additions)
        {
            details[pair.Key] = pair.Value;
        }

        return details;
    }

    private static string GetFallbackReason(
        Exception? acceleratorFailure,
        bool acceleratorCompleted,
        string acceleratorCapabilityCode)
    {
        if (acceleratorFailure is null)
        {
            return acceleratorCompleted
                ? "ACCELERATOR_REQUIRES_ROOT_VERIFICATION"
                : acceleratorCapabilityCode;
        }

        if (acceleratorFailure is EverythingQueryException queryException)
        {
            return queryException.Code;
        }

        if (acceleratorFailure is IOException { Message: "known discovery root offline" })
        {
            return "BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE";
        }

        if (acceleratorFailure is IOException { Message: "built-in scan required for non-fixed root" })
        {
            return "EVERYTHING_SKIPPED_NON_FIXED_ROOT";
        }

        if (acceleratorFailure is IOException { Message: "MFT root coverage incomplete" })
        {
            return "NTFS_MFT_ROOT_COVERAGE_INCOMPLETE";
        }

        return acceleratorFailure.GetType().Name;
    }

    private FileDiscoveryProgressSnapshot GetDiscoveryProgress()
    {
        var source = Volatile.Read(ref _activeProgressSource);
        if (source is IEverythingDiscoveryProgressSource everything)
        {
            return everything.DiscoveryProgress;
        }

        if (source is IDiscoveryProgressSource discovery)
        {
            return discovery.DiscoveryProgress;
        }

        return new FileDiscoveryProgressSnapshot(
            0,
            null,
            RuntimeServiceState.Idle,
            DateTimeOffset.UtcNow,
            "Discovery",
            LastCapability.Code);
    }

    private void SetLastCapability(RuntimeCapability capability)
    {
        IReadOnlyDictionary<string, string>? everythingDetails = _accelerator switch
        {
            IEverythingDiscoveryDiagnosticsSource diagnosticsSource =>
                diagnosticsSource.EverythingDiagnostics.ToCapabilityDetails(),
            null => new EverythingDiscoveryDiagnostics(
                Attempted: false,
                Available: false,
                AvailabilityCode: "EVERYTHING_ACCELERATOR_UNAVAILABLE",
                PageCount: 0,
                ReturnedEntryCount: 0,
                EmittedEntryCount: 0,
                FailureCode: _acceleratorConstructionError is null
                    ? null
                    : "EVERYTHING_ACCELERATOR_CONSTRUCTION_FAILED",
                FailureType: _acceleratorConstructionError,
                Completed: false,
                UpdatedAtUtc: DateTimeOffset.UtcNow).ToCapabilityDetails(),
            _ => null,
        };
        if (everythingDetails is not null)
        {
            var details = capability.Details is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(capability.Details, StringComparer.Ordinal);
            foreach (var pair in everythingDetails)
            {
                // Everything keys are deliberately namespaced, but assigning
                // rather than replacing keeps fallbackReason, root coverage,
                // and other effective-source details intact.
                details[pair.Key] = pair.Value;
            }

            capability = capability with { Details = details };
        }

        Volatile.Write(ref _lastCapability, capability);
    }
}

public sealed class DiscoveryUnavailableException : IOException
{
    public DiscoveryUnavailableException(RuntimeCapability capability, Exception? innerException)
        : base(capability.ChineseMessage, innerException) => Capability = capability;

    public RuntimeCapability Capability { get; }
}
