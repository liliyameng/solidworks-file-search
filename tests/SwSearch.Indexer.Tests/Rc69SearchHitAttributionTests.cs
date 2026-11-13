using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc69SearchHitAttributionTests
{
    [Fact]
    public async Task CoreSearch_AttributesFilenameValueAndPropertyNameHitsToTheActualMatch()
    {
        await using var sandbox = new Sandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();

        var file = CreateFile();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(CreateMetadata(file), CancellationToken.None);

        // Two-character terms deliberately use the authoritative core path,
        // including while the derived search database is catching up.
        var filenameHit = Assert.Single(await store.SearchAsync(
            new SearchQuery("F2", SearchMode.All),
            CancellationToken.None));
        Assert.Null(filenameHit.MatchedProperty);
        Assert.Null(filenameHit.MatchedValue);

        var valueHit = Assert.Single(await store.SearchAsync(
            new SearchQuery("V2", SearchMode.Property),
            CancellationToken.None));
        Assert.Equal("ValueCode", valueHit.MatchedProperty);
        Assert.Equal("V2", valueHit.MatchedValue);

        var propertyNameHit = Assert.Single(await store.SearchAsync(
            new SearchQuery("N2", SearchMode.Property),
            CancellationToken.None));
        Assert.Equal("N2", propertyNameHit.MatchedProperty);
        Assert.Equal("NameMatchValue", propertyNameHit.MatchedValue);
    }

    [Fact]
    public async Task SidecarSearch_UsesExactMatchedPropertyAndStableMultiHitTieBreak()
    {
        await using var sandbox = new Sandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();

        var file = CreateFile();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(CreateMetadata(file), CancellationToken.None);

        var acceptedSidecarReads = 0;
        store.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref acceptedSidecarReads);
            return ValueTask.CompletedTask;
        };

        try
        {
            var exactValueWinner = await WaitForSidecarHitAsync(
                store,
                new SearchQuery("TIE69", SearchMode.Property),
                () => Volatile.Read(ref acceptedSidecarReads) > 0);
            Assert.Equal("ExactValueWinner", exactValueWinner.MatchedProperty);
            Assert.Equal("TIE69", exactValueWinner.MatchedValue);

            var sameRankWinner = await WaitForSidecarHitAsync(
                store,
                new SearchQuery("SAME69", SearchMode.Property),
                () => Volatile.Read(ref acceptedSidecarReads) > 1);
            Assert.Equal("FirstExact", sameRankWinner.MatchedProperty);
            Assert.Equal("SAME69", sameRankWinner.MatchedValue);
        }
        finally
        {
            store.AfterSearchReadinessAcceptedForTestingAsync = null;
        }
    }

    private static FileCandidate CreateFile() => new(
        @"C:\RC69\Attribution-F2.SLDPRT",
        SolidWorksDocumentKind.Part,
        69,
        new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));

    private static DocumentMetadata CreateMetadata(FileCandidate file) => new(
        file,
        [
            // This intentionally comes first. Before RC69, a matching row
            // was joined back to every property in its document and this
            // unrelated value could leak into the result columns.
            new DocumentProperty(
                "OuterDimensions",
                "0.0x0.0x0.0mm",
                "0.0x0.0x0.0mm",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "ValueCode",
                "V2",
                "V2",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "N2",
                "NameMatchValue",
                "NameMatchValue",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "ExactValueWinner",
                "TIE69",
                "TIE69",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "TIE69",
                "NameMatchOnly",
                "NameMatchOnly",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "ContainsOnly",
                "xxTIE69xx",
                "xxTIE69xx",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "FirstExact",
                "SAME69",
                "SAME69",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
            new DocumentProperty(
                "SecondExact",
                "SAME69",
                "SAME69",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi),
        ],
        [],
        MetadataSource.NoApi,
        file.LastWriteTimeUtc);

    private static async Task<SearchHit> WaitForSidecarHitAsync(
        SqliteIndexStore store,
        SearchQuery query,
        Func<bool> sidecarWasAccepted)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var hits = await store.SearchAsync(query, CancellationToken.None);
            if (sidecarWasAccepted() && hits.Count == 1)
            {
                return hits[0];
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"RC69 sidecar was not ready for '{query.Text}' at {store.SearchDatabasePath}.");
    }

    private sealed class Sandbox : IAsyncDisposable
    {
        internal Sandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"swsearch-rc69-hit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            CorePath = Path.Combine(RootPath, "core.db");
        }

        private string RootPath { get; }

        internal string CorePath { get; }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
