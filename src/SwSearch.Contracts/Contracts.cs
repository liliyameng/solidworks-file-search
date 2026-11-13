using System.Text.Json.Serialization;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using SwSearch.Pathing;

namespace SwSearch.Contracts;

public static class ContractVersions
{
    public const int IndexSchema = 10;
    public const int MetadataPipe = 1;
    public const string MetadataPipeNamePrefix = "swsearch.metadata.v1.session.";
    public const int BrokerPipe = 2;
    public const string BrokerPipeNamePrefix = "swsearch.broker.v1.session.";
    // This endpoint is deliberately separate from BrokerPipe.  The latter
    // carries search traffic and the authenticated installer-update challenge;
    // an explicit application exit is a one-shot, current-user session signal.
    public const int BrokerAppExitPipe = 1;
    public const string BrokerAppExitPipeNamePrefix = "swsearch.broker.app-exit.v1.session.";
    public const string AppInstallerShutdownEventPrefix = @"Local\SwSearch.App.Shutdown.v1.session.";
    public const string AppInstallerShutdownAcknowledgedEventPrefix = @"Local\SwSearch.App.ShutdownAck.v1.session.";
    public const string PropertyViewerInstallerShutdownEventPrefix = @"Local\SwSearch.PropertyViewer.Shutdown.v1.session.";

    public static string MetadataPipeName => MetadataPipeNameForSession(GetCurrentSessionId());

    public static string BrokerPipeName => BrokerPipeNameForSession(GetCurrentSessionId());

    /// <summary>
    /// Gets the dedicated current-user/session endpoint used only for an
    /// explicit application-exit request. It never carries installer update
    /// challenges or regular Broker requests.
    /// </summary>
    public static string BrokerAppExitPipeName => BrokerAppExitPipeNameForSession(GetCurrentSessionId());

    public static string MetadataPipeNameForSession(int sessionId) =>
        MetadataPipeNamePrefix + NormalizeSessionId(sessionId);

    public static string BrokerPipeNameForSession(int sessionId) =>
        BrokerPipeNamePrefix + NormalizeSessionId(sessionId);

    public static string BrokerAppExitPipeNameForSession(int sessionId) =>
        BrokerAppExitPipeNamePrefix + NormalizeSessionId(sessionId);

    public static string AppInstallerShutdownEventName =>
        AppInstallerShutdownEventPrefix + GetCurrentSessionId().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string AppInstallerShutdownAcknowledgedEventName =>
        AppInstallerShutdownAcknowledgedEventPrefix + GetCurrentSessionId().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string PropertyViewerInstallerShutdownEventName =>
        PropertyViewerInstallerShutdownEventPrefix + GetCurrentSessionId().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string NormalizeSessionId(int sessionId)
    {
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        return sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int GetCurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }
}

public sealed class BrokerProtocolMismatchException(string message) : IOException(message);

public enum SearchMode
{
    FileName,
    Property,
    All,
    Library
}

public enum SearchScopeKind
{
    AllMachine,
    Folder
}

public sealed record SearchScope(
    SearchScopeKind Kind,
    string? RootPath = null,
    [property: JsonPropertyName("includeSubdirectories")] bool Recursive = true)
{
    [JsonIgnore]
    public bool IncludeSubdirectories => Recursive;

    public static SearchScope AllMachine { get; } = new(SearchScopeKind.AllMachine);

    public static SearchScope Folder(string rootPath, bool recursive = true) =>
        new(SearchScopeKind.Folder, SearchPath.NormalizeFolder(rootPath), recursive);

    public SearchScope Normalize() => Kind switch
    {
        SearchScopeKind.AllMachine => AllMachine,
        SearchScopeKind.Folder when Recursive => Folder(RootPath ?? string.Empty),
        SearchScopeKind.Folder => throw new ArgumentException("当前版本仅支持递归文件夹搜索。", nameof(Recursive)),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };
}

