using System.Runtime.CompilerServices;
using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Indexer.Tests;

public sealed class NtfsMftUsnDiscoveryProviderTests
{
    private static readonly NtfsVolumeState Volume = new(
        @"C:\",
        42,
        7,
        50,
        100,
        8);

    [Fact]
    public async Task MftSnapshot_BuildsOnlyThreeSolidWorksExtensionsAndCapturesBoundary()
    {
        var source = new FakeNtfsSource
        {
            Volumes = [Volume],
            MftRecords =
            [
                Record(5, 5, ".", directory: true),
                Record(10, 5, "models", directory: true),
                Record(11, 10, "a.SLDPRT"),
                Record(12, 10, "b.sldasm"),
                Record(13, 10, "c.SLDDRW"),
                Record(14, 10, "ignored.step"),
                Record(15, 10, "ignored.stp"),
            ],
        };
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var results = await CollectAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(3, results.Count);
        Assert.Contains(results, item => item.Candidate.FullPath.EndsWith(@"models\a.SLDPRT"));
        Assert.Contains(results, item => item.Candidate.Kind == SolidWorksDocumentKind.Assembly);
        Assert.Contains(results, item => item.Candidate.Kind == SolidWorksDocumentKind.Drawing);
        Assert.DoesNotContain(results, item =>
            item.Candidate.FullPath.EndsWith(".step", StringComparison.OrdinalIgnoreCase) ||
            item.Candidate.FullPath.EndsWith(".stp", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(provider.SnapshotCheckpoint);
        var cursor = Assert.Single(provider.SnapshotCheckpoint.Volumes);
        Assert.Equal(7UL, cursor.JournalId);
        Assert.Equal(100, cursor.NextUsn);
        Assert.Equal(42U, cursor.VolumeSerialNumber);
        Assert.Equal(RuntimeServiceState.Idle, provider.DiscoveryProgress.State);
        Assert.Equal("NTFS_MFT_COMPLETE", provider.DiscoveryProgress.Code);
    }

    [Fact]
    public async Task MftSnapshot_SkipsPlainFileReparseAndIndexesOnlyMarkedCloudReparseNames()
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x0004_0000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x0040_0000;
        var source = new FakeNtfsSource
        {
            Volumes = [Volume],
            MftRecords =
            [
                Record(5, 5, ".", directory: true),
                Record(10, 5, "models", directory: true),
                Record(11, 10, "offline.SLDPRT", attributes: FileAttributes.Offline),
                Record(12, 10, "recall-open.SLDASM", attributes: recallOnOpen),
                Record(13, 10, "recall-data.SLDDRW", attributes: recallOnDataAccess),
                Record(14, 10, "cloud-reparse.SLDPRT",
                    attributes: FileAttributes.ReparsePoint | FileAttributes.Offline),
                Record(15, 10, "outside-target-symlink.SLDPRT",
                    attributes: FileAttributes.ReparsePoint),
            ],
        };
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var results = await CollectAsync(provider.DiscoverEntriesAsync(CancellationToken.None));

        Assert.Equal(4, results.Count);
        Assert.All(results, static result => Assert.Null(result.Identity));
        Assert.DoesNotContain(results,
            static result => result.Candidate.FullPath.Contains("outside-target-symlink"));
        Assert.Equal(1, Assert.Single(provider.LastReport.Roots).ReparsePointsSkipped);
        Assert.Equal(0, source.PathResolutionCalls);
    }

    [Fact]
    public async Task UsnDelta_EmitsOnlyChangedSolidWorksFilesAndAdvancesExplicitCursor()
    {
        var source = new FakeNtfsSource
        {
            Volumes = [Volume with { NextUsn = 150 }],
            JournalRead = new NtfsJournalRead(
                150,
                [
                    Record(11, 10, "a.SLDPRT", reason: NtfsUsnReason.DataOverwrite, usn: 100),
                    Record(11, 10, "a.SLDPRT", reason: NtfsUsnReason.Close, usn: 110),
                    Record(12, 10, "b.SLDASM", reason: NtfsUsnReason.FileDelete, usn: 120),
                    Record(13, 10, "ignored.step", reason: NtfsUsnReason.FileCreate, usn: 130),
                ]),
        };
        source.PathsById[10] = @"C:\models";
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);
        var checkpoint = Checkpoint(100);

        var batch = await provider.ReadDeltaBatchAsync(checkpoint, 64, CancellationToken.None);

        Assert.False(batch.RequiresFullReconciliation);
        Assert.False(batch.HasMore);
        Assert.Collection(
            batch.Changes.OrderBy(static change => change.Kind),
            change =>
            {
                Assert.Equal(DiscoveryDeltaKind.Upsert, change.Kind);
                Assert.EndsWith("a.SLDPRT", change.FullPath, StringComparison.OrdinalIgnoreCase);
                Assert.NotNull(change.File);
            },
            change =>
            {
                Assert.Equal(DiscoveryDeltaKind.Delete, change.Kind);
                Assert.EndsWith("b.SLDASM", change.FullPath, StringComparison.OrdinalIgnoreCase);
                Assert.Null(change.File);
            });
        Assert.Equal(150, Assert.Single(batch.NextCheckpoint.Volumes).NextUsn);
    }

