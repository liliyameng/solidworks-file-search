using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc58MaintenancePriorityTests
{
    [Fact]
    public async Task MetadataBacklog_DefersLowPriorityMaintenanceUntilItFallsBelowLowWater()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        store.LowPriorityMaintenanceBacklogHighWaterForTesting = 4;
        store.LowPriorityMaintenanceBacklogLowWaterForTesting = 1;
        store.LowPriorityMaintenanceFairnessIntervalForTesting = TimeSpan.FromMinutes(1);

        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 4)
            .Select(index => new FileCandidate(
                $@"C:\RC58\Queued-{index:D2}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        // The first high-water observation does not take the writer for an
        // orphan/projection slice.  It opens a bounded fairness window instead.
        Assert.True(await store.ShouldDeferLowPriorityMaintenanceForTestingAsync());
        Assert.True(store.LowPriorityMaintenanceBacklogDeferralCountForTesting > 0);

        var leases = await store.LeaseMetadataAsync(
            3,
            TimeSpan.FromMinutes(1),
            "rc58-low-water",
            CancellationToken.None);
        Assert.Equal(3, leases.Count);
        foreach (var lease in leases)
        {
            await store.CompleteMetadataLeaseAsync(
                lease.FileId,
                lease.LeaseOwner,
                succeeded: true,
                cancellationToken: CancellationToken.None);
        }

        // Hysteresis keeps maintenance deferred above the low water mark, then
        // immediately allows it once only one durable metadata lease remains.
        Assert.False(await store.ShouldDeferLowPriorityMaintenanceForTestingAsync());
    }

    [Fact]
    public async Task MetadataBacklog_DoesNotBlockCheckpointSafetyPath()
    {
        await using var database = new TemporaryDatabase();
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: 64 * 1024 * 1024,
            MaximumDatabaseBytes: 128 * 1024 * 1024,
            PassiveCheckpointWalBytes: 1,
            PauseWalBytes: 1024 * 1024);
        await using var store = new SqliteIndexStore(database.Path, policy);
        await store.InitializeAsync();
        store.LowPriorityMaintenanceBacklogHighWaterForTesting = 4;
        store.LowPriorityMaintenanceBacklogLowWaterForTesting = 1;
        store.LowPriorityMaintenanceFairnessIntervalForTesting = TimeSpan.FromMinutes(1);

        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 4)
            .Select(index => new FileCandidate(
                $@"C:\RC58\Checkpoint-{index:D2}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        Assert.True(await store.ShouldDeferLowPriorityMaintenanceForTestingAsync());
        var checkpointsBefore = store.StorageCheckpointCountForTesting;

        // Checkpointing is a storage-safety action, not rebuildable maintenance:
        // it must remain reachable while low-priority work is deferred.
        _ = await store.MaintainStorageAsync(CancellationToken.None);

        Assert.True(store.StorageCheckpointCountForTesting > checkpointsBefore);
    }

    [Fact]
    public async Task MetadataBacklog_DefersSearchProjectionUntilLowWater_WithBoundedFairnessSlice()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        store.SearchProjectionMetadataBacklogHighWaterForTesting = 4;
        store.SearchProjectionMetadataBacklogLowWaterForTesting = 1;
        store.SearchProjectionMetadataFairnessIntervalForTesting = TimeSpan.FromMilliseconds(50);

        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 4)
            .Select(index => new FileCandidate(
                $@"C:\RC58\Projection-{index:D2}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        // At high water the derived FTS sidecar must leave its durable core
        // outbox intact; core search remains the authoritative fallback.
        Assert.Equal(
            "DeferredForMetadataBacklog",
            await store.GetSearchProjectionAdmissionForTestingAsync());
        Assert.True(store.SearchProjectionMetadataBacklogDeferralCountForTesting > 0);

        // A high queue is not permanent starvation: one bounded eight-file
        // (or smaller) sidecar slice is admitted after its own fairness window.
        await Task.Delay(TimeSpan.FromMilliseconds(75));
        Assert.Equal(
            "FairnessSlice",
            await store.GetSearchProjectionAdmissionForTestingAsync());
        Assert.Equal(
            8,
            store.SearchProjectionFairnessWriteBatchSizeForTesting);
        Assert.Equal(
            "DeferredForMetadataBacklog",
            await store.GetSearchProjectionAdmissionForTestingAsync());

        var leases = await store.LeaseMetadataAsync(
            3,
            TimeSpan.FromMinutes(1),
            "rc58-projection-low-water",
            CancellationToken.None);
        Assert.Equal(3, leases.Count);
        foreach (var lease in leases)
        {
            await store.CompleteMetadataLeaseAsync(
                lease.FileId,
                lease.LeaseOwner,
                succeeded: true,
                cancellationToken: CancellationToken.None);
        }

        Assert.Equal(
            "Normal",
            await store.GetSearchProjectionAdmissionForTestingAsync());
    }
}
