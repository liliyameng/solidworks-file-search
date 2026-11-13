using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc38TaskpaneLibraryTests
{
    [Fact]
    public async Task LibraryQuery_CountsAndReturnsOnlyConfiguredDirectoryBoundaries()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var files = new[]
        {
            Candidate(@"C:\DesignLibrary\A.SLDPRT"),
            Candidate(@"C:\DesignLibrary\子目录\B.SLDASM"),
            Candidate(@"C:\DesignLibrary2\prefix.SLDPRT"),
            Candidate(@"C:\outside\C.SLDDRW"),
            Candidate(@"C:\other\D.SLDPRT"),
        };
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        var query = new SearchQuery(
            string.Empty,
            SearchMode.Library,
            Limit: 200,
            LibraryRoots: [@"C:\DesignLibrary"]);
        var results = await store.SearchAsync(query, CancellationToken.None);
        var count = await store.CountSearchAsync(query, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(2, results.Count);
        Assert.All(results, hit => Assert.StartsWith(@"C:\DesignLibrary\", hit.FullPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, hit => hit.FullPath.StartsWith(@"C:\DesignLibrary2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LibraryQuery_WithoutRootsReturnsZeroAndNeverFallsBackToAllMachine()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([Candidate(@"C:\outside\A.SLDPRT")]),
            CancellationToken.None);

        var query = new SearchQuery(string.Empty, SearchMode.Library, Limit: 200, LibraryRoots: []);
        Assert.Empty(await store.SearchAsync(query, CancellationToken.None));
        Assert.Equal(0, await store.CountSearchAsync(query, CancellationToken.None));
    }

    private static FileCandidate Candidate(string path) => new(
        path,
        path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)
            ? SolidWorksDocumentKind.Assembly
            : path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)
                ? SolidWorksDocumentKind.Drawing
                : SolidWorksDocumentKind.Part,
        128,
        DateTimeOffset.UtcNow);
}
