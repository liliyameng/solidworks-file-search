using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc72DiscoveryProgressBaselineTests
{
    [Fact]
    public void RunningWithoutProviderEstimate_UsesCommittedPresentCountAsBaseline()
    {
        var estimated = FileDiscoveryBaselineEstimate.ResolveEstimatedTotal(
            indexedFileCount: 135_000,
            providerEstimate: null,
            presentFileCount: 161_415,
            discoveryState: RuntimeServiceState.Running);

        Assert.Equal(161_415L, estimated);
        Assert.Equal(
            161_415L,
            FileDiscoveryBaselineEstimate.ResolveBaseline(161_415, RuntimeServiceState.Running));
    }

    [Fact]
    public void ProviderEstimate_AlwaysWinsOverCommittedBaseline()
    {
        var estimated = FileDiscoveryBaselineEstimate.ResolveEstimatedTotal(
            indexedFileCount: 10,
            providerEstimate: 500_000,
            presentFileCount: 161_415,
            discoveryState: RuntimeServiceState.Running);

        Assert.Equal(500_000L, estimated);
    }

    [Fact]
    public void IndexedBeyondBaseline_EstimateTracksIndexedNeverBelowBaseline()
    {
        var estimated = FileDiscoveryBaselineEstimate.ResolveEstimatedTotal(
            indexedFileCount: 200_000,
            providerEstimate: null,
            presentFileCount: 161_415,
            discoveryState: RuntimeServiceState.Stalled);

        Assert.Equal(200_000L, estimated);
        // The reported baseline stays the fixed committed count; only the
        // estimated total tracks the numerator so the bar caps instead of
        // pretending the scan finished.
        Assert.Equal(
            161_415L,
            FileDiscoveryBaselineEstimate.ResolveBaseline(161_415, RuntimeServiceState.Stalled));
    }

    [Fact]
    public void EmptyFirstRun_KeepsUnknownDenominator()
    {
        Assert.Null(FileDiscoveryBaselineEstimate.ResolveEstimatedTotal(
            indexedFileCount: 0,
            providerEstimate: null,
            presentFileCount: 0,
            discoveryState: RuntimeServiceState.Running));
        Assert.Null(FileDiscoveryBaselineEstimate.ResolveBaseline(0, RuntimeServiceState.Running));
    }

    [Fact]
    public void IdleEmptyCommittedStore_StillReportsBaseline()
    {
        var estimated = FileDiscoveryBaselineEstimate.ResolveEstimatedTotal(
            indexedFileCount: 0,
            providerEstimate: null,
            presentFileCount: 0,
            discoveryState: RuntimeServiceState.Idle);

        Assert.Null(estimated);
        Assert.Equal(
            0L,
            FileDiscoveryBaselineEstimate.ResolveBaseline(0, RuntimeServiceState.Idle));
    }

    [Fact]
    public async Task Progress_BackfillsCommittedBaselineWhileFallbackScanRuns()
    {
        using var directory = new RuntimeDirectory();
        var files = directory.CreateFiles(4, "baseline");
        await using var runtime = CreateRuntime(directory);
        await runtime.InitializeAsync();
        await runtime.Store.ApplyDiscoverySnapshotAsync(
                Enumerate(files),
                new DiscoverySnapshotOptions("fixture", IsComplete: false));

        runtime.DiscoveryProgressSourceForTesting = new ProgressPublishingDiscovery(90);

        var progress = await runtime.GetProgressAsync();
        var discovery = Assert.IsType<FileDiscoveryProgressSnapshot>(progress.Discovery);

        Assert.Equal(RuntimeServiceState.Running, discovery.State);
        Assert.Equal(90L, discovery.IndexedFileCount);
        Assert.Equal(4L, discovery.BaselineFileCount);
        Assert.Equal(90L, discovery.EstimatedTotalCount);
        Assert.Equal(1L, discovery.DiscoveryGeneration);
        Assert.Null(discovery.LastSuccessfulDiscoveryUtc);
        Assert.Equal("partial", discovery.SourceState);
        Assert.False(discovery.HasCommittedCompleteSnapshot);
    }

    [Fact]
    public async Task Progress_EmptyFirstRunRunning_KeepsUnknownEstimatedTotal()
    {
        using var directory = new RuntimeDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.InitializeAsync();

        runtime.DiscoveryProgressSourceForTesting = new ProgressPublishingDiscovery(0);

        var progress = await runtime.GetProgressAsync();
        var discovery = Assert.IsType<FileDiscoveryProgressSnapshot>(progress.Discovery);

        Assert.Equal(RuntimeServiceState.Running, discovery.State);
        Assert.Equal(0L, discovery.IndexedFileCount);
        Assert.Null(discovery.BaselineFileCount);
        Assert.Null(discovery.EstimatedTotalCount);
        Assert.Equal(0L, discovery.DiscoveryGeneration);
        Assert.False(discovery.HasCommittedCompleteSnapshot);
    }

    [Fact]
    public async Task Progress_CommittedReadySnapshot_IdleReportsFullBaseline()
    {
        using var directory = new RuntimeDirectory();
        var files = directory.CreateFiles(4, "ready");
        await using var runtime = CreateRuntime(directory);
        await runtime.InitializeAsync();
        await runtime.Store.ApplyDiscoverySnapshotAsync(Enumerate(files), CancellationToken.None);

        var progress = await runtime.GetProgressAsync();
        var discovery = Assert.IsType<FileDiscoveryProgressSnapshot>(progress.Discovery);

        Assert.Equal(RuntimeServiceState.Idle, discovery.State);
        Assert.Equal(4L, discovery.IndexedFileCount);
        Assert.Equal(4L, discovery.EstimatedTotalCount);
        Assert.Equal(4L, discovery.BaselineFileCount);
        Assert.True(discovery.HasCommittedCompleteSnapshot);
    }

    private static IndexingRuntime CreateRuntime(RuntimeDirectory directory) =>
        new(
            new SqliteIndexStore(Path.Combine(directory.Path, "index.db")),
            new EmptyFixtureDiscovery(),
            new NoOpMetadata(),
            new IndexingRuntimeOptions(
                TimeSpan.FromHours(1),
                TimeSpan.FromMilliseconds(10),
                10),
            new RuntimeFileLog(Path.Combine(directory.Path, "logs")));

    private static async IAsyncEnumerable<FileCandidate> Enumerate(
        IReadOnlyList<FileCandidate> files,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private sealed class ProgressPublishingDiscovery(long indexed) : IDiscoveryProgressSource
    {
        public FileDiscoveryProgressSnapshot DiscoveryProgress { get; } =
            new FileDiscoveryProgressSnapshot(
                indexed,
                null,
                RuntimeServiceState.Running,
                DateTimeOffset.UtcNow,
                "Built-in directory scan",
                "BUILTIN_DISCOVERY_SCANNING");
    }

    private sealed class EmptyFixtureDiscovery : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoOpMetadata : IMetadataExtractionClient
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                null,
                "METADATA_SKIPPED",
                "skipped",
                true));
    }

    private sealed class RuntimeDirectory : IDisposable
    {
        internal RuntimeDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"swsearch-rc72-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal IReadOnlyList<FileCandidate> CreateFiles(int count, string prefix)
        {
            var files = new List<FileCandidate>(count);
            for (var index = 0; index < count; index++)
            {
                var path = System.IO.Path.Combine(Path, $"{prefix}-{index}.SLDPRT");
                File.WriteAllBytes(path, [1]);
                var info = new FileInfo(path);
                files.Add(new FileCandidate(
                    path,
                    SolidWorksDocumentKind.Part,
                    info.Length,
                    info.LastWriteTimeUtc));
            }

            return files;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
