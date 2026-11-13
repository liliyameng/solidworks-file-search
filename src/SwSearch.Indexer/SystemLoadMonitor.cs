using System.Runtime.InteropServices;

namespace SwSearch.Indexer;

/// <summary>
/// Raw cumulative counters from one load probe. CPU tick buckets follow the
/// <c>GetSystemTimes</c> convention where the kernel bucket already includes
/// idle time; <see cref="BusyTicks"/> must therefore be
/// <c>(kernel - idle) + user</c> so that idle + busy = total.
/// </summary>
internal readonly record struct RawSystemLoad(
    long IdleTicks,
    long BusyTicks,
    double AvailableMemoryFraction,
    long UtcTicks);

/// <summary>
/// Maps sampled system load (global CPU usage and available physical memory)
/// to the background loop cadence: a busy system stretches the poll from 1 s
/// up to 60 s and postpones routine discovery refresh passes, so indexing
/// never competes with foreground work (the observed kernel-side file storm
/// came from discovery reads at 241 MB/s while the user was working).
/// Rising a level applies immediately; falling requires three consecutive
/// lower samples so a transient spike cannot cause cadence churn.
/// </summary>
internal sealed class SystemLoadMonitor
{
    internal static readonly TimeSpan[] LevelDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
    };

    private readonly Func<RawSystemLoad> _sampler;
    private RawSystemLoad _previous;
    private bool _hasPrevious;
    private double _lastCpuUsage;
    private int _level;
    private int _lowerStreak;

    internal SystemLoadMonitor() : this(SampleOperatingSystem) { }

    internal SystemLoadMonitor(Func<RawSystemLoad> sampler)
    {
        _sampler = sampler;
    }

    /// <summary>Level of the most recent <see cref="Sample"/> call (0..3, higher is busier).</summary>
    internal int CurrentLevel => _level;

    internal int Sample()
    {
        var raw = _sampler();
        var cpu = _lastCpuUsage;
        if (_hasPrevious && raw.UtcTicks > _previous.UtcTicks)
        {
            var idleDelta = raw.IdleTicks - _previous.IdleTicks;
            var busyDelta = raw.BusyTicks - _previous.BusyTicks;
            var totalDelta = idleDelta + busyDelta;
            if (totalDelta > 0)
            {
                cpu = Math.Clamp(1 - (double)idleDelta / totalDelta, 0, 1);
                _lastCpuUsage = cpu;
            }
        }
        _previous = raw;
        _hasPrevious = true;

        var mapped = MapToLevel(cpu, raw.AvailableMemoryFraction);
        if (mapped > _level)
        {
            _level = mapped;
            _lowerStreak = 0;
        }
        else if (mapped < _level)
        {
            if (++_lowerStreak >= 3)
            {
                _level = mapped;
                _lowerStreak = 0;
            }
        }
        else
        {
            _lowerStreak = 0;
        }
        return _level;
    }

    /// <summary>
    /// Pure level mapping for testability. Memory pressure dominates: a
    /// machine under 8% available RAM is level 3 regardless of CPU, because
    /// paging makes even small read bursts expensive.
    /// </summary>
    internal static int MapToLevel(double cpuUsage, double availableMemoryFraction)
    {
        if (availableMemoryFraction < 0.08)
        {
            return 3;
        }
        if (cpuUsage >= 0.85)
        {
            return 3;
        }
        if (cpuUsage >= 0.75)
        {
            return 2;
        }
        if (cpuUsage >= 0.50)
        {
            return 1;
        }
        return 0;
    }

    private static RawSystemLoad SampleOperatingSystem()
    {
        var utcTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // A failed probe reads as the previous sample's baseline; the
            // hysteresis keeps the cadence stable either way.
            return new RawSystemLoad(0, 0, 1.0, utcTicks);
        }

        var idleTicks = ((long)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
        var kernelTicks = ((long)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
        var userTicks = ((long)user.dwHighDateTime << 32) | user.dwLowDateTime;
        var busyTicks = kernelTicks - idleTicks + userTicks;

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        var availableFraction = 1.0;
        if (GlobalMemoryStatusEx(ref memory) && memory.ullTotalPhys > 0)
        {
            availableFraction = Math.Clamp(
                (double)memory.ullAvailPhys / memory.ullTotalPhys,
                0,
                1);
        }

        return new RawSystemLoad(idleTicks, busyTicks, availableFraction, utcTicks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        internal uint dwLowDateTime;
        internal uint dwHighDateTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        internal uint dwLength;
        internal uint dwMemoryLoad;
        internal ulong ullTotalPhys;
        internal ulong ullAvailPhys;
        internal ulong ullTotalPageFile;
        internal ulong ullAvailPageFile;
        internal ulong ullTotalVirtual;
        internal ulong ullAvailVirtual;
        internal ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
