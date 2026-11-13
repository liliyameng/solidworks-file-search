using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SwSearch.InstallerCoordinator;

internal enum InstallerCoordinatorMode
{
    UserIpc,
    PrivilegedHandle
}

internal enum InstallerOperation
{
    Install,
    Upgrade,
    Repair,
    Uninstall
}

internal enum InstallerCoordinatorExitCode
{
    Success = 0,
    Usage = 64,
    SoftwareFailure = 70,
    TemporaryFailure = 75,
    ExecutionContextMismatch = 77,
    ContextDataInvalid = 78,
    DeadlineExpired = 79,
    BackgroundProcessesRemain = 80,
    CadEnvelopeInvalid = 81,
    CadSchemaInvalid = 82,
    CadUserFlagInvalid = 83,
    CadMachineIdentityInvalid = 84,
    CadUpgradeInvalid = 85,
    CadRootPolicyInvalid = 86,
    CadActionInvalid = 87,
    CadDeadlineInvalid = 88,
    CadUserIdentityInvalid = 89
}

internal sealed record InstallerCoordinatorCommand(
    InstallerCoordinatorMode Mode,
    string UpgradeCode,
    string CurrentInstallRoot,
    string LogPath,
    string InstallUserSid,
    Guid InstanceNonce,
    InstallerOperation Operation = InstallerOperation.Repair,
    long DeadlineTickCount64 = 0,
    int InstallSessionId = -1,
    bool HasInstallUserContext = true);

internal static class InstallerCoordinatorCommandLine
{
    private static readonly HashSet<string> UserValueArguments = new(StringComparer.Ordinal)
    {
        "--upgrade-code",
        "--current-root",
        "--log",
        "--install-user-sid",
        "--install-session-id",
        "--instance-nonce",
        "--install-action",
        "--deadline-tick"
    };

    internal static bool TryParse(
        IReadOnlyList<string> args,
        out InstallerCoordinatorCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        var userModeCount = args.Count(value => string.Equals(value, "--user-ipc", StringComparison.Ordinal));
        var privilegedModeCount = args.Count(value =>
            string.Equals(value, "--privileged-handle", StringComparison.Ordinal));
        if (userModeCount + privilegedModeCount != 1)
        {
            error = "Exactly one explicit mode is required: --user-ipc or --privileged-handle.";
            return false;
        }

        if (privilegedModeCount == 1)
        {
            if (args.Count != 3 ||
                !string.Equals(args[0], "--privileged-handle", StringComparison.Ordinal) ||
                !string.Equals(args[1], "--custom-action-data", StringComparison.Ordinal))
            {
                error = "Privileged mode requires exactly --privileged-handle --custom-action-data <value>.";
                return false;
            }

            return InstallerCustomActionData.TryParse(args[2], out command, out error);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--user-ipc" or "--privileged-handle")
            {
                continue;
            }

            if (!UserValueArguments.Contains(argument))
            {
                error = $"Unknown or misplaced argument: {argument}";
                return false;
            }

            if (values.ContainsKey(argument) || index + 1 >= args.Count ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Missing or duplicate value for argument: {argument}";
                return false;
            }

            values[argument] = args[++index];
        }

        foreach (var required in UserValueArguments)
        {
            if (!values.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value))
            {
                error = $"Required argument missing: {required}";
                return false;
            }
        }

        if (!Guid.TryParse(values["--upgrade-code"], out var parsedUpgradeCode))
        {
            error = "UpgradeCode is invalid.";
            return false;
        }

        if (!InstallerContextContract.TryNormalizeIndividualAccountSid(
                values["--install-user-sid"],
                out var installUserSid,
                out error))
        {
            return false;
        }

        if (!Guid.TryParse(values["--instance-nonce"], out var instanceNonce) ||
            !InstallerContextContract.IsCryptographicSessionNonce(instanceNonce))
        {
            error = "Instance nonce must be a version-4 GUID with the RFC 4122 variant.";
            return false;
        }

