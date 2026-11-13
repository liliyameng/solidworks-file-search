using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc86MetadataStartupTests
{
    [Fact]
    public async Task ExistingQueue_DelaysDiscoveryUntilFirstLeaseIsDurablySettled()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate("queued", 1, DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await SetSourceStateAsync(database.Path, "stale");

        var metadata = new BlockingUnsupportedMetadata();
        var discovery = new SettlementObservingDiscovery(store);
        await using var runtime = CreateRuntime(store, discovery, metadata);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await metadata.ExtractEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(discovery.Entered.Task.IsCompleted);

        metadata.Release();
        Assert.True(await discovery.PendingWasSettled.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NoPendingMetadata_AllowsDiscoveryImmediately()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate("complete", 1, DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(file, [], [], MetadataSource.NoApi, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await SetSourceStateAsync(database.Path, "stale");
        var discovery = new SettlementObservingDiscovery(store);
        await using var runtime = CreateRuntime(store, discovery, new NoopMetadata());
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Rc85CompatibleSuccessfulCache_IsAdoptedAndAllUnsafeRowsRemainQueued()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var files = Enumerable.Range(0, 10).Select(index => Candidate($"cache-{index}", index + 1, now)).ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        foreach (var file in files)
        {
            await store.UpsertMetadataAsync(new DocumentMetadata(
                file,
                [new DocumentProperty("Code", file.FullPath, file.FullPath, PropertyScope.Document, null,
                    MetadataSource.NoApi)],
                [], MetadataSource.NoApi, now), CancellationToken.None);
        }

        await SetVersionThreeAsync(database.Path);
        await CreateExistingUpgradeRowsAsync(database.Path, files.Take(9).ToArray());
        await MutateUnsafeUpgradeRowsAsync(database.Path, files);

        var preparationMetadata = new BlockingProbeMetadata();
        await using var runtime = CreateRuntime(store, new SettlementObservingDiscovery(store), preparationMetadata);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        await preparationMetadata.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cached = Assert.IsType<DocumentMetadata>(await store.TryGetMetadataAsync(files[0].FullPath, CancellationToken.None));
        Assert.Equal(now, cached.ExtractedAtUtc);
        Assert.Contains(cached.Properties, property => property.Name == "Code");
        Assert.Equal(8, (await store.GetStatusAsync()).PendingMetadataCount);
        Assert.Equal(0, await QueueCountAsync(database.Path, files[0].FullPath));
        Assert.Equal(0, await QueueCountAsync(database.Path, files[9].FullPath));
        Assert.Equal(ExtractorVersions.Current, await ExtractorVersionAsync(database.Path, files[9].FullPath));
        Assert.Equal("ExtractorUpgrade", await QueueReasonAsync(database.Path, files[4].FullPath));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(2));

        // A second Broker start must not reconsider rows whose first start
        // requeued them after rejecting their original unsafe disposition.
        var restartMetadata = new BlockingProbeMetadata();
        await using var restartedRuntime = CreateRuntime(
            store, new SettlementObservingDiscovery(store), restartMetadata);
        using var restartCancellation = new CancellationTokenSource();
        var restartedBackground = restartedRuntime.RunBackgroundAsync(restartCancellation.Token);
        await restartMetadata.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(8, (await store.GetStatusAsync()).PendingMetadataCount);
        Assert.Equal(ExtractorVersions.Rc85CompatibleCacheVersion,
            await ExtractorVersionAsync(database.Path, files[4].FullPath));
        restartCancellation.Cancel();
        await restartedBackground.WaitAsync(TimeSpan.FromSeconds(2));

        var changed = files[0] with { Size = 42, LastWriteTimeUtc = now.AddMinutes(1) };
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([changed]), CancellationToken.None);
        Assert.Equal("Modified", await QueueReasonAsync(database.Path, changed.FullPath));
    }

    [Fact]
    public async Task RecentReadySnapshot_DefersInitialFullDiscoveryOnlyForBoundedStartupGrace()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate("recent", 1, DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(file, [], [], MetadataSource.NoApi, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var recentStatus = await store.GetStatusAsync();
        Assert.True(recentStatus.DiscoveryGeneration > 0);
        Assert.Equal("ready", recentStatus.SourceState);
        Assert.NotNull(recentStatus.LastSuccessfulDiscoveryUtc);

        var recentDiscovery = new SettlementObservingDiscovery(store);
        await using (var recentRuntime = CreateRuntime(store, recentDiscovery, new NoopMetadata(), TimeSpan.FromMinutes(5)))
        using (var recentCancel = new CancellationTokenSource())
        {
            recentRuntime.FreshReadyStartupCatchUpGracePeriodForTesting = TimeSpan.FromMilliseconds(75);
            var recentBackground = recentRuntime.RunBackgroundAsync(recentCancel.Token);
            await Task.Delay(20);
            Assert.False(recentDiscovery.Entered.Task.IsCompleted);
            await recentDiscovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            recentCancel.Cancel();
            await recentBackground.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await using var emptyDatabase = new TemporaryDatabase();
        await using var emptyStore = new SqliteIndexStore(emptyDatabase.Path);
        await emptyStore.InitializeAsync();
        var emptyDiscovery = new SettlementObservingDiscovery(emptyStore);
        await using var emptyRuntime = CreateRuntime(emptyStore, emptyDiscovery, new NoopMetadata(), TimeSpan.FromMinutes(5));
        using var emptyCancel = new CancellationTokenSource();
        var emptyBackground = emptyRuntime.RunBackgroundAsync(emptyCancel.Token);
        await emptyDiscovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        emptyCancel.Cancel();
        await emptyBackground.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExpiredReadySnapshot_StartsFullDiscoveryImmediately()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate("expired", 1, DateTimeOffset.UtcNow.AddHours(-1));
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(file, [], [], MetadataSource.NoApi, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await SetLastSuccessfulDiscoveryAsync(database.Path, DateTimeOffset.UtcNow.AddMinutes(-10));

        var discovery = new SettlementObservingDiscovery(store);
        await using var runtime = CreateRuntime(store, discovery, new NoopMetadata(), TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LostLeaseDuringRequeue_IsNormalAndTheReplacementLeaseRemainsRecoverable()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var files = new[] { Candidate("commit", 1, now), Candidate("lost", 2, now) };
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        var leases = await store.LeaseMetadataAsync(2, TimeSpan.FromSeconds(5), "rc86-first");
        var committed = leases.Single(lease => lease.FullPath == files[0].FullPath);
        var lost = leases.Single(lease => lease.FullPath == files[1].FullPath);

        await store.CompleteMetadataLeaseAsync(committed.FileId, committed.LeaseOwner, succeeded: true);
        await store.ReleaseMetadataLeasesAsync(lost.LeaseOwner);
        var replacement = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc86-replacement"));
        var evidence = new StableFileEvidence(files[1].FullPath, files[1].FullPath, files[1].Kind,
            files[1].Size, files[1].LastWriteTimeUtc, string.Empty);

        Assert.False(await store.TryRequeueChangedFileAsync(lost, evidence));
        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(1), "rc86-contender"));
        await store.ReleaseMetadataLeasesAsync(replacement.LeaseOwner);
        Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc86-recovery"));
    }

    [Fact]
    public async Task CompatibleCacheAdoption_IsIdempotentAndOnlySupportsRc85ToRc86()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate("idempotent", 1, DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(file, [], [], MetadataSource.NoApi, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await SetVersionThreeAsync(database.Path);

        Assert.Equal(0, await store.AdoptCompletedCompatibleExtractorCacheAsync(2, ExtractorVersions.Current));
        Assert.Equal(1, await store.AdoptCompletedCompatibleExtractorCacheAsync(
            ExtractorVersions.Rc85CompatibleCacheVersion, ExtractorVersions.Current));
        Assert.Equal(0, await store.AdoptCompletedCompatibleExtractorCacheAsync(
            ExtractorVersions.Rc85CompatibleCacheVersion, ExtractorVersions.Current));
    }

    private static IndexingRuntime CreateRuntime(
        SqliteIndexStore store,
        IFileDiscoveryProvider discovery,
        IMetadataExtractionClient metadata,
        TimeSpan? refreshInterval = null) =>
        new(store, discovery, metadata, IndexingRuntimeOptions.Default with
        {
            DiscoveryRefreshInterval = refreshInterval ?? TimeSpan.FromHours(1),
            IdleDelay = TimeSpan.FromMilliseconds(5),
            MaximumConcurrentExtractions = 1,
            MaximumFilesPerSecond = 1_000,
            MaximumReadBytesPerSecond = 100_000_000,
            MaximumWriteBytesPerSecond = 100_000_000,
        }, new RuntimeFileLog(Path.Combine(Path.GetTempPath(), $"swsearch-rc86-{Guid.NewGuid():N}")));

    private static FileCandidate Candidate(string name, long size, DateTimeOffset modified) =>
        new($@"C:\RC86\{name}.SLDPRT", SolidWorksDocumentKind.Part, size, modified);

    private static async Task SetVersionThreeAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE files SET extractor_version=3,metadata_state=0,error_code=NULL; DELETE FROM index_queue;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetSourceStateAsync(string databasePath, string state)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE index_state SET source_state=@state WHERE singleton_id=1;";
        command.Parameters.AddWithValue("@state", state);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetLastSuccessfulDiscoveryAsync(string databasePath, DateTimeOffset value)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE index_state SET source_state='ready',last_success_utc=@last WHERE singleton_id=1;";
        command.Parameters.AddWithValue("@last", value.UtcTicks);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MutateUnsafeUpgradeRowsAsync(string databasePath, IReadOnlyList<FileCandidate> files)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        async Task ExecuteAsync(string sql, int index)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@path", files[index].FullPath.ToUpperInvariant());
            await command.ExecuteNonQueryAsync();
        }

        await ExecuteAsync("UPDATE files SET error_code='BROKEN' WHERE path_norm=@path;", 1);
        await ExecuteAsync("UPDATE files SET extracted_utc=NULL WHERE path_norm=@path;", 2);
        await ExecuteAsync("UPDATE index_queue SET fingerprint='mismatch' WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);", 3);
        await ExecuteAsync("UPDATE index_queue SET reason='Modified' WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);", 4);
        await ExecuteAsync("UPDATE index_queue SET attempt=1 WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);", 5);
        await ExecuteAsync("UPDATE index_queue SET failure_count=1 WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);", 6);
        await ExecuteAsync("UPDATE index_queue SET lease_owner='active',lease_until_utc=999999999999999999 WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);", 7);
        await ExecuteAsync("UPDATE index_queue SET reason='New' WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);", 8);
    }

    private static async Task CreateExistingUpgradeRowsAsync(string databasePath, IReadOnlyList<FileCandidate> files)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        foreach (var file in files)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE files SET metadata_state=1 WHERE path_norm=@path;
                INSERT INTO index_queue(file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT id,'ExtractorUpgrade',4,0,0,0,
                       printf('%lld:%lld:%s:%d',size,modified_utc,coalesce(file_identity,''),4),4
                  FROM files WHERE path_norm=@path;
                """;
            command.Parameters.AddWithValue("@path", file.FullPath.ToUpperInvariant());
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> QueueCountAsync(string databasePath, string path)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM index_queue q JOIN files f ON f.id=q.file_id WHERE f.path_norm=@path;";
        command.Parameters.AddWithValue("@path", path.ToUpperInvariant());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> QueueReasonAsync(string databasePath, string path)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT q.reason FROM index_queue q JOIN files f ON f.id=q.file_id WHERE f.path_norm=@path;";
        command.Parameters.AddWithValue("@path", path.ToUpperInvariant());
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ExtractorVersionAsync(string databasePath, string path)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extractor_version FROM files WHERE path_norm=@path;";
        command.Parameters.AddWithValue("@path", path.ToUpperInvariant());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class SettlementObservingDiscovery(SqliteIndexStore store) : IFileDiscoveryProvider
    {
        internal TaskCompletionSource<bool> PendingWasSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_DISCOVERY_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            PendingWasSettled.TrySetResult((await store.GetStatusAsync(cancellationToken)).PendingMetadataCount == 0);
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class NoopMetadata : IMetadataExtractionClient
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_METADATA_READY", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No extraction expected.");
    }

    private sealed class BlockingUnsupportedMetadata : IMetadataExtractionClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExtractEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_METADATA_READY", "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken)
        {
            ExtractEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new MetadataResponse(ContractVersions.MetadataPipe, request.RequestId, null,
                "TEST_SKIPPED", "fixture", false, request.PipelineLeaseId,
                Outcome: MetadataPipelineOutcome.Unsupported,
                Disposition: MetadataFileDisposition.Normal);
        }
    }

    private sealed class BlockingProbeMetadata : IMetadataExtractionClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            ProbeEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new RuntimeCapability(CapabilityState.Available, "TEST_METADATA_READY", "ready");
        }

        public ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No extraction expected.");
    }
}