    [Fact]
    public async Task UsnJournalGap_FailsSafeToFullReconciliationWithoutMutations()
    {
        var source = new FakeNtfsSource
        {
            Volumes = [Volume with { FirstUsn = 200, NextUsn = 250 }],
        };
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var batch = await provider.ReadDeltaBatchAsync(Checkpoint(100), 64, CancellationToken.None);

        Assert.True(batch.RequiresFullReconciliation);
        Assert.Empty(batch.Changes);
        Assert.Equal("NTFS_JOURNAL_GAP_OR_RECREATED", batch.Code);
        Assert.Equal(0, source.JournalReadCalls);
    }

    [Fact]
    public async Task DirectoryRename_RequiresFullReconciliationInsteadOfGuessingDescendantPaths()
    {
        var source = new FakeNtfsSource
        {
            Volumes = [Volume with { NextUsn = 120 }],
            JournalRead = new NtfsJournalRead(
                120,
                [Record(
                    10,
                    5,
                    "renamed",
                    directory: true,
                    reason: NtfsUsnReason.RenameNewName,
                    usn: 100)]),
        };
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var batch = await provider.ReadDeltaBatchAsync(Checkpoint(100), 64, CancellationToken.None);

        Assert.True(batch.RequiresFullReconciliation);
        Assert.Equal("NTFS_DIRECTORY_TOPOLOGY_CHANGED", batch.Code);
        Assert.Empty(batch.Changes);
    }

    [Fact]
    public async Task UsnDelta_PreviouslyNormalFileBecomesPlainReparse_EmitsDeleteOnly()
    {
        var source = new FakeNtfsSource
        {
            Volumes = [Volume with { NextUsn = 120 }],
            JournalRead = new NtfsJournalRead(
                120,
                [Record(
                    16,
                    10,
                    "outside-target-symlink.SLDPRT",
                    reason: NtfsUsnReason.BasicInfoChange | NtfsUsnReason.ReparsePointChange,
                    usn: 100,
                    attributes: FileAttributes.ReparsePoint)]),
        };
        source.PathsById[10] = @"C:\models";
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var batch = await provider.ReadDeltaBatchAsync(Checkpoint(100), 64, CancellationToken.None);

        Assert.False(batch.RequiresFullReconciliation);
        var deletion = Assert.Single(batch.Changes);
        Assert.Equal(DiscoveryDeltaKind.Delete, deletion.Kind);
        Assert.Null(deletion.File);
        Assert.Equal("usn-plain-reparse", deletion.Reason);
        Assert.DoesNotContain(batch.Changes, static change => change.Kind == DiscoveryDeltaKind.Upsert);
        Assert.Equal(1, source.ParentPathResolutionCalls);
        Assert.Equal(0, source.TargetPathResolutionCalls);
    }

