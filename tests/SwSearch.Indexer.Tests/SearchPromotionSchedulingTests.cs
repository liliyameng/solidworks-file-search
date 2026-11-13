using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class SearchPromotionSchedulingTests
{
    [Fact]
    public async Task RepeatedEquivalentActiveSearchPromotion_IsIdempotentAndNeverChangesSearchContentVersion()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\Queue\Repeated-Promotion.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        var contentVersion = store.SearchContentVersion;

        Assert.Equal(1, await store.EnqueueSearchCandidatesAsync(
            new SearchQuery("Repeated-Promotion", SearchMode.FileName)));
        for (var round = 0; round < 10; round++)
        {
            Assert.Equal(0, await store.EnqueueSearchCandidatesAsync(
                new SearchQuery("Repeated-Promotion", SearchMode.FileName)));
        }

        var queued = await ReadQueueAsync(database.Path);
        Assert.Equal("ActiveSearch", queued.Reason);
        Assert.Equal((int)MetadataQueuePriority.ActiveSearch, queued.Priority);
        Assert.Equal(contentVersion, store.SearchContentVersion);
    }

    [Fact]
    public async Task HigherPriorityPendingWork_IsAnUnconditionalPromotionNoOp()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\Queue\Viewer-Wins.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.EnqueueMetadataAsync(
            file.FullPath,
            "PropertyViewer",
            MetadataQueuePriority.PropertyViewer);

        Assert.Equal(0, await store.EnqueueSearchCandidatesAsync(
            new SearchQuery("Viewer-Wins", SearchMode.FileName)));

        var queued = await ReadQueueAsync(database.Path);
        Assert.Equal("PropertyViewer", queued.Reason);
        Assert.Equal((int)MetadataQueuePriority.PropertyViewer, queued.Priority);
    }

    [Fact]
    public async Task FailedRetry_IsRecoveredAndPromotedWithoutPublishingSearchContentChange()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\Queue\Retry-Promotion.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        var lease = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "failed-worker"));
        await store.CompleteMetadataLeaseAsync(
            lease.FileId,
            lease.LeaseOwner,
            succeeded: false,
            errorCode: "TRANSIENT",
            retryDelay: TimeSpan.FromHours(1));
        var contentVersion = store.SearchContentVersion;

        Assert.Equal(1, await store.EnqueueSearchCandidatesAsync(
            new SearchQuery("Retry-Promotion", SearchMode.FileName)));

        var recovered = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(5),
            "recovered-worker"));
        Assert.Equal("ActiveSearch", recovered.Reason);
        Assert.Equal(MetadataQueuePriority.ActiveSearch, recovered.Priority);
        Assert.Equal(contentVersion, store.SearchContentVersion);
    }

    [Fact]
    public async Task CurrentCompletedMetadata_IsNeverRequeuedBySearch()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\Queue\Already-Complete.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(
            file,
            [new DocumentProperty("Code", "DONE", "DONE", PropertyScope.Document, null, MetadataSource.DocumentManager)],
            [],
            MetadataSource.DocumentManager,
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(0, await store.EnqueueSearchCandidatesAsync(
            new SearchQuery("Already-Complete", SearchMode.FileName)));
        Assert.Equal(0, (await store.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateProducers_PerformExactlyOneSchedulingWrite()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\Queue\Concurrent-Promotion.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);

        var changes = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            store.EnqueueSearchCandidatesAsync(
                new SearchQuery("Concurrent-Promotion", SearchMode.FileName)).AsTask()));

        Assert.Equal(1, changes.Sum());
        Assert.Equal((int)MetadataQueuePriority.ActiveSearch, (await ReadQueueAsync(database.Path)).Priority);
    }

    [Fact]
    public async Task MetadataRevisionSource_ChangesOnlyForActualSearchableValues()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\Queue\Property-Revision.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        var initial = store.SearchContentVersion;
        var first = Metadata(file, "A");

        await store.UpsertMetadataAsync(first, CancellationToken.None);
        var afterFirst = store.SearchContentVersion;
        await store.UpsertMetadataAsync(first with { ExtractedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, CancellationToken.None);
        var afterEquivalent = store.SearchContentVersion;
        await store.UpsertMetadataAsync(Metadata(file, "B"), CancellationToken.None);

        Assert.Equal(initial + 1, afterFirst);
        Assert.Equal(afterFirst, afterEquivalent);
        Assert.Equal(afterFirst + 1, store.SearchContentVersion);
    }

    private static FileCandidate Candidate(string path) =>
        new(path, SolidWorksDocumentKind.Part, 10, DateTimeOffset.UtcNow);

    private static DocumentMetadata Metadata(FileCandidate file, string value) =>
        new(
            file,
            [new DocumentProperty("Code", value, value, PropertyScope.Document, null, MetadataSource.DocumentManager)],
            [],
            MetadataSource.DocumentManager,
            DateTimeOffset.UtcNow);

    private static async Task<(string Reason, int Priority)> ReadQueueAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT reason,priority FROM index_queue;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }
}
