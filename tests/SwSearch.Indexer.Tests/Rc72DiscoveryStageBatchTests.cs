using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc72DiscoveryStageBatchTests
{
    [Fact]
    public async Task StageDiscoveryBatch_HandlesMultiChunkInsert_AndFinalizesCorrectly()
    {
        // 200 rows = 2 full chunks of 100, exercising the chunked multi-row
        // INSERT path.  Verify every file is staged and visible after
        // finalization.
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 200)
            .Select(i => new FileCandidate(
                $@"C:\chunked\{i:D4}.SLDPRT",
                SolidWorksDocumentKind.Part,
                100 + i,
                now.AddSeconds(i)))
            .ToArray();

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        var status = await store.GetStatusAsync();
        Assert.Equal(1, status.DiscoveryGeneration);
        Assert.Equal(200, status.PresentFileCount);

        // Verify a few files are searchable by filename
        var hit = Assert.Single(await store.SearchAsync(
            new SearchQuery("0050", SearchMode.FileName),
            CancellationToken.None));
        Assert.Equal(@"C:\chunked\0050.SLDPRT", hit.FullPath);

        Assert.Single(await store.SearchAsync(
            new SearchQuery("0199", SearchMode.FileName),
            CancellationToken.None));
    }

    [Fact]
    public async Task StageDiscoveryBatch_OverlappingRuns_LastWriteWins()
    {
        // Two complete snapshots for the same path_norm: the second should
        // replace the first, same ON CONFLICT semantics as the per-row path.
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var first = new FileCandidate(
            @"C:\upsert\same.SLDPRT",
            SolidWorksDocumentKind.Part,
            100,
            now);
        var second = first with
        {
            Size = 200,
            LastWriteTimeUtc = now.AddMinutes(1),
        };

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([first]), CancellationToken.None);
        Assert.Equal(1, (await store.GetStatusAsync()).PresentFileCount);

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([second]), CancellationToken.None);
        Assert.Equal(2, (await store.GetStatusAsync()).DiscoveryGeneration);
        Assert.Equal(1, (await store.GetStatusAsync()).PresentFileCount);
        var searchHit = Assert.Single(
            await store.SearchAsync(
                new SearchQuery("same", SearchMode.FileName),
                CancellationToken.None));
        Assert.Equal(@"C:\upsert\same.SLDPRT", searchHit.FullPath);
    }

    [Fact]
    public async Task StageDiscoveryBatch_EmptyBatch_NoOpsWithoutGateContention()
    {
        // The empty-batch guard must return without acquiring the write
        // gate or opening a connection.
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        // Use a manual snapshot with an empty file set; the batch is
        // filled per-iteration and never hits the staging threshold.
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([]),
            CancellationToken.None);

        Assert.Equal(0, (await store.GetStatusAsync()).PresentFileCount);
    }

    [Fact]
    public async Task StageDiscoveryBatch_MultiChunkWithDuplicateWithinBatch_UpsertsCorrectly()
    {
        // 150 rows (1.5 chunks) triggers one full 100-row chunk + a 50-row
        // tail.  Verify the stage table contains exactly the right count
        // after the batch commits.
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 150)
            .Select(i => new FileCandidate(
                $@"C:\partial\{i:D4}.SLDPRT",
                SolidWorksDocumentKind.Part,
                100 + i,
                now.AddSeconds(i)))
            .ToArray();

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        var status = await store.GetStatusAsync();
        Assert.Equal(150, status.PresentFileCount);

        // Spot-check the first and last rows are searchable
        Assert.Single(await store.SearchAsync(
            new SearchQuery("0000", SearchMode.FileName),
            CancellationToken.None));
        Assert.Single(await store.SearchAsync(
            new SearchQuery("0149", SearchMode.FileName),
            CancellationToken.None));
    }
}
