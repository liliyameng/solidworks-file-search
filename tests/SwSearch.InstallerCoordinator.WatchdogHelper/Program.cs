using SwSearch.InstallerCoordinator;
using System.Text;

if (args.Length != 5 ||
    args[0] is not ("--normal-owner" or
        "--cooperative-cancel-owner" or
        "--near-deadline-owner" or
        "--noncooperative-owner") ||
    !Guid.TryParse(args[1], out var upgradeCode) ||
    !Guid.TryParseExact(args[3], "N", out var nonce))
{
    return (int)InstallerCoordinatorExitCode.Usage;
}

var installUserSid = args[2];
var mode = args[0];
var readyPath = Path.GetFullPath(args[4]);
var anchorAcknowledgementPath = readyPath + ".anchor-ack";
var command = new InstallerCoordinatorCommand(
    InstallerCoordinatorMode.PrivilegedHandle,
    upgradeCode.ToString("B"),
    @"C:\Program Files\SwSearch",
    readyPath + ".log-not-created",
    installUserSid,
    nonce);
var canonicalMutexName = InstallerCoordinationMutex.GetName(command.UpgradeCode);
var log = new ConsoleLog();
var runner = new InstallerCoordinationOwnerRunner(
    new SystemInstallerCoordinationMutexFactory(),
    new SelfProcessHardDeadlineWatchdogFactory(),
    log);

var (softTimeout, hardStopGrace) = mode switch
{
    "--normal-owner" => (TimeSpan.FromMilliseconds(2_000), TimeSpan.FromMilliseconds(1_000)),
    "--cooperative-cancel-owner" => (TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(2_200)),
    "--near-deadline-owner" => (TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(2_500)),
    "--noncooperative-owner" => (TimeSpan.FromMilliseconds(1_500), TimeSpan.FromMilliseconds(1_500)),
    _ => throw new InvalidOperationException("Unreachable helper mode.")
};
var outcome = await runner.RunExclusiveAsync(
    command,
    TimeSpan.FromSeconds(1),
    softTimeout,
    hardStopGrace,
    cancellationToken =>
    {
        PublishReadyAtomically(readyPath, nonce, mode, canonicalMutexName);
        return ExecuteAfterAnchorAcknowledgementAsync(
            mode,
            anchorAcknowledgementPath,
            nonce,
            canonicalMutexName,
            cancellationToken);
    });

var expectedStatus = mode == "--normal-owner"
    ? InstallerCoordinationOwnerRunStatus.Completed
    : InstallerCoordinationOwnerRunStatus.PhaseTimedOut;
return outcome.Status == expectedStatus
    ? (int)InstallerCoordinatorExitCode.Success
    : (int)InstallerCoordinatorExitCode.SoftwareFailure;

static async ValueTask<InstallerCoordinatorPhaseResult> ExecuteAfterAnchorAcknowledgementAsync(
    string mode,
    string acknowledgementPath,
    Guid nonce,
    string canonicalMutexName,
    CancellationToken cancellationToken)
{
    await WaitForAnchorAcknowledgementAsync(
        acknowledgementPath,
        nonce,
        canonicalMutexName,
        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    return mode switch
    {
        "--normal-owner" => await CompleteNormallyAsync(cancellationToken).ConfigureAwait(false),
        "--cooperative-cancel-owner" =>
            await CompleteAfterCooperativeCancellationAsync(cancellationToken).ConfigureAwait(false),
        "--near-deadline-owner" => await CompleteNearHardDeadlineAsync().ConfigureAwait(false),
        "--noncooperative-owner" => await NeverCompleteAsync().ConfigureAwait(false),
        _ => throw new InvalidOperationException("Unreachable helper mode.")
    };
}

static async Task WaitForAnchorAcknowledgementAsync(
    string path,
    Guid nonce,
    string canonicalMutexName,
    TimeSpan timeout)
{
    using var deadline = new CancellationTokenSource(timeout);
    var expected = nonce.ToString("N") + "|anchor-open|" + canonicalMutexName;
    while (!deadline.IsCancellationRequested)
    {
        try
        {
            if (File.Exists(path) &&
                string.Equals(
                    await File.ReadAllTextAsync(path, deadline.Token).ConfigureAwait(false),
                    expected,
                    StringComparison.Ordinal))
            {
                return;
            }
        }
        catch (IOException)
        {
        }

        await Task.Delay(10, deadline.Token).ConfigureAwait(false);
    }

    throw new TimeoutException("Parent did not confirm an open existing-mutex anchor.");
}

static async ValueTask<InstallerCoordinatorPhaseResult> CompleteNormallyAsync(
    CancellationToken cancellationToken)
{
    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
    return new InstallerCoordinatorPhaseResult("normal", []);
}

static async ValueTask<InstallerCoordinatorPhaseResult> CompleteAfterCooperativeCancellationAsync(
    CancellationToken cancellationToken)
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    throw new InvalidOperationException("Unreachable cooperative-cancellation path.");
}

static async ValueTask<InstallerCoordinatorPhaseResult> CompleteNearHardDeadlineAsync()
{
    // Intentionally ignore the soft cancellation and finish with a bounded
    // margin before the process hard deadline. The runner must retain the mutex
    // until this Task confirms completion, then disarm without a late kill.
    await Task.Delay(2_300).ConfigureAwait(false);
    return new InstallerCoordinatorPhaseResult("near-hard-deadline", []);
}

static ValueTask<InstallerCoordinatorPhaseResult> NeverCompleteAsync()
{
    var never = new TaskCompletionSource<InstallerCoordinatorPhaseResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    return new ValueTask<InstallerCoordinatorPhaseResult>(never.Task);
}

static void PublishReadyAtomically(
    string readyPath,
    Guid nonce,
    string mode,
    string canonicalMutexName)
{
    var publishingPath = readyPath + ".publishing-" + nonce.ToString("N");
    try
    {
        File.WriteAllText(
            publishingPath,
            nonce.ToString("N") + "|" + mode + "|owner-acquired|" + canonicalMutexName,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(publishingPath, readyPath);
    }
    finally
    {
        if (File.Exists(publishingPath))
        {
            File.Delete(publishingPath);
        }
    }
}

internal sealed class ConsoleLog : ICoordinatorLog
{
    public void Write(string message) => Console.Error.WriteLine(message);
}
