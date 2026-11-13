using SwSearch.App;
using SwSearch.Contracts;
using SwSearch.Indexer;
using SwSearch.Integration;

namespace SwSearch.Integration.Tests;

public sealed class Rc30LocalCacheScopeTests
{
    [Fact]
    public async Task BrokerOffline_LocalReadOnlyScopeReturnsOnlyDescendantsAndExcludesRecycleBin()
    {
        var root = Path.Combine(Path.GetTempPath(), "swsearch-rc30-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "index.db");
        var scope = Path.Combine(root, "设计库 A&B");
        var inside = Path.Combine(scope, "子目录", "范围内.SLDPRT");
        var outside = Path.Combine(root, "其他库", "范围外.SLDASM");
        var recycled = Path.Combine(root, "$RECYCLE.BIN", "回收.SLDDRW");
        try
        {
            await using (var writer = new SqliteIndexStore(database))
            {
                await writer.InitializeAsync();
                await writer.ApplyDiscoverySnapshotAsync(Files(
                    new FileCandidate(inside, SolidWorksDocumentKind.Part, 1, DateTimeOffset.UtcNow),
                    new FileCandidate(outside, SolidWorksDocumentKind.Assembly, 1, DateTimeOffset.UtcNow),
                    new FileCandidate(recycled, SolidWorksDocumentKind.Drawing, 1, DateTimeOffset.UtcNow)),
                    CancellationToken.None);
            }

            var client = new BrokerSearchClient(
                brokerPath: Path.Combine(root, "missing-broker.exe"),
                pipeName: "swsearch-rc30-offline-" + Guid.NewGuid().ToString("N"),
                startBroker: _ => ValueTask.CompletedTask,
                options: new BrokerSearchClientOptions(
                    TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2)),
                localCacheReader: new LocalCacheSearchReader(database));

            var result = await client.SearchAsync(new SearchQuery(
                "", SearchMode.All, Limit: 120, Scope: SearchScope.Folder(scope)), CancellationToken.None);

            var hit = Assert.Single(result.Hits);
            Assert.Equal(Path.GetFullPath(inside), hit.FullPath, ignoreCase: true);
            Assert.DoesNotContain(result.Hits, item => item.FullPath.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.HasMore);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async IAsyncEnumerable<FileCandidate> Files(params FileCandidate[] files)
    {
        foreach (var file in files)
        {
            yield return file;
        }
        await Task.CompletedTask;
    }
}
