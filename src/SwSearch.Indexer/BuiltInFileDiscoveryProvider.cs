using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

/// <summary>
/// Self-contained, read-only SOLIDWORKS file discovery. It uses only framework
/// APIs and Windows system calls, never starts SOLIDWORKS and never follows a
/// reparse point.
/// </summary>
public sealed class BuiltInFileDiscoveryProvider :
    IIncrementalFileDiscoveryProvider,
    IPersistedDiscoveryRootSink,
    IDiscoveryProgressSource,
    IAsyncDisposable
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;
    private readonly BuiltInDiscoveryOptions _options;
    private readonly IDiscoveryFileSystem _fileSystem;
    private readonly IDiscoveryRootSource _rootSource;
    private readonly SystemLoadMonitor _ioLoadMonitor = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly HashSet<string> _knownSuccessfulRoots;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _safeDirectoryCache =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DiscoveryRootState>? _cachedRoots;
    private DateTimeOffset _rootCacheExpiresUtc;
    private DiscoveryScanReport _lastReport = DiscoveryScanReport.Empty;
    private FileDiscoveryProgressSnapshot _progress = new(
        0,
        null,
        RuntimeServiceState.Idle,
        DateTimeOffset.UtcNow,
        "Built-in directory scan",
        "BUILTIN_DISCOVERY_IDLE");
    private bool _disposed;

    public BuiltInFileDiscoveryProvider(BuiltInDiscoveryOptions? options = null)
        : this(options ?? new BuiltInDiscoveryOptions(), new SystemDiscoveryFileSystem(), new SystemDiscoveryRootSource())
    {
    }

    internal BuiltInFileDiscoveryProvider(
        BuiltInDiscoveryOptions options,
        IDiscoveryFileSystem fileSystem,
        IDiscoveryRootSource rootSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(rootSource);
        ValidateOptions(options);
        _options = options;
        _fileSystem = fileSystem;
        _rootSource = rootSource;
        _knownSuccessfulRoots = new HashSet<string>(
            options.PreviouslyIndexedRoots
                .Where(IsEligiblePersistedRoot)
                .Select(DiscoveryPath.NormalizeKey),
            StringComparer.OrdinalIgnoreCase);
    }

    public TimeSpan RecommendedReconciliationInterval => _options.ReconciliationInterval;

    public DiscoveryScanReport LastReport => Volatile.Read(ref _lastReport);

    public FileDiscoveryProgressSnapshot DiscoveryProgress => Volatile.Read(ref _progress);

    public void RememberPreviouslyIndexedRoots(IEnumerable<string> roots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(roots);
        lock (_knownSuccessfulRoots)
        {
            foreach (var root in roots)
            {
                if (IsEligiblePersistedRoot(root))
                {
                    _knownSuccessfulRoots.Add(DiscoveryPath.NormalizeKey(root));
                }
            }

            _cachedRoots = null;
        }
    }

    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var roots = ResolveRoots(forceRefresh: true);
            var online = roots.Count(static root => root.IsOnline);
            var offlineExpected = roots.Count(static root => !root.IsOnline && root.IsExpected);
            if (offlineExpected > 0)
            {
                return ValueTask.FromResult(new RuntimeCapability(
                    CapabilityState.Degraded,
                    "BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE",
                    "一个或多个已建立索引的磁盘当前离线；为避免误删，暂缓完整对账。",
                    new Dictionary<string, string>
                    {
                        ["offlineKnownRoots"] = offlineExpected
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
            }

            if (online > 0)
            {
                return ValueTask.FromResult(new RuntimeCapability(
                    CapabilityState.Available,
                    "BUILTIN_DISCOVERY_READY",
                    "内置文件发现已就绪；不依赖 Everything。",
                    new Dictionary<string, string>
                    {
                        ["onlineRoots"] = online.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["networkRoots"] = roots.Count(static root => root.Kind == DiscoveryRootKind.ExplicitNetwork)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["nonFixedOnlineRoots"] = roots.Count(static root =>
                                root.IsOnline && root.Kind != DiscoveryRootKind.Fixed)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
            }

            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Degraded,
                "BUILTIN_DISCOVERY_NO_ONLINE_ROOT",
                "当前没有可访问的本地搜索卷；保留已有缓存。"));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Degraded,
                "BUILTIN_DISCOVERY_PROBE_FAILED",
                "内置文件发现暂时无法检查磁盘；保留已有缓存。",
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

    public IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
        CancellationToken cancellationToken) =>
        DiscoverEntriesForRootsAsync(rootPaths: null, cancellationToken);

    /// <summary>
    /// Scans a verified subset of the currently eligible roots.  This is used
    /// by the fast discovery chain when MFT has already proven a different
    /// fixed volume complete.  It is deliberately internal: callers cannot
    /// use it to claim a complete snapshot for roots they did not scan.
    /// </summary>
    internal async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesForRootsAsync(
        IReadOnlyCollection<string>? rootPaths,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedAtUtc = DateTimeOffset.UtcNow;
    var accumulators = new List<RootReportAccumulator>();
    long visitedEntries = 0;
    long solidWorksFiles = 0;
        var incompleteBecauseRootOffline = false;
        var completedNormally = false;
        try
        {
            SetProgress(0, 0, RuntimeServiceState.Running, "BUILTIN_DISCOVERY_SCANNING");
            HashSet<string>? requestedRootKeys = null;
            if (rootPaths is not null)
            {
                requestedRootKeys = new HashSet<string>(
                    rootPaths.Select(DiscoveryPath.NormalizeKey),
                    StringComparer.OrdinalIgnoreCase);
            }

            var roots = ResolveRoots(forceRefresh: true)
                .Where(root => requestedRootKeys is null ||
                               requestedRootKeys.Contains(DiscoveryPath.NormalizeKey(root.RootPath)))
                .ToArray();
            if (roots.Length == 0)
            {
                incompleteBecauseRootOffline = true;
            }

            if (requestedRootKeys is not null)
            {
                var resolvedRootKeys = roots
                    .Select(static root => DiscoveryPath.NormalizeKey(root.RootPath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var missingRoot in requestedRootKeys.Where(key => !resolvedRootKeys.Contains(key)))
                {
                    var report = new RootReportAccumulator(new DiscoveryRootState(
                        DiscoveryPath.Normalize(missingRoot),
                        DiscoveryRootKind.ExplicitLocal,
                        IsOnline: false,
                        IsExpected: true));
                    report.RecordError("ROOT_NOT_RESOLVED");
                    accumulators.Add(report);
                    incompleteBecauseRootOffline = true;
                }
            }

            var throttle = new DiscoveryThrottle(_options);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var report = new RootReportAccumulator(root);
                accumulators.Add(report);
                if (!root.IsOnline)
                {
                    if (root.IsExpected)
                    {
                        if (root.IsReparsePoint)
                        {
                            report.ReparsePointsSkipped++;
                            report.RecordError("ROOT_REPARSE_SKIPPED");
                        }
                        else
                        {
                            report.RecordError("ROOT_OFFLINE");
                        }

                        incompleteBecauseRootOffline = true;
                    }

                    continue;
                }

                var stack = new Stack<string>();
                stack.Push(root.RootPath);
                var rootEnumerationFailed = false;
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = stack.Pop();
                    IEnumerator<DiscoveryFileSystemItem>? enumerator = null;
                    try
                    {
                        enumerator = _fileSystem.EnumerateChildren(directory).GetEnumerator();
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        var isRoot = DiscoveryPath.NormalizeKey(directory) ==
                                     DiscoveryPath.NormalizeKey(root.RootPath);
                        report.RecordError(ErrorCode(exception));
                        if (!isRoot && IsExpectedTerminalSkip(exception))
                        {
                            report.RecordTerminalSkippedPrefix(directory);
                        }
                        else
                        {
                            report.RecordIncompletePrefix(directory);
                        }
                        if (isRoot)
                        {
                            rootEnumerationFailed = true;
                        }

                        continue;
                    }

                    using (enumerator)
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            DiscoveryFileSystemItem item;
                            bool moved;
                            try
                            {
                                moved = enumerator.MoveNext();
                                item = moved ? enumerator.Current : default;
                            }
                            catch (Exception exception) when (IsRecoverable(exception))
                            {
                                var isRoot = DiscoveryPath.NormalizeKey(directory) ==
                                             DiscoveryPath.NormalizeKey(root.RootPath);
                                report.RecordError(ErrorCode(exception));
                                if (!isRoot && IsExpectedTerminalSkip(exception))
                                {
                                    report.RecordTerminalSkippedPrefix(directory);
                                }
                                else
                                {
                                    report.RecordIncompletePrefix(directory);
                                }
                                if (isRoot)
                                {
                                    rootEnumerationFailed = true;
                                }

                                break;
                            }

                            if (!moved)
                            {
                                break;
                            }

                            report.VisitedEntries++;
                            visitedEntries++;
                            if ((visitedEntries & 0x3ff) == 0)
                            {
                                SetProgress(
                                    solidWorksFiles,
                                    // Live candidate count can only grow during
                                    // a scan, so it is a lower bound for the
                                    // total. The committed baseline from the
                                    // runtime supplies the stable denominator.
                                    null,
                                    RuntimeServiceState.Running,
                                    "BUILTIN_DISCOVERY_SCANNING");
                                // Mid-scan IO pacing: a running enumeration
                                // must also yield to foreground work, not
                                // only new scans.
                                var loadDelay = DiscoveryIoPacing.DelayFor(_ioLoadMonitor.Sample());
                                if (loadDelay > TimeSpan.Zero)
                                {
                                    await Task.Delay(loadDelay, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            await throttle.OnEntryAsync(cancellationToken).ConfigureAwait(false);
                            if (item.ErrorCode is not null)
                            {
                                report.RecordError(item.ErrorCode);
                                if (IsExpectedTerminalSkipCode(item.ErrorCode))
                                {
                                    report.RecordTerminalSkippedPrefix(item.FullPath);
                                }
                                else
                                {
                                    report.RecordIncompletePrefix(item.FullPath);
                                }
                                continue;
                            }

                            if (item.IsDirectory)
                            {
                                // Never traverse a directory reparse point. This is the
                                // boundary that prevents junction/symlink escapes. A file
                                // reparse point is different: its directory-entry metadata
                                // is safe to index without opening the target.
                                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    report.ReparsePointsSkipped++;
                                    report.RecordTerminalSkippedPrefix(item.FullPath);
                                    continue;
                                }

                                if (IsOfflinePlaceholder(item.Attributes))
                                {
                                    report.OfflineFilesSkipped++;
                                    report.RecordTerminalSkippedPrefix(item.FullPath);
                                    continue;
                                }

                                stack.Push(item.FullPath);
                                continue;
                            }

                            if (!DiscoveryPath.TryGetDocumentKind(item.FullPath, out var kind))
                            {
                                continue;
                            }

                            var cloudPlaceholder = IsOfflinePlaceholder(item.Attributes);
                            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 &&
                                !cloudPlaceholder)
                            {
                                // A plain file symlink can escape the indexed root. It is
                                // not a cloud placeholder and must never become a metadata
                                // candidate. Cloud providers combine ReparsePoint with an
                                // Offline/Recall flag, which is the only safe name-only case.
                                report.ReparsePointsSkipped++;
                                continue;
                            }

                            var candidate = new FileCandidate(
                                DiscoveryPath.Normalize(item.FullPath),
                                kind,
                                item.Size,
                                item.LastWriteTimeUtc);
                            var metadataOnly = cloudPlaceholder;
                            var identity = _options.CaptureFileIdentity && !metadataOnly
                                ? _fileSystem.TryGetIdentity(item.FullPath)
                                : null;
                            report.SolidWorksFiles++;
                            solidWorksFiles++;
                            yield return new DiscoveredFile(
                                candidate,
                                DiscoveryPath.NormalizeKey(item.FullPath),
                                root.RootPath,
                                identity);
                        }
                    }
                }

                report.WasRootEnumerationSuccessful = !rootEnumerationFailed;
                if (rootEnumerationFailed && root.IsExpected)
                {
                    incompleteBecauseRootOffline = true;
                }
                else if (!rootEnumerationFailed)
                {
                    lock (_knownSuccessfulRoots)
                    {
                        _knownSuccessfulRoots.Add(DiscoveryPath.NormalizeKey(root.RootPath));
                        _cachedRoots = null;
                    }
                }
            }

            completedNormally = true;
        }
        finally
        {
            var report = BuildReport(
                startedAtUtc,
                accumulators,
                completedNormally && !incompleteBecauseRootOffline);
            Volatile.Write(ref _lastReport, report);
            SetProgress(
                solidWorksFiles,
                solidWorksFiles,
                completedNormally && !incompleteBecauseRootOffline
                    ? RuntimeServiceState.Idle
                    : RuntimeServiceState.Unavailable,
                completedNormally && !incompleteBecauseRootOffline
                    ? "BUILTIN_DISCOVERY_COMPLETE"
                    : "BUILTIN_DISCOVERY_INCOMPLETE");
            _scanGate.Release();
        }

        if (incompleteBecauseRootOffline)
        {
            throw new DiscoverySnapshotIncompleteException(LastReport);
        }
    }

    private void SetProgress(long indexed, long? estimatedTotal, RuntimeServiceState state, string code) =>
        Volatile.Write(ref _progress, new FileDiscoveryProgressSnapshot(
            indexed,
            estimatedTotal,
            state,
            DateTimeOffset.UtcNow,
            "Built-in directory scan",
            code));

    public ValueTask<DiscoveredFile?> TryInspectFileAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!DiscoveryPath.TryGetDocumentKind(fullPath, out var kind))
        {
            return ValueTask.FromResult<DiscoveredFile?>(null);
        }

        try
        {
            var item = _fileSystem.Inspect(fullPath);
            if (item.ErrorCode is not null || item.IsDirectory)
            {
                return ValueTask.FromResult<DiscoveredFile?>(null);
            }

            var cloudPlaceholder = IsOfflinePlaceholder(item.Attributes);
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && !cloudPlaceholder)
            {
                return ValueTask.FromResult<DiscoveredFile?>(null);
            }

            var normalized = DiscoveryPath.Normalize(fullPath);
            var roots = ResolveRoots();
            var root = roots
                .Where(rootState => DiscoveryPath.IsWithinRoot(normalized, rootState.RootPath))
                .OrderByDescending(static rootState => rootState.RootPath.Length)
                .FirstOrDefault();
            if (root is null || !root.IsOnline)
            {
                return ValueTask.FromResult<DiscoveredFile?>(null);
            }

            if (!IsPathUnderNonReparseTree(normalized, root.RootPath))
            {
                return ValueTask.FromResult<DiscoveredFile?>(null);
            }

            var metadataOnly = cloudPlaceholder;
            var identity = _options.CaptureFileIdentity && !metadataOnly
                ? _fileSystem.TryGetIdentity(normalized)
                : null;
            return ValueTask.FromResult<DiscoveredFile?>(new DiscoveredFile(
                new FileCandidate(normalized, kind, item.Size, item.LastWriteTimeUtc),
                DiscoveryPath.NormalizeKey(normalized),
                root.RootPath,
                identity));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return ValueTask.FromResult<DiscoveredFile?>(null);
        }
    }

    internal IReadOnlyList<DiscoveryRootState> GetRootStatesForAccelerator(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ResolveRoots(forceRefresh: true);
    }

    internal DiscoveredFile? TryCreateAcceleratedEntry(
        FileCandidate candidate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidate);
        if (DiscoveryPath.IsExcludedSystemPath(candidate.FullPath))
        {
            return null;
        }
        var normalized = DiscoveryPath.Normalize(candidate.FullPath);
        var root = ResolveRoots()
            .Where(rootState => rootState.IsOnline && !rootState.IsReparsePoint &&
                                DiscoveryPath.IsWithinRoot(normalized, rootState.RootPath))
            .OrderByDescending(static rootState => rootState.RootPath.Length)
            .FirstOrDefault();
        if (root is null)
        {
            return null;
        }

        // Everything already supplies the size and modified timestamp from its
        // index. Avoid reopening 150k files here: identity remains optional and
        // is populated by built-in scans or on-demand inspection.
        return new DiscoveredFile(
            candidate with { FullPath = normalized },
            DiscoveryPath.NormalizeKey(normalized),
            root.RootPath,
            Identity: null);
    }

    public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = Channel.CreateBounded<DiscoveryChangeHint>(new BoundedChannelOptions(
            _options.WatcherQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var watchers = new List<FileSystemWatcher>();
        var queueOverflowed = 0;

        void Publish(DiscoveryChangeHint hint)
        {
            if (!channel.Writer.TryWrite(hint))
            {
                Interlocked.Exchange(ref queueOverflowed, 1);
            }
        }

        foreach (var root in ResolveRoots(forceRefresh: true).Where(static root => root.IsOnline))
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root.RootPath)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = _options.WatcherBufferSize,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*",
                };
                var rootPath = root.RootPath;
                watcher.Created += (_, args) => PublishFileHint(
                    Publish, DiscoveryChangeKind.CreatedOrChanged, args.FullPath, null, rootPath, "watcher-created");
                watcher.Changed += (_, args) => PublishFileHint(
                    Publish, DiscoveryChangeKind.CreatedOrChanged, args.FullPath, null, rootPath, "watcher-changed");
                watcher.Deleted += (_, args) => PublishFileHint(
                    Publish, DiscoveryChangeKind.Deleted, args.FullPath, null, rootPath, "watcher-deleted");
                watcher.Renamed += (_, args) => PublishRenameHint(Publish, args, rootPath);
                watcher.Error += (_, _) => Publish(new DiscoveryChangeHint(
                    DiscoveryChangeKind.ReconciliationRequired,
                    null,
                    null,
                    rootPath,
                    "watcher-error"));
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                watcher = null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                watcher?.Dispose();
                Publish(new DiscoveryChangeHint(
                    DiscoveryChangeKind.ReconciliationRequired,
                    null,
                    null,
                    root.RootPath,
                    $"watcher-start-{ErrorCode(exception)}"));
            }
        }

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref queueOverflowed, 0) != 0)
                {
                    yield return new DiscoveryChangeHint(
                        DiscoveryChangeKind.ReconciliationRequired,
                        null,
                        null,
                        string.Empty,
                        "watcher-overflow");
                }

                while (channel.Reader.TryRead(out var hint))
                {
                    yield return hint;
                }
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            channel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private static void PublishFileHint(
        Action<DiscoveryChangeHint> publish,
        DiscoveryChangeKind kind,
        string fullPath,
        string? previousFullPath,
        string rootPath,
        string reason)
    {
        if (DiscoveryPath.TryGetDocumentKind(fullPath, out _))
        {
            publish(new DiscoveryChangeHint(kind, fullPath, previousFullPath, rootPath, reason));
        }
    }

    private static void PublishRenameHint(
        Action<DiscoveryChangeHint> publish,
        RenamedEventArgs args,
        string rootPath)
    {
        var oldIsSolidWorks = DiscoveryPath.TryGetDocumentKind(args.OldFullPath, out _);
        var newIsSolidWorks = DiscoveryPath.TryGetDocumentKind(args.FullPath, out _);
        if (oldIsSolidWorks || newIsSolidWorks)
        {
            publish(new DiscoveryChangeHint(
                DiscoveryChangeKind.Renamed,
                newIsSolidWorks ? args.FullPath : null,
                oldIsSolidWorks ? args.OldFullPath : null,
                rootPath,
                "watcher-renamed"));
        }
    }

    private IReadOnlyList<DiscoveryRootState> ResolveRoots(bool forceRefresh = false)
    {
        lock (_knownSuccessfulRoots)
        {
            if (!forceRefresh && _cachedRoots is not null && DateTimeOffset.UtcNow < _rootCacheExpiresUtc)
            {
                return _cachedRoots;
            }

            _cachedRoots = _rootSource.GetRoots(_options, _knownSuccessfulRoots.ToArray());
            _rootCacheExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            return _cachedRoots;
        }
    }

    private bool IsPathUnderNonReparseTree(string fullPath, string rootPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var normalizedRoot = DiscoveryPath.Normalize(rootPath);
        var now = DateTimeOffset.UtcNow;
        var pendingSafeDirectories = new List<string>();
        while (!string.IsNullOrEmpty(directory) &&
               !directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (!DiscoveryPath.IsWithinRoot(directory, normalizedRoot))
            {
                return false;
            }

            var key = DiscoveryPath.NormalizeKey(directory);
            if (_safeDirectoryCache.TryGetValue(key, out var expires) && expires > now)
            {
                CacheSafeDirectories(pendingSafeDirectories, now);
                return true;
            }

            DiscoveryFileSystemItem item;
            try
            {
                item = _fileSystem.Inspect(directory);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return false;
            }

            if (!item.IsDirectory || item.ErrorCode is not null ||
                (item.Attributes & FileAttributes.ReparsePoint) != 0 ||
                IsOfflinePlaceholder(item.Attributes))
            {
                return false;
            }

            pendingSafeDirectories.Add(key);
            directory = Path.GetDirectoryName(directory);
        }

        if (directory is null)
        {
            return false;
        }

        CacheSafeDirectories(pendingSafeDirectories, now);
        return true;
    }

    private void CacheSafeDirectories(IEnumerable<string> directories, DateTimeOffset now)
    {
        if (_safeDirectoryCache.Count >= 50_000)
        {
            _safeDirectoryCache.Clear();
        }

        var expires = now.AddMinutes(2);
        foreach (var directory in directories)
        {
            _safeDirectoryCache[directory] = expires;
        }
    }

    private static DiscoveryScanReport BuildReport(
        DateTimeOffset startedAtUtc,
        IReadOnlyList<RootReportAccumulator> roots,
        bool completeForDeletion)
    {
        var reports = roots.Select(static root => root.ToReport()).ToArray();
        return new DiscoveryScanReport(
            startedAtUtc,
            DateTimeOffset.UtcNow,
            completeForDeletion && reports.All(static root => root.FullyEnumerated),
            reports);
    }

    private static bool IsOfflinePlaceholder(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    private bool IsEligiblePersistedRoot(string root)
    {
        var key = DiscoveryPath.NormalizeKey(root);
        if (_options.ExplicitLocalRoots
            .Concat(_options.ExplicitNetworkRoots)
            .Select(DiscoveryPath.NormalizeKey)
            .Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!_options.DiscoverAllLocalVolumes || key.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var volumeRoot = Path.GetPathRoot(DiscoveryPath.Normalize(root));
        return !string.IsNullOrWhiteSpace(volumeRoot) &&
               DiscoveryPath.NormalizeKey(volumeRoot).Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRecoverable(Exception exception) => exception is
        UnauthorizedAccessException or
        SecurityException or
        DirectoryNotFoundException or
        FileNotFoundException or
        DriveNotFoundException or
        PathTooLongException or
        IOException;

    internal static string ErrorCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => "ACCESS_DENIED",
        DirectoryNotFoundException or FileNotFoundException or DriveNotFoundException => "NOT_FOUND",
        PathTooLongException => "PATH_TOO_LONG",
        IOException => "IO_ERROR",
        _ => "UNKNOWN_ERROR",
    };

    private static bool IsExpectedTerminalSkip(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException or
        DirectoryNotFoundException or FileNotFoundException or PathTooLongException => true,
        IOException io => IsExpectedTransientIoError(io),
        _ => false,
    };

    private static bool IsExpectedTerminalSkipCode(string code) =>
        code.Trim().ToUpperInvariant() is
            "ACCESS_DENIED" or "NOT_FOUND" or "PATH_TOO_LONG" or
            "CLOUD_PLACEHOLDER" or "OFFLINE" or "SHARING_VIOLATION" or
            "LOCK_VIOLATION" or "TEMPORARY_IO";

    private static bool IsExpectedTransientIoError(IOException exception)
    {
        // Keep genuine device/corruption I/O errors visible as a degraded
        // discovery result. Only well-known short-lived Windows conditions are
        // safe to close for this pass; a later reconciliation re-enables them
        // when the directory is observable again.
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is
            21 or    // ERROR_NOT_READY
            32 or    // ERROR_SHARING_VIOLATION
            33 or    // ERROR_LOCK_VIOLATION
            64 or    // ERROR_NETNAME_DELETED
            121 or   // ERROR_SEM_TIMEOUT
            1231 or  // ERROR_NETWORK_UNREACHABLE
            1232 or  // ERROR_NETNAME_DELETED / network busy
            1233;    // ERROR_NETWORK_UNREACHABLE
    }

    private static void ValidateOptions(BuiltInDiscoveryOptions options)
    {
        if (options.YieldEveryEntries is < 1 or > 1_000_000 ||
            options.YieldDelay < TimeSpan.Zero ||
            options.MaximumEntriesPerSecond is < 0 or > 1_000_000 ||
            options.WatcherQueueCapacity is < 16 or > 1_000_000 ||
            options.WatcherBufferSize is < 4 * 1024 or > 64 * 1024 ||
            options.ReconciliationInterval < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        foreach (var root in options.ExplicitLocalRoots
                     .Concat(options.ExplicitNetworkRoots)
                     .Concat(options.PreviouslyIndexedRoots))
        {
            _ = DiscoveryPath.Normalize(root);
        }
    }

    private sealed class RootReportAccumulator(DiscoveryRootState root)
    {
        private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _incompletePrefixes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _terminalSkippedPrefixes = new(StringComparer.OrdinalIgnoreCase);

        internal long VisitedEntries { get; set; }

        internal long SolidWorksFiles { get; set; }

        internal long ReparsePointsSkipped { get; set; }

        internal long OfflineFilesSkipped { get; set; }

        internal bool WasRootEnumerationSuccessful { get; set; }

        internal void RecordError(string code)
        {
            _errors.TryGetValue(code, out var count);
            _errors[code] = count + 1;
        }

        internal void RecordIncompletePrefix(string path) =>
            _incompletePrefixes.Add(DiscoveryPath.Normalize(path));

        internal void RecordTerminalSkippedPrefix(string path)
        {
            var normalized = DiscoveryPath.Normalize(path);
            _incompletePrefixes.Add(normalized);
            _terminalSkippedPrefixes.Add(normalized);
        }

        internal DiscoveryRootReport ToReport() => new(
                root.RootPath,
                root.Kind,
                root.IsOnline,
                root.IsOnline && WasRootEnumerationSuccessful && _incompletePrefixes.Count == 0 && _errors.Count == 0,
                VisitedEntries,
                SolidWorksFiles,
                ReparsePointsSkipped,
                OfflineFilesSkipped,
                new Dictionary<string, int>(_errors, StringComparer.Ordinal))
            {
                IncompletePrefixes = _incompletePrefixes
                    .OrderBy(static path => path.Length)
                    .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                TerminalSkippedPrefixes = _terminalSkippedPrefixes
                    .OrderBy(static path => path.Length)
                    .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
    }

    private sealed class DiscoveryThrottle(BuiltInDiscoveryOptions options)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _entries;

        internal async ValueTask OnEntryAsync(CancellationToken cancellationToken)
        {
            _entries++;
            if (_entries % options.YieldEveryEntries != 0)
            {
                return;
            }

            var delay = options.YieldDelay;
            if (options.MaximumEntriesPerSecond > 0)
            {
                var expected = TimeSpan.FromSeconds((double)_entries / options.MaximumEntriesPerSecond);
                var rateDelay = expected - _stopwatch.Elapsed;
                if (rateDelay > delay)
                {
                    delay = rateDelay;
                }
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }
}

internal sealed record DiscoveryRootState(
    string RootPath,
    DiscoveryRootKind Kind,
    bool IsOnline,
    bool IsExpected,
    bool IsReparsePoint = false);

internal readonly record struct DiscoveryFileSystemItem(
    string FullPath,
    bool IsDirectory,
    FileAttributes Attributes,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string? ErrorCode = null);

internal interface IDiscoveryRootSource
{
    IReadOnlyList<DiscoveryRootState> GetRoots(
        BuiltInDiscoveryOptions options,
        IReadOnlyCollection<string> knownSuccessfulRoots);
}

internal interface IDiscoveryFileSystem
{
    IEnumerable<DiscoveryFileSystemItem> EnumerateChildren(string directory);

    DiscoveryFileSystemItem Inspect(string fullPath);

    FileSystemIdentity? TryGetIdentity(string fullPath);
}

internal sealed class SystemDiscoveryRootSource : IDiscoveryRootSource
{
    public IReadOnlyList<DiscoveryRootState> GetRoots(
        BuiltInDiscoveryOptions options,
        IReadOnlyCollection<string> knownSuccessfulRoots)
    {
        var known = new HashSet<string>(knownSuccessfulRoots, StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, DiscoveryRootState>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in options.DiscoverAllLocalVolumes ? DriveInfo.GetDrives() : Array.Empty<DriveInfo>())
        {
            DriveType type;
            try
            {
                type = drive.DriveType;
            }
            catch (Exception exception) when (BuiltInFileDiscoveryProvider.IsRecoverable(exception))
            {
                continue;
            }

            if (type is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            var path = DiscoveryPath.Normalize(drive.Name);
            var key = DiscoveryPath.NormalizeKey(path);
            bool online;
            try
            {
                online = drive.IsReady;
            }
            catch (Exception exception) when (BuiltInFileDiscoveryProvider.IsRecoverable(exception))
            {
                online = false;
            }

            var expected = known.Contains(key);
            if (online || expected)
            {
                roots[key] = new DiscoveryRootState(
                    path,
                    type == DriveType.Fixed ? DiscoveryRootKind.Fixed : DiscoveryRootKind.Removable,
                    online,
                    expected);
            }
        }

        AddExplicitRoots(options.ExplicitLocalRoots, DiscoveryRootKind.ExplicitLocal, roots);
        AddExplicitRoots(options.ExplicitNetworkRoots, DiscoveryRootKind.ExplicitNetwork, roots);

        foreach (var knownRoot in known)
        {
            if (roots.ContainsKey(knownRoot))
            {
                continue;
            }

            var path = DiscoveryPath.Normalize(knownRoot);
            var status = GetExplicitRootStatus(path);
            roots[knownRoot] = new DiscoveryRootState(
                path,
                path.StartsWith("\\\\", StringComparison.Ordinal)
                    ? DiscoveryRootKind.ExplicitNetwork
                    : DiscoveryRootKind.ExplicitLocal,
                IsOnline: status.Online,
                IsExpected: true,
                IsReparsePoint: status.ReparsePoint);
        }

        var ordered = roots.Values
            .OrderBy(static root => root.RootPath.Length)
            .ThenBy(static root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = new List<DiscoveryRootState>(ordered.Length);
        foreach (var candidate in ordered)
        {
            if (selected.Any(parent =>
                    parent.IsOnline && DiscoveryPath.IsWithinRoot(candidate.RootPath, parent.RootPath)))
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected;
    }

    private static void AddExplicitRoots(
        IReadOnlyCollection<string> configured,
        DiscoveryRootKind kind,
        IDictionary<string, DiscoveryRootState> roots)
    {
        foreach (var configuredRoot in configured)
        {
            var path = DiscoveryPath.Normalize(configuredRoot);
            var key = DiscoveryPath.NormalizeKey(path);
            var status = GetExplicitRootStatus(path);
            roots[key] = new DiscoveryRootState(
                path,
                kind,
                IsOnline: status.Online,
                IsExpected: true,
                IsReparsePoint: status.ReparsePoint);
        }
    }

    private static (bool Online, bool ReparsePoint) GetExplicitRootStatus(string path)
    {
        if (!Directory.Exists(path))
        {
            return (false, false);
        }

        try
        {
            var reparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            return (!reparse, reparse);
        }
        catch (Exception exception) when (BuiltInFileDiscoveryProvider.IsRecoverable(exception))
        {
            return (false, false);
        }
    }
}

internal sealed class SystemDiscoveryFileSystem : IDiscoveryFileSystem
{
    private const uint ShareRead = 0x0000_0001;
    private const uint ShareWrite = 0x0000_0002;
    private const uint ShareDelete = 0x0000_0004;
    private const uint OpenExisting = 3;

    public IEnumerable<DiscoveryFileSystemItem> EnumerateChildren(string directory)
    {
        // FileSystemEnumerable projects the WIN32_FIND_DATA returned by the
        // directory enumeration itself. In particular, cloud placeholder
        // length/attributes/timestamp are captured without File.Open,
        // FileStream, CreateFile or reading file content, so enumeration does
        // not hydrate OneDrive files.
        return new FileSystemEnumerable<DiscoveryFileSystemItem>(
            directory,
            static (ref FileSystemEntry entry) => new DiscoveryFileSystemItem(
                entry.ToFullPath(),
                (entry.Attributes & FileAttributes.Directory) != 0,
                entry.Attributes,
                (entry.Attributes & FileAttributes.Directory) != 0 ? 0 : entry.Length,
                entry.LastWriteTimeUtc),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
            });
    }

    public DiscoveryFileSystemItem Inspect(string fullPath)
    {
        var attributes = File.GetAttributes(fullPath);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory)
        {
            var directory = new DirectoryInfo(fullPath);
            return new DiscoveryFileSystemItem(
                directory.FullName,
                IsDirectory: true,
                attributes,
                0,
                directory.LastWriteTimeUtc);
        }

        if (!DiscoveryPath.TryGetDocumentKind(fullPath, out _))
        {
            return new DiscoveryFileSystemItem(
                Path.GetFullPath(fullPath),
                IsDirectory: false,
                attributes,
                0,
                DateTimeOffset.MinValue);
        }

        var file = new FileInfo(fullPath);
        return new DiscoveryFileSystemItem(
            file.FullName,
            IsDirectory: false,
            attributes,
            file.Length,
            file.LastWriteTimeUtc);
    }

    public FileSystemIdentity? TryGetIdentity(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = CreateFile(
            fullPath,
            desiredAccess: 0,
            ShareRead | ShareWrite | ShareDelete,
            nint.Zero,
            OpenExisting,
            flagsAndAttributes: 0,
            nint.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new FileSystemIdentity(information.VolumeSerialNumber, fileIndex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}
