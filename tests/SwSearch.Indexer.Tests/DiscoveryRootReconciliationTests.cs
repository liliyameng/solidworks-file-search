using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class DiscoveryRootReconciliationTests
{
    private static readonly DateTimeOffset Modified = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IncrementalExplicitLocalSubdirectory_DeletesOnlyInsideExactRoot()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var keep = Candidate(@"C:\Library\KeepPart.SLDPRT");
        var missing = Candidate(@"C:\Library\MissingPart.SLDPRT");
        var sibling = Candidate(@"C:\Library2\SiblingPart.SLDPRT");
        var elsewhere = Candidate(@"C:\Elsewhere\OtherPart.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([keep, missing, sibling, elsewhere]),
            CancellationToken.None);

        var root = @"c:\library\";
        var provider = CompleteProvider(root, DiscoveryRootKind.ExplicitLocal, [keep]);
        await store.ApplyDiscoverySnapshotAsync(provider, "explicit-local", CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[keep.FullPath]);
        Assert.Equal(1, states[missing.FullPath]);
        Assert.Equal(0, states[sibling.FullPath]);
        Assert.Equal(0, states[elsewhere.FullPath]);
        Assert.Equal(3, (await store.GetStatusAsync()).PresentFileCount);
        Assert.Contains(@"C:\LIBRARY", await store.GetIndexedRootsAsync(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncrementalUncSubdirectory_DeletesOnlyInsideExactRoot()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var keep = Candidate(@"\\server\share\folder\KeepPart.SLDPRT");
        var missing = Candidate(@"\\server\share\folder\MissingPart.SLDPRT");
        var sibling = Candidate(@"\\server\share\folder2\SiblingPart.SLDPRT");
        var elsewhere = Candidate(@"\\server\share\elsewhere\OtherPart.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([keep, missing, sibling, elsewhere]),
            CancellationToken.None);

        var root = @"\\SERVER\SHARE\FOLDER\";
        var provider = CompleteProvider(root, DiscoveryRootKind.ExplicitNetwork, [keep]);
        await store.ApplyDiscoverySnapshotAsync(provider, "explicit-unc", CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[keep.FullPath]);
        Assert.Equal(1, states[missing.FullPath]);
        Assert.Equal(0, states[sibling.FullPath]);
        Assert.Equal(0, states[elsewhere.FullPath]);
        Assert.Equal(3, (await store.GetStatusAsync()).PresentFileCount);
        Assert.Contains(
            @"\\SERVER\SHARE\FOLDER",
            await store.GetIndexedRootsAsync(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyCandidateSnapshot_StillReconcilesAtDriveRoot()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var keep = Candidate(@"C:\First\KeepPart.SLDPRT");
        var missing = Candidate(@"C:\Second\MissingPart.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([keep, missing]), CancellationToken.None);

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([keep]), CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[keep.FullPath]);
        Assert.Equal(1, states[missing.FullPath]);
        Assert.Equal(1, (await store.GetStatusAsync()).PresentFileCount);
        Assert.Contains(@"C:\", await store.GetIndexedRootsAsync(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersistedExpandedAncestor_DoesNotBroadenExplicitOnlyConfiguration()
    {
        var configuredRoot = @"C:\Library";
        var source = new CapturingRootSource(new DiscoveryRootState(
            configuredRoot,
            DiscoveryRootKind.ExplicitLocal,
            IsOnline: true,
            IsExpected: true));
        await using var provider = new BuiltInFileDiscoveryProvider(
            new BuiltInDiscoveryOptions
            {
                DiscoverAllLocalVolumes = false,
                ExplicitLocalRoots = [configuredRoot],
            },
            new PartialFileSystem(),
            source);

        provider.RememberPreviouslyIndexedRoots([@"C:\"]);
        _ = await provider.ProbeAsync(CancellationToken.None);

        Assert.DoesNotContain(@"C:\", source.LastKnownRoots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccessDeniedSubtree_MergesObservedFilesWithoutShrinkingTrustedCompleteSnapshot()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\Scope";
        var present = Candidate(@"C:\Scope\Good\PresentPart.SLDPRT");
        var stale = Candidate(@"C:\Scope\Good\StalePart.SLDPRT");
        var deniedCached = Candidate(@"C:\Scope\Denied\CachedPart.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            CompleteProvider(root, DiscoveryRootKind.ExplicitLocal, [present, stale, deniedCached]),
            "trusted-seed",
            CancellationToken.None);
        foreach (var candidate in new[] { present, stale })
        {
            var lease = Assert.IsType<MetadataWorkLease>(await store.TryLeaseMetadataPathAsync(
                candidate.FullPath,
                TimeSpan.FromSeconds(10),
                "seed-complete-" + Path.GetFileNameWithoutExtension(candidate.FullPath)));
            await store.SkipMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, "SEED_READY");
        }
        var trustedStatus = await store.GetStatusAsync();
        var trustedRoot = await ReadIndexedRootTrustAsync(database.Path, root);

        var fileSystem = new PartialFileSystem();
        fileSystem.SetChildren(root,
        [
            DirectoryItem(@"C:\Scope\Good"),
            DirectoryItem(@"C:\Scope\Denied"),
        ]);
        fileSystem.SetChildren(@"C:\Scope\Good", [FileItem(present.FullPath, present.Size)]);
        fileSystem.ThrowWhenEnumerating(@"C:\Scope\Denied", new UnauthorizedAccessException("injected"));
        await using var provider = new BuiltInFileDiscoveryProvider(
            new BuiltInDiscoveryOptions
            {
                DiscoverAllLocalVolumes = false,
                ExplicitLocalRoots = [root],
                CaptureFileIdentity = false,
                MaximumEntriesPerSecond = 0,
                YieldEveryEntries = 1,
                YieldDelay = TimeSpan.Zero,
            },
            fileSystem,
            new FixedRootSource(new DiscoveryRootState(
                root,
                DiscoveryRootKind.ExplicitLocal,
                IsOnline: true,
                IsExpected: true)));
        var coordinator = new IndexingCoordinator(provider, store);

        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[present.FullPath]);
        Assert.Equal(0, states[stale.FullPath]);
        Assert.Equal(0, states[deniedCached.FullPath]);
        var rootReport = Assert.Single(provider.LastReport.Roots);
        Assert.False(provider.LastReport.IsCompleteForDeletion);
        Assert.False(rootReport.FullyEnumerated);
        Assert.Contains(
            @"C:\Scope\Denied",
            rootReport.IncompletePrefixes,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            @"C:\Scope\Denied",
            rootReport.TerminalSkippedPrefixes,
            StringComparer.OrdinalIgnoreCase);
        var status = await store.GetStatusAsync();
        Assert.Equal("partial", status.SourceState);
        Assert.Equal(trustedStatus.LastSuccessfulDiscoveryUtc, status.LastSuccessfulDiscoveryUtc);
        Assert.Equal(3, status.PresentFileCount);
        Assert.Equal(trustedRoot, await ReadIndexedRootTrustAsync(database.Path, root));
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
        var progress = await store.GetMetadataProgressAsync();
        Assert.Equal(0, progress.PendingCount);
        Assert.Equal(0, progress.FailedCount);
        Assert.Equal(1, progress.SkippedCount);
        Assert.Equal(3, progress.CachedReadableFileCount);

        // Seeing the directory again is fresh evidence, so the previously
        // terminal skip re-enters the metadata queue rather than remaining a
        // permanent blind spot.
        await store.ApplyDiscoverySnapshotAsync(
            CompleteProvider(root, DiscoveryRootKind.ExplicitLocal, [present, deniedCached]),
            "recovered-scope",
            CancellationToken.None);
        Assert.Equal(1, (await store.GetMetadataProgressAsync()).PendingCount);
    }

    [Fact]
    public async Task ConfirmedPartialException_MergesStagedEntriesWithoutReconcilingUnscannedRows()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\Scope";
        var cached = Candidate(@"C:\Scope\Cached\KeepThisPart.SLDPRT");
        var observedBeforeFailure = Candidate(@"C:\Scope\Observed\NewPart.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            CompleteProvider(root, DiscoveryRootKind.ExplicitLocal, [cached]),
            "trusted-seed",
            CancellationToken.None);

        var coordinator = new IndexingCoordinator(
            new ConfirmedPartialThrowingProvider(root, observedBeforeFailure),
            store);
        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[cached.FullPath]);
        Assert.Equal(0, states[observedBeforeFailure.FullPath]);
        Assert.Equal(2, (await store.GetStatusAsync()).PresentFileCount);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    [Fact]
    public async Task GenericIOException_MergesObservedEntriesAsAStalePartialSnapshot()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\Scope";
        var cached = Candidate(@"C:\Scope\Cached\KeepThisPart.SLDPRT");
        var observedBeforeFailure = Candidate(@"C:\Scope\Observed\MustNotPublish.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            CompleteProvider(root, DiscoveryRootKind.ExplicitLocal, [cached]),
            "trusted-seed",
            CancellationToken.None);
        var trustedStatus = await store.GetStatusAsync();

        var coordinator = new IndexingCoordinator(
            new ConfirmedPartialThrowingProvider(root, observedBeforeFailure, throwGenericIOException: true),
            store);
        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[cached.FullPath]);
        Assert.Equal(0, states[observedBeforeFailure.FullPath]);
        var status = await store.GetStatusAsync();
        Assert.Equal(2, status.PresentFileCount);
        Assert.Equal(trustedStatus.DiscoveryGeneration + 1, status.DiscoveryGeneration);
        Assert.Equal(trustedStatus.LastSuccessfulDiscoveryUtc, status.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", status.SourceState);
        Assert.True(status.CacheMayBeStale);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    [Fact]
    public async Task GenericIOException_InitialPartialSnapshotPublishesUsableGeneration()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\Initial";
        var observedBeforeFailure = Candidate(@"C:\Initial\CurrentPart.SLDPRT");
        var coordinator = new IndexingCoordinator(
            new ConfirmedPartialThrowingProvider(root, observedBeforeFailure, throwGenericIOException: true),
            store);

        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var status = await store.GetStatusAsync();
        Assert.Equal(1, status.DiscoveryGeneration);
        Assert.Equal(1, status.PresentFileCount);
        Assert.Null(status.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", status.SourceState);
        Assert.True(status.CacheMayBeStale);
        var hits = await store.SearchAsync(
            new SearchQuery("CurrentPart", SearchMode.FileName),
            CancellationToken.None);
        Assert.Equal(observedBeforeFailure.FullPath, Assert.Single(hits).FullPath);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    [Fact]
    public async Task GenericIOException_AfterCommittedStageBatchPublishesTheWholeObservedPrefix()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\LargePartial";
        var observed = Enumerable.Range(0, 5_000)
            .Select(index => Candidate($@"C:\LargePartial\Part-{index:D4}.SLDPRT"))
            .ToArray();
        var coordinator = new IndexingCoordinator(
            new ConfirmedPartialThrowingProvider(root, observed, throwGenericIOException: true),
            store);

        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var status = await store.GetStatusAsync();
        Assert.Equal(1, status.DiscoveryGeneration);
        Assert.Equal(observed.Length, status.PresentFileCount);
        Assert.Null(status.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", status.SourceState);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    [Fact]
    public async Task GenericIOException_AfterOptimisticCompleteReportStillPublishesAsPartial()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\Optimistic";
        var observed = Candidate(@"C:\Optimistic\ObservedPart.SLDPRT");
        var coordinator = new IndexingCoordinator(
            new ConfirmedPartialThrowingProvider(
                root,
                observed,
                throwGenericIOException: true,
                reportCompleteBeforeFailure: true),
            store);

        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var status = await store.GetStatusAsync();
        Assert.Equal(1, status.DiscoveryGeneration);
        Assert.Equal(1, status.PresentFileCount);
        Assert.Null(status.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", status.SourceState);
        Assert.True(status.CacheMayBeStale);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    [Fact]
    public async Task ProviderBudgetTimeout_MergesObservedEntriesAsPartial()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var root = @"C:\Budget";
        var observed = Candidate(@"C:\Budget\ObservedPart.SLDPRT");
        var coordinator = new IndexingCoordinator(
            new ConfirmedPartialThrowingProvider(root, observed, new TimeoutException("budget expired")),
            store);

        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var status = await store.GetStatusAsync();
        Assert.Equal(1, status.DiscoveryGeneration);
        Assert.Equal(1, status.PresentFileCount);
        Assert.Null(status.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", status.SourceState);
        Assert.True(status.CacheMayBeStale);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    [Fact]
    public async Task FallbackOfflineRootDuringRefresh_MergesReachablePrefixWithoutDeletingCachedOfflineRoot()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var cRoot = @"C:\Current";
        var eRoot = @"E:\Offline";
        var observed = Candidate(@"C:\Current\ObservedPart.SLDPRT");
        var cachedOffline = Candidate(@"E:\Offline\CachedPart.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([cachedOffline]),
            CancellationToken.None);
        var trustedStatus = await store.GetStatusAsync();

        var fileSystem = new PartialFileSystem();
        fileSystem.SetChildren(cRoot, [FileItem(observed.FullPath, observed.Size)]);
        var onlineRoots = new[]
        {
            Root(cRoot, isOnline: true),
            Root(eRoot, isOnline: true),
        };
        var offlineRoots = new[]
        {
            Root(cRoot, isOnline: true),
            Root(eRoot, isOnline: false),
        };
        await using var builtIn = new BuiltInFileDiscoveryProvider(
            new BuiltInDiscoveryOptions
            {
                DiscoverAllLocalVolumes = false,
                CaptureFileIdentity = false,
                MaximumEntriesPerSecond = 0,
                YieldEveryEntries = 1,
                YieldDelay = TimeSpan.Zero,
            },
            fileSystem,
            new SequencedRootSource(onlineRoots, offlineRoots));
        await using var fallback = new FallbackDiscoveryProvider(
            accelerator: null,
            builtIn: builtIn);
        var coordinator = new IndexingCoordinator(fallback, store);

        var capability = await coordinator.RefreshAsync(CancellationToken.None);

        var states = await ReadStatesAsync(database.Path);
        Assert.Equal(0, states[observed.FullPath]);
        Assert.Equal(0, states[cachedOffline.FullPath]);
        var status = await store.GetStatusAsync();
        Assert.Equal(trustedStatus.DiscoveryGeneration + 1, status.DiscoveryGeneration);
        Assert.Equal(trustedStatus.LastSuccessfulDiscoveryUtc, status.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", status.SourceState);
        Assert.True(status.CacheMayBeStale);
        Assert.False(fallback.LastReport.IsCompleteForDeletion);
        var offlineRoot = Assert.Single(fallback.LastReport.Roots, root =>
            root.RootPath.Equals(DiscoveryPath.Normalize(eRoot), StringComparison.OrdinalIgnoreCase));
        Assert.False(offlineRoot.WasOnline);
        Assert.False(offlineRoot.FullyEnumerated);
        Assert.Null(fallback.SnapshotCheckpoint);
        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("DISCOVERY_PARTIAL", capability.Code);
    }

    private static FixtureIncrementalProvider CompleteProvider(
        string root,
        DiscoveryRootKind kind,
        IReadOnlyCollection<FileCandidate> candidates)
    {
        var normalizedRoot = DiscoveryPath.Normalize(root);
        var entries = candidates.Select(candidate => new DiscoveredFile(
            candidate,
            DiscoveryPath.NormalizeKey(candidate.FullPath),
            normalizedRoot,
            Identity: null)).ToArray();
        var report = new DiscoveryScanReport(
            Modified,
            Modified,
            IsCompleteForDeletion: true,
            [new DiscoveryRootReport(
                normalizedRoot,
                kind,
                WasOnline: true,
                FullyEnumerated: true,
                VisitedEntries: candidates.Count,
                SolidWorksFiles: candidates.Count,
                ReparsePointsSkipped: 0,
                OfflineFilesSkipped: 0,
                new Dictionary<string, int>())]);
        return new FixtureIncrementalProvider(entries, report);
    }

    private static FileCandidate Candidate(string path) =>
        new(path, SolidWorksDocumentKind.Part, 10, Modified);

    private static DiscoveryRootState Root(string path, bool isOnline) => new(
        DiscoveryPath.Normalize(path),
        DiscoveryRootKind.ExplicitLocal,
        isOnline,
        IsExpected: true);

    private static DiscoveryFileSystemItem DirectoryItem(string path) =>
        new(path, IsDirectory: true, FileAttributes.Directory, 0, Modified);

    private static DiscoveryFileSystemItem FileItem(string path, long size) =>
        new(path, IsDirectory: false, FileAttributes.Normal, size, Modified);

    private static async Task<Dictionary<string, int>> ReadStatesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,state FROM files;";
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            states[reader.GetString(0)] = reader.GetInt32(1);
        }

        return states;
    }

    private static async Task<(long LastSuccessUtc, long DiscoveryGeneration)> ReadIndexedRootTrustAsync(
        string databasePath,
        string root)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_success_utc,discovery_generation
              FROM indexed_roots
             WHERE root_norm=@root;
            """;
        command.Parameters.AddWithValue("@root", DiscoveryPath.Normalize(root));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private sealed class FixtureIncrementalProvider(
        IReadOnlyList<DiscoveredFile> entries,
        DiscoveryScanReport report) : IIncrementalFileDiscoveryProvider
    {
        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(6);

        public DiscoveryScanReport LastReport => report;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "FIXTURE_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in DiscoverEntriesAsync(cancellationToken))
            {
                yield return entry.Candidate;
            }
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DiscoveredFile?>(entries.FirstOrDefault(entry =>
                entry.Candidate.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed class ConfirmedPartialThrowingProvider : IIncrementalFileDiscoveryProvider
    {
        private readonly string _root;
        private readonly IReadOnlyList<DiscoveredFile> _entries;
        private readonly Exception? _enumerationFailure;
        private readonly bool _reportCompleteBeforeFailure;
        private DiscoveryScanReport _lastReport = DiscoveryScanReport.Empty;

        internal ConfirmedPartialThrowingProvider(
            string root,
            FileCandidate candidate,
            bool throwGenericIOException = false,
            bool reportCompleteBeforeFailure = false)
            : this(
                root,
                [candidate],
                throwGenericIOException ? new IOException("generic discovery I/O failure") : null,
                reportCompleteBeforeFailure)
        {
        }

        internal ConfirmedPartialThrowingProvider(
            string root,
            IReadOnlyList<FileCandidate> candidates,
            bool throwGenericIOException = false,
            bool reportCompleteBeforeFailure = false)
            : this(
                root,
                candidates,
                throwGenericIOException ? new IOException("generic discovery I/O failure") : null,
                reportCompleteBeforeFailure)
        {
        }

        internal ConfirmedPartialThrowingProvider(
            string root,
            FileCandidate candidate,
            Exception enumerationFailure,
            bool reportCompleteBeforeFailure = false)
            : this(root, [candidate], enumerationFailure, reportCompleteBeforeFailure)
        {
        }

        private ConfirmedPartialThrowingProvider(
            string root,
            IReadOnlyList<FileCandidate> candidates,
            Exception? enumerationFailure,
            bool reportCompleteBeforeFailure)
        {
            _root = DiscoveryPath.Normalize(root);
            _entries = candidates.Select(candidate => new DiscoveredFile(
                    candidate,
                    DiscoveryPath.NormalizeKey(candidate.FullPath),
                    _root,
                    Identity: null))
                .ToArray();
            _enumerationFailure = enumerationFailure;
            _reportCompleteBeforeFailure = reportCompleteBeforeFailure;
        }

        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(6);

        public DiscoveryScanReport LastReport => _lastReport;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in DiscoverEntriesAsync(cancellationToken))
            {
                yield return entry.Candidate;
            }
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            _lastReport = new DiscoveryScanReport(
                started,
                started.AddMilliseconds(1),
                IsCompleteForDeletion: _reportCompleteBeforeFailure,
                [new DiscoveryRootReport(
                    _root,
                    DiscoveryRootKind.ExplicitLocal,
                    WasOnline: true,
                    FullyEnumerated: _reportCompleteBeforeFailure,
                    VisitedEntries: _entries.Count,
                    SolidWorksFiles: _entries.Count,
                    ReparsePointsSkipped: 0,
                    OfflineFilesSkipped: 0,
                    new Dictionary<string, int>())
                {
                    IncompletePrefixes = _reportCompleteBeforeFailure
                        ? Array.Empty<string>()
                        : [_root],
                }]);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in _entries)
            {
                yield return entry;
                await Task.Yield();
            }
            if (_enumerationFailure is not null)
            {
                throw _enumerationFailure;
            }

            throw new DiscoverySnapshotIncompleteException(_lastReport);
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DiscoveredFile?>(_entries.FirstOrDefault(entry =>
                entry.Candidate.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed class FixedRootSource(params DiscoveryRootState[] roots) : IDiscoveryRootSource
    {
        public IReadOnlyList<DiscoveryRootState> GetRoots(
            BuiltInDiscoveryOptions options,
            IReadOnlyCollection<string> knownSuccessfulRoots) => roots;
    }

    private sealed class SequencedRootSource(
        params IReadOnlyList<DiscoveryRootState>[] rootSnapshots) : IDiscoveryRootSource
    {
        private int _callCount;

        public IReadOnlyList<DiscoveryRootState> GetRoots(
            BuiltInDiscoveryOptions options,
            IReadOnlyCollection<string> knownSuccessfulRoots)
        {
            var index = Math.Min(Interlocked.Increment(ref _callCount) - 1, rootSnapshots.Length - 1);
            return rootSnapshots[index];
        }
    }

    private sealed class CapturingRootSource(params DiscoveryRootState[] roots) : IDiscoveryRootSource
    {
        internal IReadOnlyCollection<string> LastKnownRoots { get; private set; } = Array.Empty<string>();

        public IReadOnlyList<DiscoveryRootState> GetRoots(
            BuiltInDiscoveryOptions options,
            IReadOnlyCollection<string> knownSuccessfulRoots)
        {
            LastKnownRoots = knownSuccessfulRoots.ToArray();
            return roots;
        }
    }

    private sealed class PartialFileSystem : IDiscoveryFileSystem
    {
        private readonly Dictionary<string, IReadOnlyList<DiscoveryFileSystemItem>> _children =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _errors = new(StringComparer.OrdinalIgnoreCase);

        internal void SetChildren(string directory, IReadOnlyList<DiscoveryFileSystemItem> children) =>
            _children[DiscoveryPath.NormalizeKey(directory)] = children;

        internal void ThrowWhenEnumerating(string directory, Exception exception) =>
            _errors[DiscoveryPath.NormalizeKey(directory)] = exception;

        public IEnumerable<DiscoveryFileSystemItem> EnumerateChildren(string directory)
        {
            var key = DiscoveryPath.NormalizeKey(directory);
            if (_errors.TryGetValue(key, out var exception))
            {
                throw exception;
            }

            return _children.TryGetValue(key, out var children)
                ? children
                : Array.Empty<DiscoveryFileSystemItem>();
        }

        public DiscoveryFileSystemItem Inspect(string fullPath) =>
            throw new NotSupportedException();

        public FileSystemIdentity? TryGetIdentity(string fullPath) => null;
    }
}
