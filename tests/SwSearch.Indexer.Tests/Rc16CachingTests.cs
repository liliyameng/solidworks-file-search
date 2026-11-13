using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc16CachingTests
{
    [Fact]
    public async Task OffsetPaging_LoadsAllMatchesPastFormer500Boundary()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 625)
            .Select(index => new FileCandidate(
                $@"C:\Paging\Part-{index:D4}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        var all = new List<SearchHit>();
        const int pageSize = 127;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await store.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All, offset, pageSize),
                CancellationToken.None);
            all.AddRange(page);
            if (page.Count < pageSize) break;
        }

        Assert.Equal(625, all.Count);
        Assert.Equal(625, all.Select(static hit => hit.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RecycleBin_IsRejectedByDiscovery_HiddenFromLegacyCache_AndPurgedOnOpen()
    {
        await using var database = new TemporaryDatabase();
        var now = DateTimeOffset.UtcNow;
        var valid = new FileCandidate(
            @"C:\Models\Kept.SLDASM",
            SolidWorksDocumentKind.Assembly,
            10,
            now);
        var recycled = new FileCandidate(
            @"C:\$RECYCLE.BIN\S-1-5-21\Deleted.SLDPRT",
            SolidWorksDocumentKind.Part,
            11,
            now);

        Assert.False(DiscoveryPath.TryGetDocumentKind(recycled.FullPath, out _));
        await using (var store = new SqliteIndexStore(database.Path))
        {
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(
                AsyncFiles.From([valid, recycled]),
                CancellationToken.None);
            Assert.Equal(valid.FullPath, Assert.Single(await store.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All),
                CancellationToken.None)).FullPath, ignoreCase: true);

            await InsertLegacyRecycleRowAsync(database.Path, recycled);
            var visible = await store.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All),
                CancellationToken.None);
            Assert.Single(visible);
            Assert.DoesNotContain(visible, static hit =>
                hit.FullPath.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase));
        }

        await using (var reopened = new SqliteIndexStore(database.Path))
        {
            await reopened.InitializeAsync();
            Assert.Equal(1, (await reopened.GetStatusAsync()).PresentFileCount);
            Assert.Equal(valid.FullPath, Assert.Single(await reopened.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All),
                CancellationToken.None)).FullPath, ignoreCase: true);
        }
    }

    private static async Task InsertLegacyRecycleRowAsync(string databasePath, FileCandidate file)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files(path,path_norm,kind,size,modified_utc,discovery_generation,state,metadata_state)
            SELECT @path,@norm,@kind,@size,@modified,discovery_generation,0,0
              FROM index_state WHERE singleton_id=1;
            """;
        command.Parameters.AddWithValue("@path", file.FullPath);
        // Simulate a pre-RC16 row whose cached normalization was lowercase and
        // therefore cannot be trusted by the query-layer recycle-bin filter.
        command.Parameters.AddWithValue("@norm", SearchPath.NormalizeKey(file.FullPath).ToLowerInvariant());
        command.Parameters.AddWithValue("@kind", (int)file.Kind);
        command.Parameters.AddWithValue("@size", file.Size);
        command.Parameters.AddWithValue("@modified", file.LastWriteTimeUtc.UtcTicks);
        await command.ExecuteNonQueryAsync();
    }
}
