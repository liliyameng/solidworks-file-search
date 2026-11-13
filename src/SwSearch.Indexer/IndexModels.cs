namespace SwSearch.Indexer;

public sealed record MetadataWorkLease(
    long FileId,
    string FullPath,
    string Reason,
    int Attempt,
    DateTimeOffset LeaseUntilUtc,
    string LeaseOwner,
    MetadataQueuePriority Priority = MetadataQueuePriority.Historical,
    string Fingerprint = "",
    long EstimatedReadBytes = 0);

internal sealed record MetadataCommitItem(
    MetadataWorkLease Lease,
    SwSearch.Contracts.DocumentMetadata Metadata,
    StableFileEvidence Evidence);

internal sealed record InteractiveMetadataCommitToken(
    Guid AttemptId,
    Guid PipelineLeaseId,
    long? FileId,
    string PathNorm,
    string? PersistedFingerprint);

public sealed record MetadataRefreshResult(
    SwSearch.Contracts.DocumentMetadata? Metadata,
    SwSearch.Contracts.MetadataRefreshStatus Status);

/// <summary>
/// Lower numeric values are serviced first.  The values are persisted, so do
/// not reorder them without a schema migration.
/// </summary>
public enum MetadataQueuePriority
{
    PropertyViewer = 0,
    ActiveSearch = 1,
    Modified = 2,
    New = 3,
    Historical = 4,
    Retry = 5,
}

public sealed record DiscoverySnapshotOptions(
    string SourceId,
    bool IsComplete,
    IReadOnlyCollection<string>? OnlineRoots = null,
    int CommitBatchSize = 5_000)
{
    public static DiscoverySnapshotOptions Complete(string sourceId = "default") =>
        new(sourceId, IsComplete: true);
}

public sealed record MetadataBatchOptions(
    int MaximumItems = 100,
    TimeSpan? MaximumAge = null,
    long MaximumEstimatedBytes = 8 * 1024 * 1024)
{
    public TimeSpan EffectiveMaximumAge => MaximumAge ?? TimeSpan.FromSeconds(5);
}

public sealed record IndexStorageStatus(
    long DatabaseBytes,
    long WalBytes,
    bool MetadataWritesPaused,
    string? PauseReason,
    long LogicalDatabaseBytes = 0,
    long ReusableDatabaseBytes = 0,
    long PageSizeBytes = 0,
    long PageCount = 0,
    long FreePageCount = 0,
    string MaintenancePhase = "idle",
    long MaintenanceGateWaitMilliseconds = 0,
    long MaintenanceGateHoldMilliseconds = 0,
    string? CoreDatabasePath = null,
    string? SearchDatabasePath = null,
    string CorePathMode = "core",
    string SearchProjectionState = "unknown",
    long SearchProjectionCurrentSequence = 0,
    long SearchProjectionAppliedSequence = 0);

public sealed class IndexStorageFaultException : IOException
{
    public IndexStorageFaultException(string code, string databasePath, Exception innerException)
        : base($"Index storage fault '{code}' for '{databasePath}'.", innerException)
    {
        Code = code;
        DatabasePath = databasePath;
    }

    public string Code { get; }

    public string DatabasePath { get; }
}

public sealed record IndexStoragePolicy(
    long TargetDatabaseBytes,
    long MaximumDatabaseBytes,
    long PassiveCheckpointWalBytes,
    long PauseWalBytes,
    long PhysicalSafetyDatabaseBytes = SqliteIndexStore.PhysicalSafetyDatabaseBytes)
{
    public static IndexStoragePolicy Default { get; } = new(
        SqliteIndexStore.TargetDatabaseBytes,
        SqliteIndexStore.MaximumDatabaseBytes,
        SqliteIndexStore.PassiveCheckpointWalBytes,
        SqliteIndexStore.PauseWalBytes);
}

public static class ExtractorVersions
{
    // Increment whenever parsing semantics or the persisted property model changes.
    public const int Current = 4;

    // Version 4 only widens ModernSolidWorks' accepted internal version range
    // (17000 to 19000).  A completed version 3 Part/Assembly cache can therefore
    // be adopted before the normal upgrade requeue, but this is deliberately
    // not a general "previous version" policy for future extractor revisions.
    public const int Rc85CompatibleCacheVersion = 3;

    internal const string Rc86CompatibleCacheAdoptionMarker =
        "rc86-compatible-cache-v3-to-v4";

    public static bool CanAdoptCompletedCache(int fromVersion, int toVersion) =>
        fromVersion == Rc85CompatibleCacheVersion && toVersion == Current;
}

public sealed record IndexStatus(
    long DiscoveryGeneration,
    bool CacheMayBeStale,
    DateTimeOffset? LastSuccessfulDiscoveryUtc,
    string SourceState,
    long PresentFileCount,
    long PendingMetadataCount);

/// <summary>
/// Read-only aggregate used by the desktop status panel.  The categories are
/// mutually exclusive and always describe currently present files only.
/// </summary>
public enum ReferenceResolutionState
{
    Resolved = 0,
    Missing = 1,
    Ambiguous = 2,
}
