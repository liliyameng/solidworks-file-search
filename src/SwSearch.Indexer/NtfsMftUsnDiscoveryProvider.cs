using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

/// <summary>
/// Reads NTFS metadata directly from the MFT and consumes later mutations from
/// the USN journal. It never opens file data and therefore does not hydrate a
/// OneDrive placeholder. Everything remains the preferred initial accelerator;
/// this provider is the dependency-free local-volume path and the authoritative
/// incremental cursor source.
/// </summary>
public sealed class NtfsMftUsnDiscoveryProvider :
    IIncrementalFileDiscoveryProvider,
    IJournalIncrementalDiscoveryProvider,
    IDiscoveryProgressSource,
    IAsyncDisposable
{
    private const string SourceId = "NTFS-MFT-USN-v1";
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;
    private readonly INtfsChangeSource _source;
    private readonly SystemLoadMonitor _ioLoadMonitor;
    private readonly BuiltInFileDiscoveryProvider? _watcherProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscoveryJournalCheckpoint? _snapshotCheckpoint;
    private DiscoveryScanReport _lastReport = DiscoveryScanReport.Empty;
    private FileDiscoveryProgressSnapshot _progress = new(
        0,
        null,
        RuntimeServiceState.Idle,
        DateTimeOffset.UtcNow,
        "NTFS MFT/USN",
        "NTFS_DISCOVERY_IDLE");
    private bool _disposed;

    public NtfsMftUsnDiscoveryProvider(BuiltInDiscoveryOptions? watcherOptions = null)
        : this(
            new WindowsNtfsChangeSource(),
            new BuiltInFileDiscoveryProvider(watcherOptions ?? new BuiltInDiscoveryOptions()))
    {
    }

    internal NtfsMftUsnDiscoveryProvider(
        INtfsChangeSource source,
        BuiltInFileDiscoveryProvider? watcherProvider = null,
        SystemLoadMonitor? ioLoadMonitor = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _watcherProvider = watcherProvider;
        _ioLoadMonitor = ioLoadMonitor ?? new SystemLoadMonitor();
    }

    public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromDays(7);

    public DiscoveryScanReport LastReport => Volatile.Read(ref _lastReport);

    public DiscoveryJournalCheckpoint? SnapshotCheckpoint => Volatile.Read(ref _snapshotCheckpoint);

    public FileDiscoveryProgressSnapshot DiscoveryProgress => Volatile.Read(ref _progress);

    public ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var volumes = _source.GetVolumes(cancellationToken);
            return ValueTask.FromResult<DiscoveryJournalCheckpoint?>(
                volumes.Count == 0 ? null : CurrentCheckpoint(volumes));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return ValueTask.FromResult<DiscoveryJournalCheckpoint?>(null);
        }
    }

    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Degraded,
                "NTFS_DISCOVERY_WINDOWS_REQUIRED",
                "NTFS MFT/USN 发现仅支持 Windows。"));
        }

        try
        {
            var volumes = _source.GetVolumes(cancellationToken);
            return ValueTask.FromResult(volumes.Count > 0
                ? new RuntimeCapability(
                    CapabilityState.Available,
                    "NTFS_MFT_USN_READY",
                    "NTFS MFT/USN 增量发现已就绪。",
                    new Dictionary<string, string>
                    {
                        ["volumes"] = volumes.Count.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    })
                : new RuntimeCapability(
                    CapabilityState.Degraded,
                    "NTFS_MFT_USN_NO_VOLUME",
                    "没有可读取 MFT/USN 的本地 NTFS 卷。"));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Degraded,
                "NTFS_MFT_USN_UNAVAILABLE",
                "当前无法读取 NTFS MFT/USN，将保留缓存并使用慢速后备。",
                new Dictionary<string, string> { ["exception"] = exception.GetType().Name }));
        }
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        var reports = new List<DiscoveryRootReport>();
        var cursors = new List<DiscoveryVolumeCursor>();
        long processedRecords = 0;
        long? estimatedRecords = 0;
        var completed = false;
        try
        {
            var volumes = _source.GetVolumes(cancellationToken);
            if (volumes.Count == 0)
            {
                throw new IOException("No readable local NTFS volume was found.");
            }

            foreach (var volume in volumes)
            {
                cursors.Add(new DiscoveryVolumeCursor(
                    volume.RootPath,
                    volume.JournalId,
                    volume.NextUsn,
                    volume.VolumeSerialNumber));
                if (estimatedRecords is not null && volume.EstimatedMftRecords is not null)
                {
                    estimatedRecords = checked(estimatedRecords.Value + volume.EstimatedMftRecords.Value);
                }
                else
                {
                    estimatedRecords = null;
                }
            }

            SetProgress(
                processedRecords,
                estimatedRecords,
                RuntimeServiceState.Running,
                "NTFS_MFT_ENUMERATING");

            foreach (var volume in volumes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directories = new Dictionary<ulong, NtfsNameRecord>();
                var files = new List<NtfsNameRecord>();
                long visited = 0;
                long reparseSkipped = 0;
                await foreach (var record in _source.EnumerateMftAsync(volume, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    visited++;
                    processedRecords++;
                    if (record.IsDirectory)
                    {
                        directories[record.FileReferenceNumber] = record;
                    }
                    else if (DiscoveryPath.TryGetDocumentKind(record.Name, out _) &&
                             !IsPlainFileReparse(record.Attributes))
                    {
                        files.Add(record);
                    }
                    else if (IsPlainFileReparse(record.Attributes))
                    {
                        reparseSkipped++;
                    }

                    if ((processedRecords & 0x3ff) == 0)
                    {
                        SetProgress(
                            processedRecords,
                            estimatedRecords,
                            RuntimeServiceState.Running,
                            "NTFS_MFT_ENUMERATING");
                        // Mid-scan IO pacing: a running enumeration must also
                        // yield to foreground work, not only new scans.
                        var loadDelay = DiscoveryIoPacing.DelayFor(_ioLoadMonitor.Sample());
                        if (loadDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(loadDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                long matched = 0;
                foreach (var record in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = BuildPath(volume.RootPath, record, directories);
                    if (path is null || !DiscoveryPath.TryGetDocumentKind(path, out var kind))
                    {
                        continue;
                    }

                    var normalized = DiscoveryPath.Normalize(path);
                    if (DiscoveryPath.IsExcludedSystemPath(normalized))
                    {
                        continue;
                    }

                    matched++;
                    var timestamp = record.TimestampUtc == DateTimeOffset.MinValue
                        ? started
                        : record.TimestampUtc;
                    yield return new DiscoveredFile(
                        new FileCandidate(normalized, kind, 0, timestamp),
                        DiscoveryPath.NormalizeKey(normalized),
                        volume.RootPath,
                        IsCloudPlaceholder(record.Attributes)
                            ? null
                            : new FileSystemIdentity(
                                volume.VolumeSerialNumber,
                                record.FileReferenceNumber));
                }

                reports.Add(new DiscoveryRootReport(
                    volume.RootPath,
                    DiscoveryRootKind.Fixed,
                    WasOnline: true,
                    FullyEnumerated: true,
                    VisitedEntries: visited,
                    SolidWorksFiles: matched,
                    ReparsePointsSkipped: reparseSkipped,
                    OfflineFilesSkipped: 0,
                    new Dictionary<string, int>()));
            }

            Volatile.Write(ref _snapshotCheckpoint, new DiscoveryJournalCheckpoint(
                SourceId,
                cursors,
                started));
            Volatile.Write(ref _lastReport, new DiscoveryScanReport(
                started,
                DateTimeOffset.UtcNow,
                IsCompleteForDeletion: true,
                reports));
            completed = true;
        }
        finally
        {
            SetProgress(
                processedRecords,
                estimatedRecords,
                completed ? RuntimeServiceState.Idle : RuntimeServiceState.Unavailable,
                completed ? "NTFS_MFT_COMPLETE" : "NTFS_MFT_INCOMPLETE");
            _gate.Release();
        }
    }

    public async ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
        DiscoveryJournalCheckpoint checkpoint,
        int maximumChanges,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (maximumChanges is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChanges));
        }

        if (!string.Equals(checkpoint.Source, SourceId, StringComparison.Ordinal))
        {
            return Reconciliation(checkpoint, checkpoint, "NTFS_CURSOR_SOURCE_CHANGED");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentVolumes = _source.GetVolumes(cancellationToken);
            var currentByRoot = currentVolumes.ToDictionary(
                static volume => DiscoveryPath.NormalizeKey(volume.RootPath),
                StringComparer.OrdinalIgnoreCase);
            var cursorByRoot = checkpoint.Volumes.ToDictionary(
                static cursor => DiscoveryPath.NormalizeKey(cursor.VolumeRoot),
                StringComparer.OrdinalIgnoreCase);
            if (currentByRoot.Count != cursorByRoot.Count ||
                currentByRoot.Keys.Any(key => !cursorByRoot.ContainsKey(key)))
            {
                return Reconciliation(
                    checkpoint,
                    CurrentCheckpoint(currentVolumes),
                    "NTFS_VOLUME_SET_CHANGED");
            }

            foreach (var pair in currentByRoot)
            {
                var current = pair.Value;
                var cursor = cursorByRoot[pair.Key];
                if (cursor.VolumeSerialNumber != current.VolumeSerialNumber ||
                    cursor.JournalId != current.JournalId ||
                    cursor.NextUsn < current.FirstUsn ||
                    cursor.NextUsn > current.NextUsn)
                {
                    return Reconciliation(
                        checkpoint,
                        CurrentCheckpoint(currentVolumes),
                        "NTFS_JOURNAL_GAP_OR_RECREATED");
                }
            }

            var changes = new List<DiscoveryDelta>(Math.Min(maximumChanges, 4096));
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextCursors = checkpoint.Volumes.ToDictionary(
                static cursor => DiscoveryPath.NormalizeKey(cursor.VolumeRoot),
                static cursor => cursor,
                StringComparer.OrdinalIgnoreCase);
            var reconciliation = false;
            var code = "NTFS_USN_DELTA_COMPLETE";
            foreach (var current in currentVolumes)
            {
                if (changes.Count >= maximumChanges)
                {
                    break;
                }

                var key = DiscoveryPath.NormalizeKey(current.RootPath);
                var prior = nextCursors[key];
                var read = _source.ReadJournal(
                    current,
                    prior.NextUsn,
                    maximumChanges - changes.Count,
                    cancellationToken);
                nextCursors[key] = prior with { NextUsn = read.NextUsn };
                foreach (var record in read.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    const NtfsUsnReason meaningfulFileChange =
                        NtfsUsnReason.DataOverwrite |
                        NtfsUsnReason.DataExtend |
                        NtfsUsnReason.DataTruncation |
                        NtfsUsnReason.NamedDataOverwrite |
                        NtfsUsnReason.NamedDataExtend |
                        NtfsUsnReason.NamedDataTruncation |
                        NtfsUsnReason.FileCreate |
                        NtfsUsnReason.FileDelete |
                        NtfsUsnReason.RenameOldName |
                        NtfsUsnReason.RenameNewName |
                        NtfsUsnReason.BasicInfoChange |
                        NtfsUsnReason.ReparsePointChange |
                        NtfsUsnReason.StreamChange;
                    if ((record.Reason & meaningfulFileChange) == 0)
                    {
                        continue;
                    }

                    if (record.IsDirectory)
                    {
                        if ((record.Reason & (NtfsUsnReason.RenameOldName |
                                              NtfsUsnReason.RenameNewName |
                                              NtfsUsnReason.FileDelete)) != 0)
                        {
                            reconciliation = true;
                            code = "NTFS_DIRECTORY_TOPOLOGY_CHANGED";
                        }

                        continue;
                    }

                    var plainFileReparse = IsPlainFileReparse(record.Attributes);
                    if (plainFileReparse &&
                        !DiscoveryPath.TryGetDocumentKind(record.Name, out _))
                    {
                        continue;
                    }

                    var parent = _source.TryResolvePathById(
                        current,
                        record.ParentFileReferenceNumber,
                        directory: true);
                    if (parent is null)
                    {
                        reconciliation = true;
                        code = "NTFS_PARENT_PATH_UNRESOLVED";
                        continue;
                    }

                    var path = Path.Combine(parent, record.Name);
                    if (!DiscoveryPath.TryGetDocumentKind(path, out var kind))
                    {
                        continue;
                    }

                    var normalized = DiscoveryPath.Normalize(path);
                    if (DiscoveryPath.IsExcludedSystemPath(normalized))
                    {
                        continue;
                    }

                    if (plainFileReparse)
                    {
                        // The same path may previously have named a normal SW
                        // document. A ReparsePointChange must actively remove
                        // that cached file and its properties. Resolve only its
                        // parent directory; never resolve/open the link target.
                        var delta = new DiscoveryDelta(
                            DiscoveryDeltaKind.Delete,
                            normalized,
                            current.RootPath,
                            null,
                            "usn-plain-reparse");
                        if (emitted.Add($"D\0{delta.FullPath}"))
                        {
                            changes.Add(delta);
                        }

                        continue;
                    }

                    if ((record.Reason & (NtfsUsnReason.FileDelete | NtfsUsnReason.RenameOldName)) != 0)
                    {
                        var delta = new DiscoveryDelta(
                            DiscoveryDeltaKind.Delete,
                            normalized,
                            current.RootPath,
                            null,
                            (record.Reason & NtfsUsnReason.RenameOldName) != 0
                                ? "usn-rename-old"
                                : "usn-delete");
                        if (emitted.Add($"D\0{delta.FullPath}"))
                        {
                            changes.Add(delta);
                        }
                    }

                    if ((record.Reason & (NtfsUsnReason.FileDelete | NtfsUsnReason.RenameOldName)) == 0)
                    {
                        var timestamp = record.TimestampUtc == DateTimeOffset.MinValue
                            ? DateTimeOffset.UtcNow
                            : record.TimestampUtc;
                        var discovered = new DiscoveredFile(
                            new FileCandidate(normalized, kind, 0, timestamp),
                            DiscoveryPath.NormalizeKey(normalized),
                            current.RootPath,
                            IsCloudPlaceholder(record.Attributes)
                                ? null
                                : new FileSystemIdentity(
                                    current.VolumeSerialNumber,
                                    record.FileReferenceNumber));
                        var delta = new DiscoveryDelta(
                            DiscoveryDeltaKind.Upsert,
                            normalized,
                            current.RootPath,
                            discovered,
                            (record.Reason & NtfsUsnReason.RenameNewName) != 0
                                ? "usn-rename-new"
                                : "usn-created-or-changed");
                        if (emitted.Add($"U\0{delta.FullPath}"))
                        {
                            changes.Add(delta);
                        }
                    }

                    if (changes.Count >= maximumChanges)
                    {
                        break;
                    }
                }
            }

            var next = new DiscoveryJournalCheckpoint(
                SourceId,
                nextCursors.Values.OrderBy(static cursor => cursor.VolumeRoot, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                DateTimeOffset.UtcNow);
            if (reconciliation)
            {
                // A partial journal page is unusable when any path cannot be
                // proven. Discard its mutations and keep the durable cursor at
                // the previous committed position; the caller will perform a
                // complete reconciliation.
                return new DiscoveryDeltaBatch(
                    checkpoint,
                    checkpoint,
                    Array.Empty<DiscoveryDelta>(),
                    HasMore: false,
                    RequiresFullReconciliation: true,
                    code);
            }

            var hasMore = currentVolumes.Any(volume =>
            {
                var cursor = nextCursors[DiscoveryPath.NormalizeKey(volume.RootPath)];
                return cursor.NextUsn < volume.NextUsn;
            });
            return new DiscoveryDeltaBatch(
                checkpoint,
                next,
                changes,
                hasMore,
                reconciliation,
                code);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(CancellationToken cancellationToken) =>
        _watcherProvider is null
            ? EmptyWatchAsync(cancellationToken)
            : _watcherProvider.WatchAsync(cancellationToken);

    public ValueTask<DiscoveredFile?> TryInspectFileAsync(
        string fullPath,
        CancellationToken cancellationToken) =>
        _watcherProvider is null
            ? ValueTask.FromResult<DiscoveredFile?>(null)
            : _watcherProvider.TryInspectFileAsync(fullPath, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcherProvider is not null)
        {
            await _watcherProvider.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private static DiscoveryDeltaBatch Reconciliation(
        DiscoveryJournalCheckpoint previous,
        DiscoveryJournalCheckpoint next,
        string code) =>
        new(previous, next, Array.Empty<DiscoveryDelta>(), false, true, code);

    private static DiscoveryJournalCheckpoint CurrentCheckpoint(IReadOnlyList<NtfsVolumeState> volumes) =>
        new(
            SourceId,
            volumes.Select(static volume => new DiscoveryVolumeCursor(
                    volume.RootPath,
                    volume.JournalId,
                    volume.NextUsn,
                    volume.VolumeSerialNumber))
                .OrderBy(static cursor => cursor.VolumeRoot, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DateTimeOffset.UtcNow);

    private static bool IsCloudPlaceholder(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    private static bool IsPlainFileReparse(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudPlaceholder(attributes);

    private static string? BuildPath(
        string root,
        NtfsNameRecord file,
        IReadOnlyDictionary<ulong, NtfsNameRecord> directories)
    {
        var segments = new List<string> { file.Name };
        var parent = file.ParentFileReferenceNumber;
        var visited = new HashSet<ulong>();
        while (directories.TryGetValue(parent, out var directory))
        {
            if (!visited.Add(parent))
            {
                return null;
            }

            if (directory.ParentFileReferenceNumber == directory.FileReferenceNumber)
            {
                break;
            }

            if (!string.IsNullOrEmpty(directory.Name) && directory.Name != ".")
            {
                segments.Add(directory.Name);
            }

            parent = directory.ParentFileReferenceNumber;
            if (segments.Count > 1024)
            {
                return null;
            }
        }

        segments.Reverse();
        var path = root;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    private void SetProgress(long indexed, long? total, RuntimeServiceState state, string code) =>
        Volatile.Write(ref _progress, new FileDiscoveryProgressSnapshot(
            indexed,
            total,
            state,
            DateTimeOffset.UtcNow,
            "NTFS MFT/USN",
            code));

    private static async IAsyncEnumerable<DiscoveryChangeHint> EmptyWatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private static bool IsRecoverable(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or
        NotSupportedException;
}

[Flags]
internal enum NtfsUsnReason : uint
{
    DataOverwrite = 0x0000_0001,
    DataExtend = 0x0000_0002,
    DataTruncation = 0x0000_0004,
    NamedDataOverwrite = 0x0000_0010,
    NamedDataExtend = 0x0000_0020,
    NamedDataTruncation = 0x0000_0040,
    FileCreate = 0x0000_0100,
    FileDelete = 0x0000_0200,
    EaChange = 0x0000_0400,
    SecurityChange = 0x0000_0800,
    RenameOldName = 0x0000_1000,
    RenameNewName = 0x0000_2000,
    IndexableChange = 0x0000_4000,
    BasicInfoChange = 0x0000_8000,
    HardLinkChange = 0x0001_0000,
    CompressionChange = 0x0002_0000,
    EncryptionChange = 0x0004_0000,
    ObjectIdChange = 0x0008_0000,
    ReparsePointChange = 0x0010_0000,
    StreamChange = 0x0020_0000,
    TransactedChange = 0x0040_0000,
    IntegrityChange = 0x0080_0000,
    Close = 0x8000_0000,
    All = 0xffff_ffff,
}

internal sealed record NtfsVolumeState(
    string RootPath,
    uint VolumeSerialNumber,
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long? EstimatedMftRecords);

internal sealed record NtfsNameRecord(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    bool IsDirectory,
    DateTimeOffset TimestampUtc,
    NtfsUsnReason Reason = 0,
    long Usn = 0,
    long NextUsn = 0,
    FileAttributes Attributes = FileAttributes.Normal);

internal sealed record NtfsJournalRead(
    long NextUsn,
    IReadOnlyList<NtfsNameRecord> Records);

internal interface INtfsChangeSource
{
    IReadOnlyList<NtfsVolumeState> GetVolumes(CancellationToken cancellationToken);

    IAsyncEnumerable<NtfsNameRecord> EnumerateMftAsync(
        NtfsVolumeState volume,
        CancellationToken cancellationToken);

    NtfsJournalRead ReadJournal(
        NtfsVolumeState volume,
        long startUsn,
        int maximumRecords,
        CancellationToken cancellationToken);

    string? TryResolvePathById(NtfsVolumeState volume, ulong fileReferenceNumber, bool directory);
}

internal sealed class WindowsNtfsChangeSource : INtfsChangeSource
{
    private const uint GenericRead = 0x8000_0000;
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint ShareAll = 0x0000_0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint FsctlEnumUsnData = 0x0009_00b3;
    private const uint FsctlReadUsnJournal = 0x0009_00bb;
    private const uint FsctlQueryUsnJournal = 0x0009_00f4;
    private const uint FsctlGetNtfsVolumeData = 0x0009_0064;
    private const int ErrorHandleEof = 38;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int BufferSize = 1024 * 1024;

    public IReadOnlyList<NtfsVolumeState> GetVolumes(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<NtfsVolumeState>();
        }

        var result = new List<NtfsVolumeState>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady ||
                    !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var root = DiscoveryPath.Normalize(drive.RootDirectory.FullName);
                using var handle = OpenVolume(root);
                var journal = QueryJournal(handle);
                var estimated = TryGetEstimatedMftRecords(handle);
                var serial = GetVolumeSerial(root);
                result.Add(new NtfsVolumeState(
                    root,
                    serial,
                    journal.UsnJournalId,
                    journal.FirstUsn,
                    journal.NextUsn,
                    estimated));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A single inaccessible volume must not disable other volumes.
            }
        }

        return result;
    }

    public async IAsyncEnumerable<NtfsNameRecord> EnumerateMftAsync(
        NtfsVolumeState volume,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var handle = OpenVolume(volume.RootPath);
        var input = new MftEnumData
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue,
        };
        var output = new byte[BufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DeviceIoControl(
                    handle,
                    FsctlEnumUsnData,
                    ref input,
                    Marshal.SizeOf<MftEnumData>(),
                    output,
                    output.Length,
                    out var bytesReturned,
                    nint.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorHandleEof)
                {
                    yield break;
                }

                throw new IOException($"FSCTL_ENUM_USN_DATA failed (Win32={error}).");
            }

            if (bytesReturned < sizeof(long))
            {
                yield break;
            }

            input.StartFileReferenceNumber = BinaryPrimitives.ReadInt64LittleEndian(output);
            foreach (var record in ParseRecords(output.AsSpan(sizeof(long), bytesReturned - sizeof(long))))
            {
                yield return record;
            }

            await Task.Yield();
        }
    }

    public NtfsJournalRead ReadJournal(
        NtfsVolumeState volume,
        long startUsn,
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = OpenVolume(volume.RootPath);
        var input = new ReadUsnJournalData
        {
            StartUsn = startUsn,
            ReasonMask = (uint)NtfsUsnReason.All,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = volume.JournalId,
        };
        var output = new byte[BufferSize];
        if (!DeviceIoControl(
                handle,
                FsctlReadUsnJournal,
                ref input,
                Marshal.SizeOf<ReadUsnJournalData>(),
                output,
                output.Length,
                out var bytesReturned,
                nint.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorJournalDeleteInProgress or ErrorJournalNotActive)
            {
                throw new IOException($"USN journal is unavailable (Win32={error}).");
            }

            throw new IOException($"FSCTL_READ_USN_JOURNAL failed (Win32={error}).");
        }

        if (bytesReturned < sizeof(long))
        {
            return new NtfsJournalRead(startUsn, Array.Empty<NtfsNameRecord>());
        }

        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(output);
        var records = ParseRecords(output.AsSpan(sizeof(long), bytesReturned - sizeof(long)))
            .Take(maximumRecords)
            .ToArray();
        if (records.Length == maximumRecords && records[^1].NextUsn > 0)
        {
            nextUsn = records[^1].NextUsn;
        }

        return new NtfsJournalRead(nextUsn, records);
    }

    public string? TryResolvePathById(
        NtfsVolumeState volume,
        ulong fileReferenceNumber,
        bool directory)
    {
        try
        {
            using var volumeHandle = OpenVolume(volume.RootPath);
            var descriptor = new FileIdDescriptor
            {
                Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
                Type = 0,
                FileId = fileReferenceNumber,
            };
            using var fileHandle = OpenFileById(
                volumeHandle,
                ref descriptor,
                FileReadAttributes,
                ShareAll,
                nint.Zero,
                directory ? BackupSemantics : 0);
            if (fileHandle.IsInvalid)
            {
                return null;
            }

            var buffer = new char[32_768];
            var length = GetFinalPathNameByHandle(fileHandle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }

            return NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<NtfsNameRecord> ParseRecords(ReadOnlySpan<byte> buffer)
    {
        var results = new List<NtfsNameRecord>();
        var offset = 0;
        while (offset + 60 <= buffer.Length)
        {
            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            if (recordLength < 60 || recordLength > buffer.Length - offset)
            {
                break;
            }

            var record = buffer.Slice(offset, checked((int)recordLength));
            var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            if (majorVersion == 2)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
                if (nameOffset + nameLength <= record.Length && (nameLength & 1) == 0)
                {
                    var timestampValue = BinaryPrimitives.ReadInt64LittleEndian(record[32..]);
                    var timestamp = timestampValue > 0
                        ? DateTimeOffset.FromFileTime(timestampValue)
                        : DateTimeOffset.MinValue;
                    var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                    var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
                    results.Add(new NtfsNameRecord(
                        BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
                        MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength)).ToString(),
                        (attributes & FileAttributes.Directory) != 0,
                        timestamp,
                        (NtfsUsnReason)BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
                        usn,
                        usn > 0 ? checked(usn + recordLength) : 0,
                        attributes));
                }
            }

            offset += checked((int)recordLength);
        }

        return results;
    }

    private static SafeFileHandle OpenVolume(string root)
    {
        var volumePath = $@"\\.\{root.TrimEnd('\\')}";
        var handle = CreateFile(
            volumePath,
            GenericRead,
            ShareAll,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Unable to open NTFS volume {root} (Win32={error}).");
        }

        return handle;
    }

    private static UsnJournalData QueryJournal(SafeFileHandle handle)
    {
        if (!DeviceIoControl(
                handle,
                FsctlQueryUsnJournal,
                nint.Zero,
                0,
                out UsnJournalData output,
                Marshal.SizeOf<UsnJournalData>(),
                out _,
                nint.Zero))
        {
            throw new IOException($"FSCTL_QUERY_USN_JOURNAL failed (Win32={Marshal.GetLastWin32Error()}).");
        }

        return output;
    }

    private static long? TryGetEstimatedMftRecords(SafeFileHandle handle)
    {
        if (!DeviceIoControl(
                handle,
                FsctlGetNtfsVolumeData,
                nint.Zero,
                0,
                out NtfsVolumeData output,
                Marshal.SizeOf<NtfsVolumeData>(),
                out _,
                nint.Zero) ||
            output.BytesPerFileRecordSegment == 0 ||
            output.MftValidDataLength <= 0)
        {
            return null;
        }

        return output.MftValidDataLength / output.BytesPerFileRecordSegment;
    }

    private static uint GetVolumeSerial(string root)
    {
        if (!GetVolumeInformation(
                root,
                null,
                0,
                out var serial,
                out _,
                out _,
                null,
                0))
        {
            return 0;
        }

        return serial;
    }

    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumData
    {
        internal long StartFileReferenceNumber;
        internal long LowUsn;
        internal long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData
    {
        internal long StartUsn;
        internal uint ReasonMask;
        internal uint ReturnOnlyOnClose;
        internal ulong Timeout;
        internal ulong BytesToWaitFor;
        internal ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalData
    {
        internal ulong UsnJournalId;
        internal long FirstUsn;
        internal long NextUsn;
        internal long LowestValidUsn;
        internal long MaxUsn;
        internal ulong MaximumSize;
        internal ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeData
    {
        internal long VolumeSerialNumber;
        internal long NumberSectors;
        internal long TotalClusters;
        internal long FreeClusters;
        internal long TotalReserved;
        internal uint BytesPerSector;
        internal uint BytesPerCluster;
        internal uint BytesPerFileRecordSegment;
        internal uint ClustersPerFileRecordSegment;
        internal long MftValidDataLength;
        internal long MftStartLcn;
        internal long Mft2StartLcn;
        internal long MftZoneStart;
        internal long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        internal uint Size;
        internal int Type;
        internal ulong FileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        ref MftEnumData input,
        int inputSize,
        [Out] byte[] output,
        int outputSize,
        out int bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        ref ReadUsnJournalData input,
        int inputSize,
        [Out] byte[] output,
        int outputSize,
        out int bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint input,
        int inputSize,
        out UsnJournalData output,
        int outputSize,
        out int bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint input,
        int inputSize,
        out NtfsVolumeData output,
        int outputSize,
        out int bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        char[]? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char[]? fileSystemNameBuffer,
        uint fileSystemNameSize);
}
