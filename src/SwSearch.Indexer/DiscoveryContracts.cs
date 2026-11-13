using SwSearch.Contracts;

namespace SwSearch.Indexer;

/// <summary>
/// Describes why a root is eligible for the built-in discovery pass. Network
/// roots are never inferred; they are present only when explicitly configured.
/// </summary>
public enum DiscoveryRootKind
{
    Fixed,
    Removable,
    ExplicitLocal,
    ExplicitNetwork,
}

/// <summary>
/// Stable identity supplied by Windows when the underlying file system supports
/// it. The pair is used as a rename hint; path, size and timestamp remain the
/// portable fallback.
/// </summary>
public sealed record FileSystemIdentity(uint VolumeSerialNumber, ulong FileIndex);

/// <summary>
/// Rich discovery item used by the incremental indexer. The legacy
/// <see cref="IFileDiscoveryProvider"/> surface remains available for callers
/// which only need a complete snapshot.
/// </summary>
public sealed record DiscoveredFile(
    FileCandidate Candidate,
    string NormalizedPath,
    string RootPath,
    FileSystemIdentity? Identity);

/// <summary>
/// Optional progress surface for discovery providers.  It is deliberately a
/// polled snapshot rather than an event: the broker owns the refresh cadence
/// and providers do not retain UI/runtime subscribers.
/// </summary>
public interface IDiscoveryProgressSource
{
    FileDiscoveryProgressSnapshot DiscoveryProgress { get; }
}

public enum DiscoveryChangeKind
{
    CreatedOrChanged,
    Deleted,
    Renamed,
    ReconciliationRequired,
}

/// <summary>
/// FileSystemWatcher events are hints, not authoritative index mutations. A
/// bounded queue can collapse an event storm into ReconciliationRequired.
/// </summary>
public sealed record DiscoveryChangeHint(
    DiscoveryChangeKind Kind,
    string? FullPath,
    string? PreviousFullPath,
    string RootPath,
    string Reason);

/// <summary>
/// Durable NTFS journal position.  The journal id protects against journal
/// recreation while NextUsn is the first record which has not been applied.
/// </summary>
public sealed record DiscoveryVolumeCursor(
    string VolumeRoot,
    ulong JournalId,
    long NextUsn,
    uint VolumeSerialNumber = 0);

public sealed record DiscoveryJournalCheckpoint(
    string Source,
    IReadOnlyList<DiscoveryVolumeCursor> Volumes,
    DateTimeOffset CapturedAtUtc);

public enum DiscoveryDeltaKind
{
    Upsert,
    Delete,
}

/// <summary>
/// A journal mutation.  Upsert carries the current candidate; Delete carries
/// only the last known normalized path.  Rename is represented as a delete and
/// an upsert, which remains idempotent when replayed after a failed commit.
/// </summary>
public sealed record DiscoveryDelta(
    DiscoveryDeltaKind Kind,
    string FullPath,
    string RootPath,
    DiscoveredFile? File,
    string Reason);

/// <summary>
/// One bounded, replayable journal page.  Consumers persist NextCheckpoint
/// only after every change in this page has committed successfully.
/// </summary>
public sealed record DiscoveryDeltaBatch(
    DiscoveryJournalCheckpoint PreviousCheckpoint,
    DiscoveryJournalCheckpoint NextCheckpoint,
    IReadOnlyList<DiscoveryDelta> Changes,
    bool HasMore,
    bool RequiresFullReconciliation,
    string Code);

/// <summary>
/// Optional MFT/USN contract used after a complete initial snapshot.  A null
/// checkpoint is never interpreted as "from zero"; callers must first consume
/// a complete snapshot and persist <see cref="SnapshotCheckpoint"/>.
/// </summary>
public interface IJournalIncrementalDiscoveryProvider
{
    DiscoveryJournalCheckpoint? SnapshotCheckpoint { get; }

    /// <summary>
    /// Captures a cheap pre-snapshot boundary. Everything-backed snapshots use
    /// this before their first page so changes racing the snapshot are replayed
    /// from USN rather than missed.
    /// </summary>
    ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(
        CancellationToken cancellationToken);

    ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
        DiscoveryJournalCheckpoint checkpoint,
        int maximumChanges,
        CancellationToken cancellationToken);
}

