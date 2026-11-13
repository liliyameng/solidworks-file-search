using Microsoft.Win32.SafeHandles;
using SwSearch.InstallerCoordinator;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

[Trait("Category", "WindowsNativeMutexHardDeadlineGate")]
public sealed class InstallerCoordinationHardDeadlineNativeGateTests
{
    private const uint ExactMutexAccess = 0x00120001;

    [Fact]
    public async Task NonCooperativePhase_SelfTerminatesHelperAndNextOwnerRecoversExactDaclAbandonment()
    {
        var installUserSid = TestInstallUserSid();
        var run = await RunHelperAsync(
            "--noncooperative-owner",
            installUserSid,
            expectAbandoned: true,
            exitTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal((int)InstallerCoordinatorExitCode.SoftwareFailure, run.ExitCode);
    }

    [Fact]
    public async Task NormalCompletion_DisarmsBeforeDeadlineAndDoesNotAbandon()
    {
        var installUserSid = TestInstallUserSid();
        var run = await RunHelperAsync(
            "--normal-owner",
            installUserSid,
            expectAbandoned: false,
            exitTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal((int)InstallerCoordinatorExitCode.Success, run.ExitCode);
    }

    [Fact]
    public async Task CooperativeCancellation_ConfirmsPhaseClosureThenDisarmsWithoutSelfTermination()
    {
        var installUserSid = TestInstallUserSid();
        var run = await RunHelperAsync(
            "--cooperative-cancel-owner",
            installUserSid,
            expectAbandoned: false,
            exitTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal((int)InstallerCoordinatorExitCode.Success, run.ExitCode);
    }

    [Fact]
    public async Task NearHardDeadlineRace_RepeatedNaturalCompletionNeverLateKillsOrAbandons()
    {
        var installUserSid = TestInstallUserSid();
        for (var round = 0; round < 10; round++)
        {
            var run = await RunHelperAsync(
                "--near-deadline-owner",
                installUserSid,
                expectAbandoned: false,
                exitTimeout: TimeSpan.FromSeconds(5));

            Assert.Equal((int)InstallerCoordinatorExitCode.Success, run.ExitCode);
        }
    }

    private static async Task<HelperRunResult> RunHelperAsync(
        string mode,
        string installUserSid,
        bool expectAbandoned,
        TimeSpan exitTimeout)
    {
        var helperPath = Path.Combine(
            AppContext.BaseDirectory,
            "InstallerCoordinatorWatchdogHelper",
            "SwSearch.InstallerCoordinator.WatchdogHelper.exe");
        Assert.True(File.Exists(helperPath), $"Watchdog helper is missing: {helperPath}");

        var nonce = Guid.NewGuid();
        var upgradeCode = Guid.NewGuid();
        var sandbox = Path.Combine(
            Path.GetTempPath(),
            "swsearch-installer-watchdog-" + nonce.ToString("N"));
        Directory.CreateDirectory(sandbox);
        var readyPath = Path.Combine(sandbox, "owner.ready");
        var anchorAcknowledgementPath = readyPath + ".anchor-ack";
        var canonicalMutexName = InstallerCoordinationMutex.GetName(upgradeCode.ToString("B"));
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = sandbox
        };
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(upgradeCode.ToString("B"));
        startInfo.ArgumentList.Add(installUserSid);
        startInfo.ArgumentList.Add(nonce.ToString("N"));
        startInfo.ArgumentList.Add(readyPath);

        using var helper = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start the isolated watchdog helper.");
        var creationHandle = helper.SafeHandle;
        var helperExited = false;
        var exitCode = int.MinValue;
        MutexObjectAnchor? mutexAnchor = null;
        try
        {
            await WaitForExactReadyAsync(
                readyPath,
                nonce,
                mode,
                canonicalMutexName,
                TimeSpan.FromSeconds(2));
            mutexAnchor = MutexObjectAnchor.OpenAfterReady(
                canonicalMutexName,
                readyWasObserved: true);
            PublishAnchorAcknowledgement(
                anchorAcknowledgementPath,
                nonce,
                canonicalMutexName);
            await helper.WaitForExitAsync().WaitAsync(exitTimeout);
            helperExited = true;
            exitCode = helper.ExitCode;
            mutexAnchor.MarkHelperExited();
            mutexAnchor.ValidateAndReleaseOnOwnerThread(
                upgradeCode,
                installUserSid,
                expectAbandoned);
        }
        finally
        {
            // On the successful path this is strictly after production
            // TryAcquire + same-thread release. On a failed path it safely
            // closes only the already-opened test anchor.
            mutexAnchor?.Dispose();

            if (!helperExited && !helper.HasExited)
            {
                // Safety cleanup is limited to the original handle returned by
                // Process.Start for this unique, test-created helper. It never
                // reopens by PID or enumerates any existing process.
                if (TerminateProcess(creationHandle, (uint)InstallerCoordinatorExitCode.SoftwareFailure))
                {
                    helperExited = helper.WaitForExit(2_000);
                }
            }

            if (helperExited || helper.HasExited)
            {
                Directory.Delete(sandbox, recursive: true);
            }
            else
            {
                throw new InvalidOperationException(
                    "P1_MSI_MUTEX_WATCHDOG_CLEANUP_BLOCKED: helper identity/liveness is uncertain; " +
                    $"sandbox retained at {sandbox}.");
            }
        }

        return new HelperRunResult(upgradeCode, exitCode);
    }

    private static async Task WaitForExactReadyAsync(
        string readyPath,
        Guid nonce,
        string mode,
        string canonicalMutexName,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var expected = nonce.ToString("N") + "|" + mode + "|owner-acquired|" + canonicalMutexName;
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(readyPath) &&
                    string.Equals(await File.ReadAllTextAsync(readyPath, deadline.Token), expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
                // The helper atomically publishes by rename, but antivirus can
                // briefly hold the destination. Retry only inside this deadline.
            }

            await Task.Delay(10, deadline.Token);
        }

        throw new TimeoutException("The watchdog helper did not publish exact ready evidence.");
    }

    private static void PublishAnchorAcknowledgement(
        string path,
        Guid nonce,
        string canonicalMutexName)
    {
        var publishingPath = path + ".publishing-" + nonce.ToString("N");
        File.WriteAllText(
            publishingPath,
            nonce.ToString("N") + "|anchor-open|" + canonicalMutexName);
        File.Move(publishingPath, path);
    }

    private static string TestInstallUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var installUserSid = identity.User?.Value;
        if (identity.IsSystem || string.IsNullOrWhiteSpace(installUserSid) || !identity.User!.IsAccountSid())
        {
            throw new InvalidOperationException(
                "P1_MSI_MUTEX_WATCHDOG_ENVIRONMENT_BLOCKED: gate requires one non-SYSTEM account token.");
        }

        return installUserSid;
    }

