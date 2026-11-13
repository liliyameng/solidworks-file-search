using SwSearch.Contracts;
using SwSearch.Indexer;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class Rc51MetadataWorkerPoolTests
{
    [Fact]
    public async Task TwoMembers_RunInParallel_AndEachMemberIsSingleFlight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ControlledMember(Guid.NewGuid(), release.Task);
        var second = new ControlledMember(Guid.NewGuid(), release.Task);
        await using var pool = CreatePool(first, second);

        var requests = Enumerable.Range(0, 4)
            .Select(index => pool.ExtractAsync(Request(index), CancellationToken.None).AsTask())
            .ToArray();

        await Task.WhenAll(first.FirstEntry, second.FirstEntry)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, first.Active + second.Active);
        Assert.Equal(1, first.MaximumActive);
        Assert.Equal(1, second.MaximumActive);
        Assert.All(pool.ActiveRequestsForTesting, active => Assert.Equal(2, active));

        release.TrySetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, first.ExtractCalls);
        Assert.Equal(2, second.ExtractCalls);
        Assert.Equal(1, first.MaximumActive);
        Assert.Equal(1, second.MaximumActive);
    }

    [Fact]
    public async Task RetirementTargetsOnlyMemberThatReturnedExactGenerationNonce()
    {
        var first = new ControlledMember(Guid.NewGuid(), Task.CompletedTask);
        var second = new ControlledMember(Guid.NewGuid(), Task.CompletedTask);
        await using var pool = CreatePool(first, second);

        var response = await pool.ExtractAsync(Request(1), CancellationToken.None);
        Assert.Equal(first.GenerationNonce, response.WorkerGenerationNonce);

        await pool.RetireGenerationAsync(
            response.WorkerGenerationNonce,
            "hard-timeout",
            CancellationToken.None);

        Assert.Equal([response.WorkerGenerationNonce], first.RetiredNonces);
        Assert.Empty(second.RetiredNonces);
    }

    [Fact]
    public async Task ProbePreservesDegradedStateWhenNoMemberIsFullyAvailable()
    {
        var degraded = new ControlledMember(
            Guid.NewGuid(),
            Task.CompletedTask,
            new RuntimeCapability(CapabilityState.Degraded, "RECOVERING", "recovering"));
        var unavailable = new ControlledMember(
            Guid.NewGuid(),
            Task.CompletedTask,
            new RuntimeCapability(CapabilityState.Unavailable, "DOWN", "down"));
        await using var pool = CreatePool(degraded, unavailable);

        var capability = await pool.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal("METADATA_POOL_RECOVERING", capability.Code);
    }

    [Fact]
    public async Task PoolAlwaysKeepsAtLeastOneConsumer_AndSupportsAtMostThree()
    {
        var one = new ControlledMember(Guid.NewGuid(), Task.CompletedTask);
        await using var pool = CreatePool(one);
        Assert.Equal(1, pool.WorkerCount);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MetadataWorkerPoolClient(Array.Empty<MetadataWorkerPoolMember>()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreatePool(
                new ControlledMember(Guid.NewGuid(), Task.CompletedTask),
                new ControlledMember(Guid.NewGuid(), Task.CompletedTask),
                new ControlledMember(Guid.NewGuid(), Task.CompletedTask),
                new ControlledMember(Guid.NewGuid(), Task.CompletedTask)));
    }

    [Fact]
    public async Task DisposeStartsAllMembers_UsesSevenSecondDeadline_AndCanCompleteOnRetry()
    {
        var client = new ControlledMember(Guid.NewGuid(), Task.CompletedTask);
        var hanging = new ControlledLifetime();
        var peer = new ControlledLifetime(completesImmediately: true);
        var pool = new MetadataWorkerPoolClient(
            [
                new MetadataWorkerPoolMember(client, client, hanging),
                new MetadataWorkerPoolMember(client, client, peer),
            ]);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => pool.DisposeAsync().AsTask());
        clock.Stop();

        Assert.Contains("7.0s", exception.Message, StringComparison.Ordinal);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(9));
        Assert.Equal(1, hanging.DisposeCalls);
        Assert.Equal(1, peer.DisposeCalls);
        Assert.True(hanging.Started.IsCompletedSuccessfully);
        Assert.True(peer.Started.IsCompletedSuccessfully);

        hanging.Complete();
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, hanging.DisposeCalls);
        Assert.Equal(1, peer.DisposeCalls);
    }

    [Fact]
    public async Task ProductionExtractionRunner_UsesStaApartment()
    {
        var apartment = await StaTaskRunner.RunAsync(
            () => Thread.CurrentThread.GetApartmentState());

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ApartmentState.STA, apartment);
        }
    }

    private static MetadataWorkerPoolClient CreatePool(params ControlledMember[] members) =>
        new(members.Select(static member =>
            new MetadataWorkerPoolMember(member, member, member)).ToArray());

    private static MetadataRequest Request(int index) =>
        new(
            ContractVersions.MetadataPipe,
            Guid.NewGuid(),
            $@"C:\RC51\fixture-{index}.SLDPRT",
            ForceRefresh: true,
            TimeoutMilliseconds: 5_000,
            WorkClass: MetadataWorkClass.Background,
            PipelineLeaseId: Guid.NewGuid(),
            ExpectedFingerprint: null,
            ExtractorVersion: ExtractorVersions.Current);

    private sealed class ControlledMember(
        Guid generationNonce,
        Task release,
        RuntimeCapability? capability = null) :
        IMetadataExtractionClient,
        IMetadataGenerationSupervisor,
        IAsyncDisposable
    {
        private readonly TaskCompletionSource _firstEntry =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Guid> _retiredNonces = [];
        private int _active;
        private int _maximumActive;
        private int _extractCalls;

        internal Guid GenerationNonce { get; } = generationNonce;

        internal Task FirstEntry => _firstEntry.Task;

        internal int Active => Volatile.Read(ref _active);

        internal int MaximumActive => Volatile.Read(ref _maximumActive);

        internal int ExtractCalls => Volatile.Read(ref _extractCalls);

        internal IReadOnlyList<Guid> RetiredNonces
        {
            get
            {
                lock (_retiredNonces)
                {
                    return _retiredNonces.ToArray();
                }
            }
        }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(capability ?? new RuntimeCapability(
                CapabilityState.Available,
                "TEST_READY",
                "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _extractCalls);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            _firstEntry.TrySetResult();
            try
            {
                await release.WaitAsync(cancellationToken);
                return new MetadataResponse(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    Metadata: null,
                    ErrorCode: null,
                    ChineseErrorMessage: null,
                    Retryable: false,
                    PipelineLeaseId: request.PipelineLeaseId,
                    WorkerGenerationNonce: GenerationNonce);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask RetireGenerationAsync(
            Guid workerGenerationNonce,
            string reason,
            CancellationToken cancellationToken = default)
        {
            lock (_retiredNonces)
            {
                _retiredNonces.Add(workerGenerationNonce);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActive);
                if (current >= active ||
                    Interlocked.CompareExchange(ref _maximumActive, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ControlledLifetime(bool completesImmediately = false) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal Task Started => _started.Task;

        internal void Complete() => _completion.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            _started.TrySetResult();
            if (completesImmediately)
            {
                return;
            }
            await _completion.Task;
        }
    }
}
