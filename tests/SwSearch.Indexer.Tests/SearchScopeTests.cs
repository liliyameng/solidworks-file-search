using SwSearch.Contracts;
using SwSearch.Testing;
using System.Diagnostics;
using System.Text.Json;

namespace SwSearch.Indexer.Tests;

public sealed class SearchScopeTests
{
    [Fact]
    public void NormalizeFolder_PreservesDriveAndUncRootsAndUnicode()
    {
        Assert.Equal(@"C:\", SearchPath.NormalizeFolder(@"C:\"));
        Assert.Equal(@"C:\设计 空格 &", SearchPath.NormalizeFolder(@"C:\设计 空格 &\"));
        Assert.Equal(@"\\server\share\设计", SearchPath.NormalizeFolder(@"\\server\share\设计\"));
        Assert.Throws<ArgumentException>(() => SearchPath.NormalizeFolder("relative"));
        Assert.Throws<ArgumentException>(() => SearchScope.Folder(@"C:\A", recursive: false).Normalize());
    }

    [Fact]
    public void NormalizeFolder_MatchesUnifiedPureLexicalGoldenMatrixWithoutIo()
    {
        var clock = Stopwatch.StartNew();
        foreach (var item in WindowsScopePathGolden.Cases())
        {
            if (item.IsValid)
            {
                Assert.Equal(item.Expected, SearchPath.NormalizeFolder(item.Input));
            }
            else
            {
                Assert.Throws<ArgumentException>(() => SearchPath.NormalizeFolder(item.Input));
            }
        }
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Lexical matrix blocked for {clock.Elapsed}.");
        Assert.NotEqual(
            SearchPath.NormalizeFolder("C:\\Unicode\\caf\u00E9"),
            SearchPath.NormalizeFolder("C:\\Unicode\\cafe\u0301"));
        Assert.Equal("C:\\Unicode\\\u212B-\uFF21\uFF22\uFF23-\uFB00",
            SearchPath.NormalizeFolder("C:\\Unicode\\\u212B-\uFF21\uFF22\uFF23-\uFB00"));
    }

    [Fact]
    public async Task FolderScope_UsesDirectoryBoundaryAndDoesNotMatchSamePrefix()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(
        [
            new FileCandidate(@"C:\A\子目录\Pump.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(@"C:\AB\Pump.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(@"C:\A & 空格\Pump.SLDASM", SolidWorksDocumentKind.Assembly, 1, now),
        ]), CancellationToken.None);

        var hits = await store.SearchAsync(
            new SearchQuery("Pump", SearchMode.FileName, Scope: SearchScope.Folder(@"C:\A")),
            CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(@"C:\A\子目录\Pump.SLDPRT", hit.FullPath);
    }

    [Fact]
    public async Task LongDriveAndUncCacheRecords_AreFoundByCanonicalScopeKeys()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var mediumDriveRoot = WindowsScopePathGolden.BuildLongPath(@"E:\MediumDrive", 300);
        var mediumUncRoot = WindowsScopePathGolden.BuildLongPath(@"\\server\share\MediumUnc", 300);
        var driveRoot = WindowsScopePathGolden.BuildLongPath(@"C:\LongDrive", 1050);
        var uncRoot = WindowsScopePathGolden.BuildLongPath(@"\\server\share\LongUnc", 1050);
        var driveFile = driveRoot + @"\PumpDrive.SLDPRT";
        var uncFile = uncRoot + @"\PumpUnc.SLDASM";
        var mediumDriveFile = mediumDriveRoot + @"\PumpMediumDrive.SLDPRT";
        var mediumUncFile = mediumUncRoot + @"\PumpMediumUnc.SLDASM";
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(
        [
            new FileCandidate(driveFile, SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(uncFile, SolidWorksDocumentKind.Assembly, 1, now),
            new FileCandidate(mediumDriveFile, SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(mediumUncFile, SolidWorksDocumentKind.Assembly, 1, now),
            new FileCandidate(driveRoot + @"2\PumpSibling.SLDDRW", SolidWorksDocumentKind.Drawing, 1, now),
        ]), CancellationToken.None);

        var driveHits = await store.SearchAsync(
            new SearchQuery("Pump", SearchMode.FileName, Scope: SearchScope.Folder(driveRoot + @"\.\Child\..\")),
            CancellationToken.None);
        var uncHits = await store.SearchAsync(
            new SearchQuery("Pump", SearchMode.FileName, Scope: SearchScope.Folder(uncRoot.ToLowerInvariant() + @"\")),
            CancellationToken.None);
        var mediumDriveHits = await store.SearchAsync(
            new SearchQuery("Pump", SearchMode.FileName, Scope: SearchScope.Folder(mediumDriveRoot)),
            CancellationToken.None);
        var mediumUncHits = await store.SearchAsync(
            new SearchQuery("Pump", SearchMode.FileName, Scope: SearchScope.Folder(mediumUncRoot)),
            CancellationToken.None);

        Assert.Equal(driveFile, Assert.Single(driveHits).FullPath);
        Assert.Equal(uncFile, Assert.Single(uncHits).FullPath);
        Assert.Equal(mediumDriveFile, Assert.Single(mediumDriveHits).FullPath);
        Assert.Equal(mediumUncFile, Assert.Single(mediumUncHits).FullPath);
    }

    [Fact]
    public async Task UnicodeCompositionAndCompatibilitySequences_RemainDistinctCachePaths()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        const string composed = "C:\\Unicode\\caf\u00E9\\Part.SLDPRT";
        const string decomposed = "C:\\Unicode\\cafe\u0301\\Part.SLDPRT";
        const string compatibility = "C:\\Unicode\\\u212B-\uFF21\uFF22\uFF23-\uFB00\\Part.SLDPRT";
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(
        [
            new FileCandidate(composed, SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(decomposed, SolidWorksDocumentKind.Part, 1, now),
            new FileCandidate(compatibility, SolidWorksDocumentKind.Part, 1, now),
        ]), CancellationToken.None);

        var hits = await store.SearchAsync(
            new SearchQuery(string.Empty, SearchMode.All, Scope: SearchScope.Folder(@"C:\Unicode")),
            CancellationToken.None);

        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, hit => hit.FullPath == composed);
        Assert.Contains(hits, hit => hit.FullPath == decomposed);
        Assert.Contains(hits, hit => hit.FullPath == compatibility);
    }

    [Fact]
    public async Task SuperscriptDosDeviceRules_AreExactInSqliteCacheScopes()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var accepted = new[]
        {
            "C:\\Controls\\COM\u2074\\Part.SLDPRT",
            "C:\\Controls\\COM\u2070\\Part.SLDPRT",
            "C:\\Controls\\COM\u00B9foo\\Part.SLDPRT",
            "C:\\Controls\\LPT\uFF11\\Part.SLDPRT",
            "\\\\server\\share\\com\u2074.txt\\Part.SLDPRT",
        };
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(
            accepted.Select(path => new FileCandidate(path, SolidWorksDocumentKind.Part, 1, now)).ToArray()),
            CancellationToken.None);

        foreach (var path in accepted)
        {
            var root = path[..path.LastIndexOf('\\')];
            var hit = Assert.Single(await store.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All, Scope: SearchScope.Folder(root)),
                CancellationToken.None));
            Assert.Equal(path, hit.FullPath);
        }

        foreach (var rejected in new[]
        {
            "C:\\folder\\COM\u00B9",
            "C:\\folder\\COM\u00B2.txt",
            "C:\\folder\\LPT\u00B3",
            "C:\\COM\u00B9\\folder",
            "\\\\server\\share\\lpt\u00B2\\folder",
        })
        {
            Assert.Throws<ArgumentException>(() => SearchScope.Folder(rejected));
        }
    }

    [Fact]
    public void BrokerWire_FolderScopeCarriesNormalizedRootAndIncludeSubdirectories()
    {
        var request = new BrokerRequest(
            ContractVersions.BrokerPipe,
            Guid.NewGuid(),
            BrokerOperation.Search,
            new SearchQuery(
                string.Empty,
                SearchMode.All,
                Scope: SearchScope.Folder(@"C:\A\B\")));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(request, options);

        Assert.Contains("\"protocolVersion\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"rootPath\":\"C:\\\\A\\\\B\"", json, StringComparison.Ordinal);
        Assert.Contains("\"includeSubdirectories\":true", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<BrokerRequest>(json, options)!;
        Assert.Equal(SearchScopeKind.Folder, roundTrip.Query!.EffectiveScope.Kind);
        Assert.Equal(@"C:\A\B", roundTrip.Query.EffectiveScope.RootPath);
        Assert.True(roundTrip.Query.EffectiveScope.IncludeSubdirectories);
    }
}
