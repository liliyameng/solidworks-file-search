using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class OfflineFallbackTests
{
    [Fact]
    public async Task UnavailableEverything_DoesNotEnumerateAndKeepsCacheReadOnlyStale()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\cached.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        var discovery = new UnavailableDiscovery();
        var coordinator = new IndexingCoordinator(discovery, store);

        var capability = await coordinator.RefreshAsync();

        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.False(discovery.DiscoverCalled);
        var hit = Assert.Single(await store.SearchAsync(
            new SearchQuery("cached", SearchMode.FileName),
            CancellationToken.None));
        Assert.True(hit.CacheMayBeStale);
        Assert.Equal(1, (await store.GetStatusAsync()).PresentFileCount);
    }

    private sealed class UnavailableDiscovery : IFileDiscoveryProvider
    {
        internal bool DiscoverCalled { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Degraded,
                "EVERYTHING_IPC_NOT_FOUND",
                "offline"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DiscoverCalled = true;
            await Task.Yield();
            yield break;
        }
    }
}
