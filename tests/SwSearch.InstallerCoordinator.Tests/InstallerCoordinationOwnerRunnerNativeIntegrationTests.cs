using SwSearch.InstallerCoordinator;
using System.Security.Principal;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

[Trait("Category", "WindowsNativeMutexOwnerRunnerGate")]
public sealed class InstallerCoordinationOwnerRunnerNativeIntegrationTests
{
    [Fact]
    public async Task ProductionHost_ActualGlobalMutex_AsyncSuccessReleasesAndReacquires()
    {
        var fixture = CreateFixture(ExecutorBehavior.AsyncSuccess);

        var exitCode = await fixture.Host.RunAsync(fixture.Command);

        Assert.Equal((int)InstallerCoordinatorExitCode.Success, exitCode);
        AssertReacquirable(fixture.Command);
    }

    [Fact]
    public async Task ProductionHost_ActualGlobalMutex_ConcurrentOwnerTimesOutFailSafeThenReacquires()
    {
        var fixture = CreateFixture(ExecutorBehavior.AsyncSuccess, mutexTimeout: TimeSpan.FromMilliseconds(60));
        using var ownerReady = new ManualResetEventSlim(false);
        using var releaseOwner = new ManualResetEventSlim(false);
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                var acquisition = InstallerCoordinationMutex.TryAcquire(
                    fixture.Command.UpgradeCode,
                    fixture.Command.InstallUserSid,
                    TimeSpan.FromSeconds(1),
                    fixture.Log);
                if (acquisition.Status != InstallerCoordinationLockStatus.Acquired || acquisition.Lease is null)
                {
                    throw new InvalidOperationException($"Fixture owner failed: {acquisition.Status}.");
                }

                ownerReady.Set();
                if (!releaseOwner.Wait(TimeSpan.FromSeconds(3)))
                {
                    throw new TimeoutException("Fixture owner release signal timed out.");
                }

                acquisition.Lease.ReleaseOnOwnerThread();
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
                ownerReady.Set();
            }
        });
        owner.Start();
        Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(2)));

        var exitCode = await fixture.Host.RunAsync(fixture.Command);
        Assert.Equal((int)InstallerCoordinatorExitCode.TemporaryFailure, exitCode);
        Assert.Equal(0, fixture.Executor.Calls);

        releaseOwner.Set();
        Assert.True(owner.Join(2_000));
        Assert.Null(ownerFailure);
        AssertReacquirable(fixture.Command);
    }

    [Fact]
    public async Task ProductionHost_ActualGlobalMutex_ExecutorExceptionReleasesAndReacquires()
    {
        var fixture = CreateFixture(ExecutorBehavior.Fault);

        var exitCode = await fixture.Host.RunAsync(fixture.Command);

        Assert.Equal((int)InstallerCoordinatorExitCode.SoftwareFailure, exitCode);
        AssertReacquirable(fixture.Command);
    }

    [Fact]
    public async Task ProductionHost_ActualGlobalMutex_IndependentCancellationReleasesAndReacquires()
    {
        var fixture = CreateFixture(ExecutorBehavior.IndependentCancellation);

        var exitCode = await fixture.Host.RunAsync(fixture.Command);

        Assert.Equal((int)InstallerCoordinatorExitCode.SoftwareFailure, exitCode);
        AssertReacquirable(fixture.Command);
    }

    [Fact]
    public async Task ProductionHost_ActualGlobalMutex_CooperativeTotalTimeoutReleasesAndReacquires()
    {
        var fixture = CreateFixture(
            ExecutorBehavior.CooperativeTimeout,
            phaseTimeout: TimeSpan.FromMilliseconds(80));

        var exitCode = await fixture.Host.RunAsync(fixture.Command);

        Assert.Equal((int)InstallerCoordinatorExitCode.SoftwareFailure, exitCode);
        Assert.True(fixture.Executor.CancellationObserved);
        AssertReacquirable(fixture.Command);
    }

    private static NativeFixture CreateFixture(
        ExecutorBehavior behavior,
        TimeSpan? mutexTimeout = null,
        TimeSpan? phaseTimeout = null)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var installUserSid = identity.User?.Value;
        if (identity.IsSystem || string.IsNullOrWhiteSpace(installUserSid) || !identity.User!.IsAccountSid())
        {
            throw new InvalidOperationException(
                "P1_MSI_MUTEX_OWNER_RUNNER_ENVIRONMENT_BLOCKED: gate requires one non-SYSTEM account token.");
        }

        var command = new InstallerCoordinatorCommand(
            InstallerCoordinatorMode.PrivilegedHandle,
            Guid.NewGuid().ToString("B"),
            @"C:\Program Files\SwSearch",
            @"C:\Temp\coordinator-owner-runner-gate.log",
            installUserSid,
            Guid.NewGuid());
        var log = new MemoryLog();
        var executor = new AsyncFakeExecutor(behavior);
        var runner = new InstallerCoordinationOwnerRunner(
            new SystemInstallerCoordinationMutexFactory(),
            new RecordingWatchdogFactory(),
            log);
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity("S-1-5-18", true),
            runner,
            executor,
            log,
            privilegedPhaseTimeout: phaseTimeout ?? TimeSpan.FromSeconds(1),
            mutexTimeout: mutexTimeout ?? TimeSpan.FromMilliseconds(500),
            hardStopGrace: TimeSpan.FromSeconds(1));
        return new NativeFixture(command, host, executor, log);
    }

    private static void AssertReacquirable(InstallerCoordinatorCommand command)
    {
        var reacquired = InstallerCoordinationMutex.TryAcquire(
            command.UpgradeCode,
            command.InstallUserSid,
            TimeSpan.FromMilliseconds(500),
            new MemoryLog());
        Assert.Equal(InstallerCoordinationLockStatus.Acquired, reacquired.Status);
        Assert.NotNull(reacquired.Lease);
        reacquired.Lease!.ReleaseOnOwnerThread();
    }

    private enum ExecutorBehavior
    {
        AsyncSuccess,
        Fault,
        IndependentCancellation,
        CooperativeTimeout
    }

    private sealed class AsyncFakeExecutor(ExecutorBehavior behavior) : IInstallerCoordinatorPhaseExecutor
    {
        internal int Calls { get; private set; }
        internal bool CancellationObserved { get; private set; }

        public ValueTask<InstallerCoordinatorPhaseResult> ExecuteUserIpcAsync(
            InstallerCoordinatorCommand command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Privileged owner-runner fixture reached user IPC executor.");

        public async ValueTask<InstallerCoordinatorPhaseResult> ExecutePrivilegedHandleAsync(
            InstallerCoordinatorCommand command,
            CancellationToken cancellationToken)
        {
            Calls++;
            switch (behavior)
            {
                case ExecutorBehavior.AsyncSuccess:
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    return new InstallerCoordinatorPhaseResult("success", []);
                case ExecutorBehavior.Fault:
                    await Task.Yield();
                    throw new InvalidOperationException("native-owner-runner-fixture-fault");
                case ExecutorBehavior.IndependentCancellation:
                    await Task.Yield();
                    throw new OperationCanceledException(new CancellationToken(canceled: true));
                case ExecutorBehavior.CooperativeTimeout:
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        CancellationObserved = true;
                        throw;
                    }

                    throw new InvalidOperationException("Unreachable timeout fixture path.");
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private sealed class RecordingWatchdogFactory : IInstallerCoordinatorHardDeadlineWatchdogFactory
    {
        public IInstallerCoordinatorHardDeadlineWatchdog Arm(
            long coordinationStartedTimestamp,
            TimeSpan hardTimeout,
            InstallerCoordinatorCommand command) => new Watchdog();

        private sealed class Watchdog : IInstallerCoordinatorHardDeadlineWatchdog
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class MemoryLog : ICoordinatorLog
    {
        internal List<string> Entries { get; } = [];
        public void Write(string message) => Entries.Add(message);
    }

    private sealed record NativeFixture(
        InstallerCoordinatorCommand Command,
        InstallerCoordinatorHost Host,
        AsyncFakeExecutor Executor,
        MemoryLog Log);
}