        if (!int.TryParse(
                values["--install-session-id"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var installSessionId) || installSessionId < 0)
        {
            error = "Install session ID must be a non-negative Int32.";
            return false;
        }

        if (!InstallerContextContract.TryParseOperation(values["--install-action"], out var operation))
        {
            error = "Install action is invalid.";
            return false;
        }

        if (!long.TryParse(
                values["--deadline-tick"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var deadlineTick) || deadlineTick <= 0)
        {
            error = "Deadline tick must be a positive Int64.";
            return false;
        }

        if (!string.Equals(
                values["--current-root"],
                InstallerContextContract.RegisteredRootPolicy,
                StringComparison.Ordinal))
        {
            error = "Current root must use the registered-component trust policy.";
            return false;
        }

        command = new InstallerCoordinatorCommand(
            InstallerCoordinatorMode.UserIpc,
            parsedUpgradeCode.ToString("B").ToUpperInvariant(),
            values["--current-root"],
            values["--log"],
            installUserSid,
            instanceNonce,
            operation,
            deadlineTick,
            installSessionId);
        return true;
    }
}

internal sealed record CoordinatorExecutionIdentity(
    string? UserSid,
    bool IsLocalSystem,
    int? SessionId = null)
{
    private const int TokenSessionIdInformationClass = 12;
    internal static CoordinatorExecutionIdentity Capture()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value;
        return new CoordinatorExecutionIdentity(
            sid,
            string.Equals(sid, "S-1-5-18", StringComparison.Ordinal),
            TryGetTokenSessionId(identity.AccessToken, out var sessionId) ? sessionId : null);
    }

    private static bool TryGetTokenSessionId(SafeAccessTokenHandle token, out int sessionId)
    {
        sessionId = -1;
        uint value = 0;
        if (!GetTokenInformation(
                token,
                TokenSessionIdInformationClass,
                out value,
                sizeof(uint),
                out var returned) ||
            returned != sizeof(uint) || value > int.MaxValue)
        {
            return false;
        }

        sessionId = (int)value;
        return true;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}

internal sealed record InstallerCoordinatorPhaseResult(
    string Summary,
    IReadOnlyList<string> RemainingComponents);

internal interface IInstallerMonotonicClock
{
    long GetMilliseconds();
}

internal sealed class EnvironmentTickCount64Clock : IInstallerMonotonicClock
{
    public long GetMilliseconds() => Environment.TickCount64;
}

internal interface IInstallerCoordinatorPhaseExecutor
{
    ValueTask<InstallerCoordinatorPhaseResult> ExecuteUserIpcAsync(
        InstallerCoordinatorCommand command,
        CancellationToken cancellationToken);

    ValueTask<InstallerCoordinatorPhaseResult> ExecutePrivilegedHandleAsync(
        InstallerCoordinatorCommand command,
        CancellationToken cancellationToken);
}

internal interface IInstallerCoordinationMutexFactory
{
    InstallerCoordinationLockAcquisition TryAcquire(
        string upgradeCode,
        string installUserSid,
        TimeSpan timeout,
        ICoordinatorLog log);
}

internal sealed class SystemInstallerCoordinationMutexFactory : IInstallerCoordinationMutexFactory
{
    public InstallerCoordinationLockAcquisition TryAcquire(
        string upgradeCode,
        string installUserSid,
        TimeSpan timeout,
        ICoordinatorLog log) =>
        InstallerCoordinationMutex.TryAcquire(upgradeCode, installUserSid, timeout, log);
}

internal sealed class InstallerCoordinatorHost
{
    private readonly CoordinatorExecutionIdentity _identity;
    private readonly IInstallerCoordinationOwnerRunner _ownerRunner;
    private readonly IInstallerCoordinatorPhaseExecutor _executor;
    private readonly ICoordinatorLog _log;
    private readonly TimeSpan _userPhaseTimeout;
    private readonly TimeSpan _privilegedPhaseTimeout;
    private readonly TimeSpan _mutexTimeout;
    private readonly TimeSpan _hardStopGrace;
    private readonly IInstallerMonotonicClock _monotonicClock;

    internal InstallerCoordinatorHost(
        CoordinatorExecutionIdentity identity,
        IInstallerCoordinationOwnerRunner ownerRunner,
        IInstallerCoordinatorPhaseExecutor executor,
        ICoordinatorLog log,
        TimeSpan? userPhaseTimeout = null,
        TimeSpan? privilegedPhaseTimeout = null,
        TimeSpan? mutexTimeout = null,
        TimeSpan? hardStopGrace = null,
        IInstallerMonotonicClock? monotonicClock = null)
    {
        _identity = identity;
        _ownerRunner = ownerRunner;
        _executor = executor;
        _log = log;
        _userPhaseTimeout = userPhaseTimeout ?? TimeSpan.FromSeconds(4);
        _privilegedPhaseTimeout = privilegedPhaseTimeout ?? TimeSpan.FromSeconds(15);
        _mutexTimeout = mutexTimeout ?? TimeSpan.FromSeconds(3);
        _hardStopGrace = hardStopGrace ?? TimeSpan.FromSeconds(2);
        _monotonicClock = monotonicClock ?? new EnvironmentTickCount64Clock();
    }