    [Fact]
    public async Task UsnDelta_NewPlainFileReparse_NeverUpsertsMetadataCandidate()
    {
        var source = new FakeNtfsSource
        {
            Volumes = [Volume with { NextUsn = 120 }],
            JournalRead = new NtfsJournalRead(
                120,
                [Record(
                    17,
                    10,
                    "new-outside-target-symlink.SLDPRT",
                    reason: NtfsUsnReason.FileCreate | NtfsUsnReason.ReparsePointChange,
                    usn: 100,
                    attributes: FileAttributes.ReparsePoint)]),
        };
        source.PathsById[10] = @"C:\models";
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var batch = await provider.ReadDeltaBatchAsync(Checkpoint(100), 64, CancellationToken.None);

        Assert.False(batch.RequiresFullReconciliation);
        Assert.DoesNotContain(batch.Changes, static change => change.Kind == DiscoveryDeltaKind.Upsert);
        Assert.Equal(0, source.TargetPathResolutionCalls);
    }

    [Fact]
    public async Task UsnDelta_PlainReparseWithUnresolvedParent_ReconcilesWithoutAdvancingCursor()
    {
        var checkpoint = Checkpoint(100);
        var source = new FakeNtfsSource
        {
            Volumes = [Volume with { NextUsn = 120 }],
            JournalRead = new NtfsJournalRead(
                120,
                [Record(
                    18,
                    999,
                    "unresolved-outside-target.SLDPRT",
                    reason: NtfsUsnReason.ReparsePointChange,
                    usn: 100,
                    attributes: FileAttributes.ReparsePoint)]),
        };
        await using var provider = new NtfsMftUsnDiscoveryProvider(source);

        var batch = await provider.ReadDeltaBatchAsync(checkpoint, 64, CancellationToken.None);

        Assert.True(batch.RequiresFullReconciliation);
        Assert.Empty(batch.Changes);
        Assert.Same(checkpoint, batch.NextCheckpoint);
        Assert.Equal(100, Assert.Single(batch.NextCheckpoint.Volumes).NextUsn);
        Assert.Equal(1, source.ParentPathResolutionCalls);
        Assert.Equal(0, source.TargetPathResolutionCalls);
    }

    private static NtfsNameRecord Record(
        ulong id,
        ulong parent,
        string name,
        bool directory = false,
        NtfsUsnReason reason = 0,
        long usn = 0,
        FileAttributes attributes = FileAttributes.Normal) =>
        new(
            id,
            parent,
            name,
            directory,
            DateTimeOffset.UtcNow,
            reason,
            usn,
            usn == 0 ? 0 : usn + 8,
            attributes);

