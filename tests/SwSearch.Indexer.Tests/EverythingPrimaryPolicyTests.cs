using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class EverythingPrimaryPolicyTests
{
    [Fact]
    public async Task ProbeAsync_AvailableEverything_DoesNotProbeBuiltInOrMft()
    {
        var accelerator = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            []);
        var builtIn = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            []);
        var journal = new CountingJournalProvider(Checkpoint());
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn, mftProvider: journal);

        var capability = await fallback.ProbeAsync(CancellationToken.None);

        Assert.Equal("EVERYTHING_IPC_READY", capability.Code);
        Assert.Equal(0, builtIn.ProbeCalls);
        Assert.Equal(0, journal.ProbeCalls);
    }

    [Fact]
    public async Task DiscoverEntries_SuccessfulEverything_StopsWithoutMftOrBuiltInFullScan()
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
            @"D:\models\not-needed.SLDASM",
            SolidWorksDocumentKind.Assembly,
            20,
            now);
        var eFile = new FileCandidate(
            @"E:\models\not-needed.SLDPRT",
            SolidWorksDocumentKind.Part,
            30,
            now);
        var fileSystem = new RootMapFileSystem();
        fileSystem.SetChildren(cRoot, [DirectoryItem(@"C:\models")]);
        fileSystem.SetChildren(@"C:\models", [FileItem(cFile)]);
        fileSystem.SetChildren(dRoot, [DirectoryItem(@"D:\models")]);
        fileSystem.SetChildren(@"D:\models", [FileItem(dFile)]);
        fileSystem.SetChildren(eRoot, [DirectoryItem(@"E:\models")]);
        fileSystem.SetChildren(@"E:\models", [FileItem(eFile)]);
        await using var builtIn = CreateFixedRootBuiltIn(fileSystem, cRoot, dRoot, eRoot);
        var accelerator = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            [cFile]);
        var journal = new CountingJournalProvider(Checkpoint());
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn, mftProvider: journal);

        var entries = await ReadAllAsync(fallback.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(cFile.FullPath, Assert.Single(entries).Candidate.FullPath);
        Assert.Equal(0, journal.ProbeCalls);
        Assert.Equal(0, journal.DiscoverCalls);
        Assert.Equal(0, journal.CaptureCheckpointCalls);
        Assert.Empty(fileSystem.EnumeratedDirectories);
        Assert.Equal("EVERYTHING_IPC_READY", fallback.LastCapability.Code);
        Assert.True(fallback.LastReport.IsCompleteForDeletion);
        Assert.All(fallback.LastReport.Roots, static root => Assert.True(root.FullyEnumerated));
        Assert.Null(fallback.SnapshotCheckpoint);
    }

    [Fact]
    public async Task DiscoverEntries_SuccessfulEverything_ReturnsEverythingSetWithoutBuiltInEnumeration()
    {
        var now = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var accelerator = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            [
                new FileCandidate(
                    @"C:\models\a.SLDPRT",
                    SolidWorksDocumentKind.Part,
                    1,
                    now),
                new FileCandidate(
                    @"D:\models\b.SLDASM",
                    SolidWorksDocumentKind.Assembly,
                    2,
                    now),
            ]);
        var builtIn = new CountingIncrementalBuiltIn();
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn);

        var entries = await ReadAllAsync(fallback.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(
            [@"C:\models\a.SLDPRT", @"D:\models\b.SLDASM"],
            entries.Select(static entry => entry.Candidate.FullPath).ToArray());
        Assert.All(entries, static entry => Assert.Null(entry.Identity));
        Assert.Equal(0, builtIn.DiscoverEntriesCalls);
        Assert.Equal(0, builtIn.TryInspectCalls);
        Assert.Equal("EVERYTHING_IPC_READY", fallback.LastCapability.Code);
    }

    [Fact]
    public async Task ReadDeltaBatchAsync_AvailableEverything_RequiresFullReconciliationWithoutJournalRead()
    {
        var accelerator = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "EVERYTHING_IPC_READY", "ready"),
            []);
        var builtIn = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            []);
        var journal = new CountingJournalProvider(Checkpoint());
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn, mftProvider: journal);
        var checkpoint = Checkpoint();

        var batch = await fallback.ReadDeltaBatchAsync(checkpoint, 64, CancellationToken.None);

        Assert.True(batch.RequiresFullReconciliation);
        Assert.False(batch.HasMore);
        Assert.Empty(batch.Changes);
        Assert.Equal("EVERYTHING_AVAILABLE_FULL_SNAPSHOT", batch.Code);
        Assert.Equal(0, journal.ProbeCalls);
        Assert.Equal(0, journal.ReadDeltaCalls);
    }

    [Fact]
    public async Task ReadDeltaBatchAsync_UnavailableEverything_StillForwardsToJournal()
    {
        var accelerator = new CountingProvider(
            new RuntimeCapability(CapabilityState.Degraded, "EVERYTHING_IPC_NOT_FOUND", "missing"),
            []);
        var builtIn = new CountingProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            []);
        var journal = new CountingJournalProvider(Checkpoint());
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn, mftProvider: journal);
        var checkpoint = Checkpoint();

        var batch = await fallback.ReadDeltaBatchAsync(checkpoint, 64, CancellationToken.None);

        Assert.False(batch.RequiresFullReconciliation);
        Assert.Equal("test-journal", batch.Code);
        Assert.Equal(1, journal.ReadDeltaCalls);
    }

    private static DiscoveryJournalCheckpoint Checkpoint() => new(
        "test-mft",
        [new DiscoveryVolumeCursor(@"C:\", 1, 1, 7)],
        DateTimeOffset.UtcNow);

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

    private static DiscoveryFileSystemItem FileItem(FileCandidate candidate) => new(
        candidate.FullPath,
        IsDirectory: false,
        FileAttributes.Normal,
        candidate.Size,
        candidate.LastWriteTimeUtc);

    private static DiscoveryFileSystemItem DirectoryItem(string path) => new(
        path,
        IsDirectory: true,
        FileAttributes.Directory,
        0,
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

    private sealed class CountingProvider(
        RuntimeCapability capability,
        IReadOnlyList<FileCandidate> files) : IFileDiscoveryProvider
    {
        internal int ProbeCalls { get; private set; }

        internal int DiscoverCalls { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCalls++;
            return ValueTask.FromResult(capability);
        }

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
                await Task.Yield();
            }
        }
    }

    private sealed class CountingJournalProvider(DiscoveryJournalCheckpoint checkpoint) :
        IIncrementalFileDiscoveryProvider,
        IJournalIncrementalDiscoveryProvider
    {
        internal int ProbeCalls { get; private set; }

        internal int DiscoverCalls { get; private set; }

        internal int CaptureCheckpointCalls { get; private set; }

        internal int ReadDeltaCalls { get; private set; }

        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(6);

        public DiscoveryScanReport LastReport => DiscoveryScanReport.Empty;

        public DiscoveryJournalCheckpoint? SnapshotCheckpoint => checkpoint;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCalls++;
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "NTFS_MFT_USN_READY",
                "ready"));
        }

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DiscoveredFile?>(null);
        }

        public ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCheckpointCalls++;
            return ValueTask.FromResult<DiscoveryJournalCheckpoint?>(checkpoint);
        }

        public ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
            DiscoveryJournalCheckpoint current,
            int maximumChanges,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadDeltaCalls++;
            return ValueTask.FromResult(new DiscoveryDeltaBatch(
                current,
                current,
                Array.Empty<DiscoveryDelta>(),
                HasMore: false,
                RequiresFullReconciliation: false,
                "test-journal"));
        }
    }

    private sealed class CountingIncrementalBuiltIn : IIncrementalFileDiscoveryProvider
    {
        internal int DiscoverEntriesCalls { get; private set; }

        internal int TryInspectCalls { get; private set; }

        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(6);

        public DiscoveryScanReport LastReport => DiscoveryScanReport.Empty;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "BUILTIN_DISCOVERY_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverEntriesCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverEntriesCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryInspectCalls++;
            return ValueTask.FromResult<DiscoveredFile?>(null);
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
}