public sealed record DiscoveryRootReport(
    string RootPath,
    DiscoveryRootKind Kind,
    bool WasOnline,
    bool FullyEnumerated,
    long VisitedEntries,
    long SolidWorksFiles,
    long ReparsePointsSkipped,
    long OfflineFilesSkipped,
    IReadOnlyDictionary<string, int> Errors)
{
    /// <summary>
    /// Normalized directory or file prefixes that could not be authoritatively
    /// enumerated during this pass. Reconciliation may remove stale entries in
    /// successful siblings, but must retain cached entries at or below every
    /// prefix in this collection.
    /// </summary>
    public IReadOnlyList<string> IncompletePrefixes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A subset of <see cref="IncompletePrefixes"/> whose cached entries are
    /// still searchable, but whose queued metadata work can be closed for this
    /// pass. These are expected skip boundaries such as access-denied,
    /// disappeared, cloud-only or transiently locked subtrees. A later observed
    /// file under the same prefix is made eligible for metadata extraction again.
    /// </summary>
    public IReadOnlyList<string> TerminalSkippedPrefixes { get; init; } = Array.Empty<string>();
}

public sealed record DiscoveryScanReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool IsCompleteForDeletion,
    IReadOnlyList<DiscoveryRootReport> Roots)
{
    public static DiscoveryScanReport Empty { get; } = new(
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        IsCompleteForDeletion: false,
        Array.Empty<DiscoveryRootReport>());
}

/// <summary>
/// Thrown only after all online roots have been streamed when one or more roots
/// known to contain indexed data is offline. Snapshot consumers must discard
/// that reconciliation pass so an unplugged volume is not interpreted as mass
/// deletion.
/// </summary>
public sealed class DiscoverySnapshotIncompleteException : IOException
{
    public DiscoverySnapshotIncompleteException(DiscoveryScanReport report)
        : base("一个或多个已知搜索根当前离线；本轮结果不能用于删除对账。") => Report = report;

    public DiscoveryScanReport Report { get; }
}

/// <summary>
/// Optional richer contract for persistent incremental discovery. A runtime
/// should consume watcher hints immediately and still schedule a complete
/// reconciliation at <see cref="RecommendedReconciliationInterval"/> because
/// Windows can drop watcher events.
/// </summary>
public interface IIncrementalFileDiscoveryProvider : IFileDiscoveryProvider
{
    TimeSpan RecommendedReconciliationInterval { get; }

    DiscoveryScanReport LastReport { get; }

    IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(CancellationToken cancellationToken);

    ValueTask<DiscoveredFile?> TryInspectFileAsync(
        string fullPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lets the runtime restore root history from the persistent index after the
/// provider has been constructed. This closes the unplugged-volume safety gap
/// across application restarts.
/// </summary>
public interface IPersistedDiscoveryRootSink
{
    void RememberPreviouslyIndexedRoots(IEnumerable<string> roots);
}

/// <summary>
/// Resource policy for the built-in enumerator. Zero means no explicit rate
/// ceiling; enumeration is still single-concurrency and periodically yields.
/// </summary>
public sealed class BuiltInDiscoveryOptions
{
    public bool DiscoverAllLocalVolumes { get; init; } = true;

    public IReadOnlyCollection<string> ExplicitLocalRoots { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> ExplicitNetworkRoots { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Roots persisted by the index store from an earlier successful pass. If
    /// one is absent now, the scan is deliberately incomplete for deletion.
    /// </summary>
    public IReadOnlyCollection<string> PreviouslyIndexedRoots { get; init; } = Array.Empty<string>();

    public bool CaptureFileIdentity { get; init; } = true;

    public int YieldEveryEntries { get; init; } = 128;

    public TimeSpan YieldDelay { get; init; } = TimeSpan.FromMilliseconds(1);

    public int MaximumEntriesPerSecond { get; init; } = 2_000;

    public int WatcherQueueCapacity { get; init; } = 2_048;

    public int WatcherBufferSize { get; init; } = 8 * 1024;

    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromHours(6);
}
