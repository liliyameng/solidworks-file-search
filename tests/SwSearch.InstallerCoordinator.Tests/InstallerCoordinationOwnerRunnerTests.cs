using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallerCoordinationOwnerRunnerTests
{
    private static readonly InstallerCoordinatorCommand Command = new(
        InstallerCoordinatorMode.PrivilegedHandle,
        "{D9158356-29B3-44D6-A15A-ACDCE8328703}",
        @"C:\Program Files\SwSearch",
        @"C:\Temp\coordinator.log",
        "S-1-5-21-111111111-222222222-333333333-1001",
        Guid.Parse("34A7C1DF-4BF1-4EB6-8E7E-E2B5F3B52CD4"));

    [Fact]
    public async Task AsyncPhaseCompletesAwayFromOwner_ButLeaseReleasesOnAcquiringThread()
    {
        var mutex = new ThreadAffineMutexFactory();
        var watchdog = new RecordingWatchdogFactory();
        var runner = new InstallerCoordinationOwnerRunner(mutex, watchdog, new MemoryLog());
        var phaseCompletionThread = 0;

        var outcome = await runner.RunExclusiveAsync(
            Command,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            async cancellationToken =>
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                phaseCompletionThread = Environment.CurrentManagedThreadId;
                return new InstallerCoordinatorPhaseResult("async-success", []);
            });

        Assert.Equal(InstallerCoordinationOwnerRunStatus.Completed, outcome.Status);
        Assert.NotEqual(0, mutex.AcquireThreadId);
        Assert.Equal(mutex.AcquireThreadId, mutex.DisposeThreadId);
        Assert.NotEqual(mutex.AcquireThreadId, phaseCompletionThread);
        Assert.Equal(1, watchdog.Armed);
        Assert.Equal(1, watchdog.Disarmed);
    }

    [Fact]
    public async Task DeadlineCancelsPhase_ButOwnerDoesNotReleaseUntilPhaseConfirmsCompletion()
    {
        var mutex = new ThreadAffineMutexFactory();
        var runner = new InstallerCoordinationOwnerRunner(
            mutex,
            new RecordingWatchdogFactory(),
            new MemoryLog());
        var cancellationObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPhaseToFinish = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var run = runner.RunExclusiveAsync(
            Command,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromSeconds(2),
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    await allowPhaseToFinish.Task.ConfigureAwait(false);
                    throw;
                }

                throw new InvalidOperationException("Unreachable fixture path.");
            });

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        Assert.Equal(0, mutex.DisposeCalls);

        allowPhaseToFinish.TrySetResult(true);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(InstallerCoordinationOwnerRunStatus.PhaseTimedOut, outcome.Status);
        Assert.Equal(1, mutex.DisposeCalls);
        Assert.Equal(mutex.AcquireThreadId, mutex.DisposeThreadId);
    }

    [Fact]
    public async Task ExecutorExceptionAndIndependentCancellationMapExplicitlyAndAlwaysRelease()
    {
        var exceptionMutex = new ThreadAffineMutexFactory();
        var exceptionRunner = new InstallerCoordinationOwnerRunner(
            exceptionMutex,
            new RecordingWatchdogFactory(),
            new MemoryLog());
        var faulted = await exceptionRunner.RunExclusiveAsync(
            Command,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            _ => throw new InvalidOperationException("fixture-fault"));

        Assert.Equal(InstallerCoordinationOwnerRunStatus.PhaseFaulted, faulted.Status);
        Assert.IsType<InvalidOperationException>(faulted.Exception);
        Assert.Equal(exceptionMutex.AcquireThreadId, exceptionMutex.DisposeThreadId);

        var canceledMutex = new ThreadAffineMutexFactory();
        var canceledRunner = new InstallerCoordinationOwnerRunner(
            canceledMutex,
            new RecordingWatchdogFactory(),
            new MemoryLog());
        var canceled = await canceledRunner.RunExclusiveAsync(
            Command,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            _ => ValueTask.FromCanceled<InstallerCoordinatorPhaseResult>(new CancellationToken(canceled: true)));

        Assert.Equal(InstallerCoordinationOwnerRunStatus.PhaseCanceled, canceled.Status);
        Assert.IsType<TaskCanceledException>(canceled.Exception);
        Assert.Equal(canceledMutex.AcquireThreadId, canceledMutex.DisposeThreadId);
    }

    [Fact]
    public async Task TotalDeadlineStartsBeforeMutexWait_AndOnlyRemainingTimeReachesPhase()
    {
        var mutex = new ThreadAffineMutexFactory { AcquisitionDelay = TimeSpan.FromMilliseconds(60) };
        var runner = new InstallerCoordinationOwnerRunner(
            mutex,
            new RecordingWatchdogFactory(),
            new MemoryLog());
        var observedRemaining = TimeSpan.Zero;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await runner.RunExclusiveAsync(
            Command,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1),
            async cancellationToken =>
            {
                var phaseStarted = started.Elapsed;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    observedRemaining = started.Elapsed - phaseStarted;
                    throw;
                }

                throw new InvalidOperationException("Unreachable fixture path.");
            });

        Assert.Equal(InstallerCoordinationOwnerRunStatus.PhaseTimedOut, outcome.Status);
        Assert.InRange(observedRemaining, TimeSpan.Zero, TimeSpan.FromMilliseconds(80));
        Assert.Equal(mutex.AcquireThreadId, mutex.DisposeThreadId);
    }

    private sealed class ThreadAffineMutexFactory : IInstallerCoordinationMutexFactory
    {
        internal TimeSpan AcquisitionDelay { get; set; }
        internal int AcquireThreadId { get; private set; }
        internal int DisposeThreadId { get; private set; }
        internal int DisposeCalls { get; private set; }

        public InstallerCoordinationLockAcquisition TryAcquire(
            string upgradeCode,
            string installUserSid,
            TimeSpan timeout,
            ICoordinatorLog log)
        {
            if (AcquisitionDelay > TimeSpan.Zero)
            {
                Thread.Sleep(AcquisitionDelay);
            }

            AcquireThreadId = Environment.CurrentManagedThreadId;
            return new InstallerCoordinationLockAcquisition(
                InstallerCoordinationLockStatus.Acquired,
                new ThreadAffineLease(this, AcquireThreadId));
        }

        private sealed class ThreadAffineLease(
            ThreadAffineMutexFactory owner,
            int acquireThreadId) : IInstallerCoordinationLease
        {
            public void ReleaseOnOwnerThread()
            {
                owner.DisposeThreadId = Environment.CurrentManagedThreadId;
                owner.DisposeCalls++;
                if (owner.DisposeThreadId != acquireThreadId)
                {
                    throw new SynchronizationLockException(
                        $"Lease acquired on {acquireThreadId} and released on {owner.DisposeThreadId}.");
                }
            }

            public void Dispose() => ReleaseOnOwnerThread();
        }
    }

    private sealed class RecordingWatchdogFactory : IInstallerCoordinatorHardDeadlineWatchdogFactory
    {
        internal int Armed { get; private set; }
        internal int Disarmed { get; private set; }

        public IInstallerCoordinatorHardDeadlineWatchdog Arm(
            long coordinationStartedTimestamp,
            TimeSpan hardTimeout,
            InstallerCoordinatorCommand command)
        {
            Armed++;
            return new RecordingWatchdog(this);
        }

        private sealed class RecordingWatchdog(RecordingWatchdogFactory owner)
            : IInstallerCoordinatorHardDeadlineWatchdog
        {
            public void Dispose() => owner.Disarmed++;
        }
    }

    private sealed class MemoryLog : ICoordinatorLog
    {
        public void Write(string message)
        {
        }
    }
}