    internal async Task<int> RunAsync(InstallerCoordinatorCommand command)
    {
        if (!IsExpectedExecutionContext(command))
        {
            _log.Write(
                $"execution-context-mismatch mode={command.Mode} is-system={_identity.IsLocalSystem}");
            return (int)InstallerCoordinatorExitCode.ExecutionContextMismatch;
        }

        var configuredTimeout = command.Mode == InstallerCoordinatorMode.UserIpc
            ? _userPhaseTimeout
            : _privilegedPhaseTimeout;
        var timeout = GetBoundedRemainingTimeout(command, configuredTimeout);
        if (timeout <= TimeSpan.Zero)
        {
            _log.Write($"coordination-deadline-invalid-or-expired mode={command.Mode}");
            return (int)InstallerCoordinatorExitCode.DeadlineExpired;
        }
        Func<CancellationToken, ValueTask<InstallerCoordinatorPhaseResult>> phase = command.Mode switch
        {
            InstallerCoordinatorMode.UserIpc =>
                cancellationToken => _executor.ExecuteUserIpcAsync(command, cancellationToken),
            InstallerCoordinatorMode.PrivilegedHandle =>
                cancellationToken => _executor.ExecutePrivilegedHandleAsync(command, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Mode))
        };

        var ownerResult = await _ownerRunner.RunExclusiveAsync(
                command,
                _mutexTimeout <= timeout ? _mutexTimeout : timeout,
                timeout,
                _hardStopGrace,
                phase)
            .ConfigureAwait(false);

        if (ownerResult.Status == InstallerCoordinationOwnerRunStatus.LockUnavailable)
        {
            _log.Write(
                $"coordination-failed reason=global-mutex status={ownerResult.LockStatus} " +
                $"detail={ownerResult.Detail ?? "<none>"}");
            return (int)InstallerCoordinatorExitCode.TemporaryFailure;
        }

