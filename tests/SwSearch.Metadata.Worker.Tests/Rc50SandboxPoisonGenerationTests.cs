namespace SwSearch.Metadata.Worker.Tests;

public sealed class Rc50SandboxPoisonGenerationTests
{
    [Fact]
    public async Task TransportPoison_IsMonotonic_LastReleaseRetiresG1_AndLateG1CannotTouchG2()
    {
        var processes = new FakeProcessServices();
        await using var client = CreateClient(processes);
        var generation1 = await client.EnsureOwnedWorkerForTestingAsync();
        var handle1 = Assert.Single(processes.Handles);
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failRequestA = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSuccessfulRequestB = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        void MarkEntered()
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.TrySetResult();
            }
        }

        var requestA = client.ExecuteAgainstGenerationForTestingAsync<int>(
            generation1,
            async _ =>
            {
                MarkEntered();
                await failRequestA.Task;
                throw new IOException("synthetic transport failure");
            }).AsTask();
        var requestB = client.ExecuteAgainstGenerationForTestingAsync(
            generation1,
            async _ =>
            {
                MarkEntered();
                await releaseSuccessfulRequestB.Task;
                return 42;
            }).AsTask();

        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        failRequestA.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => requestA);

        var poisoned = Assert.IsType<WorkerGenerationHealthSnapshot>(
            client.CurrentGenerationHealthForTesting);
        Assert.Equal(generation1, poisoned.Token);
        Assert.True(poisoned.IsUnhealthy);
        Assert.Equal(1, poisoned.ActiveRequests);
        Assert.False(poisoned.RetirementScheduled);
        Assert.Equal(0, handle1.TerminateCalls);

        releaseSuccessfulRequestB.TrySetResult();
        Assert.Equal(42, await requestB);
        await handle1.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handle1.TerminateCalls);
        Assert.Equal(1, handle1.DisposeCalls);
        Assert.True(handle1.Exited);

        var generation2 = await client.EnsureOwnedWorkerForTestingAsync();
        Assert.True(generation2.Generation > generation1.Generation);
        var handle2 = Assert.Single(processes.Handles, static handle => !handle.Exited);

        await client.RetireGenerationAsync(
            generation1.ReadyNonce,
            "late-g1-transport-failure");

        var current = Assert.IsType<WorkerGenerationHealthSnapshot>(
            client.CurrentGenerationHealthForTesting);
        Assert.Equal(generation2, current.Token);
        Assert.False(current.IsUnhealthy);
        Assert.False(current.RetirementScheduled);
        Assert.Equal(0, handle2.TerminateCalls);
        Assert.Equal(1, handle1.TerminateCalls);
        Assert.Equal(1, handle1.DisposeCalls);
    }

    private static MetadataWorkerClient CreateClient(FakeProcessServices processes) =>
        new(
            new MetadataWorkerClientOptions(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "rc50-worker.exe")),
                PipeName: $"swsearch.rc50.sandbox.{Guid.NewGuid():N}"),
            processes,
            processes.WorkerInstanceExists,
            _ => true,
            _ => true,
            new WorkerRecoverySettings(
                StartupAttemptLimit: 1,
                StartupRetryDelay: TimeSpan.Zero,
                ReadinessTimeout: TimeSpan.FromMilliseconds(100),
                ReadinessPollInterval: TimeSpan.FromMilliseconds(1),
                StartupFailureCooldown: TimeSpan.Zero,
                StartupCoordinationTimeout: TimeSpan.FromMilliseconds(100)),
            new FakeStartupCoordinator());

    private sealed class FakeProcessServices : IWorkerProcessServices
    {
        private readonly object _gate = new();

        internal List<FakeProcessHandle> Handles { get; } = [];

        public void PrepareIsolation()
        {
        }

        public IWorkerProcessHandle CreateSuspended(WorkerLaunchRequest request)
        {
            lock (_gate)
            {
                var handle = new FakeProcessHandle(request.PipeName);
                Handles.Add(handle);
                return handle;
            }
        }

        public void Attach(IWorkerProcessHandle process)
        {
        }

        public void Resume(IWorkerProcessHandle process) =>
            Assert.IsType<FakeProcessHandle>(process).Resumed = true;

        internal bool WorkerInstanceExists(string pipeName)
        {
            lock (_gate)
            {
                return Handles.Any(handle =>
                    string.Equals(handle.PipeName, pipeName, StringComparison.OrdinalIgnoreCase) &&
                    handle.Resumed &&
                    !handle.Exited);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeProcessHandle(string pipeName) : IWorkerProcessHandle
    {
        private readonly object _gate = new();
        private int _disposeCalls;
        private int _terminateCalls;

        internal string PipeName { get; } = pipeName;

        internal bool Resumed { get; set; }

        internal bool Exited { get; private set; }

        internal int TerminateCalls => Volatile.Read(ref _terminateCalls);

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited
        {
            get
            {
                lock (_gate)
                {
                    return Exited;
                }
            }
        }

        public void Terminate()
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _terminateCalls);
                Exited = true;
            }
        }

        public bool WaitForExit(int milliseconds) => HasExited;

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            Disposed.TrySetResult();
        }
    }

    private sealed class FakeStartupCoordinator : IWorkerStartupCoordinator
    {
        public ValueTask<IWorkerStartupLease?> AcquireAsync(
            string pipeName,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IWorkerStartupLease?>(new FakeStartupLease());
        }

        private sealed class FakeStartupLease : IWorkerStartupLease
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
