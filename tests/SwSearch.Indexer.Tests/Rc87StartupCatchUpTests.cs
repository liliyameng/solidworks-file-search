using System.Runtime.CompilerServices;
using System.Reflection;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc87StartupCatchUpTests
{
    [Fact]
    public async Task OfflineCreatedFile_IsPublishedAfterFirstSettlementAndBoundedFreshReadyGrace()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var existing = Candidate("existing", 1, now);
        var createdWhileStopped = Candidate("created-while-stopped", 2, now.AddSeconds(1));
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([existing]), CancellationToken.None);

        var metadata = new BlockingUnsupportedMetadata();
        var discovery = new RecordingSnapshotDiscovery(store, [existing, createdWhileStopped]);
        await using var runtime = CreateRuntime(store, discovery, metadata, TimeSpan.FromDays(7));
        runtime.FreshReadyStartupCatchUpGracePeriodForTesting = TimeSpan.FromMilliseconds(75);
        using var cancellation = new CancellationTokenSource();

        var background = runtime.RunBackgroundAsync(cancellation.Token);
        await metadata.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.False(discovery.Entered.Task.IsCompleted);

        metadata.Release();
        await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await discovery.PendingWasSettled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(async () => (await store.GetStatusAsync()).PresentFileCount == 2,
            TimeSpan.FromSeconds(2));
        Assert.Equal(1, discovery.DiscoverCalls);

        await Task.Delay(150);
        Assert.Equal(1, discovery.DiscoverCalls);

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancellationDuringFreshReadyGrace_DoesNotStartDiscovery()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate("ready", 1, DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(file, [], [], MetadataSource.NoApi, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var discovery = new RecordingSnapshotDiscovery(store, [file]);
        await using var runtime = CreateRuntime(store, discovery, new NoopMetadata(), TimeSpan.FromDays(7));
        runtime.FreshReadyStartupCatchUpGracePeriodForTesting = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource();

        var background = runtime.RunBackgroundAsync(cancellation.Token);
        // An elapsed delay can cancel during Initialize/Prepare rather than
        // during the fresh-ready grace.  The existing phase metrics record the
        // first empty metadata consumer pass only after that initialization has
        // completed; it is therefore a stable, non-production test boundary.
        await WaitUntilAsync(
            () => Task.FromResult(runtime.RuntimePhaseMetricsForTesting.IdleDelayOperations > 0),
            TimeSpan.FromSeconds(2));
        Assert.False(discovery.Entered.Task.IsCompleted);
        cancellation.Cancel();

        await background.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(discovery.Entered.Task.IsCompleted);
    }

    [Fact]
    public void InitialDeadline_UsesEarlierNormalExpiryInsteadOfFreshReadyGrace()
    {
        var now = new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);
        var refreshInterval = TimeSpan.FromDays(7);
        var normalDeadline = now.AddSeconds(10);
        var status = new IndexStatus(
            DiscoveryGeneration: 1,
            CacheMayBeStale: false,
            LastSuccessfulDiscoveryUtc: normalDeadline.Subtract(refreshInterval),
            SourceState: "ready",
            PresentFileCount: 1,
            PendingMetadataCount: 0);

        var deadline = InvokeInitialDiscoveryDeadline(
            status, refreshInterval, now, TimeSpan.FromSeconds(30));

        Assert.Equal(normalDeadline, deadline);
    }

    [Fact]
    public void InitialDeadline_PartialSnapshotIsImmediatelyDueDespiteGenerationAndTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);
        var status = new IndexStatus(
            DiscoveryGeneration: 9,
            CacheMayBeStale: false,
            LastSuccessfulDiscoveryUtc: now,
            SourceState: "partial",
            PresentFileCount: 10,
            PendingMetadataCount: 0);

        var deadline = InvokeInitialDiscoveryDeadline(
            status, TimeSpan.FromDays(7), now, TimeSpan.FromSeconds(30));

        Assert.Equal(DateTimeOffset.MinValue, deadline);
    }

    private static IndexingRuntime CreateRuntime(
        SqliteIndexStore store,
        IFileDiscoveryProvider discovery,
        IMetadataExtractionClient metadata,
        TimeSpan refreshInterval) =>
        new(store, discovery, metadata, IndexingRuntimeOptions.Default with
        {
            DiscoveryRefreshInterval = refreshInterval,
            IdleDelay = TimeSpan.FromMilliseconds(5),
            MaximumConcurrentExtractions = 1,
            MaximumFilesPerSecond = 1_000,
            MaximumReadBytesPerSecond = 100_000_000,
            MaximumWriteBytesPerSecond = 100_000_000,
        }, new RuntimeFileLog(Path.Combine(Path.GetTempPath(), $"swsearch-rc87-{Guid.NewGuid():N}")));

    private static FileCandidate Candidate(string name, long size, DateTimeOffset modified) =>
        new($@"C:\RC87\{name}.SLDPRT", SolidWorksDocumentKind.Part, size, modified);

    private static DateTimeOffset InvokeInitialDiscoveryDeadline(
        IndexStatus status,
        TimeSpan refreshInterval,
        DateTimeOffset now,
        TimeSpan grace)
    {
        var method = typeof(IndexingRuntime).GetMethod(
            "InitialDiscoveryDeadline",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<DateTimeOffset>(method.Invoke(null, [status, refreshInterval, now, grace]));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(await condition(), "The expected state was not reached before the timeout.");
    }

    private sealed class RecordingSnapshotDiscovery(
        SqliteIndexStore store,
        IReadOnlyList<FileCandidate> snapshot) : IFileDiscoveryProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> PendingWasSettled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DiscoverCalls => Volatile.Read(ref _discoverCalls);
        private int _discoverCalls;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_DISCOVERY_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _discoverCalls);
            PendingWasSettled.TrySetResult((await store.GetStatusAsync(cancellationToken)).PendingMetadataCount == 0);
            Entered.TrySetResult();
            foreach (var candidate in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return candidate;
                await Task.Yield();
            }
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
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_METADATA_READY", "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new MetadataResponse(ContractVersions.MetadataPipe, request.RequestId, null,
                "TEST_SKIPPED", "fixture", false, request.PipelineLeaseId,
                Outcome: MetadataPipelineOutcome.Unsupported,
                Disposition: MetadataFileDisposition.Normal);
        }
    }
}
