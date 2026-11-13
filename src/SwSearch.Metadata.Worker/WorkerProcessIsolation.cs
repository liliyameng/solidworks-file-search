using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwSearch.Metadata.Worker;

/// <summary>
/// Native Windows job object owned by the client.  Closing it terminates an
/// orphaned worker and the kernel enforces the per-process memory ceiling.
/// No service, driver or separately installed component is required.
/// </summary>
internal sealed class WorkerProcessIsolation : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _job;

    internal WorkerProcessIsolation(long maximumProcessMemoryBytes)
    {
        if (maximumProcessMemoryBytes is < 64L * 1024 * 1024 or > 2L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumProcessMemoryBytes));
        }

        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid)
        {
            throw new WorkerIsolationException("job-create", Marshal.GetLastWin32Error());
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitKillOnJobClose,
            },
            ProcessMemoryLimit = (UIntPtr)(ulong)maximumProcessMemoryBytes,
        };
        if (!SetInformationJobObject(
                _job,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new WorkerIsolationException("job-configure", error);
        }
    }

    internal void Attach(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_job, process))
        {
            throw new WorkerIsolationException("job-attach", Marshal.GetLastWin32Error());
        }

        // Priority is advisory only; isolation and the memory/kill guarantees
        // are already active before the primary thread can run.
        _ = SetPriorityClass(process, BelowNormalPriorityClass);
    }

    public void Dispose() => _job.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    private const uint BelowNormalPriorityClass = 0x0000_4000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

internal sealed class WorkerIsolationException(string stage, int nativeErrorCode)
    : Win32Exception(nativeErrorCode)
{
    internal string Stage { get; } = stage;
}
