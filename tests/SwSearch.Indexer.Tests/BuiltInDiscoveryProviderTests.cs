using System.Runtime.CompilerServices;
using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Indexer.Tests;

public sealed class BuiltInDiscoveryProviderTests
{
    [Fact]
    public async Task BuiltInDiscovery_StreamsNestedSolidWorksFilesCaseInsensitively()
    {
        using var directory = new TemporaryDirectory();
        var nested = Directory.CreateDirectory(Path.Combine(directory.Path, "一级", "二级"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "零件.SLDPRT"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "装配.sldasm"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "工程图.SldDrW"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "忽略.txt"), "not solidworks");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "忽略.SLDPRT.bak"), "not solidworks");

        await using var provider = CreateTemporaryRootProvider(directory.Path);
        var entries = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(2, entries.Count);
        Assert.Equal(
            [SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly],
            entries.Select(static entry => entry.Candidate.Kind).OrderBy(static kind => kind).ToArray());
        Assert.All(entries, entry =>
        {
            Assert.True(DiscoveryPath.IsWithinRoot(entry.Candidate.FullPath, directory.Path));
            Assert.Equal(DiscoveryPath.NormalizeKey(entry.Candidate.FullPath), entry.NormalizedPath);
            Assert.Equal(DiscoveryPath.Normalize(directory.Path), entry.RootPath);
        });
        Assert.True(provider.LastReport.IsCompleteForDeletion);
        Assert.Equal(2, Assert.Single(provider.LastReport.Roots).SolidWorksFiles);
    }

    [Fact]
    public async Task BuiltInDiscovery_CancellationStopsIterativeScan()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 16; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory.Path, $"P-{index:D2}.SLDPRT"), [1]);
        }

        await using var provider = CreateTemporaryRootProvider(directory.Path);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = provider.DiscoverEntriesAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask());
        Assert.False(provider.LastReport.IsCompleteForDeletion);
    }

    [Fact]
    public async Task BuiltInDiscovery_SkipsReparseAndAggregatesAccessErrorsByRoot()
    {
        using var directory = new TemporaryDirectory();
        var root = DiscoveryPath.Normalize(directory.Path);
        var loop = Path.Combine(root, "loop");
        var denied = Path.Combine(root, "denied");
        var good = Path.Combine(root, "good.SLDPRT");
        var fileSystem = new FakeFileSystem();
        fileSystem.SetChildren(root,
        [
            DirectoryItem(loop, FileAttributes.Directory | FileAttributes.ReparsePoint),
            DirectoryItem(denied, FileAttributes.Directory),
            FileItem(good, 42),
        ]);
        fileSystem.ThrowWhenEnumerating(denied, new UnauthorizedAccessException("injected"));
        fileSystem.ThrowWhenEnumerating(loop, new InvalidOperationException("reparse point was followed"));
        var source = new FakeRootSource(new DiscoveryRootState(
            root,
            DiscoveryRootKind.ExplicitLocal,
            IsOnline: true,
            IsExpected: true));
        await using var provider = new BuiltInFileDiscoveryProvider(TestOptions(), fileSystem, source);

        var entries = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(good, Assert.Single(entries).Candidate.FullPath);
        Assert.DoesNotContain(DiscoveryPath.NormalizeKey(loop), fileSystem.EnumeratedDirectories);
        var report = Assert.Single(provider.LastReport.Roots);
        Assert.Equal(1, report.ReparsePointsSkipped);
        Assert.Equal(1, report.Errors["ACCESS_DENIED"]);
        Assert.False(report.FullyEnumerated);
        Assert.Contains(denied, report.IncompletePrefixes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(denied, report.TerminalSkippedPrefixes, StringComparer.OrdinalIgnoreCase);
        Assert.False(provider.LastReport.IsCompleteForDeletion);
    }

    [Fact]
    public async Task BuiltInDiscovery_LeavesUnknownIoFailureDiagnosableInsteadOfClosingItAsSkipped()
    {
        using var directory = new TemporaryDirectory();
        var root = DiscoveryPath.Normalize(directory.Path);
        var broken = Path.Combine(root, "broken");
        var fileSystem = new FakeFileSystem();
        fileSystem.SetChildren(root, [DirectoryItem(broken, FileAttributes.Directory)]);
        fileSystem.ThrowWhenEnumerating(broken, new IOException("injected unknown I/O failure"));
        var source = new FakeRootSource(new DiscoveryRootState(
            root,
            DiscoveryRootKind.ExplicitLocal,
            IsOnline: true,
            IsExpected: true));
        await using var provider = new BuiltInFileDiscoveryProvider(TestOptions(), fileSystem, source);

        _ = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        var report = Assert.Single(provider.LastReport.Roots);
        Assert.Equal(1, report.Errors["IO_ERROR"]);
        Assert.Contains(broken, report.IncompletePrefixes, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(broken, report.TerminalSkippedPrefixes, StringComparer.OrdinalIgnoreCase);
        Assert.False(provider.LastReport.IsCompleteForDeletion);
    }

    [Fact]
    public async Task BuiltInDiscovery_IndexesCloudPlaceholderNamesWithoutOpeningFiles()
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x0004_0000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x0040_0000;
        using var directory = new TemporaryDirectory();
        var root = DiscoveryPath.Normalize(directory.Path);
        var junction = Path.Combine(root, "outside-junction");
        var fileSystem = new FakeFileSystem();
        fileSystem.SetChildren(root,
        [
            DirectoryItem(junction, FileAttributes.Directory | FileAttributes.ReparsePoint),
            FileItem(Path.Combine(root, "offline.SLDPRT"), 101, FileAttributes.Offline),
            FileItem(Path.Combine(root, "recall-open.SLDASM"), 102, recallOnOpen),
            FileItem(Path.Combine(root, "recall-data.SLDDRW"), 103, recallOnDataAccess),
            FileItem(Path.Combine(root, "cloud-reparse.SLDPRT"), 104,
                FileAttributes.ReparsePoint | FileAttributes.Offline),
            FileItem(Path.Combine(root, "outside-target-symlink.SLDPRT"), 106,
                FileAttributes.ReparsePoint),
            FileItem(Path.Combine(root, "ignored.step"), 105, FileAttributes.Offline),
        ]);
        fileSystem.ThrowWhenEnumerating(
            junction,
            new InvalidOperationException("directory reparse point was followed"));
        var source = new FakeRootSource(new DiscoveryRootState(
            root,
            DiscoveryRootKind.ExplicitLocal,
            IsOnline: true,
            IsExpected: true));
        await using var provider = new BuiltInFileDiscoveryProvider(
            TestOptions(captureFileIdentity: true),
            fileSystem,
            source);

        var entries = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        // Drawing support was removed from the product: the SLDDRW fixture
        // (size 103) must be excluded rather than indexed by name.
        Assert.Equal(3, entries.Count);
        Assert.Equal([101, 102, 104],
            entries.Select(static entry => entry.Candidate.Size).Order().ToArray());
        Assert.DoesNotContain(
            entries,
            static entry => entry.Candidate.FullPath.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, static entry => Assert.Null(entry.Identity));
        Assert.Equal(0, fileSystem.TryGetIdentityCalls);
        Assert.Equal(0, fileSystem.InspectCalls);
        Assert.DoesNotContain(DiscoveryPath.NormalizeKey(junction), fileSystem.EnumeratedDirectories);
        var report = Assert.Single(provider.LastReport.Roots);
        Assert.Equal(3, report.SolidWorksFiles);
        Assert.Equal(2, report.ReparsePointsSkipped);
        Assert.Equal(0, report.OfflineFilesSkipped);
    }

    [Fact]
    public async Task BuiltInDiscovery_NeverFollowsExplicitRootThatIsAReparsePoint()
    {
        using var directory = new TemporaryDirectory();
        var root = DiscoveryPath.Normalize(directory.Path);
        var fileSystem = new FakeFileSystem();
        var source = new FakeRootSource(new DiscoveryRootState(
            root,
            DiscoveryRootKind.ExplicitLocal,
            IsOnline: false,
            IsExpected: true,
            IsReparsePoint: true));
        await using var provider = new BuiltInFileDiscoveryProvider(TestOptions(), fileSystem, source);

        var exception = await Assert.ThrowsAsync<DiscoverySnapshotIncompleteException>(async () =>
            await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None)));

        Assert.Empty(fileSystem.EnumeratedDirectories);
        var report = Assert.Single(exception.Report.Roots);
        Assert.Equal(1, report.ReparsePointsSkipped);
        Assert.Equal(1, report.Errors["ROOT_REPARSE_SKIPPED"]);
    }

    [Fact]
    public async Task BuiltInDiscovery_OfflineKnownRootRejectsDeletionSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var root = DiscoveryPath.Normalize(directory.Path);
        var source = new FakeRootSource(new DiscoveryRootState(
            root,
            DiscoveryRootKind.Removable,
            IsOnline: false,
            IsExpected: true));
        await using var provider = new BuiltInFileDiscoveryProvider(
            TestOptions(previouslyIndexedRoots: [root]),
            new FakeFileSystem(),
            source);

        var exception = await Assert.ThrowsAsync<DiscoverySnapshotIncompleteException>(async () =>
            await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None)));

        Assert.False(exception.Report.IsCompleteForDeletion);
        var rootReport = Assert.Single(exception.Report.Roots);
        Assert.False(rootReport.WasOnline);
        Assert.Equal(1, rootReport.Errors["ROOT_OFFLINE"]);
    }

    [Fact]
    public async Task RememberPreviouslyIndexedRoots_RestoresOfflineProtectionAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var root = DiscoveryPath.Normalize(directory.Path);
        var source = new KnownRootsBecomeOfflineSource();
        await using var provider = new BuiltInFileDiscoveryProvider(
            TestOptions(explicitRoots: [root]),
            new FakeFileSystem(),
            source);

        provider.RememberPreviouslyIndexedRoots([root]);
        var capability = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE", capability.Code);
        Assert.Contains(DiscoveryPath.NormalizeKey(root), source.LastKnownRoots);
    }

    [Fact]
    public async Task TryInspectFile_ReturnsOnlyEligibleOnlineSolidWorksFile()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "inspect.SLDASM");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        await using var provider = CreateTemporaryRootProvider(directory.Path);

        var inspected = await provider.TryInspectFileAsync(file, CancellationToken.None);
        var ignored = await provider.TryInspectFileAsync(
            Path.Combine(directory.Path, "not-a-model.txt"),
            CancellationToken.None);

        Assert.NotNull(inspected);
        Assert.Equal(4, inspected.Candidate.Size);
        Assert.Equal(SolidWorksDocumentKind.Assembly, inspected.Candidate.Kind);
        Assert.Null(ignored);
    }

    [Fact]
    public void Defaults_AreLowImpactAndReconcileWatcherLoss()
    {
        var options = new BuiltInDiscoveryOptions();

        Assert.Equal(2_000, options.MaximumEntriesPerSecond);
        Assert.True(options.CaptureFileIdentity);
        Assert.Equal(TimeSpan.FromHours(6), options.ReconciliationInterval);
        Assert.Empty(options.ExplicitNetworkRoots);
    }

    private static BuiltInFileDiscoveryProvider CreateTemporaryRootProvider(string root) => new(
        TestOptions(explicitRoots: [root]));

    private static BuiltInDiscoveryOptions TestOptions(
        IReadOnlyCollection<string>? explicitRoots = null,
        IReadOnlyCollection<string>? previouslyIndexedRoots = null,
        bool captureFileIdentity = false) => new()
        {
            DiscoverAllLocalVolumes = false,
            ExplicitLocalRoots = explicitRoots ?? Array.Empty<string>(),
            PreviouslyIndexedRoots = previouslyIndexedRoots ?? Array.Empty<string>(),
            CaptureFileIdentity = captureFileIdentity,
            MaximumEntriesPerSecond = 0,
            YieldEveryEntries = 1,
            YieldDelay = TimeSpan.Zero,
        };

    private static DiscoveryFileSystemItem DirectoryItem(string path, FileAttributes attributes) => new(
        path,
        IsDirectory: true,
        attributes,
        0,
        DateTimeOffset.UtcNow);

    private static DiscoveryFileSystemItem FileItem(
        string path,
        long size,
        FileAttributes attributes = FileAttributes.Normal) => new(
        path,
        IsDirectory: false,
        attributes,
        size,
        DateTimeOffset.UtcNow);

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class FakeRootSource(params DiscoveryRootState[] roots) : IDiscoveryRootSource
    {
        public IReadOnlyList<DiscoveryRootState> GetRoots(
            BuiltInDiscoveryOptions options,
            IReadOnlyCollection<string> knownSuccessfulRoots) => roots;
    }

    private sealed class KnownRootsBecomeOfflineSource : IDiscoveryRootSource
    {
        internal IReadOnlyCollection<string> LastKnownRoots { get; private set; } = Array.Empty<string>();

        public IReadOnlyList<DiscoveryRootState> GetRoots(
            BuiltInDiscoveryOptions options,
            IReadOnlyCollection<string> knownSuccessfulRoots)
        {
            LastKnownRoots = knownSuccessfulRoots.ToArray();
            return knownSuccessfulRoots.Select(root => new DiscoveryRootState(
                DiscoveryPath.Normalize(root),
                DiscoveryRootKind.Removable,
                IsOnline: false,
                IsExpected: true)).ToArray();
        }
    }

    private sealed class FakeFileSystem : IDiscoveryFileSystem
    {
        private readonly Dictionary<string, IReadOnlyList<DiscoveryFileSystemItem>> _children =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _errors = new(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> EnumeratedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int InspectCalls { get; private set; }
        internal int TryGetIdentityCalls { get; private set; }

        internal void SetChildren(string directory, IReadOnlyList<DiscoveryFileSystemItem> children) =>
            _children[DiscoveryPath.NormalizeKey(directory)] = children;

        internal void ThrowWhenEnumerating(string directory, Exception exception) =>
            _errors[DiscoveryPath.NormalizeKey(directory)] = exception;

        public IEnumerable<DiscoveryFileSystemItem> EnumerateChildren(string directory)
        {
            var key = DiscoveryPath.NormalizeKey(directory);
            EnumeratedDirectories.Add(key);
            if (_errors.TryGetValue(key, out var exception))
            {
                throw exception;
            }

            return _children.TryGetValue(key, out var children)
                ? children
                : Array.Empty<DiscoveryFileSystemItem>();
        }

        public DiscoveryFileSystemItem Inspect(string fullPath)
        {
            InspectCalls++;
            return _children.Values.SelectMany(static items => items)
                .First(item => item.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        }

        public FileSystemIdentity? TryGetIdentity(string fullPath)
        {
            TryGetIdentityCalls++;
            return null;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"swsearch-discovery-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class FallbackDiscoveryProviderTests
{
    [Fact]
    public async Task ExplicitLocalRoot_SuccessfulEverythingStillWinsWithoutBuiltInScan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"swsearch-explicit-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var filePath = Path.Combine(root, "explicit.SLDPRT");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3]);
            var candidate = new FileCandidate(
                filePath,
                SolidWorksDocumentKind.Part,
                3,
                File.GetLastWriteTimeUtc(filePath));
            var accelerator = new FakeProvider(
                new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
                _ => AsyncFiles.From([candidate]));
            await using var builtIn = new BuiltInFileDiscoveryProvider(new BuiltInDiscoveryOptions
            {
                DiscoverAllLocalVolumes = false,
                ExplicitLocalRoots = [root],
                CaptureFileIdentity = false,
                MaximumEntriesPerSecond = 0,
                YieldEveryEntries = 1,
                YieldDelay = TimeSpan.Zero,
            });
            await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

            var result = Assert.Single(await ReadAllAsync(
                provider.DiscoverEntriesAsync(CancellationToken.None)));

            Assert.Equal(filePath, result.Candidate.FullPath);
            Assert.True(accelerator.DiscoverCalled);
            Assert.Equal("EVERYTHING_IPC_READY", provider.LastCapability.Code);
            Assert.True(provider.LastReport.IsCompleteForDeletion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncrementalFallback_PreservesIdentityReportWatcherAndInterval()
    {
        var candidates = Candidates();
        var root = @"C:\";
        var entries = candidates.Select((candidate, index) => new DiscoveredFile(
            candidate,
            DiscoveryPath.NormalizeKey(candidate.FullPath),
            root,
            new FileSystemIdentity(7, (ulong)(index + 10)))).ToArray();
        var report = new DiscoveryScanReport(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            IsCompleteForDeletion: true,
            [new DiscoveryRootReport(root, DiscoveryRootKind.Fixed, true, true, 2, 2, 0, 0,
                new Dictionary<string, int>())]);
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Degraded, "EVERYTHING_IPC_NOT_FOUND", "missing"),
            _ => AsyncFiles.From(Array.Empty<FileCandidate>()));
        var builtIn = new FakeIncrementalProvider(entries, report, TimeSpan.FromHours(9));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        var results = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));
        var watcherHints = await ReadAllAsync(provider.WatchAsync(CancellationToken.None));

        Assert.Equal([10UL, 11UL], results.Select(static entry => entry.Identity!.FileIndex).ToArray());
        Assert.Same(report, provider.LastReport);
        Assert.Equal(TimeSpan.FromHours(9), provider.RecommendedReconciliationInterval);
        Assert.Equal(DiscoveryChangeKind.ReconciliationRequired, Assert.Single(watcherHints).Kind);
    }

    [Fact]
    public async Task SuccessfulEverything_IsDirectPortableSnapshotWithoutBuiltInEnumeration()
    {
        var candidate = Candidates()[0];
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            _ => AsyncFiles.From([candidate]));
        var builtIn = new FakeIncrementalProvider(
            [],
            DiscoveryScanReport.Empty,
            TimeSpan.FromHours(6));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        var result = Assert.Single(await ReadAllAsync(
            provider.DiscoverEntriesAsync(CancellationToken.None)));

        Assert.Equal(candidate.FullPath, result.Candidate.FullPath);
        Assert.Null(result.Identity);
        Assert.True(provider.LastReport.IsCompleteForDeletion);
        Assert.Equal(1, Assert.Single(provider.LastReport.Roots).SolidWorksFiles);
        Assert.False(builtIn.DiscoverCalled);
    }

    [Fact]
    public async Task SuccessfulEverything_IsAuthorityAndSkipsMissingFixedVolumeVerification()
    {
        var now = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var cRoot = @"C:\";
        var dRoot = @"D:\";
        var eRoot = @"E:\";
        var cFile = new FileCandidate(
            @"C:\models\from-everything.SLDPRT",
            SolidWorksDocumentKind.Part,
            10,
            now);
        var dFile = new FileCandidate(
            @"D:\models\missing-from-everything.SLDASM",
            SolidWorksDocumentKind.Assembly,
            20,
            now);
        var eFile = new FileCandidate(
            @"E:\models\also-missing-from-everything.SLDPRT",
            SolidWorksDocumentKind.Part,
            30,
            now);
        var fileSystem = new RootMapFileSystem();
        fileSystem.SetChildren(cRoot, [ToFileSystemItem(cFile)]);
        fileSystem.SetChildren(dRoot, [ToFileSystemItem(dFile)]);
        fileSystem.SetChildren(eRoot, [ToFileSystemItem(eFile)]);
        await using var builtIn = CreateFixedRootBuiltIn(fileSystem, cRoot, dRoot, eRoot);
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            _ => AsyncFiles.From([cFile]));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        var entries = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Single(entries);
        Assert.Equal(cFile.FullPath, Assert.Single(entries).Candidate.FullPath);
        Assert.True(accelerator.DiscoverCalled);
        Assert.Equal("EVERYTHING_IPC_READY", provider.LastCapability.Code);
        Assert.True(provider.LastReport.IsCompleteForDeletion);
        Assert.Equal(
            [DiscoveryPath.Normalize(cRoot), DiscoveryPath.Normalize(dRoot), DiscoveryPath.Normalize(eRoot)],
            provider.LastReport.Roots.Select(static root => root.RootPath).OrderBy(static root => root).ToArray());
        Assert.All(provider.LastReport.Roots, static root => Assert.True(root.FullyEnumerated));
        Assert.Empty(fileSystem.EnumeratedDirectories);
    }

    [Fact]
    public async Task MftMissingFixedVolumes_ScansOnlyUncoveredRootsAndDropsJournalCheckpoint()
    {
        var now = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var cRoot = @"C:\";
        var dRoot = @"D:\";
        var eRoot = @"E:\";
        var cFile = new FileCandidate(
            @"C:\models\from-mft.SLDPRT",
            SolidWorksDocumentKind.Part,
            10,
            now);
        var dFile = new FileCandidate(
            @"D:\models\missing-from-mft.SLDASM",
            SolidWorksDocumentKind.Assembly,
            20,
            now);
        var eFile = new FileCandidate(
            @"E:\models\also-missing-from-mft.SLDPRT",
            SolidWorksDocumentKind.Part,
            30,
            now);
        var mftEntry = new DiscoveredFile(
            cFile,
            DiscoveryPath.NormalizeKey(cFile.FullPath),
            DiscoveryPath.Normalize(cRoot),
            new FileSystemIdentity(7, 42));
        var mftReport = new DiscoveryScanReport(
            now,
            now,
            IsCompleteForDeletion: true,
            [new DiscoveryRootReport(
                DiscoveryPath.Normalize(cRoot),
                DiscoveryRootKind.Fixed,
                WasOnline: true,
                FullyEnumerated: true,
                VisitedEntries: 1,
                SolidWorksFiles: 1,
                ReparsePointsSkipped: 0,
                OfflineFilesSkipped: 0,
                new Dictionary<string, int>())]);
        var fileSystem = new RootMapFileSystem();
        fileSystem.SetChildren(cRoot, [ToFileSystemItem(cFile)]);
        fileSystem.SetChildren(dRoot, [ToFileSystemItem(dFile)]);
        fileSystem.SetChildren(eRoot, [ToFileSystemItem(eFile)]);
        await using var builtIn = CreateFixedRootBuiltIn(fileSystem, cRoot, dRoot, eRoot);
        var mft = new PartialMftProvider([mftEntry], mftReport, now);
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Degraded, "EVERYTHING_IPC_NOT_FOUND", "missing"),
            _ => AsyncFiles.From(Array.Empty<FileCandidate>()));
        await using var provider = new FallbackDiscoveryProvider(
            accelerator,
            builtIn,
            mftProvider: mft);

        var entries = await ReadAllAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(
            [cFile.FullPath, dFile.FullPath, eFile.FullPath],
            entries.Select(static entry => entry.Candidate.FullPath).OrderBy(static path => path).ToArray());
        Assert.True(mft.DiscoverCalled);
        Assert.Equal(1, mft.CaptureCheckpointCalls);
        Assert.Null(provider.SnapshotCheckpoint);
        Assert.Equal("BUILTIN_DISCOVERY_FALLBACK", provider.LastCapability.Code);
        Assert.Equal(
            string.Join(";", [DiscoveryPath.Normalize(dRoot), DiscoveryPath.Normalize(eRoot)]),
            provider.LastCapability.Details!["mftMissingRoots"]);
        Assert.True(provider.LastReport.IsCompleteForDeletion);
        Assert.Equal(
            [DiscoveryPath.Normalize(cRoot), DiscoveryPath.Normalize(dRoot), DiscoveryPath.Normalize(eRoot)],
            provider.LastReport.Roots.Select(static root => root.RootPath).OrderBy(static root => root).ToArray());
        Assert.Contains(DiscoveryPath.NormalizeKey(dRoot), fileSystem.EnumeratedDirectories);
        Assert.Contains(DiscoveryPath.NormalizeKey(eRoot), fileSystem.EnumeratedDirectories);
        Assert.DoesNotContain(DiscoveryPath.NormalizeKey(cRoot), fileSystem.EnumeratedDirectories);
    }

    [Fact]
    public async Task MissingEverything_UsesBuiltInWithoutCallingAcceleratorDiscovery()
    {
        var expected = Candidates();
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Degraded, "EVERYTHING_IPC_NOT_FOUND", "missing"),
            _ => AsyncFiles.From(Array.Empty<FileCandidate>()));
        var builtIn = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            _ => AsyncFiles.From(expected));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        var capability = await provider.ProbeAsync(CancellationToken.None);
        var results = await ReadAllAsync(provider.DiscoverAsync(CancellationToken.None));

        Assert.Equal(CapabilityState.Available, capability.State);
        Assert.Equal("BUILTIN_DISCOVERY_READY", capability.Code);
        Assert.False(accelerator.DiscoverCalled);
        Assert.Equal(expected, results);
    }

    [Fact]
    public async Task EverythingTimeoutAfterPartialPage_DoesNotStartBuiltInScan()
    {
        var expected = Candidates();
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            _ => TimeoutAfter(expected[0]));
        var builtIn = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            _ => AsyncFiles.From(expected));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        var exception = await Assert.ThrowsAsync<EverythingQueryException>(async () =>
            await ReadAllAsync(provider.DiscoverAsync(CancellationToken.None)));

        Assert.Equal("EVERYTHING_QUERY_TIMEOUT", exception.Code);
        Assert.False(builtIn.DiscoverCalled);
        Assert.Equal("EVERYTHING_QUERY_FAILED", provider.LastCapability.Code);
    }

    [Fact]
    public async Task DamagedEverythingRecord_DoesNotStartBuiltInScan()
    {
        var expected = Candidates();
        var malformed = expected[0] with { FullPath = @"C:\bad.txt" };
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            _ => AsyncFiles.From([malformed]));
        var builtIn = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            _ => AsyncFiles.From(expected));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ReadAllAsync(provider.DiscoverAsync(CancellationToken.None)));

        Assert.False(builtIn.DiscoverCalled);
        Assert.Equal("EVERYTHING_QUERY_FAILED", provider.LastCapability.Code);
    }

    [Fact]
    public async Task AcceleratorProbeException_FallsBack()
    {
        var expected = Candidates();
        var accelerator = new FakeProvider(
            new InvalidDataException("corrupt probe"),
            _ => throw new InvalidOperationException("must not enumerate"));
        var builtIn = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            _ => AsyncFiles.From(expected));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);

        var results = await ReadAllAsync(provider.DiscoverAsync(CancellationToken.None));

        Assert.Equal(expected, results);
        Assert.False(accelerator.DiscoverCalled);
    }

    [Fact]
    public async Task Cancellation_DoesNotTriggerExpensiveFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var accelerator = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            token => Cancelled(token));
        var builtIn = new FakeProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            _ => AsyncFiles.From(Candidates()));
        await using var provider = new FallbackDiscoveryProvider(accelerator, builtIn);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ReadAllAsync(provider.DiscoverAsync(cancellation.Token)));
        Assert.False(builtIn.DiscoverCalled);
    }

    private static BuiltInFileDiscoveryProvider CreateFixedRootBuiltIn(
        RootMapFileSystem fileSystem,
        params string[] rootPaths) => new(
        new BuiltInDiscoveryOptions
        {
            DiscoverAllLocalVolumes = false,
            CaptureFileIdentity = false,
            MaximumEntriesPerSecond = 0,
            YieldEveryEntries = 1,
            YieldDelay = TimeSpan.Zero,
        },
        fileSystem,
        new FixedRootSource(rootPaths.Select(static root => new DiscoveryRootState(
            DiscoveryPath.Normalize(root),
            DiscoveryRootKind.Fixed,
            IsOnline: true,
            IsExpected: true)).ToArray()));

    private static DiscoveryFileSystemItem ToFileSystemItem(FileCandidate candidate) => new(
        candidate.FullPath,
        IsDirectory: false,
        FileAttributes.Normal,
        candidate.Size,
        candidate.LastWriteTimeUtc);

    private static FileCandidate[] Candidates()
    {
        var now = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        return
        [
            new FileCandidate(@"C:\models\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(@"D:\drawings\b.SLDDRW", SolidWorksDocumentKind.Drawing, 2, now),
        ];
    }

    private static async IAsyncEnumerable<FileCandidate> TimeoutAfter(
        FileCandidate first,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return first;
        await Task.Yield();
        throw new EverythingQueryException("EVERYTHING_QUERY_TIMEOUT", "injected timeout");
    }

    private static async IAsyncEnumerable<FileCandidate> Cancelled(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class FakeProvider : IFileDiscoveryProvider
    {
        private readonly RuntimeCapability? _capability;
        private readonly Exception? _probeException;
        private readonly Func<CancellationToken, IAsyncEnumerable<FileCandidate>> _discover;

        internal FakeProvider(
            RuntimeCapability capability,
            Func<CancellationToken, IAsyncEnumerable<FileCandidate>> discover)
        {
            _capability = capability;
            _discover = discover;
        }

        internal FakeProvider(
            Exception probeException,
            Func<CancellationToken, IAsyncEnumerable<FileCandidate>> discover)
        {
            _probeException = probeException;
            _discover = discover;
        }

        internal bool DiscoverCalled { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_probeException is not null)
            {
                throw _probeException;
            }

            return ValueTask.FromResult(_capability!);
        }

        public IAsyncEnumerable<FileCandidate> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoverCalled = true;
            return _discover(cancellationToken);
        }
    }

    private sealed class FakeIncrementalProvider(
        IReadOnlyList<DiscoveredFile> entries,
        DiscoveryScanReport report,
        TimeSpan reconciliationInterval) : IIncrementalFileDiscoveryProvider
    {
        internal bool DiscoverCalled { get; private set; }

        public TimeSpan RecommendedReconciliationInterval => reconciliationInterval;

        public DiscoveryScanReport LastReport => report;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "BUILTIN_DISCOVERY_READY",
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
            DiscoverCalled = true;
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
            yield return new DiscoveryChangeHint(
                DiscoveryChangeKind.ReconciliationRequired,
                null,
                null,
                @"C:\",
                "test");
            await Task.CompletedTask;
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

    private sealed class FixedRootSource(IReadOnlyList<DiscoveryRootState> roots) : IDiscoveryRootSource
    {
        public IReadOnlyList<DiscoveryRootState> GetRoots(
            BuiltInDiscoveryOptions options,
            IReadOnlyCollection<string> knownSuccessfulRoots) => roots;
    }

    private sealed class RootMapFileSystem : IDiscoveryFileSystem
    {
        private readonly Dictionary<string, IReadOnlyList<DiscoveryFileSystemItem>> _children =
            new(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> EnumeratedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal void SetChildren(string directory, IReadOnlyList<DiscoveryFileSystemItem> children) =>
            _children[DiscoveryPath.NormalizeKey(directory)] = children;

        public IEnumerable<DiscoveryFileSystemItem> EnumerateChildren(string directory)
        {
            var normalized = DiscoveryPath.NormalizeKey(directory);
            EnumeratedDirectories.Add(normalized);
            return _children.TryGetValue(normalized, out var children)
                ? children
                : Array.Empty<DiscoveryFileSystemItem>();
        }

        public DiscoveryFileSystemItem Inspect(string fullPath) =>
            _children.Values.SelectMany(static children => children).First(item =>
                item.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

        public FileSystemIdentity? TryGetIdentity(string fullPath) => null;
    }

    private sealed class PartialMftProvider :
        IIncrementalFileDiscoveryProvider,
        IJournalIncrementalDiscoveryProvider
    {
        private readonly IReadOnlyList<DiscoveredFile> _entries;
        private readonly DiscoveryJournalCheckpoint _checkpoint;

        internal PartialMftProvider(
            IReadOnlyList<DiscoveredFile> entries,
            DiscoveryScanReport report,
            DateTimeOffset timestamp)
        {
            _entries = entries;
            LastReport = report;
            _checkpoint = new DiscoveryJournalCheckpoint(
                "test-mft",
                [new DiscoveryVolumeCursor(@"C:\\", 1, 1, 7)],
                timestamp);
        }

        internal bool DiscoverCalled { get; private set; }

        internal int CaptureCheckpointCalls { get; private set; }

        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(6);

        public DiscoveryScanReport LastReport { get; }

        public DiscoveryJournalCheckpoint? SnapshotCheckpoint => _checkpoint;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "NTFS_MFT_USN_READY",
                "ready"));
        }

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
            DiscoverCalled = true;
            foreach (var entry in _entries)
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
            return ValueTask.FromResult<DiscoveredFile?>(_entries.FirstOrDefault(entry =>
                entry.Candidate.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)));
        }

        public ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCheckpointCalls++;
            return ValueTask.FromResult<DiscoveryJournalCheckpoint?>(_checkpoint);
        }

        public ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
            DiscoveryJournalCheckpoint checkpoint,
            int maximumChanges,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DiscoveryDeltaBatch(
                checkpoint,
                checkpoint,
                Array.Empty<DiscoveryDelta>(),
                HasMore: false,
                RequiresFullReconciliation: true,
                "test"));
        }
    }
}
