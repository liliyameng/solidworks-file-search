using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc51MetadataSchedulingTests
{
    [Fact]
    public void InteractiveThrottleNeverDisablesLastMetadataConsumer()
    {
        var governor = new ResourceGovernor(IndexingRuntimeOptions.Default);

        governor.NotifyInteractiveActivity();

        Assert.Equal(1, governor.RecommendedConcurrency(2));
        Assert.True(governor.CurrentFilesPerSecond > 0);
        Assert.True(governor.CurrentReadBytesPerSecond > 0);
    }

    [Fact]
    public async Task SynchronousDiscoveryCannotStartBeforeExistingMetadataQueueGetsFirstClaim()
    {
        using var sandbox = new Sandbox();
        var partPath = Path.Combine(sandbox.Path, "queued.SLDPRT");
        await File.WriteAllTextAsync(partPath, "fixture");
        var info = new FileInfo(partPath);
        var candidate = new FileCandidate(
            partPath,
            SolidWorksDocumentKind.Part,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        var store = new SqliteIndexStore(Path.Combine(sandbox.Path, "index.db"));
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(
            Single(candidate),
            new DiscoverySnapshotOptions("rc86-startup-gate", IsComplete: false),
            CancellationToken.None);

        var order = 0;
        var claimOrder = 0;
        var discovery = new SynchronouslyBlockingDiscovery(
            () => Interlocked.Increment(ref order));
        var metadata = new ImmediateMetadata();
        await using var runtime = new IndexingRuntime(
            store,
            discovery,
            metadata,
            IndexingRuntimeOptions.Default with
            {
                DiscoveryRefreshInterval = TimeSpan.FromHours(1),
                IdleDelay = TimeSpan.FromMilliseconds(10),
                MetadataBatchSize = 4,
                MaximumFilesPerSecond = 1_000,
                MaximumReadBytesPerSecond = 100_000_000,
                MaximumWriteBytesPerSecond = 100_000_000,
                MaximumConcurrentExtractions = 1,
            },
            new RuntimeFileLog(Path.Combine(sandbox.Path, "logs")));
        runtime.MetadataLeaseClaimedForTesting =
            () => claimOrder = Interlocked.Increment(ref order);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        var discoveryOrder = await discovery.Entered.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotEqual(0, claimOrder);
        Assert.True(
            claimOrder < discoveryOrder,
            $"Metadata claim order {claimOrder} must precede discovery order {discoveryOrder}.");

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CancellationDuringFirstPassWaitDrainsTheStartedMetadataLoop()
    {
        using var sandbox = new Sandbox();
        var metadata = new ControllableProbeMetadata(respondsToCancellation: true);
        await using var runtime = CreateRuntime(sandbox, metadata);
        using var cancellation = new CancellationTokenSource();

        var background = runtime.RunBackgroundAsync(cancellation.Token);
        await metadata.ProbeEntered.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await background.WaitAsync(TimeSpan.FromSeconds(2));
        await metadata.ProbeExited.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, runtime.TrackedBackgroundLoopCountForTesting);
    }

    [Fact]
    public async Task FirstPassTimeoutLogFailureStillDrainsTheStartedMetadataLoop()
    {
        using var sandbox = new Sandbox();
        var metadata = new ControllableProbeMetadata(respondsToCancellation: true);
        await using var runtime = CreateRuntime(sandbox, metadata);
        runtime.MetadataFirstPassTimeoutForTesting = TimeSpan.FromMilliseconds(25);
        runtime.MetadataFirstPassTimeoutLogForTesting =
            static _ => throw new InvalidOperationException("intentional log sink failure");

        var background = runtime.RunBackgroundAsync(CancellationToken.None);
        await metadata.ProbeEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => background.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("log sink", failure.Message, StringComparison.Ordinal);
        await metadata.ProbeExited.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, runtime.TrackedBackgroundLoopCountForTesting);
    }

    [Fact]
    public async Task CancellationIgnoringLoopRemainsStronglyTrackedUntilOwnerDisposeDrainsIt()
    {
        using var sandbox = new Sandbox();
        var metadata = new ControllableProbeMetadata(respondsToCancellation: false);
        var runtime = CreateRuntime(sandbox, metadata);
        runtime.BackgroundLoopDrainTimeoutForTesting = TimeSpan.FromMilliseconds(40);
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        try
        {
            var background = runtime.RunBackgroundAsync(cancellation.Token);
            await metadata.ProbeEntered.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await background.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, runtime.TrackedBackgroundLoopCountForTesting);

            runtime.BackgroundLoopDrainTimeoutForTesting = TimeSpan.FromSeconds(2);
            var dispose = runtime.DisposeAsync().AsTask();
            await Task.Delay(30);
            Assert.False(dispose.IsCompleted);

            metadata.Release();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            disposed = true;
            Assert.Equal(0, runtime.TrackedBackgroundLoopCountForTesting);
        }
        finally
        {
            metadata.Release();
            if (!disposed)
            {
                await runtime.DisposeAsync();
            }
        }
    }

    private static IndexingRuntime CreateRuntime(
        Sandbox sandbox,
        IMetadataExtractionClient metadata) =>
        new(
            new SqliteIndexStore(Path.Combine(sandbox.Path, "index.db")),
            new EmptyDiscovery(),
            metadata,
            IndexingRuntimeOptions.Default with
            {
                DiscoveryRefreshInterval = TimeSpan.FromHours(1),
                IdleDelay = TimeSpan.FromMilliseconds(10),
                MaximumConcurrentExtractions = 1,
            },
            new RuntimeFileLog(Path.Combine(sandbox.Path, "logs")));

    private static async IAsyncEnumerable<FileCandidate> Single(
        FileCandidate file,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return file;
        await Task.CompletedTask;
    }

    private sealed class SynchronouslyBlockingDiscovery(Func<int> onEnter) :
        IFileDiscoveryProvider
    {
        private readonly TaskCompletionSource<int> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<int> Entered => _entered.Task;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "SYNC_DISCOVERY_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _entered.TrySetResult(onEnter());

            // Deliberately block synchronously before the iterator's first
            // incomplete await. This models an Everything/MFT provider whose
            // native call does not yield while producing a large snapshot.
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ImmediateMetadata : IMetadataExtractionClient
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "TEST_METADATA_READY",
                "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                Metadata: null,
                ErrorCode: "TEST_PARTIAL",
                ChineseErrorMessage: "fixture",
                Retryable: false,
                PipelineLeaseId: request.PipelineLeaseId,
                Outcome: MetadataPipelineOutcome.Unsupported,
                Disposition: MetadataFileDisposition.Normal));
    }

    private sealed class EmptyDiscovery : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "EMPTY_DISCOVERY_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ControllableProbeMetadata(bool respondsToCancellation) :
        IMetadataExtractionClient
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ProbeEntered => _entered.Task;

        internal Task ProbeExited => _exited.Task;

        internal void Release() => _release.TrySetResult();

        public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            try
            {
                if (respondsToCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else
                {
                    await _release.Task.ConfigureAwait(false);
                }

                return new RuntimeCapability(
                    CapabilityState.Available,
                    "CONTROLLED_METADATA_READY",
                    "ready");
            }
            finally
            {
                _exited.TrySetResult();
            }
        }

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No extraction is expected in this scheduling gate.");
    }

    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"swsearch-rc51-scheduling-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
