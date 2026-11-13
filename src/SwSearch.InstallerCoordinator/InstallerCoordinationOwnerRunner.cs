using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwSearch.InstallerCoordinator;

internal enum InstallerCoordinationOwnerRunStatus
{
    Completed,
    LockUnavailable,
    PhaseTimedOut,
    PhaseCanceled,
    PhaseFaulted,
    WatchdogUnavailable
}

internal sealed record InstallerCoordinationOwnerRunResult(
    InstallerCoordinationOwnerRunStatus Status,
    InstallerCoordinatorPhaseResult? PhaseResult = null,
    InstallerCoordinationLockStatus? LockStatus = null,
    string? Detail = null,
    Exception? Exception = null);

internal interface IInstallerCoordinationOwnerRunner
{
    Task<InstallerCoordinationOwnerRunResult> RunExclusiveAsync(
        InstallerCoordinatorCommand command,
        TimeSpan mutexTimeout,
        TimeSpan totalPhaseTimeout,
        TimeSpan hardStopGrace,
        Func<CancellationToken, ValueTask<InstallerCoordinatorPhaseResult>> phase);
}

internal interface IInstallerCoordinatorHardDeadlineWatchdogFactory
{
    IInstallerCoordinatorHardDeadlineWatchdog Arm(
        long coordinationStartedTimestamp,
        TimeSpan hardTimeout,
        InstallerCoordinatorCommand command);
}

internal interface IInstallerCoordinatorHardDeadlineWatchdog : IDisposable
{
}

/// <summary>
/// Owns the thread-affine named-mutex lease entirely on one dedicated thread.
/// RunOwnerScope is intentionally synchronous and must never become async: it
/// acquires, waits synchronously for the phase Task to confirm completion, and
/// releases in its finally block on that same thread.
/// </summary>
internal sealed class InstallerCoordinationOwnerRunner : IInstallerCoordinationOwnerRunner
{
    private readonly IInstallerCoordinationMutexFactory _mutexFactory;
    private readonly IInstallerCoordinatorHardDeadlineWatchdogFactory _watchdogFactory;
    private readonly ICoordinatorLog _log;

    internal InstallerCoordinationOwnerRunner(
        IInstallerCoordinationMutexFactory mutexFactory,
        IInstallerCoordinatorHardDeadlineWatchdogFactory watchdogFactory,
        ICoordinatorLog log)
    {
        _mutexFactory = mutexFactory;
        _watchdogFactory = watchdogFactory;
        _log = log;
    }

    public Task<InstallerCoordinationOwnerRunResult> RunExclusiveAsync(
        InstallerCoordinatorCommand command,
        TimeSpan mutexTimeout,
        TimeSpan totalPhaseTimeout,
        TimeSpan hardStopGrace,
        Func<CancellationToken, ValueTask<InstallerCoordinatorPhaseResult>> phase)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(phase);
        ValidatePositiveTimeout(mutexTimeout, nameof(mutexTimeout));
        ValidatePositiveTimeout(totalPhaseTimeout, nameof(totalPhaseTimeout));
        ValidatePositiveTimeout(hardStopGrace, nameof(hardStopGrace));

        // LongRunning gives this ownership scope a dedicated thread with no
        // SynchronizationContext. The returned Task never owns or exposes the
        // lease; it only observes the completed owner-scope result.
        return Task.Factory.StartNew(
            () => RunOwnerScope(
                command,
                mutexTimeout,
                totalPhaseTimeout,
                hardStopGrace,
                phase),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private InstallerCoordinationOwnerRunResult RunOwnerScope(
        InstallerCoordinatorCommand command,
        TimeSpan mutexTimeout,
        TimeSpan totalPhaseTimeout,
        TimeSpan hardStopGrace,
        Func<CancellationToken, ValueTask<InstallerCoordinatorPhaseResult>> phase)
    {
        // The soft deadline is established before the mutex wait. The phase
        // receives only what remains after acquisition. A non-cooperative phase
        // is never detached: the process watchdog ends this coordinator process,
        // allowing Windows to close the handle and mark the mutex abandoned.
        var started = Stopwatch.GetTimestamp();
        var hardTimeout = totalPhaseTimeout + hardStopGrace;
        IInstallerCoordinatorHardDeadlineWatchdog watchdog;
        try
        {
            watchdog = _watchdogFactory.Arm(started, hardTimeout, command);
            _log.Write(
                $"coordination-watchdog=armed mode={command.Mode} hard-timeout-ms={hardTimeout.TotalMilliseconds:0}");
        }
        catch (Exception exception)
        {
            return new InstallerCoordinationOwnerRunResult(
                InstallerCoordinationOwnerRunStatus.WatchdogUnavailable,
                Detail: exception.Message,
                Exception: exception);
        }

        using (watchdog)
        {
            var remainingBeforeMutex = Remaining(started, totalPhaseTimeout);
            if (remainingBeforeMutex <= TimeSpan.Zero)
            {
                return new InstallerCoordinationOwnerRunResult(
                    InstallerCoordinationOwnerRunStatus.PhaseTimedOut,
                    Detail: "Total coordination deadline elapsed before mutex acquisition.");
            }

            var boundedMutexTimeout = mutexTimeout <= remainingBeforeMutex
                ? mutexTimeout
                : remainingBeforeMutex;
            var acquisition = _mutexFactory.TryAcquire(
                command.UpgradeCode,
                command.InstallUserSid,
                boundedMutexTimeout,
                _log);
            if (acquisition.Status != InstallerCoordinationLockStatus.Acquired || acquisition.Lease is null)
            {
                return new InstallerCoordinationOwnerRunResult(
                    InstallerCoordinationOwnerRunStatus.LockUnavailable,
                    LockStatus: acquisition.Status,
                    Detail: acquisition.Detail);
            }

            var lease = acquisition.Lease;
            try
            {
                var remainingForPhase = Remaining(started, totalPhaseTimeout);
                if (remainingForPhase <= TimeSpan.Zero)
                {
                    return new InstallerCoordinationOwnerRunResult(
                        InstallerCoordinationOwnerRunStatus.PhaseTimedOut,
                        Detail: "Total coordination deadline elapsed during mutex acquisition.");
                }

                using var phaseCancellation = new CancellationTokenSource(remainingForPhase);
                try
                {
                    var phaseTask = phase(phaseCancellation.Token).AsTask();

                    // This synchronous wait is intentional and confined to the
                    // dedicated owner thread. The phase's async I/O continues on
                    // the thread pool because this thread has no SynchronizationContext.
                    var result = phaseTask.GetAwaiter().GetResult();
                    return phaseCancellation.IsCancellationRequested
                        ? new InstallerCoordinationOwnerRunResult(
                            InstallerCoordinationOwnerRunStatus.PhaseTimedOut,
                            Detail: "Phase completed only after its total deadline was canceled.")
                        : new InstallerCoordinationOwnerRunResult(
                            InstallerCoordinationOwnerRunStatus.Completed,
                            PhaseResult: result);
                }
                catch (OperationCanceledException exception) when (phaseCancellation.IsCancellationRequested)
                {
                    return new InstallerCoordinationOwnerRunResult(
                        InstallerCoordinationOwnerRunStatus.PhaseTimedOut,
                        Detail: exception.Message,
                        Exception: exception);
                }
                catch (OperationCanceledException exception)
                {
                    return new InstallerCoordinationOwnerRunResult(
                        InstallerCoordinationOwnerRunStatus.PhaseCanceled,
                        Detail: exception.Message,
                        Exception: exception);
                }
                catch (Exception exception)
                {
                    return new InstallerCoordinationOwnerRunResult(
                        InstallerCoordinationOwnerRunStatus.PhaseFaulted,
                        Detail: exception.Message,
                        Exception: exception);
                }
            }
            finally
            {
                // Never move this release outside RunOwnerScope or beyond an
                // await. Release failures are intentionally not swallowed.
                lease.ReleaseOnOwnerThread();
            }
        }
    }

    private static TimeSpan Remaining(long started, TimeSpan totalTimeout)
    {
        var remaining = totalTimeout - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void ValidatePositiveTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and positive.");
        }
    }
}