public static class SearchPath
{
    public static string NormalizeFolder(string path) => WindowsScopePath.Normalize(path);

    public static string NormalizeKey(string path) => WindowsScopePath.NormalizeKey(path);

    public static string DescendantPrefix(string normalizedRoot) => WindowsScopePath.DescendantPrefix(normalizedRoot);

    public static string DescendantPrefixKey(string normalizedRoot) => WindowsScopePath.DescendantPrefixKey(normalizedRoot);

    public static bool Contains(string normalizedRoot, string candidatePath) =>
        WindowsScopePath.Contains(normalizedRoot, candidatePath);
}

public enum SolidWorksDocumentKind
{
    Part,
    Assembly,
    // Kept only so older pipe clients can deserialize their historical
    // payloads. New indexers deliberately reject this value.
    Drawing
}

/// <summary>
/// Defines the document kinds accepted by the current index format.
/// <see cref="SolidWorksDocumentKind.Drawing"/> remains a wire-compatible
/// legacy value, not an indexable document kind.
/// </summary>
public static class SupportedSolidWorksDocumentKinds
{
    public static bool IsSupported(SolidWorksDocumentKind kind) =>
        kind is SolidWorksDocumentKind.Part or SolidWorksDocumentKind.Assembly;
}

public enum PropertyScope
{
    Document,
    Configuration,
    ReferencedModel
}

public enum MetadataSource
{
    DocumentManager = 0,
    WindowsPropertyStore = 1,
    PersistentCache = 2,
    FileSystemOnly = 3,
    NoApi = 4,
    DrawingCache = 5
}

public enum CapabilityState
{
    Available,
    Degraded,
    Unavailable
}

public sealed record RuntimeCapability(
    CapabilityState State,
    string Code,
    string ChineseMessage,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record FileCandidate(
    string FullPath,
    SolidWorksDocumentKind Kind,
    long Size,
    DateTimeOffset LastWriteTimeUtc);

public sealed record DocumentProperty(
    string Name,
    string RawValue,
    string ResolvedValue,
    PropertyScope Scope,
    string? ConfigurationName,
    MetadataSource Source,
    string? OriginPath = null,
    string? DisplayValue = null,
    string? CachedValue = null,
    bool IsLinked = false);

public static class SolidWorksPropertyValue
{
    public static bool TryGetResolvedDisplayValue(DocumentProperty property, out string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        var resolved = GetResolvedDisplayValue(
            property.RawValue, property.ResolvedValue, property.DisplayValue, property.CachedValue, property.IsLinked);
        if (resolved is not null)
        {
            value = resolved;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static string? GetResolvedDisplayValue(
        string? rawValue,
        string? resolvedValue,
        string? displayValue,
        string? cachedValue,
        bool isLinked)
    {
        var candidates = isLinked
            ? new[] { resolvedValue, displayValue, cachedValue }
            : new[] { resolvedValue, displayValue, cachedValue, rawValue };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !IsUnresolvedExpression(candidate))
            {
                return candidate.Trim();
            }
        }
        return null;
    }

    public static bool IsUnresolvedExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        return candidate.Contains("$PRP", StringComparison.OrdinalIgnoreCase) ||
               candidate.Contains("$PRPSHEET", StringComparison.OrdinalIgnoreCase) ||
               candidate.Contains("$<", StringComparison.Ordinal) ||
               candidate.Contains("<$", StringComparison.Ordinal) ||
               candidate.StartsWith("$-", StringComparison.Ordinal) ||
               candidate.EndsWith("-$", StringComparison.Ordinal);
    }
}

public sealed record DocumentReference(
    string ReferencedPath,
    string? ReferencedConfiguration,
    bool IsResolved);

public sealed record DocumentMetadata(
    FileCandidate File,
    IReadOnlyList<DocumentProperty> Properties,
    IReadOnlyList<DocumentReference> References,
    MetadataSource Source,
    DateTimeOffset ExtractedAtUtc,
    string? WarningCode = null,
    IReadOnlyList<string>? ConfigurationNames = null);

