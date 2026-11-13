using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallerCoordinatorHostTests
{
    private const string UpgradeCode = "{D9158356-29B3-44D6-A15A-ACDCE8328703}";
    private const string InstallUserSid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const int InstallSessionId = 7;
    private static readonly Guid Nonce = Guid.Parse("34A7C1DF-4BF1-4EB6-8E7E-E2B5F3B52CD4");

    [Theory]
    [InlineData("--user-ipc", 0)]
    [InlineData("--privileged-handle", 1)]
    public void CommandLine_AcceptsExactlyOneExplicitMode(
        string mode,
        int expected)
    {
        var parsed = InstallerCoordinatorCommandLine.TryParse(
            ValidArguments(mode),
            out var command,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(command);
        Assert.Equal((InstallerCoordinatorMode)expected, command.Mode);
        Assert.Equal(UpgradeCode, command.UpgradeCode);
        Assert.Equal(InstallUserSid, command.InstallUserSid);
        Assert.Equal(InstallSessionId, command.InstallSessionId);
        Assert.Equal(Nonce, command.InstanceNonce);
    }

    [Fact]
    public void CommandLine_MissingDuplicateMixedAndUnknownModesFailClosed()
    {
        var invalid = new[]
        {
            ValidArguments(null),
            ValidArguments("--user-ipc").Concat(["--privileged-handle"]).ToArray(),
            ValidArguments("--user-ipc").Concat(["--user-ipc"]).ToArray(),
            ValidArguments("--user-ipc").Concat(["--coordinate"]).ToArray(),
            ValidArguments("--user-ipc").Concat(["--log", @"C:\duplicate.log"]).ToArray(),
            ValidArguments("--user-ipc").Where(value => value != "--instance-nonce" && value != Nonce.ToString("D")).ToArray()
        };

        foreach (var arguments in invalid)
        {
            Assert.False(InstallerCoordinatorCommandLine.TryParse(arguments, out var command, out _));
            Assert.Null(command);
        }
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-1-0")]
    [InlineData("not-a-sid")]
    public void CommandLine_NonIndividualInstallUserSidFailsClosed(string sid)
    {
        var arguments = ReplaceArgument(ValidArguments("--user-ipc"), "--install-user-sid", sid);

        Assert.False(InstallerCoordinatorCommandLine.TryParse(arguments, out _, out _));
    }

    [Fact]
    public async Task UserMode_UsesOnlyUserExecutorAndPassesExactGlobalLockIdentity()
    {
        var command = Command(InstallerCoordinatorMode.UserIpc);
        var mutex = new FakeMutexFactory();
        var executor = new FakeExecutor();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity(InstallUserSid, false, InstallSessionId),
            OwnerRunner(mutex),
            executor,
            new MemoryLog());

        var exitCode = await host.RunAsync(command);

        Assert.Equal((int)InstallerCoordinatorExitCode.Success, exitCode);
        Assert.Equal(1, executor.UserCalls);
        Assert.Equal(0, executor.PrivilegedCalls);
        Assert.Equal(UpgradeCode, mutex.UpgradeCode);
        Assert.Equal(InstallUserSid, mutex.InstallUserSid);
    }

    [Fact]
    public async Task PrivilegedMode_UsesOnlyStrictHandleExecutorAndSameGlobalLockIdentity()
    {
        var command = Command(InstallerCoordinatorMode.PrivilegedHandle);
        var mutex = new FakeMutexFactory();
        var executor = new FakeExecutor();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity("S-1-5-18", true),
            OwnerRunner(mutex),
            executor,
            new MemoryLog());

        var exitCode = await host.RunAsync(command);

        Assert.Equal((int)InstallerCoordinatorExitCode.Success, exitCode);
        Assert.Equal(0, executor.UserCalls);
        Assert.Equal(1, executor.PrivilegedCalls);
        Assert.Equal(UpgradeCode, mutex.UpgradeCode);
        Assert.Equal(InstallUserSid, mutex.InstallUserSid);
    }

    [Theory]
    [InlineData(0, "S-1-5-18", true)]
    [InlineData(0, "S-1-5-21-9-9-9-1002", false)]
    [InlineData(1, InstallUserSid, false)]
    public async Task WrongExecutionIdentityFailsBeforeLockOrExecutor(
        int mode,
        string runtimeSid,
        bool isSystem)
    {
        var mutex = new FakeMutexFactory();
        var executor = new FakeExecutor();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity(runtimeSid, isSystem),
            OwnerRunner(mutex),
            executor,
            new MemoryLog());

        var exitCode = await host.RunAsync(Command((InstallerCoordinatorMode)mode));

        Assert.Equal((int)InstallerCoordinatorExitCode.ExecutionContextMismatch, exitCode);
        Assert.Equal(0, mutex.Calls);
        Assert.Equal(0, executor.UserCalls + executor.PrivilegedCalls);
    }

    [Fact]
    public async Task SameSidWrongSessionFailsBeforeLockOrExecutor()
    {
        var mutex = new FakeMutexFactory();
        var executor = new FakeExecutor();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity(InstallUserSid, false, InstallSessionId + 1),
            OwnerRunner(mutex),
            executor,
            new MemoryLog());

        var exitCode = await host.RunAsync(Command(InstallerCoordinatorMode.UserIpc));

        Assert.Equal((int)InstallerCoordinatorExitCode.ExecutionContextMismatch, exitCode);
        Assert.Equal(0, mutex.Calls);
        Assert.Equal(0, executor.UserCalls);
    }

    [Fact]
    public async Task AbsoluteDeadlineExpiredOrTooFarFailsBeforeOwnerRunner()
    {
        var clock = new FakeMonotonicClock { Milliseconds = 10_000 };
        var owner = new RecordingOwnerRunner();
        var executor = new FakeExecutor();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity(InstallUserSid, false, InstallSessionId),
            owner,
            executor,
            new MemoryLog(),
            monotonicClock: clock);

        var expired = Command(InstallerCoordinatorMode.UserIpc) with { DeadlineTickCount64 = 9_999 };
        var tooFar = Command(InstallerCoordinatorMode.UserIpc) with
        {
            DeadlineTickCount64 = 10_000 + (long)InstallerContextContract.MaximumDeadlineHorizon.TotalMilliseconds + 1
        };

        Assert.Equal((int)InstallerCoordinatorExitCode.DeadlineExpired, await host.RunAsync(expired));
        Assert.Equal((int)InstallerCoordinatorExitCode.DeadlineExpired, await host.RunAsync(tooFar));
        Assert.Equal(0, owner.Calls);
        Assert.Equal(0, executor.UserCalls + executor.PrivilegedCalls);
    }

    [Fact]
    public async Task OneAbsoluteDeadlineReducesSecondPhaseBudgetInsteadOfResetting()
    {
        var clock = new FakeMonotonicClock { Milliseconds = 1_000 };
        var owner = new RecordingOwnerRunner();
        const long deadline = 5_000;
        var user = Command(InstallerCoordinatorMode.UserIpc) with { DeadlineTickCount64 = deadline };
        var privileged = Command(InstallerCoordinatorMode.PrivilegedHandle) with { DeadlineTickCount64 = deadline };

        var userHost = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity(InstallUserSid, false, InstallSessionId),
            owner,
            new FakeExecutor(),
            new MemoryLog(),
            monotonicClock: clock);
        Assert.Equal((int)InstallerCoordinatorExitCode.Success, await userHost.RunAsync(user));
        Assert.Equal(TimeSpan.FromSeconds(4), owner.PhaseTimeouts[0]);

        clock.Milliseconds = 3_500;
        var privilegedHost = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity("S-1-5-18", true, 0),
            owner,
            new FakeExecutor(),
            new MemoryLog(),
            monotonicClock: clock);
        Assert.Equal((int)InstallerCoordinatorExitCode.Success, await privilegedHost.RunAsync(privileged));
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), owner.PhaseTimeouts[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GlobalLockFailureNeverRunsCoordination(int status)
    {
        var mutex = new FakeMutexFactory
        {
            Result = new InstallerCoordinationLockAcquisition(
                (InstallerCoordinationLockStatus)status,
                Detail: "fixture")
        };
        var executor = new FakeExecutor();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity(InstallUserSid, false, InstallSessionId),
            OwnerRunner(mutex),
            executor,
            new MemoryLog());

        var exitCode = await host.RunAsync(Command(InstallerCoordinatorMode.UserIpc));

        Assert.Equal((int)InstallerCoordinatorExitCode.TemporaryFailure, exitCode);
        Assert.Equal(0, executor.UserCalls + executor.PrivilegedCalls);
    }

    [Fact]
    public async Task PrivilegedGlobalTimeoutIsBoundedAndFailsClosed()
    {
        var executor = new FakeExecutor { BlockPrivileged = true };
        var mutex = new FakeMutexFactory();
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity("S-1-5-18", true),
            OwnerRunner(mutex),
            executor,
            new MemoryLog(),
            privilegedPhaseTimeout: TimeSpan.FromMilliseconds(20),
            hardStopGrace: TimeSpan.FromMilliseconds(100));

        var exitCode = await host.RunAsync(Command(InstallerCoordinatorMode.PrivilegedHandle));

        Assert.Equal((int)InstallerCoordinatorExitCode.SoftwareFailure, exitCode);
        Assert.Equal(1, executor.PrivilegedCalls);
    }

    [Fact]
    public async Task VerifiedBackgroundSurvivor_ReturnsRecoverableComponentSpecificFailure()
    {
        var log = new MemoryLog();
        var executor = new FakeExecutor
        {
            RemainingComponents = ["SwSearch.Metadata.Worker.exe"]
        };
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity("S-1-5-18", true),
            OwnerRunner(new FakeMutexFactory()),
            executor,
            log);

        var exitCode = await host.RunAsync(Command(InstallerCoordinatorMode.PrivilegedHandle));

        Assert.Equal((int)InstallerCoordinatorExitCode.BackgroundProcessesRemain, exitCode);
        Assert.Contains(log.Entries, entry =>
            entry.Contains("actual-components=SwSearch.Metadata.Worker.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Contains("SwSearch.App.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveSurvivor_IsNeverFailedAsForceableBackgroundAndFallsThroughToRestartManager()
    {
        var log = new MemoryLog();
        var executor = new FakeExecutor
        {
            RemainingComponents = ["SwSearch.App.exe", "SwSearch.PropertyViewer.exe"]
        };
        var host = new InstallerCoordinatorHost(
            new CoordinatorExecutionIdentity("S-1-5-18", true),
            OwnerRunner(new FakeMutexFactory()),
            executor,
            log);

        var exitCode = await host.RunAsync(Command(InstallerCoordinatorMode.PrivilegedHandle));

        Assert.Equal((int)InstallerCoordinatorExitCode.Success, exitCode);
        Assert.Contains(log.Entries, entry => entry.StartsWith("restart-manager-required", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.StartsWith("coordination-background-blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalMutexNameAndAcl_AreStableSharedAndExactLeastPrivilege()
    {
        var userName = InstallerCoordinationMutex.GetName(UpgradeCode);
        var privilegedName = InstallerCoordinationMutex.GetName(UpgradeCode.ToLowerInvariant());
        var compactName = InstallerCoordinationMutex.GetName("d915835629b344d6a15aacdce8328703");
        const string expectedName =
            @"Global\SwSearch.InstallCoordinator.D915835629B344D6A15AACDCE8328703.v2";
        var invalidUppercaseNamespace = expectedName.Replace(@"Global\", @"GLOBAL\", StringComparison.Ordinal);
        var sddl = InstallerCoordinationMutex.GetExpectedSddl(InstallUserSid);

        Assert.Equal(expectedName, userName);
        Assert.Equal(expectedName, privilegedName);
        Assert.Equal(expectedName, compactName);
        Assert.StartsWith(@"Global\", userName, StringComparison.Ordinal);
        Assert.True(InstallerCoordinationMutex.IsCanonicalName(userName, UpgradeCode));
        Assert.NotEqual(userName, invalidUppercaseNamespace);
        Assert.False(InstallerCoordinationMutex.IsCanonicalName(invalidUppercaseNamespace, UpgradeCode));
        Assert.DoesNotContain(Nonce.ToString("N"), userName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0x00120001u, InstallerCoordinationMutex.ExactRequiredAccess);
        Assert.Equal(
            $"D:P(A;;0x00120001;;;{InstallUserSid})(A;;0x00120001;;;SY)(A;;0x00120001;;;BA)",
            sddl);
        Assert.DoesNotContain("WD", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-1-0", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain("GA", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain("GW", sddl, StringComparison.Ordinal);
    }

    private static string[] ValidArguments(string? mode)
    {
        if (string.Equals(mode, "--privileged-handle", StringComparison.Ordinal))
        {
            return
            [
                "--privileged-handle",
                "--custom-action-data",
                $"v=2;user=1;sid={InstallUserSid};session={InstallSessionId};upgrade={UpgradeCode};nonce={Nonce:D};" +
                $"root={InstallerContextContract.RegisteredRootPolicy};action=repair;deadline=20000"
            ];
        }

        var values = new List<string>();
        if (mode is not null)
        {
            values.Add(mode);
        }

        values.AddRange([
            "--upgrade-code", UpgradeCode,
            "--current-root", InstallerContextContract.RegisteredRootPolicy,
            "--log", @"C:\Temp\SwSearch.InstallCoordinator.log",
            "--install-user-sid", InstallUserSid,
            "--install-session-id", InstallSessionId.ToString(),
            "--instance-nonce", Nonce.ToString("D"),
            "--install-action", "repair",
            "--deadline-tick", "20000"
        ]);
        return values.ToArray();
    }

    private static string[] ReplaceArgument(string[] arguments, string name, string value)
    {
        var clone = arguments.ToArray();
        clone[Array.IndexOf(clone, name) + 1] = value;
        return clone;
    }

    private static InstallerCoordinatorCommand Command(InstallerCoordinatorMode mode) =>
        new(
            mode,
            UpgradeCode,
            @"C:\Program Files\SwSearch\.",
            @"C:\Temp\coordinator.log",
            InstallUserSid,
            Nonce,
            InstallSessionId: InstallSessionId);

    private static InstallerCoordinationOwnerRunner OwnerRunner(FakeMutexFactory mutex) =>
        new(mutex, new FakeWatchdogFactory(), new MemoryLog());

    private sealed class FakeMutexFactory : IInstallerCoordinationMutexFactory
    {
        internal int Calls { get; private set; }
        internal string? UpgradeCode { get; private set; }
        internal string? InstallUserSid { get; private set; }
        internal InstallerCoordinationLockAcquisition Result { get; set; } =
            new(InstallerCoordinationLockStatus.Acquired, new FakeLease());

        public InstallerCoordinationLockAcquisition TryAcquire(
            string upgradeCode,
            string installUserSid,
            TimeSpan timeout,
            ICoordinatorLog log)
        {
            Calls++;
            UpgradeCode = upgradeCode;
            InstallUserSid = installUserSid;
            return Result;
        }
    }

    private sealed class FakeExecutor : IInstallerCoordinatorPhaseExecutor
    {
        internal int UserCalls { get; private set; }
        internal int PrivilegedCalls { get; private set; }
        internal bool BlockPrivileged { get; set; }
        internal IReadOnlyList<string> RemainingComponents { get; set; } = [];

        public ValueTask<InstallerCoordinatorPhaseResult> ExecuteUserIpcAsync(
            InstallerCoordinatorCommand command,
            CancellationToken cancellationToken)
        {
            UserCalls++;
            return ValueTask.FromResult(new InstallerCoordinatorPhaseResult("user", RemainingComponents));
        }

        public async ValueTask<InstallerCoordinatorPhaseResult> ExecutePrivilegedHandleAsync(
            InstallerCoordinatorCommand command,
            CancellationToken cancellationToken)
        {
            PrivilegedCalls++;
            if (BlockPrivileged)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new InstallerCoordinatorPhaseResult("privileged", RemainingComponents);
        }
    }

    private sealed class FakeMonotonicClock : IInstallerMonotonicClock
    {
        internal long Milliseconds { get; set; }
        public long GetMilliseconds() => Milliseconds;
    }

    private sealed class RecordingOwnerRunner : IInstallerCoordinationOwnerRunner
    {
        internal int Calls { get; private set; }
        internal List<TimeSpan> PhaseTimeouts { get; } = [];

        public async Task<InstallerCoordinationOwnerRunResult> RunExclusiveAsync(
            InstallerCoordinatorCommand command,
            TimeSpan mutexTimeout,
            TimeSpan phaseTimeout,
            TimeSpan hardStopGrace,
            Func<CancellationToken, ValueTask<InstallerCoordinatorPhaseResult>> phase)
        {
            Calls++;
            PhaseTimeouts.Add(phaseTimeout);
            var result = await phase(CancellationToken.None);
            return new InstallerCoordinationOwnerRunResult(
                InstallerCoordinationOwnerRunStatus.Completed,
                PhaseResult: result,
                LockStatus: InstallerCoordinationLockStatus.Acquired);
        }
    }

    private sealed class FakeLease : IInstallerCoordinationLease
    {
        public void ReleaseOnOwnerThread()
        {
        }

        public void Dispose()
        {
            ReleaseOnOwnerThread();
        }
    }

    private sealed class FakeWatchdogFactory : IInstallerCoordinatorHardDeadlineWatchdogFactory
    {
        public IInstallerCoordinatorHardDeadlineWatchdog Arm(
            long coordinationStartedTimestamp,
            TimeSpan hardTimeout,
            InstallerCoordinatorCommand command) => new FakeWatchdog();

        private sealed class FakeWatchdog : IInstallerCoordinatorHardDeadlineWatchdog
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
}
