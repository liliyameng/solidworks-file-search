using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class Rc48WorkerRecoveryTests
{
    [Fact]
    public async Task TwoClients_SamePipeJoinOneStartup_WithoutPollingOrDuplicateCreate()
    {
        var services = new FakeProcessServices
        {
            ReadinessDelay = TimeSpan.FromMilliseconds(150),
        };
        var coordinator = new FakeStartupCoordinator();
        await using var first = CreateClient(
            services,
            startupCoordinator: coordinator,
            pipeName: "Rc48.Shared.Pipe",
            readinessTimeout: TimeSpan.FromSeconds(1));
        await using var second = CreateClient(
            services,
            startupCoordinator: coordinator,
            pipeName: "rc48.shared.pipe",
            readinessTimeout: TimeSpan.FromSeconds(1));

        var starts = new[]
        {
            first.EnsureOwnedOrExternalWorkerForTestingAsync().AsTask(),
            second.EnsureOwnedOrExternalWorkerForTestingAsync().AsTask(),
        };
        var results = await Task.WhenAll(starts);

        Assert.Single(results, token => token is not null);
        Assert.Single(results, token => token is null);
        Assert.Equal(1, services.PrepareCalls);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.AttachCalls);
        Assert.Equal(1, services.ResumeCalls);
        Assert.Equal(2, coordinator.AcquireCalls);
        Assert.Equal(1, coordinator.MaximumConcurrentHolders);
        Assert.Single(coordinator.ObservedPipeKeys);
        Assert.Equal(
            WorkerInstanceSignal.Name("Rc48.Shared.Pipe"),
            WorkerInstanceSignal.Name("rc48.shared.pipe"));
        Assert.Equal(
            NamedMutexWorkerStartupCoordinator.Name("Rc48.Shared.Pipe"),
            NamedMutexWorkerStartupCoordinator.Name("rc48.shared.pipe"));
    }

    [Fact]
    public async Task ProductionNamedMutex_TwoClientsHandOffOnOwnerThread_AndCreateOnce()
    {
        var pipeName = $"Rc48.Named.{Guid.NewGuid():N}";
        var services = new FakeProcessServices
        {
            ReadinessDelay = TimeSpan.FromMilliseconds(100),
        };
        await using var first = CreateClient(
            services,
            startupCoordinator: NamedMutexWorkerStartupCoordinator.Shared,
            pipeName: pipeName,
            readinessTimeout: TimeSpan.FromSeconds(1));
        await using var second = CreateClient(
            services,
            startupCoordinator: NamedMutexWorkerStartupCoordinator.Shared,
            pipeName: pipeName.ToUpperInvariant(),
            readinessTimeout: TimeSpan.FromSeconds(1));

        var results = await Task.WhenAll(
                first.EnsureOwnedOrExternalWorkerForTestingAsync().AsTask(),
                second.EnsureOwnedOrExternalWorkerForTestingAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(results, token => token is not null);
        Assert.Single(results, token => token is null);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.AttachCalls);
        Assert.Equal(1, services.ResumeCalls);
    }

    [Fact]
    public async Task ProductionNamedMutex_TimeoutAndCancellationDoNotAcquireLater()
    {
        var pipeName = $"Rc48.Named.Bounds.{Guid.NewGuid():N}";
        var coordinator = NamedMutexWorkerStartupCoordinator.Shared;
        var owner = await coordinator.AcquireAsync(
            pipeName,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.NotNull(owner);

        var timedOut = await coordinator.AcquireAsync(
            pipeName,
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None);
        Assert.Null(timedOut);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.AcquireAsync(pipeName, TimeSpan.FromSeconds(1), cancellation.Token).AsTask());

        await owner!.DisposeAsync();
        var successor = await coordinator.AcquireAsync(
            pipeName,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.NotNull(successor);
        await successor!.DisposeAsync();
    }

    [Fact]
    public async Task ProductionNamedMutex_ReleaseFaultIsPermanentlyFailClosedWithoutSecondCreate()
    {
        var releaseAttempts = 0;
        var coordinator = new NamedMutexWorkerStartupCoordinator(_ =>
        {
            Interlocked.Increment(ref releaseAttempts);
            throw new Win32Exception(5);
        });
        var services = new FakeProcessServices();
        var client = CreateClient(
            services,
            startupCoordinator: coordinator,
            startupFailureCooldown: TimeSpan.Zero);

        var first = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());
        var retry = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal("startup-coordinate-release", first.Stage);
        Assert.Equal("startup-coordinate-release", retry.Stage);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, releaseAttempts);

        await Assert.ThrowsAsync<Win32Exception>(
            () => client.DisposeAsync().AsTask());
        Assert.True(services.Handles[0].Terminated);
        Assert.True(services.Handles[0].Disposed);
        Assert.Equal(1, releaseAttempts);
    }

    [Fact]
    public async Task FourConcurrentColdStartCallers_CreateAssignAndResumeExactlyOnce()
    {
        var services = new FakeProcessServices();
        await using var client = CreateClient(services);

        var starts = Enumerable.Range(0, 4)
            .Select(_ => client.EnsureOwnedWorkerForTestingAsync().AsTask())
            .ToArray();
        var tokens = await Task.WhenAll(starts);

        Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        Assert.Equal(1, services.PrepareCalls);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.AttachCalls);
        Assert.Equal(1, services.ResumeCalls);
        Assert.Equal(
            ["prepare", "create", "attach", "resume"],
            services.Events.Where(item => item is "prepare" or "create" or "attach" or "resume"));
        Assert.True(services.Handles[0].Resumed);
        Assert.False(services.Handles[0].Terminated);
    }

    [Fact]
    public async Task ReadinessAfterMoreThanFiftyMilliseconds_JoinsSameFlightWithoutRestart()
    {
        var services = new FakeProcessServices
        {
            ReadinessDelay = TimeSpan.FromMilliseconds(150),
        };
        await using var client = CreateClient(services, readinessTimeout: TimeSpan.FromSeconds(1));

        var starts = Enumerable.Range(0, 4)
            .Select(_ => client.EnsureOwnedWorkerForTestingAsync().AsTask())
            .ToArray();
        var tokens = await Task.WhenAll(starts);

        Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.ResumeCalls);
        Assert.False(services.Handles[0].Terminated);
    }

    [Fact]
    public async Task ReadinessTimeout_CleansCreatedProcess_ClearsFlightAndAllowsBoundedRecovery()
    {
        var services = new FakeProcessServices
        {
            PublishReadyAutomatically = false,
        };
        var coordinator = new FakeStartupCoordinator();
        await using var client = CreateClient(
            services,
            startupAttemptLimit: 1,
            readinessTimeout: TimeSpan.FromMilliseconds(30),
            startupCoordinator: coordinator,
            startupFailureCooldown: TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(() =>
            client.EnsureOwnedWorkerForTestingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("ready-nonce", failure.Stage);
        Assert.Equal(1, services.CreateCalls);
        Assert.True(services.Handles[0].Terminated);
        Assert.True(services.Handles[0].Disposed);
        Assert.Equal(0, coordinator.CurrentHolders);

        services.PublishReadyAutomatically = true;
        var recovered = await client.EnsureOwnedWorkerForTestingAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(default, recovered.ReadyNonce);
        Assert.Equal(2, services.CreateCalls);
        Assert.Equal(1, coordinator.MaximumConcurrentHolders);
    }

    [Fact]
    public async Task ReadinessTimeout_WhenTerminationFails_RetainsLeaseAndBlocksCompetingClient()
    {
        var services = new FakeProcessServices
        {
            PublishReadyAutomatically = false,
            TerminateError = new Win32Exception(32),
        };
        var coordinator = new FakeStartupCoordinator();
        var first = CreateClient(
            services,
            startupAttemptLimit: 1,
            readinessTimeout: TimeSpan.FromMilliseconds(30),
            startupCoordinator: coordinator,
            startupFailureCooldown: TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(() =>
            first.EnsureOwnedWorkerForTestingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("process-terminate", failure.Stage);
        Assert.Equal(1, services.CreateCalls);
        Assert.False(services.Handles[0].Disposed);
        Assert.Equal(1, coordinator.CurrentHolders);

        var second = CreateClient(
            services,
            startupAttemptLimit: 1,
            readinessTimeout: TimeSpan.FromMilliseconds(30),
            coordinationTimeout: TimeSpan.FromMilliseconds(40),
            startupCoordinator: coordinator,
            startupFailureCooldown: TimeSpan.Zero);
        var competing = await Assert.ThrowsAsync<MetadataWorkerStartupException>(() =>
            second.EnsureOwnedWorkerForTestingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("startup-coordinate", competing.Stage);
        Assert.Equal(1, services.CreateCalls);

        services.TerminateError = null;
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.True(services.Handles[0].Disposed);
        Assert.Equal(0, coordinator.CurrentHolders);
    }

    [Fact]
    public async Task AssignErrorFive_FailsClosedBeforeResume_AndTerminatesOriginalHandle()
    {
        var diagnostics = new ConcurrentQueue<MetadataWorkerDiagnostic>();
        var services = new FakeProcessServices
        {
            AttachError = new WorkerIsolationException("job-attach", 5),
        };
        var workerPath = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "secret-user", "worker.exe");
        await using var client = CreateClient(
            services,
            diagnostics,
            workerPath,
            startupAttemptLimit: 1);

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal("job-attach", failure.Stage);
        Assert.Equal(5, failure.NativeErrorCode);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.AttachCalls);
        Assert.Equal(0, services.ResumeCalls);
        Assert.True(services.Handles[0].Terminated);
        Assert.True(services.Handles[0].Disposed);
        Assert.Equal(
            ["prepare", "create", "attach", "terminate", "dispose"],
            services.Events);
        var diagnostic = Assert.Single(
            diagnostics,
            item => item.Code == "METADATA_WORKER_STARTUP_FAILED");
        Assert.Contains("stage=job-attach", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("native_error=5", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("path=<external>\\worker.exe", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-user", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("degraded", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssignFailure_WhenTerminateThrows_RetainsHandleAndStartupLeaseAndBlocksAllCreates()
    {
        var services = new FakeProcessServices
        {
            AttachError = new WorkerIsolationException("job-attach", 5),
            TerminateError = new Win32Exception(32),
        };
        var coordinator = new FakeStartupCoordinator();
        var first = CreateClient(
            services,
            startupAttemptLimit: 1,
            startupCoordinator: coordinator,
            coordinationTimeout: TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => first.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal("process-terminate", failure.Stage);
        Assert.Equal(32, failure.NativeErrorCode);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.AttachCalls);
        Assert.Equal(0, services.ResumeCalls);
        Assert.False(services.Handles[0].Disposed);
        Assert.Equal(1, coordinator.CurrentHolders);

        await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => first.EnsureOwnedWorkerForTestingAsync().AsTask());
        Assert.Equal(1, services.CreateCalls);

        var second = CreateClient(
            services,
            startupAttemptLimit: 1,
            startupCoordinator: coordinator,
            coordinationTimeout: TimeSpan.FromMilliseconds(40));
        var competing = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => second.EnsureOwnedWorkerForTestingAsync().AsTask());
        Assert.Equal("startup-coordinate", competing.Stage);
        Assert.Equal(1, services.CreateCalls);

        services.TerminateError = null;
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.True(services.Handles[0].Terminated);
        Assert.True(services.Handles[0].Disposed);
        Assert.Equal(0, coordinator.CurrentHolders);
    }

    [Fact]
    public async Task AssignFailure_WhenExitWaitTimesOut_RetainsHandleAndBlocksRetry()
    {
        var services = new FakeProcessServices
        {
            AttachError = new WorkerIsolationException("job-attach", 5),
            ExitOnTerminate = false,
            WaitForExitResult = false,
        };
        var coordinator = new FakeStartupCoordinator();
        var client = CreateClient(
            services,
            startupAttemptLimit: 1,
            startupCoordinator: coordinator);

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal("process-wait-exit", failure.Stage);
        Assert.True(services.Handles[0].Terminated);
        Assert.False(services.Handles[0].Disposed);
        await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());
        Assert.Equal(1, services.CreateCalls);

        services.ExitOnTerminate = true;
        services.WaitForExitResult = true;
        await client.DisposeAsync();
        Assert.True(services.Handles[0].Disposed);
        Assert.Equal(0, coordinator.CurrentHolders);
    }

    [Fact]
    public async Task RetainedStartupLease_IsClearedOnlyAfterReleaseSucceeds_AndReleaseCanRetry()
    {
        var services = new FakeProcessServices
        {
            AttachError = new WorkerIsolationException("job-attach", 5),
            TerminateError = new Win32Exception(32),
        };
        var coordinator = new FakeStartupCoordinator();
        var client = CreateClient(
            services,
            startupAttemptLimit: 1,
            startupCoordinator: coordinator);

        await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());
        Assert.Equal(1, coordinator.CurrentHolders);

        services.TerminateError = null;
        coordinator.DisposeFailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(() => client.DisposeAsync().AsTask());

        var process = Assert.Single(services.Handles);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
        Assert.Equal(2, process.TerminateCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(1, coordinator.CurrentHolders);
        Assert.Equal(1, coordinator.DisposeCalls);

        await client.DisposeAsync();

        Assert.Equal(2, process.TerminateCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(0, coordinator.CurrentHolders);
        Assert.Equal(2, coordinator.DisposeCalls);
    }

    [Fact]
    public async Task OrdinaryStartupLeaseReleaseFailure_IsRetainedAndRetriedWithoutSecondCreate()
    {
        var services = new FakeProcessServices();
        var coordinator = new FakeStartupCoordinator
        {
            DisposeFailuresRemaining = 1,
        };
        await using var client = CreateClient(
            services,
            startupCoordinator: coordinator,
            startupFailureCooldown: TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal("startup-coordinate-release", failure.Stage);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, coordinator.CurrentHolders);
        Assert.Equal(1, coordinator.DisposeCalls);
        Assert.False(services.Handles[0].Terminated);

        var recovered = await client.EnsureOwnedWorkerForTestingAsync();

        Assert.NotEqual(default, recovered.ReadyNonce);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(0, coordinator.CurrentHolders);
        Assert.Equal(2, coordinator.DisposeCalls);
        Assert.False(services.Handles[0].Terminated);
    }

    [Fact]
    public async Task OrdinaryLeaseReleaseFailure_ImmediateDisposeReleasesLeaseBeforeClearingProcessState()
    {
        var services = new FakeProcessServices();
        var coordinator = new FakeStartupCoordinator
        {
            DisposeFailuresRemaining = 1,
        };
        var client = CreateClient(
            services,
            startupCoordinator: coordinator,
            startupFailureCooldown: TimeSpan.Zero);

        await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());
        Assert.Equal(1, coordinator.CurrentHolders);

        await client.DisposeAsync();

        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.Handles[0].TerminateCalls);
        Assert.Equal(1, services.Handles[0].DisposeCalls);
        Assert.Equal(0, coordinator.CurrentHolders);
        Assert.Equal(2, coordinator.DisposeCalls);

        var successor = await coordinator.AcquireAsync(
            MetadataWorkerDefaults.PipeName,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.NotNull(successor);
        await successor!.DisposeAsync();
        Assert.Equal(1, services.CreateCalls);
    }

    [Fact]
    public async Task ResumeFailure_TerminatesTheSameOriginalHandle()
    {
        var services = new FakeProcessServices
        {
            ResumeError = new Win32Exception(31),
        };
        await using var client = CreateClient(services, startupAttemptLimit: 1);

        await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.AttachCalls);
        Assert.Equal(1, services.ResumeCalls);
        Assert.False(services.Handles[0].Resumed);
        Assert.True(services.Handles[0].Terminated);
        Assert.True(services.Handles[0].Disposed);
    }

    [Fact]
    public void ReadyNonceArguments_AreStrictAndNeverAcceptTrailingValues()
    {
        var nonce = Guid.NewGuid();

        Assert.Equal(nonce, WorkerReadySignal.ParseArguments(["--ready-nonce", nonce.ToString("N")]));
        Assert.Null(WorkerReadySignal.ParseArguments([]));
        Assert.Null(WorkerReadySignal.ParseArguments(["--probe"]));
        Assert.Throws<ArgumentException>(() =>
            WorkerReadySignal.ParseArguments(["--ready-nonce", nonce.ToString("N"), "extra"]));

        var pipeName = "Rc48 自定义 Pipe";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pipeName));
        var startup = WorkerReadySignal.ParseStartupArguments(
            ["--ready-nonce", nonce.ToString("N"), "--pipe-name-base64", encoded]);
        Assert.Equal(nonce, startup?.ReadyNonce);
        Assert.Equal(pipeName, startup?.PipeName);
        Assert.Throws<ArgumentException>(() => WorkerReadySignal.ParseStartupArguments(
            ["--ready-nonce", nonce.ToString("N"), "--pipe-name-base64", "not-base64!"]));
    }

    [Fact]
    public async Task CancelledCaller_DoesNotCancelOrTerminateSharedStartup()
    {
        var services = new FakeProcessServices
        {
            PublishReadyAutomatically = false,
        };
        await using var client = CreateClient(services, readinessTimeout: TimeSpan.FromSeconds(2));
        using var callerCancellation = new CancellationTokenSource();
        var cancelledCaller = client.EnsureOwnedWorkerForTestingAsync(callerCancellation.Token).AsTask();
        await WaitUntilAsync(() => services.ResumeCalls == 1, TimeSpan.FromSeconds(1));
        var survivingCaller = client.EnsureOwnedWorkerForTestingAsync().AsTask();

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);
        services.PublishReady();
        var token = await survivingCaller;

        Assert.NotEqual(default, token.ReadyNonce);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(1, services.ResumeCalls);
        Assert.False(services.Handles[0].Terminated);
    }

    [Fact]
    public async Task StaleGenerationFailure_CannotTerminateReplacementGeneration()
    {
        var services = new FakeProcessServices();
        await using var client = CreateClient(services);
        var first = await client.EnsureOwnedWorkerForTestingAsync();
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRequest = client.ExecuteAgainstGenerationForTestingAsync<int>(
            first,
            async cancellationToken =>
            {
                await releaseFailure.Task.WaitAsync(cancellationToken);
                throw new IOException("stale transport failure");
            }).AsTask();
        services.Handles[0].Exited = true;
        var second = await client.EnsureOwnedWorkerForTestingAsync();

        releaseFailure.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => staleRequest);

        Assert.NotEqual(first.Generation, second.Generation);
        Assert.True(services.Handles[0].Disposed);
        Assert.False(services.Handles[1].Terminated);
        Assert.False(services.Handles[1].Disposed);
    }

    [Fact]
    public async Task ConcurrentRetirementAndDispose_TerminateAndDisposeOneSingleFlight()
    {
        var services = new FakeProcessServices();
        var client = CreateClient(services);
        var generation = await client.EnsureOwnedWorkerForTestingAsync();
        using var terminateEntered = new ManualResetEventSlim();
        using var allowTerminate = new ManualResetEventSlim();
        services.TerminateEntered = terminateEntered;
        services.AllowTerminate = allowTerminate;

        var retirement = Task.Run(async () =>
            await client.RetireGenerationForTestingAsync(generation));
        Assert.True(terminateEntered.Wait(TimeSpan.FromSeconds(1)));
        var disposal = Task.Run(async () => await client.DisposeAsync());

        allowTerminate.Set();
        await Task.WhenAll(retirement, disposal).WaitAsync(TimeSpan.FromSeconds(5));

        var process = Assert.Single(services.Handles);
        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RetirementReservation_PrecedesDisposeAndPreventsSemaphoreDestructionRace()
    {
        var services = new FakeProcessServices();
        var client = CreateClient(services);
        var generation = await client.EnsureOwnedWorkerForTestingAsync();
        var reservationRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetirement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.AfterTerminationReservationForTesting = async () =>
        {
            reservationRegistered.TrySetResult();
            await releaseRetirement.Task;
        };

        var retirement = client.RetireGenerationForTestingAsync(generation).AsTask();
        await reservationRegistered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposal = client.DisposeAsync().AsTask();

        await WaitUntilAsync(
            () => services.Handles[0].TerminateCalls == 1,
            TimeSpan.FromSeconds(2));
        Assert.False(disposal.IsCompleted);

        releaseRetirement.TrySetResult();
        await Task.WhenAll(retirement, disposal).WaitAsync(TimeSpan.FromSeconds(5));

        var process = Assert.Single(services.Handles);
        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(0, client.TerminationIdentityCountForTesting);
    }

    [Fact]
    public async Task OneRequestCancellation_DoesNotTerminateAnotherActiveRequest()
    {
        var services = new FakeProcessServices();
        await using var client = CreateClient(services);
        var generation = await client.EnsureOwnedWorkerForTestingAsync();
        using var firstCancellation = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = client.ExecuteAgainstGenerationForTestingAsync<int>(
            generation,
            async cancellationToken =>
            {
                firstEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 1;
            },
            firstCancellation.Token).AsTask();
        await firstEntered.Task;
        var second = client.ExecuteAgainstGenerationForTestingAsync<int>(
            generation,
            async cancellationToken =>
            {
                await secondRelease.Task.WaitAsync(cancellationToken);
                return 2;
            }).AsTask();

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(services.Handles[0].Terminated);
        secondRelease.TrySetResult();
        Assert.Equal(2, await second);
        Assert.False(services.Handles[0].Terminated);
    }

    [Fact]
    public async Task ExitedOwnedWorker_IsAutomaticallyReplaced()
    {
        var services = new FakeProcessServices();
        await using var client = CreateClient(services);

        await client.EnsureOwnedWorkerForTestingAsync();
        services.Handles[0].Exited = true;
        await client.EnsureOwnedWorkerForTestingAsync();

        Assert.Equal(2, services.CreateCalls);
        Assert.Equal(2, services.AttachCalls);
        Assert.Equal(2, services.ResumeCalls);
        Assert.True(services.Handles[0].Disposed);
        Assert.False(services.Handles[1].Exited);
        Assert.Equal(0, client.TerminationIdentityCountForTesting);
    }

    [Fact]
    public async Task CompletedTerminationIdentities_AreRemovedAcrossManyGenerations()
    {
        var services = new FakeProcessServices();
        await using var client = CreateClient(services);

        for (var generation = 0; generation < 24; generation++)
        {
            await client.EnsureOwnedWorkerForTestingAsync();
            services.Handles[^1].Exited = true;
        }
        await client.EnsureOwnedWorkerForTestingAsync();

        Assert.Equal(25, services.CreateCalls);
        Assert.Equal(0, client.TerminationIdentityCountForTesting);
    }

    [Fact]
    public async Task HandleWrappingFailure_TransfersCreationHandleToFailClosedCleanup()
    {
        var services = new FakeProcessServices
        {
            CreateFailureAfterHandle = new InvalidOperationException("safe-handle wrapping failed"),
        };
        await using var client = CreateClient(services, startupAttemptLimit: 1);

        var failure = await Assert.ThrowsAsync<MetadataWorkerStartupException>(
            () => client.EnsureOwnedWorkerForTestingAsync().AsTask());

        Assert.Equal("process-create-suspended", failure.Stage);
        Assert.Equal(1, services.CreateCalls);
        Assert.Equal(0, services.AttachCalls);
        Assert.Equal(0, services.ResumeCalls);
        Assert.True(services.Handles[0].Terminated);
        Assert.True(services.Handles[0].Disposed);
    }

    [Fact]
    public void ProductionLaunchIlGate_RequiresAttachBeforeResume_AndRetainsWrappingFailureHandle()
    {
        var root = typeof(MetadataWorkerClient).GetMethod(
            "StartOwnedWorkerGenerationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stateMachine = root.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var moveNext = stateMachine.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var guardedLaunch = typeof(MetadataWorkerClient).GetMethod(
            nameof(MetadataWorkerClient.AttachAndResumeIsolatedProcess),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var callGraph = ProductionCallGraph(moveNext).ToArray();

        Assert.Contains(guardedLaunch, callGraph);
        Assert.True(ResumeCallsAreConfinedToGuardedHelper(moveNext, guardedLaunch));
        Assert.False(ResumeCallsAreConfinedToGuardedHelper(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafeDirectResumeRoot),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            guardedLaunch));
        Assert.False(ResumeCallsAreConfinedToGuardedHelper(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafeIndirectResumeRoot),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            guardedLaunch));
        Assert.False(ResumeCallsAreConfinedToGuardedHelper(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafeAsyncResumeRoot),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            guardedLaunch));
        Assert.True(AttachDominatesEveryResumeControlFlowPath(guardedLaunch));
        Assert.False(AttachDominatesEveryResumeControlFlowPath(typeof(Rc48WorkerRecoveryTests).GetMethod(
            nameof(UnsafeResumeBeforeAttach),
            BindingFlags.Static | BindingFlags.NonPublic)!));
        Assert.False(AttachDominatesEveryResumeControlFlowPath(typeof(Rc48WorkerRecoveryTests).GetMethod(
            nameof(UnsafeConditionalAttach),
            BindingFlags.Static | BindingFlags.NonPublic)!));
        Assert.False(AttachDominatesEveryResumeControlFlowPath(typeof(Rc48WorkerRecoveryTests).GetMethod(
            nameof(UnsafeCaughtAttachThenResume),
            BindingFlags.Static | BindingFlags.NonPublic)!));
        Assert.False(AttachDominatesEveryResumeControlFlowPath(typeof(Rc48WorkerRecoveryTests).GetMethod(
            nameof(UnsafeFilteredAttachThenResume),
            BindingFlags.Static | BindingFlags.NonPublic)!));

        var createSuspended = typeof(SystemWorkerProcessServices).GetMethod(
            nameof(SystemWorkerProcessServices.CreateSuspended))!;
        Assert.True(
            CreationHandleWrappingFailuresTransferPreallocatedCapability(createSuspended),
            DescribeIl(createSuspended));
        Assert.False(CreationHandleWrappingFailuresTransferPreallocatedCapability(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafePostCreateWrappingFixture),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            createMethodName: nameof(CreateProcessFixture),
            capabilityTypeName: nameof(UnsafeCreationCapability),
            transferExceptionType: typeof(UnsafeWrappingTransferException),
            requireProductionWrappers: false));
        Assert.False(CreationHandleWrappingFailuresTransferPreallocatedCapability(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafeHandlerWrappingFixture),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            createMethodName: nameof(CreateProcessFixture),
            capabilityTypeName: nameof(UnsafeCreationCapability),
            transferExceptionType: typeof(UnsafeWrappingTransferException),
            requireProductionWrappers: false));
        Assert.False(CreationHandleWrappingFailuresTransferPreallocatedCapability(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafeOutOfOrderWrappingFixture),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            createMethodName: nameof(CreateProcessFixture),
            capabilityTypeName: nameof(UnsafeCreationCapability),
            transferExceptionType: typeof(UnsafeWrappingTransferException),
            requireProductionWrappers: false));
        Assert.False(CreationHandleWrappingFailuresTransferPreallocatedCapability(
            typeof(Rc48WorkerRecoveryTests).GetMethod(
                nameof(UnsafeHandlerAllocationWrappingFixture),
                BindingFlags.Static | BindingFlags.NonPublic)!,
            createMethodName: nameof(CreateProcessFixture),
            capabilityTypeName: nameof(UnsafeCreationCapability),
            transferExceptionType: typeof(UnsafeWrappingTransferException),
            requireProductionWrappers: false));
    }

    private static void UnsafeResumeBeforeAttach(
        IWorkerProcessServices services,
        IWorkerProcessHandle process)
    {
        services.Resume(process);
        services.Attach(process);
    }

    private static void UnsafeDirectResumeRoot(
        IWorkerProcessServices services,
        IWorkerProcessHandle process) => services.Resume(process);

    private static void UnsafeIndirectResumeRoot(
        IWorkerProcessServices services,
        IWorkerProcessHandle process) => UnsafeResumeCarrier.Resume(services, process);

    private static async Task UnsafeAsyncResumeRoot(
        IWorkerProcessServices services,
        IWorkerProcessHandle process)
    {
        await Task.Yield();
        UnsafeResumeCarrier.Resume(services, process);
    }

    private static class UnsafeResumeCarrier
    {
        internal static void Resume(
            IWorkerProcessServices services,
            IWorkerProcessHandle process) => services.Resume(process);
    }

    private static void UnsafePostCreateWrappingFixture()
    {
        var capability = new UnsafeCreationCapability();
        if (!CreateProcessFixture())
        {
            throw new Win32Exception();
        }

        UnsafePotentiallyThrowingWrapper();
        try
        {
            capability.OwnRaw();
            capability.AdoptSafeProcessHandle();
            capability.AdoptSafeThreadHandle();
            capability.Disarm();
        }
        catch (Exception exception)
        {
            throw new UnsafeWrappingTransferException(exception);
        }
    }

    private static bool CreateProcessFixture() => true;

    private static void UnsafeHandlerWrappingFixture()
    {
        var capability = new UnsafeCreationCapability();
        if (!CreateProcessFixture())
        {
            throw new Win32Exception();
        }

        try
        {
            capability.OwnRaw();
            capability.AdoptSafeProcessHandle();
            capability.AdoptSafeThreadHandle();
            capability.Disarm();
            throw new InvalidOperationException();
        }
        catch (Exception exception)
        {
            UnsafePotentiallyThrowingWrapper();
            throw new UnsafeWrappingTransferException(exception);
        }
    }

    private static void UnsafeOutOfOrderWrappingFixture()
    {
        var capability = new UnsafeCreationCapability();
        var transfer = new UnsafeWrappingTransferException();
        if (!CreateProcessFixture())
        {
            throw new Win32Exception();
        }

        try
        {
            capability.OwnRaw();
            capability.Disarm();
            capability.AdoptSafeProcessHandle();
            capability.AdoptSafeThreadHandle();
        }
        catch
        {
            throw transfer;
        }
    }

    private static void UnsafeHandlerAllocationWrappingFixture()
    {
        var capability = new UnsafeCreationCapability();
        var transfer = new UnsafeWrappingTransferException();
        if (!CreateProcessFixture())
        {
            throw new Win32Exception();
        }

        try
        {
            capability.OwnRaw();
            capability.AdoptSafeProcessHandle();
            capability.AdoptSafeThreadHandle();
            capability.Disarm();
        }
        catch
        {
            _ = new object[1];
            throw transfer;
        }
    }

    private static void UnsafePotentiallyThrowingWrapper() =>
        throw new InvalidOperationException();

    private sealed class UnsafeCreationCapability
    {
        internal void OwnRaw() { }
        internal void AdoptSafeProcessHandle() { }
        internal void AdoptSafeThreadHandle() { }
        internal void Disarm() { }
    }

    private sealed class UnsafeWrappingTransferException : Exception
    {
        internal UnsafeWrappingTransferException()
            : base("unsafe wrapping transfer")
        {
        }

        internal UnsafeWrappingTransferException(Exception innerException)
            : base("unsafe wrapping transfer", innerException)
        {
        }
    }

    private static void UnsafeConditionalAttach(
        bool shouldAttach,
        IWorkerProcessServices services,
        IWorkerProcessHandle process)
    {
        if (shouldAttach)
        {
            services.Attach(process);
        }
        services.Resume(process);
    }

    private static void UnsafeCaughtAttachThenResume(
        IWorkerProcessServices services,
        IWorkerProcessHandle process)
    {
        try
        {
            services.Attach(process);
        }
        catch
        {
        }
        services.Resume(process);
    }

    private static void UnsafeFilteredAttachThenResume(
        IWorkerProcessServices services,
        IWorkerProcessHandle process)
    {
        try
        {
            services.Attach(process);
        }
        catch (Exception) when (AlwaysHandleAttachFailure())
        {
        }
        services.Resume(process);
    }

    private static bool AlwaysHandleAttachFailure() => true;

    private static bool ResumeCallsAreConfinedToGuardedHelper(
        MethodBase root,
        MethodBase guardedHelper)
    {
        var sawGuardedResume = false;
        foreach (var method in ProductionCallGraph(root))
        {
            foreach (var called in CalledMethods(method).Where(IsResumeCall))
            {
                if (!SameMethod(method, guardedHelper))
                {
                    return false;
                }
                sawGuardedResume = true;
            }
        }

        return sawGuardedResume;
    }

    private static bool AttachDominatesEveryResumeControlFlowPath(MethodBase method)
    {
        var instructions = DecodeInstructions(method).ToArray();
        var body = method.GetMethodBody();
        if (instructions.Length == 0 || body is null)
        {
            return false;
        }

        var byOffset = instructions.ToDictionary(instruction => instruction.Offset);
        var pending = new Queue<(int Offset, bool Attached)>();
        var visited = new HashSet<(int Offset, bool Attached)>();
        pending.Enqueue((instructions[0].Offset, false));
        var sawResume = false;
        while (pending.TryDequeue(out var state))
        {
            if (!visited.Add(state) || !byOffset.TryGetValue(state.Offset, out var instruction))
            {
                continue;
            }

            var attached = state.Attached || IsAttachCall(instruction.CalledMethod);
            if (IsResumeCall(instruction.CalledMethod))
            {
                sawResume = true;
                if (!attached)
                {
                    return false;
                }
            }

            var exceptionSuccessors = new HashSet<int>();
            foreach (var clause in body.ExceptionHandlingClauses.Cast<ExceptionHandlingClause>())
            {
                if (IsWithin(instruction.Offset, clause.TryOffset, clause.TryLength))
                {
                    exceptionSuccessors.Add(
                        clause.Flags == ExceptionHandlingClauseOptions.Filter
                            ? clause.FilterOffset
                            : clause.HandlerOffset);
                }
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter &&
                    IsWithin(
                        instruction.Offset,
                        clause.FilterOffset,
                        clause.HandlerOffset - clause.FilterOffset))
                {
                    // Model the filter-true edge; endfilter otherwise looks
                    // like a terminal instruction to the normal CFG walker.
                    exceptionSuccessors.Add(clause.HandlerOffset);
                }
            }
            foreach (var exceptionSuccessor in exceptionSuccessors)
            {
                if (byOffset.ContainsKey(exceptionSuccessor))
                {
                    // Exceptions occur before the call's postcondition.  In
                    // particular, a throwing Attach must not mark the catch
                    // path as attached.
                    pending.Enqueue((exceptionSuccessor, state.Attached));
                }
            }

            foreach (var successor in Successors(instruction, byOffset))
            {
                pending.Enqueue((successor, attached));
            }
        }

        return sawResume;
    }

    private static IEnumerable<int> Successors(
        IlInstruction instruction,
        IReadOnlyDictionary<int, IlInstruction> instructions)
    {
        if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
        {
            yield break;
        }

        foreach (var target in instruction.BranchTargets)
        {
            if (instructions.ContainsKey(target))
            {
                yield return target;
            }
        }

        if (instruction.OpCode.FlowControl != FlowControl.Branch &&
            instructions.ContainsKey(instruction.NextOffset))
        {
            yield return instruction.NextOffset;
        }
    }

    private static bool IsAttachCall(MethodBase? method) =>
        method?.DeclaringType == typeof(IWorkerProcessServices) &&
        method.Name == nameof(IWorkerProcessServices.Attach);

    private static bool IsResumeCall(MethodBase? method) =>
        method?.DeclaringType == typeof(IWorkerProcessServices) &&
        method.Name == nameof(IWorkerProcessServices.Resume);

    private static bool SameMethod(MethodBase left, MethodBase right) =>
        left.Module == right.Module && left.MetadataToken == right.MetadataToken;

    private static bool CreationHandleWrappingFailuresTransferPreallocatedCapability(
        MethodBase method,
        string createMethodName = "CreateProcess",
        string capabilityTypeName = "CreatedProcessTerminationHandle",
        Type? transferExceptionType = null,
        bool requireProductionWrappers = true)
    {
        transferExceptionType ??= typeof(WorkerProcessCreationException);
        var body = method.GetMethodBody();
        var instructions = DecodeInstructions(method).ToArray();
        if (body is null || instructions.Length == 0)
        {
            return false;
        }

        var createProcess = instructions.SingleOrDefault(instruction =>
            instruction.CalledMethod?.Name == createMethodName);
        var capabilityConstructor = instructions.SingleOrDefault(instruction =>
            instruction.CalledMethod is ConstructorInfo &&
            instruction.CalledMethod.DeclaringType?.Name == capabilityTypeName);
        var transferConstructor = instructions.SingleOrDefault(instruction =>
            instruction.CalledMethod is ConstructorInfo &&
            instruction.CalledMethod.DeclaringType == transferExceptionType);
        if (createProcess.CalledMethod is null ||
            capabilityConstructor.CalledMethod is null ||
            transferConstructor.CalledMethod is null ||
            capabilityConstructor.Offset >= createProcess.Offset ||
            transferConstructor.Offset >= createProcess.Offset)
        {
            return false;
        }

        var ownRaw = instructions.SingleOrDefault(instruction =>
            instruction.CalledMethod?.DeclaringType?.Name == capabilityTypeName &&
            instruction.CalledMethod.Name == "OwnRaw");
        var transferCatch = body.ExceptionHandlingClauses
            .Cast<ExceptionHandlingClause>()
            .SingleOrDefault(clause =>
                clause.Flags == ExceptionHandlingClauseOptions.Clause &&
                ownRaw.CalledMethod is not null &&
                IsWithin(ownRaw.Offset, clause.TryOffset, clause.TryLength));
        if (transferCatch is null ||
            !instructions.Any(instruction =>
                IsWithin(instruction.Offset, transferCatch.HandlerOffset, transferCatch.HandlerLength) &&
                instruction.OpCode == OpCodes.Throw))
        {
            return false;
        }

        var handlerCalls = instructions
            .Where(instruction =>
                IsWithin(instruction.Offset, transferCatch.HandlerOffset, transferCatch.HandlerLength) &&
                instruction.CalledMethod is not null)
            .ToArray();
        var handlerAllocates = instructions.Any(instruction =>
            IsWithin(instruction.Offset, transferCatch.HandlerOffset, transferCatch.HandlerLength) &&
            instruction.OpCode is var opCode &&
            (opCode == OpCodes.Newarr || opCode == OpCodes.Box || opCode == OpCodes.Localloc));
        if (handlerCalls.Length != 0 || handlerAllocates)
        {
            // The handler may only load and throw the exception allocated
            // before CreateProcessW; allocation or cleanup here is forbidden.
            return false;
        }

        var byOffset = instructions.ToDictionary(instruction => instruction.Offset);
        if (!byOffset.TryGetValue(createProcess.NextOffset, out var createResultBranch) ||
            createResultBranch.OpCode.FlowControl != FlowControl.Cond_Branch ||
            !createResultBranch.BranchTargets
                .Append(createResultBranch.NextOffset)
                .Any(entry => ReachesThroughOnlyNops(
                    entry,
                    transferCatch.TryOffset,
                    byOffset)))
        {
            // The CreateProcess-success edge must enter the protected region
            // directly; no unowned, potentially-throwing gap is permitted.
            return false;
        }

        var firstProtectedCall = instructions.FirstOrDefault(instruction =>
            IsWithin(instruction.Offset, transferCatch.TryOffset, transferCatch.TryLength) &&
            instruction.CalledMethod is not null);
        if (firstProtectedCall.CalledMethod?.DeclaringType?.Name != capabilityTypeName ||
            firstProtectedCall.CalledMethod.Name != "OwnRaw")
        {
            return false;
        }

        var postCreateCallsAreCovered = instructions
            .Where(instruction =>
                instruction.Offset > createProcess.Offset &&
                instruction.CalledMethod is not null)
            .All(instruction =>
                IsWithin(instruction.Offset, transferCatch.TryOffset, transferCatch.TryLength) ||
                instruction.Offset < transferCatch.TryOffset &&
                (instruction.CalledMethod!.DeclaringType == typeof(Marshal) &&
                 instruction.CalledMethod.Name == nameof(Marshal.GetLastWin32Error) ||
                 instruction.CalledMethod is ConstructorInfo &&
                 instruction.CalledMethod.DeclaringType == typeof(Win32Exception)));
        if (!postCreateCallsAreCovered)
        {
            return false;
        }

        var protectedCallInstructions = instructions
            .Where(instruction =>
                IsWithin(instruction.Offset, transferCatch.TryOffset, transferCatch.TryLength) &&
                instruction.CalledMethod is not null)
            .ToArray();
        var orderedCapabilitySteps = new[]
            { "OwnRaw", "AdoptSafeProcessHandle", "AdoptSafeThreadHandle", "Disarm" }
            .Select(name => protectedCallInstructions.SingleOrDefault(instruction =>
                instruction.CalledMethod!.DeclaringType?.Name == capabilityTypeName &&
                instruction.CalledMethod.Name == name))
            .ToArray();
        if (orderedCapabilitySteps.Any(step => step.CalledMethod is null) ||
            !orderedCapabilitySteps
                .Select(step => step.Offset)
                .SequenceEqual(orderedCapabilitySteps.Select(step => step.Offset).Order()) ||
            !CapabilityStepsDominateEveryNormalTryExit(
                transferCatch,
                instructions,
                capabilityTypeName))
        {
            return false;
        }
        if (!requireProductionWrappers)
        {
            return true;
        }

        var safeProcessConstructor = protectedCallInstructions.SingleOrDefault(instruction =>
            instruction.CalledMethod is ConstructorInfo &&
            instruction.CalledMethod.DeclaringType == typeof(Microsoft.Win32.SafeHandles.SafeProcessHandle));
        var safeThreadConstructor = protectedCallInstructions.SingleOrDefault(instruction =>
            instruction.CalledMethod is ConstructorInfo &&
            instruction.CalledMethod.DeclaringType == typeof(Microsoft.Win32.SafeHandles.SafeWaitHandle));
        var suspendedConstructor = protectedCallInstructions.SingleOrDefault(instruction =>
            instruction.CalledMethod is ConstructorInfo &&
            instruction.CalledMethod.DeclaringType?.Name == "SuspendedWorkerProcessHandle");
        return safeProcessConstructor.CalledMethod is not null &&
               safeThreadConstructor.CalledMethod is not null &&
               suspendedConstructor.CalledMethod is not null &&
               orderedCapabilitySteps[0].Offset < safeProcessConstructor.Offset &&
               safeProcessConstructor.Offset < orderedCapabilitySteps[1].Offset &&
               orderedCapabilitySteps[1].Offset < safeThreadConstructor.Offset &&
               safeThreadConstructor.Offset < orderedCapabilitySteps[2].Offset &&
               orderedCapabilitySteps[2].Offset < suspendedConstructor.Offset &&
               suspendedConstructor.Offset < orderedCapabilitySteps[3].Offset;
    }

    private static bool CapabilityStepsDominateEveryNormalTryExit(
        ExceptionHandlingClause transferCatch,
        IReadOnlyList<IlInstruction> instructions,
        string capabilityTypeName)
    {
        string[] steps =
            ["OwnRaw", "AdoptSafeProcessHandle", "AdoptSafeThreadHandle", "Disarm"];
        var byOffset = instructions.ToDictionary(instruction => instruction.Offset);
        var pending = new Queue<(int Offset, int Stage)>();
        var visited = new HashSet<(int Offset, int Stage)>();
        pending.Enqueue((transferCatch.TryOffset, 0));
        while (pending.TryDequeue(out var state))
        {
            if (!visited.Add(state) || !byOffset.TryGetValue(state.Offset, out var instruction))
            {
                continue;
            }

            var stage = state.Stage;
            if (instruction.CalledMethod?.DeclaringType?.Name == capabilityTypeName &&
                Array.IndexOf(steps, instruction.CalledMethod.Name) is var stepIndex &&
                stepIndex >= 0)
            {
                if (stepIndex != stage)
                {
                    return false;
                }
                stage++;
            }

            if (instruction.OpCode.FlowControl == FlowControl.Throw)
            {
                continue;
            }
            var successors = Successors(instruction, byOffset).ToArray();
            if (instruction.OpCode.FlowControl == FlowControl.Return || successors.Length == 0)
            {
                if (stage != steps.Length)
                {
                    return false;
                }
                continue;
            }
            foreach (var successor in successors)
            {
                if (!IsWithin(successor, transferCatch.TryOffset, transferCatch.TryLength))
                {
                    if (stage != steps.Length)
                    {
                        return false;
                    }
                    continue;
                }
                pending.Enqueue((successor, stage));
            }
        }
        return true;
    }

    private static bool IsWithin(int offset, int start, int length) =>
        offset >= start && offset < start + length;

    private static bool ReachesThroughOnlyNops(
        int entry,
        int target,
        IReadOnlyDictionary<int, IlInstruction> instructions)
    {
        var offset = entry;
        while (offset != target)
        {
            if (!instructions.TryGetValue(offset, out var instruction) ||
                instruction.OpCode != OpCodes.Nop ||
                instruction.NextOffset <= offset)
            {
                return false;
            }
            offset = instruction.NextOffset;
        }
        return true;
    }

    private static string DescribeIl(MethodBase method)
    {
        var clauses = method.GetMethodBody()?.ExceptionHandlingClauses
            .Cast<ExceptionHandlingClause>()
            .Select(clause =>
                $"EH try={clause.TryOffset}+{clause.TryLength} handler={clause.HandlerOffset}+{clause.HandlerLength}") ?? [];
        return string.Join(
            Environment.NewLine,
            clauses.Concat(DecodeInstructions(method).Select(instruction =>
                $"IL_{instruction.Offset:X4} {instruction.OpCode.Name} next={instruction.NextOffset:X4} " +
                $"targets={string.Join(',', instruction.BranchTargets.Select(target => target.ToString("X4")))} " +
                $"call={instruction.CalledMethod?.DeclaringType?.Name}.{instruction.CalledMethod?.Name}")));
    }

    private static IEnumerable<MethodBase> ProductionCallGraph(MethodBase root)
    {
        var pending = new Stack<MethodBase>();
        var visited = new HashSet<MethodBase>();
        var targetAssembly = root.Module.Assembly;
        pending.Push(root);
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method))
            {
                continue;
            }

            yield return method;
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } asyncStateMachine)
            {
                var moveNext = asyncStateMachine.StateMachineType.GetMethod(
                    "MoveNext",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext is not null)
                {
                    pending.Push(moveNext);
                }
            }
            foreach (var called in CalledMethods(method))
            {
                if (called.DeclaringType?.Assembly == targetAssembly)
                {
                    pending.Push(called);
                }
            }
        }
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        foreach (var instruction in DecodeInstructions(method))
        {
            if (instruction.CalledMethod is not null)
            {
                yield return instruction.CalledMethod;
            }
        }
    }

    private static IEnumerable<IlInstruction> DecodeInstructions(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var index = 0; index < bytes.Length;)
        {
            var offset = index;
            ushort value = bytes[index++];
            if (value == 0xfe)
            {
                value = (ushort)(0xfe00 | bytes[index++]);
            }

            var opCode = OpCodeMap[value];
            MethodBase? calledMethod = null;
            IReadOnlyList<int> branchTargets = [];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, index);
                calledMethod = method.Module.ResolveMethod(token, typeArguments, methodArguments)!;
            }
            else if (opCode.OperandType == OperandType.ShortInlineBrTarget)
            {
                var nextOffset = index + 1;
                branchTargets = [nextOffset + unchecked((sbyte)bytes[index])];
            }
            else if (opCode.OperandType == OperandType.InlineBrTarget)
            {
                var nextOffset = index + 4;
                branchTargets = [nextOffset + BitConverter.ToInt32(bytes, index)];
            }
            else if (opCode.OperandType == OperandType.InlineSwitch)
            {
                var targetCount = BitConverter.ToInt32(bytes, index);
                var nextOffset = index + 4 + (targetCount * 4);
                var targets = new int[targetCount];
                for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
                {
                    targets[targetIndex] = nextOffset +
                        BitConverter.ToInt32(bytes, index + 4 + (targetIndex * 4));
                }
                branchTargets = targets;
            }

            index += OperandSize(opCode.OperandType, bytes, index);
            yield return new IlInstruction(
                offset,
                index,
                opCode,
                calledMethod,
                branchTargets);
        }
    }

    private readonly record struct IlInstruction(
        int Offset,
        int NextOffset,
        OpCode OpCode,
        MethodBase? CalledMethod,
        IReadOnlyList<int> BranchTargets);

    private static int OperandSize(OperandType operandType, byte[] bytes, int index) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or
                OperandType.InlineI or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, index) * 4),
            _ => throw new InvalidOperationException($"Unsupported IL operand: {operandType}"),
        };

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodeMap =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private static MetadataWorkerClient CreateClient(
        FakeProcessServices services,
        ConcurrentQueue<MetadataWorkerDiagnostic>? diagnostics = null,
        string? workerPath = null,
        int startupAttemptLimit = 3,
        TimeSpan? readinessTimeout = null,
        IWorkerStartupCoordinator? startupCoordinator = null,
        TimeSpan? coordinationTimeout = null,
        string? pipeName = null,
        TimeSpan? startupFailureCooldown = null) =>
        new(
            new MetadataWorkerClientOptions(
                workerPath ?? Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "worker.exe"),
                PipeName: pipeName,
                DiagnosticSink: diagnostics is null
                    ? null
                    : (diagnostic, _) =>
                    {
                        diagnostics.Enqueue(diagnostic);
                        return ValueTask.CompletedTask;
                    }),
            services,
            services.WorkerInstanceExists,
            services.ReadySignalExists,
            _ => true,
            new WorkerRecoverySettings(
                StartupAttemptLimit: startupAttemptLimit,
                StartupRetryDelay: TimeSpan.Zero,
                ReadinessTimeout: readinessTimeout ?? TimeSpan.FromMilliseconds(500),
                ReadinessPollInterval: TimeSpan.FromMilliseconds(5),
                StartupFailureCooldown: startupFailureCooldown ?? TimeSpan.FromSeconds(5),
                StartupCoordinationTimeout: coordinationTimeout ?? TimeSpan.FromSeconds(2)),
            startupCoordinator ?? new FakeStartupCoordinator());

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private sealed class FakeProcessServices : IWorkerProcessServices
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, DateTimeOffset> _resumedAt = [];
        private readonly HashSet<Guid> _publishedReady = [];

        internal List<string> Events { get; } = [];

        internal List<FakeProcessHandle> Handles { get; } = [];

        internal Exception? AttachError { get; set; }

        internal Exception? ResumeError { get; set; }

        internal Exception? TerminateError { get; set; }

        internal Exception? CreateFailureAfterHandle { get; set; }

        internal bool ExitOnTerminate { get; set; } = true;

        internal bool WaitForExitResult { get; set; } = true;

        internal ManualResetEventSlim? TerminateEntered { get; set; }

        internal ManualResetEventSlim? AllowTerminate { get; set; }

        internal bool PublishReadyAutomatically { get; set; } = true;

        internal TimeSpan ReadinessDelay { get; init; }

        internal int PrepareCalls { get; private set; }

        internal int CreateCalls { get; private set; }

        internal int AttachCalls { get; private set; }

        internal int ResumeCalls { get; private set; }

        public void PrepareIsolation()
        {
            lock (_gate)
            {
                PrepareCalls++;
                Events.Add("prepare");
            }
        }

        public IWorkerProcessHandle CreateSuspended(WorkerLaunchRequest request)
        {
            lock (_gate)
            {
                CreateCalls++;
                Events.Add("create");
                var handle = new FakeProcessHandle(
                    request.ReadyNonce,
                    request.PipeName,
                    Events,
                    _gate,
                    this);
                Handles.Add(handle);
                if (CreateFailureAfterHandle is not null)
                {
                    throw new WorkerProcessCreationException(handle, CreateFailureAfterHandle);
                }
                return handle;
            }
        }

        public void Attach(IWorkerProcessHandle process)
        {
            lock (_gate)
            {
                AttachCalls++;
                Events.Add("attach");
                if (AttachError is not null)
                {
                    ExceptionDispatchInfo.Capture(AttachError).Throw();
                }
            }
        }

        public void Resume(IWorkerProcessHandle process)
        {
            var handle = Assert.IsType<FakeProcessHandle>(process);
            lock (_gate)
            {
                ResumeCalls++;
                Events.Add("resume");
                if (ResumeError is not null)
                {
                    ExceptionDispatchInfo.Capture(ResumeError).Throw();
                }
                handle.Resumed = true;
                _resumedAt[handle.ReadyNonce] = DateTimeOffset.UtcNow;
            }
        }

        internal bool ReadySignalExists(Guid nonce)
        {
            lock (_gate)
            {
                return ReadySignalExistsUnsafe(nonce);
            }
        }

        internal bool WorkerInstanceExists(string pipeName)
        {
            var pipeKey = WorkerPipeIdentity.Canonicalize(pipeName);
            lock (_gate)
            {
                return Handles.Any(handle =>
                    WorkerPipeIdentity.Canonicalize(handle.PipeName) == pipeKey &&
                    handle.Resumed &&
                    !handle.Exited &&
                    ReadySignalExistsUnsafe(handle.ReadyNonce));
            }
        }

        private bool ReadySignalExistsUnsafe(Guid nonce)
        {
            if (_publishedReady.Contains(nonce))
            {
                return true;
            }
            return PublishReadyAutomatically &&
                   _resumedAt.TryGetValue(nonce, out var resumedAt) &&
                   DateTimeOffset.UtcNow - resumedAt >= ReadinessDelay;
        }

        internal void PublishReady()
        {
            lock (_gate)
            {
                foreach (var nonce in _resumedAt.Keys)
                {
                    _publishedReady.Add(nonce);
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStartupCoordinator : IWorkerStartupCoordinator
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _observedPipeKeys =
            new(StringComparer.Ordinal);
        private int _acquireCalls;
        private int _currentHolders;
        private int _maximumConcurrentHolders;
        private int _disposeCalls;
        private int _disposeFailuresRemaining;

        internal int AcquireCalls => Volatile.Read(ref _acquireCalls);

        internal int CurrentHolders => Volatile.Read(ref _currentHolders);

        internal int MaximumConcurrentHolders => Volatile.Read(ref _maximumConcurrentHolders);

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal int DisposeFailuresRemaining
        {
            get => Volatile.Read(ref _disposeFailuresRemaining);
            set => Volatile.Write(ref _disposeFailuresRemaining, value);
        }

        internal IReadOnlyCollection<string> ObservedPipeKeys => _observedPipeKeys.Keys.ToArray();

        public async ValueTask<IWorkerStartupLease?> AcquireAsync(
            string pipeName,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acquireCalls);
            var pipeKey = WorkerPipeIdentity.Canonicalize(pipeName);
            _observedPipeKeys.TryAdd(pipeKey, 0);
            var gate = _gates.GetOrAdd(pipeKey, static _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(timeout, cancellationToken))
            {
                return null;
            }

            var holders = Interlocked.Increment(ref _currentHolders);
            var observedMaximum = Volatile.Read(ref _maximumConcurrentHolders);
            while (holders > observedMaximum)
            {
                var prior = Interlocked.CompareExchange(
                    ref _maximumConcurrentHolders,
                    holders,
                    observedMaximum);
                if (prior == observedMaximum)
                {
                    break;
                }
                observedMaximum = prior;
            }

            return new FakeStartupLease(this, gate);
        }

        private void Release(SemaphoreSlim gate)
        {
            Interlocked.Increment(ref _disposeCalls);
            while (true)
            {
                var remaining = Volatile.Read(ref _disposeFailuresRemaining);
                if (remaining <= 0)
                {
                    break;
                }
                if (Interlocked.CompareExchange(
                        ref _disposeFailuresRemaining,
                        remaining - 1,
                        remaining) == remaining)
                {
                    throw new IOException("synthetic startup lease release failure");
                }
            }

            Interlocked.Decrement(ref _currentHolders);
            gate.Release();
        }

        private sealed class FakeStartupLease(
            FakeStartupCoordinator owner,
            SemaphoreSlim gate) : IWorkerStartupLease
        {
            private readonly object _leaseGate = new();
            private bool _released;

            public ValueTask DisposeAsync()
            {
                lock (_leaseGate)
                {
                    if (_released)
                    {
                        return ValueTask.CompletedTask;
                    }

                    owner.Release(gate);
                    _released = true;
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeProcessHandle(
        Guid readyNonce,
        string pipeName,
        List<string> events,
        object gate,
        FakeProcessServices services) : IWorkerProcessHandle
    {
        internal Guid ReadyNonce { get; } = readyNonce;

        internal string PipeName { get; } = pipeName;

        internal bool Exited { get; set; }

        internal bool Resumed { get; set; }

        internal bool Terminated { get; private set; }

        internal bool Disposed { get; private set; }

        internal int TerminateCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public bool HasExited => Exited;

        public void Terminate()
        {
            lock (gate)
            {
                events.Add("terminate");
                TerminateCalls++;
                services.TerminateEntered?.Set();
                if (services.AllowTerminate is { } release &&
                    !release.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("synthetic terminate release timed out");
                }
                if (services.TerminateError is not null)
                {
                    ExceptionDispatchInfo.Capture(services.TerminateError).Throw();
                }
                Terminated = true;
                Exited = services.ExitOnTerminate;
            }
        }

        public bool WaitForExit(int milliseconds) => services.WaitForExitResult;

        public void Dispose()
        {
            lock (gate)
            {
                events.Add("dispose");
                DisposeCalls++;
                Disposed = true;
            }
        }
    }
}
