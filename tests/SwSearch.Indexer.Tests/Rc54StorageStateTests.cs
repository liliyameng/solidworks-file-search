using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc54StorageStateTests
{
    [Fact]
    public async Task WalPressure_UsesHighLowHysteresisAndKeepsCapacityReasonsExplicit()
    {
        await using var database = new TemporaryDatabase();
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: 128,
            MaximumDatabaseBytes: 256,
            PassiveCheckpointWalBytes: 32,
            PauseWalBytes: 64,
            PhysicalSafetyDatabaseBytes: 512);
        await using var store = new SqliteIndexStore(database.Path, policy);

        Assert.Null(store.ResolveStoragePauseReason(100, 100, 63));
        Assert.Equal("WAL_PRESSURE", store.ResolveStoragePauseReason(100, 100, 64));
        Assert.Equal("WAL_PRESSURE", store.ResolveStoragePauseReason(100, 100, 63));
        Assert.Equal("WAL_PRESSURE", store.ResolveStoragePauseReason(100, 100, 33));
        Assert.Null(store.ResolveStoragePauseReason(100, 100, 32));
        Assert.Null(store.ResolveStoragePauseReason(100, 100, 63));

        Assert.Equal("DATABASE_HARD_LIMIT", store.ResolveStoragePauseReason(100, 256, 0));
        Assert.Equal("DATABASE_PHYSICAL_SAFETY_LIMIT", store.ResolveStoragePauseReason(512, 0, 0));
        Assert.True(IndexingRuntime.IsCapacityPauseReason("WAL_PRESSURE"));
        Assert.True(IndexingRuntime.IsCapacityPauseReason("DATABASE_HARD_LIMIT"));
        Assert.True(IndexingRuntime.IsCapacityPauseReason("DATABASE_PHYSICAL_SAFETY_LIMIT"));
        Assert.False(IndexingRuntime.IsCapacityPauseReason("INDEX_STORAGE_PRESSURE"));
        Assert.False(IndexingRuntime.IsCapacityPauseReason(null));
    }

    [Theory]
    [InlineData(CapabilityState.Available, "RC54_METADATA_READY", "INDEX_READY")]
    [InlineData(CapabilityState.Degraded, "RC54_METADATA_RECOVERING", "RC54_METADATA_RECOVERING")]
    [InlineData(CapabilityState.Unavailable, "RC54_METADATA_UNAVAILABLE", "RC54_METADATA_UNAVAILABLE")]
    public async Task Runtime_RecoveryExposesLatestCapabilityInsteadOfPrePressureSnapshot(
        CapabilityState capabilityState,
        string capabilityCode,
        string expectedRecoveredCode)
    {
        await using var database = new TemporaryDatabase();
        var logRoot = Path.Combine(
            Path.GetTempPath(),
            $"swsearch-rc54-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logRoot);
        try
        {
            var policy = new IndexStoragePolicy(
                TargetDatabaseBytes: 64 * 1024 * 1024,
                MaximumDatabaseBytes: 128 * 1024 * 1024,
                PassiveCheckpointWalBytes: 4 * 1024,
                PauseWalBytes: 32 * 1024);
            await using var store = new SqliteIndexStore(database.Path, policy);
            var metadata = new MutableMetadata();
            await using var runtime = new IndexingRuntime(
                store,
                new EmptyDiscovery(),
                metadata,
                new IndexingRuntimeOptions(
                    TimeSpan.FromHours(1),
                    TimeSpan.FromMilliseconds(10),
                    MetadataBatchSize: 1,
                    PressureCooldownSeconds: 1),
                new RuntimeFileLog(logRoot));
            await runtime.InitializeAsync();

            var seed = new FileCandidate(
                @"C:\Rc54\Seed.SLDPRT",
                SolidWorksDocumentKind.Part,
                1,
                DateTimeOffset.UtcNow);
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([seed]), CancellationToken.None);

            await using var readerConnection =
                new SqliteConnection($"Data Source={database.Path};Cache=Private");
            await readerConnection.OpenAsync();
            await using var pin = readerConnection.CreateCommand();
            pin.CommandText = "SELECT id,path FROM files ORDER BY id;";
            await using var pinnedReader = await pin.ExecuteReaderAsync();
            Assert.True(await pinnedReader.ReadAsync());

            var files = Enumerable.Range(0, 500)
                .Select(index => new FileCandidate(
                    $@"C:\Rc54\Part-{index:D4}.SLDPRT",
                    SolidWorksDocumentKind.Part,
                    1_024 + index,
                    DateTimeOffset.UtcNow))
                .Prepend(seed)
                .ToArray();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
            Assert.True((await store.GetStorageStatusAsync()).MetadataWritesPaused);

            Assert.Equal(0, await runtime.ProcessPendingMetadataAsync(1));
            Assert.Equal("INDEX_STORAGE_PRESSURE", runtime.CurrentCapability.Code);

            metadata.Capability = new RuntimeCapability(
                capabilityState,
                capabilityCode,
                capabilityCode);
            _ = await runtime.RefreshAndProcessAsync(1);
            Assert.Equal("INDEX_STORAGE_PRESSURE", runtime.CurrentCapability.Code);

            await pinnedReader.DisposeAsync();
            await readerConnection.DisposeAsync();
            IndexStorageStatus fresh = await store.GetStorageStatusAsync();
            for (var attempt = 0; attempt < 5 && fresh.MetadataWritesPaused; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1_100));
                _ = await runtime.ProcessPendingMetadataAsync(1);
                fresh = await store.GetStorageStatusAsync();
            }
            Assert.False(fresh.MetadataWritesPaused, fresh.PauseReason);
            Assert.Equal(expectedRecoveredCode, runtime.CurrentCapability.Code);

            var log = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(logRoot, "*.log").Select(File.ReadAllText));
            Assert.Contains("INDEX_STORAGE_PRESSURE", log, StringComparison.Ordinal);
            Assert.Contains("state=blocked;reason=WAL_PRESSURE", log, StringComparison.Ordinal);
            Assert.Contains("INDEX_STORAGE_RECOVERED", log, StringComparison.Ordinal);
            Assert.Contains("state=recovered;reason=NONE", log, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(logRoot))
            {
                Directory.Delete(logRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(CapabilityState.Degraded, "RC54_CONCURRENT_RECOVERING")]
    [InlineData(CapabilityState.Unavailable, "RC54_CONCURRENT_UNAVAILABLE")]
    public async Task Projection_ConcurrentStorageReadyAndMetadataUpdateNeverLoseLatestMetadata(
        CapabilityState metadataState,
        string metadataCode)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var projection = CreateReadyProjection();
            Assert.True(projection.PublishStoragePressure(StoragePressure()));
            var latestMetadata = new RuntimeCapability(
                metadataState,
                metadataCode,
                metadataCode);

            await RunConcurrently(
                () => projection.PublishStorageReady(),
                () => projection.UpdateMetadata(latestMetadata));

            Assert.Equal(metadataCode, projection.Current.Code);
        }
    }

    [Fact]
    public async Task Projection_ConcurrentDiscoveryAndMetadataUpdatesProjectBothLatestComponents()
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var projection = new RuntimeCapabilityProjection(
                new RuntimeCapability(
                    CapabilityState.Degraded,
                    "RC54_OLD_DISCOVERY",
                    "old discovery"),
                new RuntimeCapability(
                    CapabilityState.Degraded,
                    "RC54_OLD_METADATA",
                    "old metadata"));
            var latestDiscovery = new RuntimeCapability(
                CapabilityState.Available,
                "RC54_LATEST_DISCOVERY",
                "latest discovery");
            var latestMetadata = new RuntimeCapability(
                CapabilityState.Unavailable,
                "RC54_LATEST_METADATA",
                "latest metadata");

            await RunConcurrently(
                () => projection.UpdateDiscovery(latestDiscovery),
                () => projection.UpdateMetadata(latestMetadata));

            Assert.Equal("RC54_LATEST_METADATA", projection.Current.Code);
        }
    }

    [Fact]
    public async Task Projection_StoragePressureAlwaysCoversConcurrentComponentsAndReadyExposesLatest()
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var projection = CreateReadyProjection();
            Assert.True(projection.PublishStoragePressure(StoragePressure()));
            var latestDiscovery = new RuntimeCapability(
                CapabilityState.Available,
                "RC54_PRESSURE_DISCOVERY",
                "latest discovery");
            var latestMetadata = new RuntimeCapability(
                CapabilityState.Unavailable,
                "RC54_PRESSURE_METADATA",
                "latest metadata");

            await RunConcurrently(
                () => projection.UpdateDiscovery(latestDiscovery),
                () => projection.UpdateMetadata(latestMetadata));

            Assert.Equal("INDEX_STORAGE_PRESSURE", projection.Current.Code);
            Assert.True(projection.PublishStorageReady());
            Assert.Equal("RC54_PRESSURE_METADATA", projection.Current.Code);
        }
    }

    private static RuntimeCapabilityProjection CreateReadyProjection() =>
        new(
            new RuntimeCapability(
                CapabilityState.Available,
                "RC54_DISCOVERY_READY",
                "ready"),
            new RuntimeCapability(
                CapabilityState.Available,
                "RC54_METADATA_READY",
                "ready"));

    private static RuntimeCapability StoragePressure() =>
        new(
            CapabilityState.Degraded,
            "INDEX_STORAGE_PRESSURE",
            "pressure",
            new Dictionary<string, string> { ["reason"] = "WAL_PRESSURE" });

    private static async Task RunConcurrently(Action first, Action second)
    {
        using var participantsReady = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        var firstTask = Task.Run(() =>
        {
            participantsReady.Signal();
            start.Wait();
            first();
        });
        var secondTask = Task.Run(() =>
        {
            participantsReady.Signal();
            start.Wait();
            second();
        });

        Assert.True(participantsReady.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        await Task.WhenAll(firstTask, secondTask);
    }

    private sealed class EmptyDiscovery : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC54_DISCOVERY_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class MutableMetadata : IMetadataExtractionClient
    {
        public RuntimeCapability Capability { get; set; } = new(
            CapabilityState.Available,
            "RC54_METADATA_READY",
            "ready");

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Capability);

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The storage recovery gate must run before extraction.");
    }
}
