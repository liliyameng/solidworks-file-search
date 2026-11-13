using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class DiscoveryAndQueueTests
{
    [Fact]
    public async Task Snapshot_CommitsGenerationAndMarksMissingFileDeleted()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var part = new FileCandidate(@"C:\项目\泵体.SLDPRT", SolidWorksDocumentKind.Part, 10, now);
        var drawing = new FileCandidate(@"C:\项目\泵体.SLDDRW", SolidWorksDocumentKind.Drawing, 20, now);

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([part, drawing]), CancellationToken.None);
        var first = await store.GetStatusAsync();
        Assert.Equal(1, first.DiscoveryGeneration);
        Assert.Equal(2, first.PresentFileCount);
        Assert.Equal(2, first.PendingMetadataCount);

        var filenameHits = await store.SearchAsync(
            new SearchQuery("泵体", SearchMode.FileName),
            CancellationToken.None);
        Assert.Equal(2, filenameHits.Count);

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([drawing]), CancellationToken.None);
        var second = await store.GetStatusAsync();
        Assert.Equal(2, second.DiscoveryGeneration);
        Assert.Equal(1, second.PresentFileCount);
        Assert.Single(await store.SearchAsync(
            new SearchQuery("泵体", SearchMode.FileName),
            CancellationToken.None));
    }

    [Fact]
    public async Task FailedSnapshot_DoesNotAdvanceOrDeletePreviousGeneration()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\stable.SLDASM",
            SolidWorksDocumentKind.Assembly,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ApplyDiscoverySnapshotAsync(
                AsyncFiles.FailAfter(file with { Size = 2 }),
                CancellationToken.None));

        var status = await store.GetStatusAsync();
        Assert.Equal(1, status.DiscoveryGeneration);
        Assert.Equal(1, status.PresentFileCount);
    }

    [Fact]
    public async Task GenericSnapshotFailure_DoesNotPublishMetadataProgressEpoch()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\epoch-stable.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        var publishedEpoch = store.MetadataProgressEpoch;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ApplyDiscoverySnapshotAsync(
                AsyncFiles.FailAfter(file with { Size = 2 }),
                CancellationToken.None));

        Assert.Equal(publishedEpoch, store.MetadataProgressEpoch);
        Assert.Equal(1, (await store.GetStatusAsync()).DiscoveryGeneration);
    }

    [Fact]
    public async Task SnapshotChange_DoesNotInvalidateAnActiveLease()
    {
        await using var database = new TemporaryDatabase();
        var original = new FileCandidate(
            @"C:\active-lease.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        var changed = original with
        {
            Size = original.Size + 1,
            LastWriteTimeUtc = original.LastWriteTimeUtc.AddMinutes(1),
        };
        string leaseFingerprint;
        await using (var firstStore = new SqliteIndexStore(database.Path))
        {
            await firstStore.InitializeAsync();
            await firstStore.ApplyDiscoverySnapshotAsync(AsyncFiles.From([original]), CancellationToken.None);
            var lease = Assert.Single(await firstStore.LeaseMetadataAsync(
                1,
                TimeSpan.FromSeconds(30),
                "active-owner"));
            leaseFingerprint = lease.Fingerprint;

            await firstStore.ApplyDiscoverySnapshotAsync(AsyncFiles.From([changed]), CancellationToken.None);
            Assert.Empty(await firstStore.LeaseMetadataAsync(
                1,
                TimeSpan.FromSeconds(1),
                "other-owner"));
            await firstStore.CompleteMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, succeeded: true);
        }

        // The staged evidence survives the broker/store restart. It was
        // persisted with the full snapshot rather than relying on the next
        // scan to happen to observe the same change again.
        await using var recoveredStore = new SqliteIndexStore(database.Path);
        await recoveredStore.InitializeAsync();
        // Lease admission replays protected evidence in the same BEGIN
        // IMMEDIATE transaction before it selects a candidate, so a stale
        // fingerprint cannot be leased after restart.
        var replacement = Assert.Single(await recoveredStore.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "replacement-owner"));
        Assert.NotEqual(leaseFingerprint, replacement.Fingerprint);
    }

    [Fact]
    public async Task LiveHintChange_DoesNotInvalidateAnActiveLease()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var original = new FileCandidate(
            @"C:\active-hint.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([original]), CancellationToken.None);
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "active-hint-owner"));
        var changed = original with
        {
            Size = original.Size + 1,
            LastWriteTimeUtc = original.LastWriteTimeUtc.AddMinutes(1),
        };

        await store.ApplyDiscoveryHintAsync(
            new DiscoveredFile(
                changed,
                DiscoveryPath.NormalizeKey(changed.FullPath),
                @"C:\",
                Identity: null),
            CancellationToken.None);

        await store.CompleteMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, succeeded: true);
        var replayed = await store.ApplyDeferredDiscoveryChangesAsync();
        Assert.True(replayed.Changed);
        Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "hint-replacement-owner"));
    }

    [Fact]
    public async Task LaterUnleasedHint_SupersedesOlderDeferredLeaseEvidence()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var original = new FileCandidate(
            @"C:\deferred-supersede.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([original]), CancellationToken.None);
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "deferred-supersede-owner"));
        var firstObservation = original with
        {
            Size = 2,
            LastWriteTimeUtc = original.LastWriteTimeUtc.AddMinutes(1),
        };
        await store.ApplyDiscoveryHintAsync(new DiscoveredFile(
            firstObservation,
            DiscoveryPath.NormalizeKey(firstObservation.FullPath),
            @"C:\",
            Identity: null));

        await store.CompleteMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, succeeded: true);
        var latestObservation = original with
        {
            Size = 3,
            LastWriteTimeUtc = original.LastWriteTimeUtc.AddMinutes(2),
        };
        await store.ApplyDiscoveryHintAsync(new DiscoveredFile(
            latestObservation,
            DiscoveryPath.NormalizeKey(latestObservation.FullPath),
            @"C:\",
            Identity: null));

        var leaseAfterLatestObservation = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "deferred-supersede-replacement"));
        Assert.Equal(
            $"{latestObservation.Size}:{latestObservation.LastWriteTimeUtc.UtcTicks}::{ExtractorVersions.Current}",
            leaseAfterLatestObservation.Fingerprint);
    }

    [Fact]
    public async Task SnapshotDeletion_DuringActiveLeasePersistsTombstoneUntilLeaseCompletes()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var original = new FileCandidate(
            @"C:\active-delete.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([original]), CancellationToken.None);
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "active-delete-owner"));

        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([]),
            new DiscoverySnapshotOptions("active-delete", IsComplete: true, OnlineRoots: [@"C:\"]),
            CancellationToken.None);
        Assert.Equal(1, (await store.GetStatusAsync()).PresentFileCount);

        await store.CompleteMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, succeeded: true);
        Assert.Empty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "post-delete-owner"));
        Assert.Equal(0, (await store.GetStatusAsync()).PresentFileCount);
    }

    [Fact]
    public async Task JournalDeletion_DuringActiveLeasePersistsTombstoneForReplay()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var original = new FileCandidate(
            @"C:\journal-active-delete.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([original]), CancellationToken.None);
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "journal-active-owner"));
        var result = await store.ApplyDiscoveryDeltaBatchAsync(
        [
            new DiscoveryDelta(
                DiscoveryDeltaKind.Delete,
                original.FullPath,
                @"C:\",
                File: null,
                Reason: "test-delete"),
        ]);
        Assert.False(result.Changed);
        Assert.True(result.DeferredForActiveLease);

        await store.CompleteMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, succeeded: true);
        Assert.Empty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "journal-post-delete-owner"));
        Assert.Equal(0, (await store.GetStatusAsync()).PresentFileCount);
    }

    [Fact]
    public async Task LiveHintBatch_PublishesOneWorkEpochForManyChanges()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 3)
            .Select(index => new FileCandidate(
                $@"C:\hint-batch-{index}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        var epochBefore = store.MetadataProgressEpoch;

        var changed = files
            .Select(file => file with
            {
                Size = file.Size + 10,
                LastWriteTimeUtc = file.LastWriteTimeUtc.AddMinutes(1),
            })
            .Select(file => new DiscoveredFile(
                file,
                DiscoveryPath.NormalizeKey(file.FullPath),
                @"C:\",
                Identity: null))
            .ToArray();
        var result = await store.ApplyDiscoveryHintBatchAsync(changed, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.DeferredForActiveLease);
        Assert.Equal(epochBefore + 1, store.MetadataProgressEpoch);
        Assert.Equal(3, (await store.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task Lease_ExpiresAndIsRecoveredAfterRestart()
    {
        await using var database = new TemporaryDatabase();
        var file = new FileCandidate(
            @"C:\lease.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await using (var firstStore = new SqliteIndexStore(database.Path))
        {
            await firstStore.InitializeAsync();
            await firstStore.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
            var firstLease = Assert.Single(await firstStore.LeaseMetadataAsync(
                1,
                TimeSpan.FromMilliseconds(20),
                "worker-a"));
            Assert.Equal(1, firstLease.Attempt);
        }

        await Task.Delay(40);
        await using var recoveredStore = new SqliteIndexStore(database.Path);
        await recoveredStore.InitializeAsync();
        var recovered = Assert.Single(await recoveredStore.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(1),
            "worker-b"));
        Assert.Equal(2, recovered.Attempt);
        await recoveredStore.CompleteMetadataLeaseAsync(recovered.FileId, "worker-b", true);
        Assert.Equal(0, (await recoveredStore.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task Initialize_CreatesExplicitSchemaV3AndWal()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={database.Path}");
        await connection.OpenAsync();
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(ContractVersions.IndexSchema, Convert.ToInt32(await version.ExecuteScalarAsync()));
        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task MetadataProgress_AggregatesCompletedPartialPendingAndFailed()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var modified = DateTimeOffset.UtcNow;
        var failed = new FileCandidate(@"C:\Progress\failed.SLDPRT", SolidWorksDocumentKind.Part, 1, modified);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([failed]), CancellationToken.None);

        var first = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "progress-a"));
        await store.CompleteMetadataLeaseAsync(first.FileId, "progress-a", false, "TEST", TimeSpan.Zero);
        var second = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "progress-b"));
        await store.CompleteMetadataLeaseAsync(second.FileId, "progress-b", false, "TEST", TimeSpan.Zero);

        var completed = new FileCandidate(@"C:\Progress\completed.SLDASM", SolidWorksDocumentKind.Assembly, 2, modified);
        var partial = new FileCandidate(@"C:\Progress\partial.SLDDRW", SolidWorksDocumentKind.Drawing, 3, modified);
        var pending = new FileCandidate(@"C:\Progress\pending.SLDPRT", SolidWorksDocumentKind.Part, 4, modified);
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([failed, completed, partial, pending]),
            CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(
            completed, [], [], MetadataSource.NoApi, modified), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(
            partial, [], [], MetadataSource.WindowsPropertyStore, modified, "NOAPI_FORMAT_UNSUPPORTED"), CancellationToken.None);

        var progress = await store.GetMetadataProgressAsync();

        Assert.Equal(4, progress.DiscoveredFileCount);
        Assert.Equal(1, progress.CompletedCount);
        Assert.Equal(1, progress.PartialCount);
        Assert.Equal(1, progress.PendingCount);
        Assert.Equal(1, progress.FailedCount);
    }
}
