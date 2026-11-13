using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc56MetadataMaintenanceSchedulingTests
{
    [Fact]
    public async Task HealthyMetadataCommitBurst_CoalescesMaintenanceInsteadOfAwaitingEachBatch()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        store.MetadataCommitMaintenanceIntervalForTesting = TimeSpan.FromMinutes(1);

        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 4)
            .Select(index => new FileCandidate(
                $@"C:\RC56\Commit-{index:D2}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        var schedulesBefore = store.MetadataCommitMaintenanceScheduleCountForTesting;
        var maintenanceRequestsBefore = store.StorageMaintenanceStartRequestCountForTesting;

        foreach (var file in files)
        {
            await store.UpsertMetadataAsync(new DocumentMetadata(
                file,
                [new DocumentProperty(
                    "Code",
                    Path.GetFileNameWithoutExtension(file.FullPath),
                    Path.GetFileNameWithoutExtension(file.FullPath),
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                now),
                CancellationToken.None);
        }

        // A healthy burst asks the low-priority owner once.  Before RC56 each
        // completed item synchronously called MaintainStorageAsync, which also
        // kicked orphan/projection maintenance once per commit.
        Assert.Equal(
            schedulesBefore + 1,
            store.MetadataCommitMaintenanceScheduleCountForTesting);
        Assert.Equal(
            maintenanceRequestsBefore + 1,
            store.StorageMaintenanceStartRequestCountForTesting);
        Assert.Equal(4, (await store.GetStatusAsync()).PresentFileCount);
    }

    [Fact]
    public async Task ExplicitMaintenance_RemainsAnImmediateWalRecoverySafetyPath()
    {
        await using var database = new TemporaryDatabase();
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: 64 * 1024 * 1024,
            MaximumDatabaseBytes: 128 * 1024 * 1024,
            PassiveCheckpointWalBytes: 4 * 1024,
            // Leave enough hysteresis that a valid small post-checkpoint WAL
            // tail is not mistaken for a failed recovery.  The production
            // contract is the pause state/lease admission, not zero bytes.
            PauseWalBytes: 64 * 1024);
        await using var store = new SqliteIndexStore(database.Path, policy);
        await store.InitializeAsync();

        var seed = new FileCandidate(
            @"C:\RC56\Seed.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([seed]), CancellationToken.None);

        await using var readerConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={database.Path};Cache=Private");
        await readerConnection.OpenAsync();
        await using var command = readerConnection.CreateCommand();
        command.CommandText = "SELECT id,path FROM files ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var burst = Enumerable.Range(0, 400)
            .Select(index => new FileCandidate(
                $@"C:\RC56\Wal-{index:D4}.SLDPRT",
                SolidWorksDocumentKind.Part,
                1_024 + index,
                DateTimeOffset.UtcNow))
            .Prepend(seed)
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(burst), CancellationToken.None);
        var pressured = await store.GetStorageStatusAsync(forceFresh: true);
        Assert.True(pressured.MetadataWritesPaused, $"wal={pressured.WalBytes}");
        Assert.Equal("WAL_PRESSURE", pressured.PauseReason);

        await reader.DisposeAsync();
        await readerConnection.DisposeAsync();

        var recovered = await store.MaintainStorageAsync();
        Assert.False(recovered.MetadataWritesPaused, $"wal={recovered.WalBytes}");
        Assert.True(store.StorageCheckpointCountForTesting > 0);
        Assert.NotEmpty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMinutes(1),
            "rc56-recovered",
            CancellationToken.None));
    }
}