        if (ownerResult.Status == InstallerCoordinationOwnerRunStatus.Completed &&
            ownerResult.PhaseResult is { } result)
        {
            _log.Write($"phase-complete mode={command.Mode} summary={result.Summary}");
            if (result.RemainingComponents.Count != 0)
            {
                _log.Write(
                    "restart-manager-required actual-components=" +
                    string.Join(",", result.RemainingComponents.Distinct(StringComparer.OrdinalIgnoreCase)));
            }

            var remainingBackground = result.RemainingComponents
                .Where(component =>
                    component.Equals("SwSearch.Broker.exe", StringComparison.OrdinalIgnoreCase) ||
                    component.Equals("SwSearch.Metadata.Worker.exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (remainingBackground.Length != 0)
            {
                _log.Write("coordination-background-blocked actual-components=" +
                           string.Join(",", remainingBackground));
                return (int)InstallerCoordinatorExitCode.BackgroundProcessesRemain;
            }

            return (int)InstallerCoordinatorExitCode.Success;
        }

        if (ownerResult.Status == InstallerCoordinationOwnerRunStatus.PhaseTimedOut &&
            command.Mode == InstallerCoordinatorMode.UserIpc)
        {
            // An unavailable/old CurrentUserOnly endpoint is expected during an
            // RC12 upgrade. The separately wired privileged action remains the
            // mandatory strict-handle fallback; this is an explicit transition,
            // not a silent safety success.
            _log.Write("user-ipc-timeout; privileged-handle-fallback-required");
            return (int)InstallerCoordinatorExitCode.Success;
        }

        if (ownerResult.Status == InstallerCoordinationOwnerRunStatus.PhaseTimedOut)
        {
            _log.Write($"coordination-timeout mode={command.Mode} timeout-ms={timeout.TotalMilliseconds:0}");
            return (int)InstallerCoordinatorExitCode.SoftwareFailure;
        }

        _log.Write(
            $"coordination-failure mode={command.Mode} owner-status={ownerResult.Status} " +
            $"type={ownerResult.Exception?.GetType().Name ?? "<none>"} " +
            $"message={ownerResult.Detail ?? "<none>"}");
        return (int)InstallerCoordinatorExitCode.SoftwareFailure;
    }

    private bool IsExpectedExecutionContext(InstallerCoordinatorCommand command) => command.Mode switch
    {
        InstallerCoordinatorMode.UserIpc =>
            command.HasInstallUserContext &&
            !_identity.IsLocalSystem && !string.IsNullOrWhiteSpace(_identity.UserSid) &&
            _identity.SessionId == command.InstallSessionId &&
            string.Equals(_identity.UserSid, command.InstallUserSid, StringComparison.Ordinal),
        InstallerCoordinatorMode.PrivilegedHandle => _identity.IsLocalSystem,
        _ => false
    };

    private TimeSpan GetBoundedRemainingTimeout(
        InstallerCoordinatorCommand command,
        TimeSpan configuredTimeout)
    {
        if (command.DeadlineTickCount64 == 0)
        {
            // Direct in-memory tests and self-test fixtures may omit the MSI
            // session deadline. Production command-line parsing never does.
            return configuredTimeout;
        }

        var remainingMilliseconds = command.DeadlineTickCount64 - _monotonicClock.GetMilliseconds();
        if (remainingMilliseconds <= 0 ||
            remainingMilliseconds > InstallerContextContract.MaximumDeadlineHorizon.TotalMilliseconds)
        {
            return TimeSpan.Zero;
        }

        var remaining = TimeSpan.FromMilliseconds(remainingMilliseconds);
        return remaining < configuredTimeout ? remaining : configuredTimeout;
    }
}

internal sealed class ProductionInstallerCoordinatorPhaseExecutor(ICoordinatorLog log)
    : IInstallerCoordinatorPhaseExecutor
{
    public async ValueTask<InstallerCoordinatorPhaseResult> ExecuteUserIpcAsync(
        InstallerCoordinatorCommand command,
        CancellationToken cancellationToken)
    {
        // ProductShutdownTransport is constructed only in this explicitly
        // impersonated current-user branch. Its pipes remain CurrentUserOnly;
        // an unavailable or pre-protocol RC12 endpoint is handled by the later
        // privileged strict-handle action, never by widening the pipe ACL.
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new WindowsInstalledProductCatalog(),
            new ProductShutdownTransport(TimeSpan.FromMilliseconds(750)),
            new SystemCoordinatorDelay(),
            log);
        var outcome = await coordinator.ShutdownAsync(
                InstallerCoordinatorOptions.CreateDefault(command.UpgradeCode, command.CurrentInstallRoot),
                new ShutdownRequestContext(
                    command.InstallUserSid,
                    command.InstallSessionId,
                    command.InstanceNonce),
                cancellationToken)
            .ConfigureAwait(false);
        return new InstallerCoordinatorPhaseResult(
            $"products={outcome.TrustedProductCount};app={outcome.App};" +
            $"property-viewer={outcome.PropertyViewer};broker={outcome.Broker};worker={outcome.Worker}",
            []);
    }

    public async ValueTask<InstallerCoordinatorPhaseResult> ExecutePrivilegedHandleAsync(
        InstallerCoordinatorCommand command,
        CancellationToken cancellationToken)
    {
        // This call graph must remain free of ProductShutdownTransport and any
        // named-pipe construction. The deferred no-impersonate/SYSTEM action is
        // restricted to the reviewed final-path/held-handle implementation.
        var coordinator = new InstallationCoordinator(
            new WindowsInstalledProductCatalog(),
            new WindowsProductProcessCatalog(),
            new SystemCoordinatorDelay(),
            log);
        var outcome = await coordinator.CoordinateAsync(
                InstallerCoordinatorOptions.CreateDefault(command.UpgradeCode, command.CurrentInstallRoot),
                cancellationToken)
            .ConfigureAwait(false);
        var remaining = outcome.RemainingProductProcesses
            .Select(process => process.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new InstallerCoordinatorPhaseResult(
            $"terminated={outcome.Terminated.Count};remaining={remaining.Length};" +
            $"ignored-unverified={outcome.IgnoredUnverifiedProcesses.Count}",
            remaining);
    }

}
