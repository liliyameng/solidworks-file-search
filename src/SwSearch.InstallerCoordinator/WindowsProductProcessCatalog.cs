using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwSearch.InstallerCoordinator;

internal sealed class WindowsProductProcessCatalog : IProductProcessCatalog
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint StillActive = 259;
    private const uint CoordinatorExitCode = 0x53575348;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private static readonly HashSet<string> ExpectedProcessNames = ComponentSpecification.All
        .Select(item => Path.GetFileNameWithoutExtension(item.FileName))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (!ExpectedProcessNames.Contains(processName))
                {
                    continue;
                }

                var acquisition = Acquire(process.Id, includeTerminateRight: false);
                if (acquisition.Status == ProcessLeaseStatus.Acquired && acquisition.Lease is not null)
                {
                    using (acquisition.Lease)
                    {
                        snapshots.Add(acquisition.Lease.Snapshot);
                    }
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<ProcessSnapshot>>(snapshots);
    }

    public ValueTask<ProcessLeaseAcquisition> AcquireTerminationLeaseAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Acquire(processId, includeTerminateRight: true));
    }

    private static ProcessLeaseAcquisition Acquire(int processId, bool includeTerminateRight)
    {
        var access = ProcessQueryLimitedInformation | Synchronize;
        if (includeTerminateRight)
        {
            access |= ProcessTerminate;
        }

        var processHandle = OpenProcess(access, false, processId);
        if (processHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            processHandle.Dispose();
            return new ProcessLeaseAcquisition(
                error == ErrorAccessDenied
                    ? ProcessLeaseStatus.AccessDenied
                    : error == ErrorInvalidParameter
                        ? ProcessLeaseStatus.Exited
                        : ProcessLeaseStatus.Unverifiable);
        }

        if (WaitForSingleObject(processHandle, 0) == WaitObject0)
        {
            processHandle.Dispose();
            return new ProcessLeaseAcquisition(ProcessLeaseStatus.Exited);
        }

        var built = TryBuildSnapshot(processId, processHandle);
        if (built is null)
        {
            processHandle.Dispose();
            return new ProcessLeaseAcquisition(ProcessLeaseStatus.Unverifiable);
        }

        var (snapshot, imageAnchor) = built.Value;
        return new ProcessLeaseAcquisition(
            ProcessLeaseStatus.Acquired,
            new StableProcessTerminationLease(processHandle, imageAnchor, snapshot, includeTerminateRight));
    }

    private static (ProcessSnapshot Snapshot, OpenedFileIdentity ImageAnchor)? TryBuildSnapshot(
        int processId,
        SafeProcessHandle processHandle)
    {
        var reportedBefore = QueryImagePath(processHandle);
        if (reportedBefore is null || !GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            return null;
        }

        var imageAnchor = WindowsFinalPath.TryOpenFileAnchor(reportedBefore, denyReplacement: true);
        if (imageAnchor is null)
        {
            return null;
        }

        var ownershipTransferred = false;
        try
        {
            var reportedAfter = QueryImagePath(processHandle);
            if (reportedAfter is null ||
                !WindowsFinalPath.ReportedPathMatchesFinal(reportedBefore, imageAnchor.FinalPath) ||
                !WindowsFinalPath.ReportedPathMatchesFinal(reportedAfter, imageAnchor.FinalPath))
            {
                return null;
            }

            if (!GetExitCodeProcess(processHandle, out var exitCode) || exitCode != StillActive)
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(imageAnchor.FinalPath);
            var snapshot = new ProcessSnapshot(
                processId,
                DateTimeOffset.FromFileTime(ToLong(creationTime)),
                imageAnchor.FinalPath,
                Path.GetFileName(imageAnchor.FinalPath),
                version.CompanyName,
                version.ProductName,
                version.ProductVersion,
                version.OriginalFilename,
                HasVisibleTopLevelWindow(processId),
                imageAnchor.ObjectId);
            ownershipTransferred = true;
            return (snapshot, imageAnchor);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            // Ownership transfers only when a snapshot was returned. The caller's
            // tuple keeps the same open image handle as the termination lease.
            if (!ownershipTransferred)
            {
                imageAnchor.Dispose();
            }
        }
    }

    private static string? QueryImagePath(SafeProcessHandle handle)
    {
        var capacity = 32_767u;
        var builder = new StringBuilder((int)capacity + 1);
        return QueryFullProcessImageName(handle, 0, builder, ref capacity)
            ? builder.ToString()
            : null;
    }

    private static bool HasVisibleTopLevelWindow(int processId)
    {
        var found = false;
        _ = EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == (uint)processId && IsWindowVisible(window))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    private sealed class StableProcessTerminationLease(
        SafeProcessHandle processHandle,
        OpenedFileIdentity imageAnchor,
        ProcessSnapshot snapshot,
        bool canTerminate) : IProcessTerminationLease
    {
        private bool _disposed;

        public ProcessSnapshot Snapshot { get; } = snapshot;

        public SingleProcessTerminationResult TerminateSingleProcess()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (WaitForSingleObject(processHandle, 0) == WaitObject0)
            {
                return SingleProcessTerminationResult.AlreadyExited;
            }

            if (!canTerminate)
            {
                return SingleProcessTerminationResult.Failed;
            }

            if (!TerminateProcess(processHandle, CoordinatorExitCode))
            {
                return WaitForSingleObject(processHandle, 0) == WaitObject0
                    ? SingleProcessTerminationResult.AlreadyExited
                    : SingleProcessTerminationResult.Failed;
            }

            return WaitForSingleObject(processHandle, 2_000) == WaitObject0
                ? SingleProcessTerminationResult.Terminated
                : SingleProcessTerminationResult.Failed;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            imageAnchor.Dispose();
            processHandle.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