    private static DiscoveryJournalCheckpoint Checkpoint(long nextUsn) =>
        new(
            "NTFS-MFT-USN-v1",
            [new DiscoveryVolumeCursor(@"C:\", 7, nextUsn, 42)],
            DateTimeOffset.UtcNow);

    private static async Task<List<DiscoveredFile>> CollectAsync(
        IAsyncEnumerable<DiscoveredFile> source)
    {
        var results = new List<DiscoveredFile>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class FakeNtfsSource : INtfsChangeSource
    {
        internal IReadOnlyList<NtfsVolumeState> Volumes { get; init; } = [];
        internal IReadOnlyList<NtfsNameRecord> MftRecords { get; init; } = [];
        internal NtfsJournalRead JournalRead { get; init; } = new(0, []);
        internal Dictionary<ulong, string> PathsById { get; } = [];
        internal int JournalReadCalls { get; private set; }
        internal int PathResolutionCalls { get; private set; }
        internal int ParentPathResolutionCalls { get; private set; }
        internal int TargetPathResolutionCalls { get; private set; }

        public IReadOnlyList<NtfsVolumeState> GetVolumes(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Volumes;
        }

        public async IAsyncEnumerable<NtfsNameRecord> EnumerateMftAsync(
            NtfsVolumeState volume,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var record in MftRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }

            await Task.CompletedTask;
        }

        public NtfsJournalRead ReadJournal(
            NtfsVolumeState volume,
            long startUsn,
            int maximumRecords,
            CancellationToken cancellationToken)
        {
            JournalReadCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return JournalRead with { Records = JournalRead.Records.Take(maximumRecords).ToArray() };
        }

        public string? TryResolvePathById(
            NtfsVolumeState volume,
            ulong fileReferenceNumber,
            bool directory)
        {
            PathResolutionCalls++;
            if (directory)
            {
                ParentPathResolutionCalls++;
            }
            else
            {
                TargetPathResolutionCalls++;
            }

            return PathsById.GetValueOrDefault(fileReferenceNumber);
        }
    }
}

public sealed class FallbackIncrementalDiscoveryTests
{
    [Fact]
    public async Task MissingEverything_PrefersMftAndForwardsItsLiveProgress()
    {
        var builtIn = new FixedProvider(
            new RuntimeCapability(
                CapabilityState.Available,
                "BUILTIN_READY",
                "ready",
                new Dictionary<string, string> { ["nonFixedOnlineRoots"] = "0" }),
            []);
        var file = new FileCandidate(
            @"C:\models\a.SLDPRT",
            SolidWorksDocumentKind.Part,
            0,
            DateTimeOffset.UtcNow);
        var mft = new FixedMftProvider(file);
        await using var provider = new FallbackDiscoveryProvider(
            new FixedProvider(
                new RuntimeCapability(CapabilityState.Degraded, "NO_EVERYTHING", "missing"),
                []),
            builtIn,
            mftProvider: mft);

        var results = await CollectCandidatesAsync(provider.DiscoverAsync(CancellationToken.None));

        Assert.Equal(file.FullPath, Assert.Single(results).FullPath);
        Assert.Equal(0, builtIn.DiscoverCalls);
        Assert.Equal("NTFS_TEST_RUNNING", provider.DiscoveryProgress.Code);
        Assert.Equal(10, provider.DiscoveryProgress.EstimatedTotalCount);
        Assert.NotNull(provider.SnapshotCheckpoint);
    }

    private static async Task<List<FileCandidate>> CollectCandidatesAsync(
        IAsyncEnumerable<FileCandidate> source)
    {
        var results = new List<FileCandidate>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class FixedProvider(
        RuntimeCapability capability,
        IReadOnlyList<FileCandidate> files) : IFileDiscoveryProvider
    {
        internal int DiscoverCalls { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(capability);

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FixedMftProvider(FileCandidate file) :
        IIncrementalFileDiscoveryProvider,
        IJournalIncrementalDiscoveryProvider,
        IDiscoveryProgressSource
    {
        private readonly DiscoveryJournalCheckpoint _checkpoint = new(
            "NTFS-MFT-USN-v1",
            [new DiscoveryVolumeCursor(@"C:\", 7, 100)],
            DateTimeOffset.UtcNow);

        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromDays(1);
        public DiscoveryScanReport LastReport => DiscoveryScanReport.Empty;
        public DiscoveryJournalCheckpoint? SnapshotCheckpoint => _checkpoint;
        public FileDiscoveryProgressSnapshot DiscoveryProgress => new(
            5,
            10,
            RuntimeServiceState.Running,
            DateTimeOffset.UtcNow,
            "NTFS test",
            "NTFS_TEST_RUNNING");

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "NTFS_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DiscoveredFile(
                file,
                DiscoveryPath.NormalizeKey(file.FullPath),
                @"C:\",
                null);
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DiscoveredFile?>(null);

        public ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DiscoveryJournalCheckpoint?>(_checkpoint);

        public ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
            DiscoveryJournalCheckpoint checkpoint,
            int maximumChanges,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DiscoveryDeltaBatch(
                checkpoint,
                checkpoint,
                [],
                false,
                false,
                "EMPTY"));
    }
}