public enum PropertyCardConfigSource
{
    ActivePluginSetting,
    LastKnownGood,
    BackupXlsx,
    Unavailable
}

public sealed record PropertyCardFieldDefinition(
    string Module,
    string PropertyName,
    string DisplayName,
    string Control,
    string? DefaultValue,
    bool IsReadOnly,
    int Order,
    IReadOnlyList<string>? Options = null);

public sealed record PropertyCardClassificationRule(
    string? Level1,
    string? Level2,
    string? Level3,
    string PropertyName,
    int Order,
    string? Module = null);

public sealed record PropertyCardClassificationNode(
    string? Level1,
    string? Level2,
    string? Level3,
    int Order);

public sealed record PropertyCardConfigSnapshot(
    IReadOnlyList<PropertyCardFieldDefinition> Fields,
    IReadOnlyList<PropertyCardClassificationRule> ClassificationRules,
    IReadOnlyList<PropertyCardClassificationNode> Classifications,
    PropertyCardConfigSource Source,
    string SourceLabel,
    DateTimeOffset UpdatedAtUtc,
    bool IsDegraded,
    string? DegradationReason = null);

public sealed record SearchQuery(
    string Text,
    SearchMode Mode,
    int Offset = 0,
    int Limit = 100,
    IReadOnlyCollection<SolidWorksDocumentKind>? Kinds = null,
    SearchScope? Scope = null,
    long? ExpectedRevision = null,
    string? PageFingerprint = null,
    IReadOnlyCollection<string>? LibraryRoots = null,
    string? SortColumn = null,
    bool SortDescending = false,
    bool? IsInteractive = null)
{
    public SearchScope EffectiveScope => (Scope ?? SearchScope.AllMachine).Normalize();

    // Null is the wire-compatible value used by older clients that predate the
    // interaction marker. Treat it as a real user query so upgrades never
    // accidentally remove foreground protection from legacy callers.
    public bool EffectiveIsInteractive => IsInteractive ?? true;
}

