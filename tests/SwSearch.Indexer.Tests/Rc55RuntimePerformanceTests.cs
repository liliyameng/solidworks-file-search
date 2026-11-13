using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc55RuntimePerformanceTests
{
    [Fact]
    public async Task HealthyStorageSnapshot_CoalescesSequentialAndConcurrentHotPathProbes()
    {
        using var sandbox = new Sandbox();
        await CreateDatabaseAsync(sandbox.DatabasePath);
        await using var store = SqliteIndexStore.OpenReadOnlyExisting(sandbox.DatabasePath);
        store.StorageStatusSnapshotLifetimeForTesting = TimeSpan.FromSeconds(5);

        var probesBefore = store.StorageStatusFullProbeCount;
        var first = await store.GetStorageStatusAsync(forceFresh: true);
        Assert.Equal(probesBefore + 1, store.StorageStatusFullProbeCount);

        var cacheHitsBefore = store.StorageStatusCacheHitCount;
        for (var index = 0; index < 32; index++)
        {
            Assert.Same(first, await store.GetStorageStatusAsync());
        }

        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => store.GetStorageStatusAsync().AsTask()));
        Assert.All(concurrent, status => Assert.Same(first, status));
        Assert.Equal(probesBefore + 1, store.StorageStatusFullProbeCount);
        Assert.Equal(cacheHitsBefore + 64, store.StorageStatusCacheHitCount);
    }

    [Fact]
    public async Task ExpiredStorageSnapshot_UsesSingleFlightAndCachedPathAvoidsFullPragmas()
    {
        using var sandbox = new Sandbox();
        await CreateDatabaseAsync(sandbox.DatabasePath);
        await using var store = SqliteIndexStore.OpenReadOnlyExisting(sandbox.DatabasePath);
        store.StorageStatusSnapshotLifetimeForTesting = TimeSpan.FromMilliseconds(20);
        _ = await store.GetStorageStatusAsync(forceFresh: true);
        await Task.Delay(40);

        var probesBefore = store.StorageStatusFullProbeCount;
        var cacheHitsBefore = store.StorageStatusCacheHitCount;
        var clock = Stopwatch.StartNew();
        _ = await Task.WhenAll(
            Enumerable.Range(0, 24)
                .Select(_ => store.GetStorageStatusAsync().AsTask()));
        clock.Stop();

        Assert.Equal(probesBefore + 1, store.StorageStatusFullProbeCount);
        Assert.Equal(cacheHitsBefore + 23, store.StorageStatusCacheHitCount);
        Console.WriteLine(
            $"RC55_STORAGE_SNAPSHOT callers=24 full_probes=1 cache_hits=23 elapsed_ms={clock.Elapsed.TotalMilliseconds:F2}");
    }

    [Fact]
    public async Task SuccessfulClaimExtractCommitCycle_UsesAtMostOneFullStorageProbe()
    {
        using var sandbox = new Sandbox();
        var filePath = System.IO.Path.Combine(sandbox.Path, "One.SLDPRT");
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var evidence = StableFileEvidenceReader.Capture(filePath);
        var store = new SqliteIndexStore(sandbox.DatabasePath);
        await using var runtime = new IndexingRuntime(
            store,
            new EmptyDiscovery(),
            new StableMetadata(),
            IndexingRuntimeOptions.Default with
            {
                MetadataBatchSize = 1,
                MaximumConcurrentExtractions = 1,
                MaximumFilesPerSecond = 1_000,
                MaximumReadBytesPerSecond = 100_000_000,
                MaximumWriteBytesPerSecond = 100_000_000,
            },
            new RuntimeFileLog(System.IO.Path.Combine(sandbox.Path, "logs")));
        await runtime.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(
            Single(evidence.Candidate),
            CancellationToken.None);
        store.StorageStatusSnapshotLifetimeForTesting = TimeSpan.FromMilliseconds(20);
        _ = await store.GetStorageStatusAsync(forceFresh: true);
        await Task.Delay(40);

        var probesBefore = store.StorageStatusFullProbeCount;
        var cacheHitsBefore = store.StorageStatusCacheHitCount;
        var processed = await runtime.ProcessPendingMetadataAsync(1);

        Assert.Equal(1, processed);
        Assert.Equal(1, store.StorageStatusFullProbeCount - probesBefore);
        Assert.True(store.StorageStatusCacheHitCount - cacheHitsBefore >= 3);
        var metrics = runtime.RuntimePhaseMetricsForTesting;
        Assert.Equal(1, metrics.ClaimedItems);
        Assert.Equal(1, metrics.ExtractionOperations);
        Assert.Equal(1, metrics.CommittedItems);
        Assert.Equal(1, metrics.StorageFullProbes);
        Console.WriteLine(
            $"RC55_CYCLE processed={processed} full_probes={store.StorageStatusFullProbeCount - probesBefore} " +
            $"cache_hits={store.StorageStatusCacheHitCount - cacheHitsBefore}");
    }

    [Fact]
    public void StorageSnapshot_PressureAndNearThresholdAlwaysRequireFreshProbe()
    {
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: 1_024,
            MaximumDatabaseBytes: 2_048,
            PassiveCheckpointWalBytes: 32,
            PauseWalBytes: 64,
            PhysicalSafetyDatabaseBytes: 4_096);
        var expires = DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks;
        var now = DateTimeOffset.UtcNow.UtcTicks;

        Assert.True(SqliteIndexStore.CanReuseStorageStatusSnapshot(
            Status(walBytes: 31, logicalBytes: 1_000, databaseBytes: 1_000),
            expires,
            now,
            policy));
        Assert.False(SqliteIndexStore.CanReuseStorageStatusSnapshot(
            Status(walBytes: 32, logicalBytes: 1_000, databaseBytes: 1_000),
            expires,
            now,
            policy));
        Assert.False(SqliteIndexStore.CanReuseStorageStatusSnapshot(
            Status(walBytes: 0, logicalBytes: 1_920, databaseBytes: 1_000),
            expires,
            now,
            policy));
        Assert.False(SqliteIndexStore.CanReuseStorageStatusSnapshot(
            Status(walBytes: 0, logicalBytes: 1_000, databaseBytes: 3_840),
            expires,
            now,
            policy));
        Assert.False(SqliteIndexStore.CanReuseStorageStatusSnapshot(
            Status(walBytes: 64, logicalBytes: 1_000, databaseBytes: 1_000, paused: true),
            expires,
            now,
            policy));
    }

    [Theory]
    [InlineData("bearing", null, true)]
    [InlineData("bearing", true, true)]
    [InlineData("bearing", false, false)]
    [InlineData("", true, false)]
    [InlineData("   ", null, false)]
    public void InteractiveThrottle_RequiresExplicitUserQueryWithNonEmptyText(
        string text,
        bool? isInteractive,
        bool expected)
    {
        var query = new SearchQuery(text, SearchMode.All, IsInteractive: isInteractive);

        Assert.Equal(expected, IndexingRuntime.ShouldNotifyInteractiveActivity(query));
    }

    [Fact]
    public void LegacyQueryPayload_DefaultsToInteractiveButExplicitBackgroundMarkerRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<SearchQuery>(
            """{"Text":"bearing","Mode":0}""")!;
        Assert.True(legacy.EffectiveIsInteractive);

        var background = new SearchQuery("bearing", SearchMode.All, IsInteractive: false);
        var roundTrip = JsonSerializer.Deserialize<SearchQuery>(
            JsonSerializer.Serialize(background))!;
        Assert.False(roundTrip.EffectiveIsInteractive);
    }

    [Fact]
    public void MetadataLoop_DelaysOnlyWhenNoItemWasProcessed()
    {
        Assert.True(IndexingRuntime.ShouldDelayAfterMetadataBatch(0));
        Assert.False(IndexingRuntime.ShouldDelayAfterMetadataBatch(1));
        Assert.False(IndexingRuntime.ShouldDelayAfterMetadataBatch(64));
    }

    [Fact]
    public void PhaseMetrics_AggregateOnlyCountsAndDurationsWithoutSensitivePayloads()
    {
        var metrics = new RuntimePhaseMetrics();
        metrics.RecordClaim(TimeSpan.FromMilliseconds(2), claimedItems: 4);
        metrics.RecordExtraction(TimeSpan.FromMilliseconds(3));
        metrics.RecordCommit(TimeSpan.FromMilliseconds(5), attemptedItems: 1, committedItems: 1);
        metrics.RecordStorageSnapshot(
            TimeSpan.FromMilliseconds(7),
            checks: 1,
            fullProbes: 1,
            cacheHits: 4);
        metrics.RecordIdleDelay(TimeSpan.FromMilliseconds(11));

        var snapshot = metrics.Snapshot();
        Assert.Equal(4, snapshot.ClaimedItems);
        Assert.Equal(1, snapshot.ExtractionOperations);
        Assert.Equal(1, snapshot.CommittedItems);
        Assert.Equal(1, snapshot.StorageFullProbes);
        Assert.Equal(4, snapshot.StorageCacheHits);
        Assert.Equal(1, snapshot.IdleDelayOperations);
        var message = snapshot.ToLogMessage();
        Assert.Contains("claim_ms=", message, StringComparison.Ordinal);
        Assert.Contains("extract_ms=", message, StringComparison.Ordinal);
        Assert.Contains("commit_ms=", message, StringComparison.Ordinal);
        Assert.Contains("storage_snapshot_ms=", message, StringComparison.Ordinal);
        Assert.Contains("idle_delay_ms=", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', message);
        Assert.DoesNotContain(".SLD", message, StringComparison.OrdinalIgnoreCase);
    }

    private static IndexStorageStatus Status(
        long walBytes,
        long logicalBytes,
        long databaseBytes,
        bool paused = false) =>
        new(
            DatabaseBytes: databaseBytes,
            WalBytes: walBytes,
            MetadataWritesPaused: paused,
            PauseReason: paused ? "WAL_PRESSURE" : null,
            LogicalDatabaseBytes: logicalBytes);

    private static async Task CreateDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE search_projection_state(
                singleton_id INTEGER PRIMARY KEY,
                current_sequence INTEGER NOT NULL
            );
            INSERT INTO search_projection_state(singleton_id,current_sequence) VALUES(1,0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async IAsyncEnumerable<FileCandidate> Single(FileCandidate candidate)
    {
        yield return candidate;
        await Task.CompletedTask;
    }

    private sealed class EmptyDiscovery : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC55_DISCOVERY_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StableMetadata : IMetadataExtractionClient
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC55_METADATA_READY",
                "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = StableFileEvidenceReader.Capture(request.FullPath);
            var contractEvidence = new MetadataFileEvidence(
                evidence.RequestedPath,
                evidence.FinalPath,
                evidence.Kind,
                evidence.Size,
                evidence.LastWriteTimeUtc,
                evidence.FileIdentity);
            return ValueTask.FromResult(new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                new DocumentMetadata(
                    evidence.Candidate,
                    [],
                    [],
                    MetadataSource.NoApi,
                    DateTimeOffset.UtcNow),
                ErrorCode: null,
                ChineseErrorMessage: null,
                Retryable: false,
                PipelineLeaseId: request.PipelineLeaseId,
                Outcome: MetadataPipelineOutcome.Completed,
                BeforeEvidence: contractEvidence,
                AfterEvidence: contractEvidence));
        }
    }

    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"swsearch-rc55-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "core.db");
        }

        internal string Path { get; }

        internal string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
