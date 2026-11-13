using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc73ShortTermSidecarTests
{
    [Fact]
    public async Task ShortTerm_OneAndTwoCharacter_UsesSidecarLikeForCountPagingAndAttribution()
    {
        await using var sandbox = new Sandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();

        var abPart = new FileCandidate(
            @"C:\RC73\AB-Part.SLDPRT",
            SolidWorksDocumentKind.Part,
            10,
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));
        var folderAssembly = new FileCandidate(
            @"C:\RC73\FolderAB\Plain.SLDASM",
            SolidWorksDocumentKind.Assembly,
            11,
            new DateTimeOffset(2026, 8, 13, 0, 0, 1, TimeSpan.Zero));
        var noisePart = new FileCandidate(
            @"C:\RC73\Noise.SLDPRT",
            SolidWorksDocumentKind.Part,
            12,
            new DateTimeOffset(2026, 8, 13, 0, 0, 2, TimeSpan.Zero));

        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([abPart, folderAssembly, noisePart]),
            CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                abPart,
                [new DocumentProperty(
                    "Code",
                    "ABMODEL",
                    "ABMODEL",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                abPart.LastWriteTimeUtc),
            CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                folderAssembly,
                [new DocumentProperty(
                    "Code",
                    "XYVALUE",
                    "XYVALUE",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                folderAssembly.LastWriteTimeUtc),
            CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                noisePart,
                [new DocumentProperty(
                    "Other",
                    "ZZZ",
                    "ZZZ",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                noisePart.LastWriteTimeUtc),
            CancellationToken.None);

        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));

        var sidecarReads = 0;
        store.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref sidecarReads);
            return ValueTask.CompletedTask;
        };

        try
        {
            // Two-character term: filename, property value, All paging, and
            // kind/scope filters all run through the sidecar LIKE path.
            Assert.Equal(1, await store.CountSearchAsync(
                new SearchQuery("AB", SearchMode.FileName), CancellationToken.None));
            var filenameHit = Assert.Single(await store.SearchAsync(
                new SearchQuery("AB", SearchMode.FileName, Limit: 20), CancellationToken.None));
            Assert.Equal(abPart.FullPath, filenameHit.FullPath);
            Assert.Null(filenameHit.MatchedProperty);
            Assert.Null(filenameHit.MatchedValue);

            Assert.Equal(1, await store.CountSearchAsync(
                new SearchQuery("AB", SearchMode.Property), CancellationToken.None));
            var propertyHit = Assert.Single(await store.SearchAsync(
                new SearchQuery("AB", SearchMode.Property, Limit: 20), CancellationToken.None));
            Assert.Equal(abPart.FullPath, propertyHit.FullPath);
            Assert.Equal("Code", propertyHit.MatchedProperty);
            Assert.Equal("ABMODEL", propertyHit.MatchedValue);

            Assert.Equal(2, await store.CountSearchAsync(
                new SearchQuery("AB", SearchMode.All), CancellationToken.None));
            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allCount = await store.CountSearchAsync(
                new SearchQuery("AB", SearchMode.All), CancellationToken.None);
            for (var offset = 0; offset < allCount; offset++)
            {
                var page = await store.SearchAsync(
                    new SearchQuery("AB", SearchMode.All, Offset: offset, Limit: 1),
                    CancellationToken.None);
                Assert.Single(page);
                Assert.True(allPaths.Add(page[0].FullPath), page[0].FullPath);
            }
            Assert.Equal(allCount, allPaths.Count);

            Assert.Equal(1, await store.CountSearchAsync(
                new SearchQuery(
                    "AB",
                    SearchMode.All,
                    Limit: 20,
                    Kinds: [SolidWorksDocumentKind.Part]),
                CancellationToken.None));
            Assert.Equal(1, await store.CountSearchAsync(
                new SearchQuery(
                    "AB",
                    SearchMode.All,
                    Limit: 20,
                    Scope: SearchScope.Folder(@"C:\RC73\FolderAB")),
                CancellationToken.None));

            // Single-character term still reaches the same sidecar LIKE path.
            Assert.Equal(1, await store.CountSearchAsync(
                new SearchQuery("B", SearchMode.FileName), CancellationToken.None));
            var singlePropertyHit = Assert.Single(await store.SearchAsync(
                new SearchQuery("B", SearchMode.Property, Limit: 20), CancellationToken.None));
            Assert.Equal("Code", singlePropertyHit.MatchedProperty);
            Assert.Equal("ABMODEL", singlePropertyHit.MatchedValue);

            Assert.True(
                Volatile.Read(ref sidecarReads) > 0,
                "Short-term searches must execute the sidecar LIKE path.");
        }
        finally
        {
            store.AfterSearchReadinessAcceptedForTestingAsync = null;
        }
    }

    private static async Task WaitForSearchReadyAsync(Sandbox sandbox, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var current = await ReadScalarAsync(
                    sandbox.CorePath,
                    "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;");
                if (await ReadScalarAsync(
                        sandbox.CorePath,
                        "SELECT rebuild_required FROM search_projection_state WHERE singleton_id=1;") == 0 &&
                    await ReadScalarAsync(
                        sandbox.CorePath,
                        "SELECT count(*) FROM search_projection_outbox;") == 0 &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT ready FROM search_state WHERE singleton_id=1;") == 1 &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT applied_sequence FROM search_state WHERE singleton_id=1;") == current)
                {
                    return;
                }
            }
            catch (SqliteException)
            {
                // The sidecar worker owns this sandbox-local derived file.
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("RC73 short-term sidecar did not become ready.");
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class Sandbox : IAsyncDisposable
    {
        internal Sandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"swsearch-rc73-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            CorePath = Path.Combine(RootPath, "core.db");
            SearchPath = Path.Combine(RootPath, "core.search.db");
        }

        internal string RootPath { get; }

        internal string CorePath { get; }

        internal string SearchPath { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