/// <summary>
/// Last-resort containment for a phase that ignores cancellation. It terminates
/// only this installer-coordinator process; it never opens or terminates a
/// product process. Normal cooperative paths disarm it after the mutex has been
/// released by the owner thread.
/// </summary>
internal sealed class SelfProcessHardDeadlineWatchdogFactory : IInstallerCoordinatorHardDeadlineWatchdogFactory
{
    public IInstallerCoordinatorHardDeadlineWatchdog Arm(
        long coordinationStartedTimestamp,
        TimeSpan hardTimeout,
        InstallerCoordinatorCommand command) =>
        new SelfProcessHardDeadlineWatchdog(coordinationStartedTimestamp, hardTimeout);

    private sealed class SelfProcessHardDeadlineWatchdog : IInstallerCoordinatorHardDeadlineWatchdog
    {
        private const int Armed = 0;
        private const int Disarmed = 1;
        private const int Firing = 2;
        private static readonly TimeSpan DisarmJoinLimit = TimeSpan.FromMilliseconds(250);
        private readonly ManualResetEventSlim _disarmed = new(initialState: false);
        private readonly Thread _thread;
        private int _state = Armed;
        private int _signalDisposed;

        internal SelfProcessHardDeadlineWatchdog(
            long coordinationStartedTimestamp,
            TimeSpan hardTimeout)
        {
            _thread = new Thread(() => Watch(coordinationStartedTimestamp, hardTimeout))
            {
                IsBackground = true,
                Name = "SwSearch installer hard-deadline watchdog"
            };
            _thread.Start();
        }

        public void Dispose()
        {
            var previous = Interlocked.CompareExchange(ref _state, Disarmed, Armed);
            if (previous == Firing)
            {
                // The firing path owns process termination now. Never wait for,
                // log ahead of, or otherwise obstruct that critical path.
                return;
            }

            if (previous == Armed)
            {
                _disarmed.Set();
            }

            // Disarm won the CAS, so firing can no longer start. Joining is only
            // cleanup and is strictly bounded; if the scheduler has not retired
            // the watcher yet, its small signal object remains valid until the
            // short-lived coordinator process exits.
            if (_thread.Join(DisarmJoinLimit) &&
                Interlocked.Exchange(ref _signalDisposed, 1) == 0)
            {
                _disarmed.Dispose();
            }
        }

        private void Watch(
            long coordinationStartedTimestamp,
            TimeSpan hardTimeout)
        {
            var remaining = hardTimeout - Stopwatch.GetElapsedTime(coordinationStartedTimestamp);
            if (remaining > TimeSpan.Zero && _disarmed.Wait(remaining))
            {
                return;
            }

            TryFireHardDeadline();
        }

        // This method is the audited firing entry. Once its CAS wins it performs
        // no managed I/O, logging, pipe access, monitor acquisition, wait, or
        // join before attempting to terminate this coordinator process itself.
        private void TryFireHardDeadline()
        {
            if (Interlocked.CompareExchange(ref _state, Firing, Armed) != Armed)
            {
                return;
            }

            if (!TerminateProcess(GetCurrentProcess(), (uint)InstallerCoordinatorExitCode.SoftwareFailure))
            {
                Environment.FailFast(
                    "Unable to terminate the non-cooperative SwSearch installer coordinator process.");
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