    private sealed class MutexObjectAnchor : IDisposable
    {
        private readonly SafeWaitHandle _handle;
        private AnchorState _state = AnchorState.Open;

        private MutexObjectAnchor(SafeWaitHandle handle)
        {
            _handle = handle;
        }

        internal static MutexObjectAnchor OpenAfterReady(
            string canonicalMutexName,
            bool readyWasObserved)
        {
            if (!readyWasObserved ||
                !canonicalMutexName.StartsWith(@"Global\SwSearch.InstallCoordinator.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "P1_WD_2_ANCHOR_ORDER_BLOCKED: existing mutex may only be opened after exact ready evidence.");
            }

            var handle = OpenMutex(ExactMutexAccess, inheritHandle: false, canonicalMutexName);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "P1_WD_2_ENVIRONMENT_BLOCKED: failed to open the ready helper's existing canonical mutex.");
            }

            return new MutexObjectAnchor(handle);
        }

        internal void MarkHelperExited()
        {
            EnsureState(AnchorState.Open);
            Assert.False(_handle.IsClosed);
            Assert.False(_handle.IsInvalid);
            _state = AnchorState.HelperExited;
        }

        internal void ValidateAndReleaseOnOwnerThread(
            Guid upgradeCode,
            string installUserSid,
            bool expectAbandoned)
        {
            EnsureState(AnchorState.HelperExited);
            Assert.False(_handle.IsClosed);
            Assert.False(_handle.IsInvalid);

            var log = new MemoryLog();
            var reacquired = InstallerCoordinationMutex.TryAcquire(
                upgradeCode.ToString("B"),
                installUserSid,
                TimeSpan.FromMilliseconds(500),
                log);
            Assert.Equal(InstallerCoordinationLockStatus.Acquired, reacquired.Status);
            Assert.NotNull(reacquired.Lease);
            if (expectAbandoned)
            {
                Assert.Contains(log.Entries, entry =>
                    entry.Contains("abandoned-recovered", StringComparison.Ordinal));
            }
            else
            {
                Assert.DoesNotContain(log.Entries, entry =>
                    entry.Contains("abandoned-recovered", StringComparison.Ordinal));
            }

            // There is no await between production acquisition and release.
            reacquired.Lease!.ReleaseOnOwnerThread();
            _state = AnchorState.Validated;
        }

        public void Dispose()
        {
            if (_state == AnchorState.Disposed)
            {
                return;
            }

            _handle.Dispose();
            _state = AnchorState.Disposed;
        }

        private void EnsureState(AnchorState expected)
        {
            if (_state != expected)
            {
                throw new InvalidOperationException(
                    $"P1_WD_2_ANCHOR_ORDER_BLOCKED: expected {expected}, actual {_state}.");
            }
        }

        private enum AnchorState
        {
            Open,
            HelperExited,
            Validated,
            Disposed
        }
    }

    private sealed class MemoryLog : ICoordinatorLog
    {
        internal List<string> Entries { get; } = [];
        public void Write(string message) => Entries.Add(message);
    }

    private sealed record HelperRunResult(Guid UpgradeCode, int ExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenMutexW")]
    private static extern SafeWaitHandle OpenMutex(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);
}
