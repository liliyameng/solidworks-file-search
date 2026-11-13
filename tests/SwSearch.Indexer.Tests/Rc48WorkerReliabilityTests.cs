using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc48WorkerReliabilityTests
{
    [Fact]
    public void CircuitBreaker_CoalescesConcurrentStartupIncident()
    {
        var breaker = new WorkerCircuitBreaker(3, TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;
        var permits = new List<WorkerCircuitPermit>();

        for (var index = 0; index < 4; index++)
        {
            Assert.True(breaker.TryAcquire(now, out var permit));
            permits.Add(permit);
        }

        for (var index = 0; index < permits.Count; index++)
        {
            permits[index].Failure(now.AddMilliseconds(index * 10), "METADATA_WORKER_UNAVAILABLE");
        }

        Assert.Equal(WorkerCircuitState.Closed, breaker.Snapshot(now.AddSeconds(1)).State);
        Assert.True(breaker.TryAcquire(now.AddSeconds(1), out var next));
        next.Success(now.AddSeconds(1));
    }

    [Fact]
    public void CircuitBreaker_LateClosedSuccessCannotCloseNewOpenEpoch()
    {
        var breaker = new WorkerCircuitBreaker(1, TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        Assert.True(breaker.TryAcquire(now, out var failing));
        Assert.True(breaker.TryAcquire(now, out var lateSuccess));

        failing.Failure(now, "METADATA_WORKER_CRASHED");
        lateSuccess.Success(now.AddSeconds(1));

        Assert.Equal(WorkerCircuitState.Open, breaker.Snapshot(now.AddSeconds(1)).State);
        Assert.False(breaker.TryAcquire(now.AddSeconds(1), out _));
    }

    [Fact]
    public void CircuitBreaker_HalfOpenHasSinglePermit_AndAbandonCannotWedgeRecovery()
    {
        var breaker = new WorkerCircuitBreaker(1, TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;
        Assert.True(breaker.TryAcquire(now, out var closed));
        closed.Failure(now, "METADATA_WORKER_CRASHED");

        Assert.True(breaker.TryAcquire(now.AddSeconds(10), out var halfOpen));
        Assert.Equal(WorkerCircuitState.HalfOpen, breaker.Snapshot(now.AddSeconds(10)).State);
        Assert.False(breaker.TryAcquire(now.AddSeconds(10), out _));
        halfOpen.Abandon(now.AddSeconds(11));
        Assert.Equal(WorkerCircuitState.Open, breaker.Snapshot(now.AddSeconds(11)).State);

        Assert.True(breaker.TryAcquire(now.AddSeconds(21), out var retry));
        retry.Success(now.AddSeconds(21));
        Assert.Equal(WorkerCircuitState.Closed, breaker.Snapshot(now.AddSeconds(21)).State);
    }

    [Fact]
    public async Task Progress_ExposesPersistedDiscoveryTrustFromTheIndexStore()
    {
        using var directory = new RuntimeDirectory();
        var files = directory.CreateFiles(1, "progress-trust");
        var metadata = new ControlledMetadata(files, TimeSpan.Zero);
        await using var runtime = CreateRuntime(directory, files, metadata, failureTripCount: 3);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await WaitUntilAsync(
            async () =>
            {
                var status = await runtime.Store.GetStatusAsync();
                return status.DiscoveryGeneration > 0 &&
                       status.LastSuccessfulDiscoveryUtc.HasValue &&
                       string.Equals(status.SourceState, "ready", StringComparison.OrdinalIgnoreCase);
            },
            TimeSpan.FromSeconds(5));

        var expected = await runtime.Store.GetStatusAsync();
        var progress = await runtime.GetProgressAsync();
        var discovery = Assert.IsType<FileDiscoveryProgressSnapshot>(progress.Discovery);

        Assert.Equal(expected.DiscoveryGeneration, discovery.DiscoveryGeneration);
        Assert.Equal(expected.LastSuccessfulDiscoveryUtc, discovery.LastSuccessfulDiscoveryUtc);
        Assert.Equal(expected.SourceState, discovery.SourceState);
        Assert.True(discovery.HasCommittedCompleteSnapshot);

        cancellation.Cancel();
        await background;
    }

    [Fact]
    public async Task LongHealthyBatch_PulsesHeartbeatAndNeverReportsStalled()
    {
        using var directory = new RuntimeDirectory();
        var files = directory.CreateFiles(6, "healthy");
        var metadata = new ControlledMetadata(files, TimeSpan.FromMilliseconds(120));
        await using var runtime = CreateRuntime(directory, files, metadata, failureTripCount: 3);
        runtime.MetadataStallThresholdForTesting = TimeSpan.FromMilliseconds(250);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        var observedRunning = false;
        var observedStart = DateTimeOffset.UtcNow;

        await WaitUntilAsync(() => metadata.ExtractCalls > 0, TimeSpan.FromSeconds(5));
        while (metadata.ExtractCalls < files.Count)
        {
            var progress = await runtime.GetProgressAsync();
            var service = Assert.IsType<MetadataServiceProgressSnapshot>(progress.MetadataService);
            observedRunning |= service.State == RuntimeServiceState.Running;
            Assert.NotEqual(RuntimeServiceState.Stalled, service.State);
            await Task.Delay(25);
        }

        await WaitUntilAsync(
            async () => (await runtime.Store.GetStatusAsync()).PendingMetadataCount == 0,
            TimeSpan.FromSeconds(5));
        Assert.True(DateTimeOffset.UtcNow - observedStart > runtime.MetadataStallThresholdForTesting);
        Assert.True(observedRunning);
        cancellation.Cancel();
        await background;
    }

    [Fact]
    public async Task HungExtraction_StopsPulsingAndReportsTrueStall()
    {
        using var directory = new RuntimeDirectory();
        var files = directory.CreateFiles(1, "stalled");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new ControlledMetadata(files, TimeSpan.Zero, release);
        await using var runtime = CreateRuntime(directory, files, metadata, failureTripCount: 3);
        runtime.MetadataStallThresholdForTesting = TimeSpan.FromMilliseconds(200);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await WaitUntilAsync(() => metadata.ExtractCalls > 0, TimeSpan.FromSeconds(5));
        await Task.Delay(350);

        var progress = await runtime.GetProgressAsync();
        var service = Assert.IsType<MetadataServiceProgressSnapshot>(progress.MetadataService);
        Assert.Equal(RuntimeServiceState.Stalled, service.State);
        Assert.Equal("METADATA_RUNNING", service.Code);

        release.TrySetResult();
        await WaitUntilAsync(
            async () => (await runtime.Store.GetStatusAsync()).PendingMetadataCount == 0,
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await background;
    }

    [Fact]
    public async Task OpenCircuit_IsReportedUnavailableInsteadOfIdle()
    {
        using var directory = new RuntimeDirectory();
        var files = directory.CreateFiles(2, "unavailable");
        var metadata = new UnavailableMetadata();
        await using var runtime = CreateRuntime(directory, files, metadata, failureTripCount: 1);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await WaitUntilAsync(() => metadata.ExtractCalls > 0, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            async () => (await runtime.GetProgressAsync()).MetadataService?.Code == "METADATA_CIRCUIT_OPEN",
            TimeSpan.FromSeconds(5));

        var progress = await runtime.GetProgressAsync();
        var service = Assert.IsType<MetadataServiceProgressSnapshot>(progress.MetadataService);
        Assert.Equal(RuntimeServiceState.Unavailable, service.State);
        Assert.Equal("METADATA_CIRCUIT_OPEN", service.Code);
        await Task.Delay(100);
        var unchanged = Assert.IsType<MetadataServiceProgressSnapshot>(
            (await runtime.GetProgressAsync()).MetadataService);
        Assert.Equal(RuntimeServiceState.Unavailable, unchanged.State);
        Assert.Equal(service.LastHeartbeatUtc, unchanged.LastHeartbeatUtc);
        cancellation.Cancel();
        await background;
    }

    private static IndexingRuntime CreateRuntime(
        RuntimeDirectory directory,
        IReadOnlyList<FileCandidate> files,
        IMetadataExtractionClient metadata,
        int failureTripCount) =>
        new(
            new SqliteIndexStore(Path.Combine(directory.Path, "index.db")),
            new FixtureDiscovery(files),
            metadata,
            new IndexingRuntimeOptions(
                TimeSpan.FromHours(1),
                TimeSpan.FromMilliseconds(10),
                files.Count,
                MaximumFilesPerSecond: 1_000,
                MaximumReadBytesPerSecond: 100_000_000,
                MaximumWriteBytesPerSecond: 100_000_000,
                SoftFileTimeoutMilliseconds: 1_000,
                HardFileTimeoutMilliseconds: 5_000,
                WorkerFailureTripCount: failureTripCount,
                WorkerCircuitCooldownSeconds: 1,
                MaximumConcurrentExtractions: 1,
                SlowExtractionMilliseconds: 5_000),
            new RuntimeFileLog(Path.Combine(directory.Path, "logs")));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!await condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(await condition());
    }

    private sealed class FixtureDiscovery(IReadOnlyList<FileCandidate> files) : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ControlledMetadata(
        IReadOnlyList<FileCandidate> files,
        TimeSpan delay,
        TaskCompletionSource? release = null) : IMetadataExtractionClient
    {
        private int _extractCalls;

        internal int ExtractCalls => Volatile.Read(ref _extractCalls);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _extractCalls);
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Delay(delay, cancellationToken);
            }

            var file = files.Single(item => item.FullPath == request.FullPath);
            return new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                new DocumentMetadata(file, [], [], MetadataSource.NoApi, DateTimeOffset.UtcNow),
                null,
                null,
                false);
        }
    }

    private sealed class UnavailableMetadata : IMetadataExtractionClient
    {
        private int _extractCalls;

        internal int ExtractCalls => Volatile.Read(ref _extractCalls);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _extractCalls);
            return ValueTask.FromResult(new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                null,
                "METADATA_WORKER_UNAVAILABLE",
                "unavailable",
                true));
        }
    }

    private sealed class RuntimeDirectory : IDisposable
    {
        internal RuntimeDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"swsearch-rc48-{Guid.NewGuid():N}");
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