public static class SearchPagingFingerprint
{
    public static string Create(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var scope = query.EffectiveScope;
        var kinds = (query.Kinds ?? Array.Empty<SolidWorksDocumentKind>())
            .Distinct().Order().Select(static value => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var roots = (query.LibraryRoots ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(SearchPath.NormalizeKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var canonical = string.Join("\u001f", new[]
        {
            query.Text.Trim().ToUpperInvariant(),
            ((int)query.Mode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)scope.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            scope.RootPath is null ? string.Empty : SearchPath.NormalizeKey(scope.RootPath),
            string.Join(',', kinds),
            string.Join('\u001e', roots),
            query.SortColumn ?? string.Empty,
            query.SortDescending ? "DESC" : "ASC",
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record SearchHit(
    string FullPath,
    SolidWorksDocumentKind Kind,
    string FileName,
    string? MatchedProperty,
    string? MatchedValue,
    string? OriginModelPath,
    double Rank,
    bool CacheMayBeStale,
    string? OriginConfiguration = null);

/// <summary>
/// Progress for the active PRT/ASM metadata work set.
/// </summary>
/// <param name="CachedReadableFileCount">
/// For index-store snapshots, durable active rows searchable through the core
/// index or its sidecar. Historical extracted rows remain coverage during a
/// re-extract, and every current terminal outcome counts because its filename
/// row survives even when metadata is missing or unreadable. The store never
/// reports this below <see cref="ProcessedCount"/>.
/// </param>
public sealed record MetadataProgressSnapshot(
    long DiscoveredFileCount,
    long CompletedCount,
    long PartialCount,
    long PendingCount,
    long FailedCount,
    long? CachedReadableFileCount = null,
    long SkippedCount = 0)
{
    /// <summary>
    /// Current-extractor active PRT/ASM rows whose metadata work has settled.
    /// Files whose metadata is intentionally not read (for example a cloud-only
    /// placeholder) are terminal work, but remain distinct from parse failures.
    /// Store-backed snapshots partition the work set so this value plus
    /// <see cref="PendingCount"/> equals <see cref="DiscoveredFileCount"/>.
    /// </summary>
    public long ProcessedCount => CompletedCount + PartialCount + FailedCount + SkippedCount;
}

public enum RuntimeServiceState
{
    Idle,
    Running,
    Paused,
    Unavailable,
    Stalled,
}

public sealed record FileDiscoveryProgressSnapshot(
    long IndexedFileCount,
    long? EstimatedTotalCount,
    RuntimeServiceState State,
    DateTimeOffset LastHeartbeatUtc,
    string Source,
    string Code,
    // These nullable persisted-state fields are intentionally trailing for
    // backwards-compatible IPC.  A running provider can enumerate a large
    // candidate set before the store has safely published a complete
    // snapshot.  Consumers must not present that in-memory count as a
    // completed file index.
    long? DiscoveryGeneration = null,
    DateTimeOffset? LastSuccessfulDiscoveryUtc = null,
    string? SourceState = null,
    // Fixed conservative denominator captured from the last committed store
    // snapshot when the provider publishes no live estimate.  Unlike
    // EstimatedTotalCount it is never adjusted while indexed counts climb past
    // it, so a rescan cannot fake stability by keeping numerator == denominator.
    long? BaselineFileCount = null)
{
    /// <summary>
    /// True when this producer supplied durable discovery trust information,
    /// rather than only the provider's transient enumeration progress.
    /// </summary>
    [JsonIgnore]
    public bool HasPersistedDiscoveryState =>
        DiscoveryGeneration is > 0 ||
        LastSuccessfulDiscoveryUtc.HasValue ||
        !string.IsNullOrWhiteSpace(SourceState);

    /// <summary>
    /// A file percentage may represent a completed index only after the
    /// store has committed a nonzero generation, recorded a successful
    /// complete pass, and marked its source state ready.  A partial or stale
    /// snapshot remains searchable, but it is not a completed discovery run.
    /// </summary>
    [JsonIgnore]
    public bool HasCommittedCompleteSnapshot =>
        DiscoveryGeneration is > 0 &&
        LastSuccessfulDiscoveryUtc.HasValue &&
        string.Equals(SourceState, "ready", StringComparison.OrdinalIgnoreCase);
}

public sealed record MetadataServiceProgressSnapshot(
    long CompletedCount,
    long PartialCount,
    long PendingCount,
    long FailedCount,
    RuntimeServiceState State,
    DateTimeOffset LastHeartbeatUtc,
    string Code);

public sealed record RuntimeProgressSnapshot(
    MetadataProgressSnapshot Metadata,
    string DiscoverySource,
    string DiscoveryCode,
    bool IsPaused,
    double MaximumFilesPerSecond,
    long MaximumReadBytesPerSecond,
    TimeSpan? EstimatedRemaining,
    bool MetadataWritesPaused = false,
    string? StoragePauseReason = null,
    long WalBytes = 0,
    FileDiscoveryProgressSnapshot? Discovery = null,
    MetadataServiceProgressSnapshot? MetadataService = null,
    long DatabaseBytes = 0,
    long? LogicalDatabaseBytes = null,
    long? ReusableDatabaseBytes = null,
    string? StorageMaintenancePhase = null,
    /// <summary>
    /// Monotonically increases when a committed discovery/metadata work batch
    /// intentionally changes the current metadata work set. Consumers may use
    /// this as the only boundary at which a visible metadata-progress retreat
    /// is expected.
    /// </summary>
    long MetadataProgressEpoch = 0,
    /// <summary>
    /// Identifies the broker runtime that issued <see cref="MetadataProgressEpoch"/>.
    /// Epoch values are only ordered within this session; a new session resets
    /// the consumer's progress high-water mark.
    /// </summary>
    string? MetadataProgressEpochSession = null);

public enum BrokerRuntimePhase
{
    Starting,
    FirstIndex,
    CacheReady,
    Updating,
    Ready,
    Degraded
}

/// <summary>
/// Lightweight state returned independently of discovery and metadata work.
/// Epoch changes when the Broker process restarts; Revision changes only after
/// a committed index update and lets clients coalesce live-query refreshes.
/// </summary>
public sealed record BrokerStateSnapshot(
    string Epoch,
    long Revision,
    BrokerRuntimePhase Phase,
    bool CacheAvailable,
    bool BackgroundUpdating,
    long FileCount,
    DateTimeOffset? LastUpdatedUtc,
    RuntimeCapability Capability);

public enum BrokerOperation
{
    Ping,
    Search,
    Refresh,
    GetProgress,
    SetBackgroundPaused,
    TryGetMetadata,
    RefreshMetadata,
    GetPropertyCardConfig,
    GetState,
    ShutdownForUpdate,
    BeginShutdownForUpdate
}

public sealed record ShutdownForUpdateChallengeRequest(
    string ExpectedUserSid,
    int ExpectedSessionId,
    Guid Nonce);

public sealed record ShutdownForUpdateChallenge(
    Guid ChallengeId);

public sealed record ShutdownForUpdateRequest(
    string ExpectedInstallRoot,
    string ExpectedProductVersion,
    string ExpectedUserSid,
    int ExpectedSessionId,
    Guid Nonce,
    Guid ChallengeId);

public sealed record ShutdownForUpdateChallengeBinding(
    string ExpectedUserSid,
    int ExpectedSessionId,
    Guid Nonce,
    Guid ChallengeId);

public static class ShutdownForUpdateIdentity
{
    public static ShutdownForUpdateChallengeBinding? CreateChallenge(
        ShutdownForUpdateChallengeRequest? request)
    {
        if (!MatchesCurrentProcessIdentity(
                request?.ExpectedUserSid,
                request?.ExpectedSessionId ?? -1) ||
            !IsCryptographicSessionNonce(request!.Nonce))
        {
            return null;
        }

        return new ShutdownForUpdateChallengeBinding(
            request.ExpectedUserSid,
            request.ExpectedSessionId,
            request.Nonce,
            CreateCryptographicGuid());
    }

    public static bool MatchesCurrentProcess(
        ShutdownForUpdateRequest? request,
        ShutdownForUpdateChallengeBinding? challenge)
    {
        if (request is null || challenge is null ||
            !IsCryptographicSessionNonce(request.Nonce) || request.ChallengeId == Guid.Empty ||
            !string.Equals(request.ExpectedUserSid, challenge.ExpectedUserSid, StringComparison.Ordinal) ||
            request.ExpectedSessionId != challenge.ExpectedSessionId ||
            request.Nonce != challenge.Nonce ||
            !FixedTimeGuidEquals(request.ChallengeId, challenge.ChallengeId) ||
            !MatchesCurrentProcessIdentity(request.ExpectedUserSid, request.ExpectedSessionId))
        {
            return false;
        }

        var currentVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return false;
        }

        try
        {
            var currentRoot = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expectedRoot = Path.GetFullPath(request.ExpectedInstallRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(currentVersion, request.ExpectedProductVersion, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(currentRoot, expectedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool MatchesCurrentProcessIdentity(string? expectedUserSid, int expectedSessionId)
    {
        if (!OperatingSystem.IsWindows() ||
            expectedSessionId < 0 ||
            string.IsNullOrWhiteSpace(expectedUserSid))
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        using var process = Process.GetCurrentProcess();
        return string.Equals(identity.User?.Value, expectedUserSid, StringComparison.Ordinal) &&
               process.SessionId == expectedSessionId;
    }

    private static Guid CreateCryptographicGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }

    private static bool IsCryptographicSessionNonce(Guid nonce)
    {
        Span<byte> bytes = stackalloc byte[16];
        nonce.TryWriteBytes(bytes);
        return (bytes[7] & 0xF0) == 0x40 && (bytes[8] & 0xC0) == 0x80;
    }

    private static bool FixedTimeGuidEquals(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes);
        right.TryWriteBytes(rightBytes);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public sealed record BrokerRequest(
    int ProtocolVersion,
    Guid RequestId,
    BrokerOperation Operation,
    SearchQuery? Query = null,
    bool? Paused = null,
    string? FullPath = null,
    ShutdownForUpdateRequest? ShutdownForUpdate = null,
    ShutdownForUpdateChallengeRequest? ShutdownChallengeRequest = null);

public sealed record BrokerResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<SearchHit>? Hits = null,
    RuntimeCapability? Capability = null,
    RuntimeProgressSnapshot? Progress = null,
    DocumentMetadata? Metadata = null,
    PropertyCardConfigSnapshot? PropertyCardConfig = null,
    BrokerStateSnapshot? State = null,
    ShutdownForUpdateChallenge? ShutdownChallenge = null,
    bool? HasMore = null,
    long? IndexRevision = null,
    string? PageFingerprint = null,
    bool PageReset = false,
    long? TotalCount = null,
    MetadataRefreshStatus? MetadataRefresh = null);

public sealed record MetadataRefreshStatus(
    bool Fresh,
    bool CacheStale,
    string? ErrorCode,
    string ChineseMessage,
    bool Retryable);

public enum MetadataWorkClass
{
    Background,
    Interactive,
}

public enum MetadataFileDisposition
{
    Normal,
    CloudPlaceholder,
    ReparsePoint,
}

public enum MetadataPipelineOutcome
{
    Unknown,
    Completed,
    Partial,
    FileChanged,
    FileGone,
    FileUnreadable,
    Deferred,
    Unsupported,
    Failed,
}

public sealed record MetadataFileEvidence(
    string RequestedPath,
    string FinalPath,
    SolidWorksDocumentKind Kind,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string FileIdentity);

public sealed record MetadataRequest(
    int ProtocolVersion,
    Guid RequestId,
    string FullPath,
    bool ForceRefresh,
    int TimeoutMilliseconds,
    MetadataWorkClass WorkClass = MetadataWorkClass.Background,
    Guid PipelineLeaseId = default,
    string? ExpectedFingerprint = null,
    int ExtractorVersion = 0);

public sealed record MetadataResponse(
    int ProtocolVersion,
    Guid RequestId,
    DocumentMetadata? Metadata,
    string? ErrorCode,
    string? ChineseErrorMessage,
    bool Retryable,
    Guid PipelineLeaseId = default,
    Guid WorkerGenerationNonce = default,
    MetadataPipelineOutcome Outcome = MetadataPipelineOutcome.Unknown,
    MetadataFileDisposition Disposition = MetadataFileDisposition.Normal,
    MetadataFileEvidence? BeforeEvidence = null,
    MetadataFileEvidence? AfterEvidence = null);

public interface IFileDiscoveryProvider
{
    ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<FileCandidate> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IMetadataExtractionClient
{
    ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken);

    ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken);
}

public interface IMetadataGenerationSupervisor
{
    ValueTask RetireGenerationAsync(
        Guid workerGenerationNonce,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker for a Broker-owned metadata service. Production implementations keep
/// scheduling and cache ownership in Broker while isolating all file access in
/// a supervised, disposable worker generation.
/// </summary>
public interface IBrokerHostedMetadataExtractionClient : IMetadataExtractionClient
{
}

public interface IIndexStore
{
    ValueTask ApplyDiscoverySnapshotAsync(
        IAsyncEnumerable<FileCandidate> files,
        CancellationToken cancellationToken);

    ValueTask UpsertMetadataAsync(DocumentMetadata metadata, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    ValueTask<DocumentMetadata?> TryGetMetadataAsync(string fullPath, CancellationToken cancellationToken);
}

public interface ISearchService
{
    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
