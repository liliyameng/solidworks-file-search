using SwSearch.Indexer;

namespace SwSearch.Indexer.Tests;

public sealed class SystemLoadMonitorTests
{
    /// <summary>
    /// Produces monotonically increasing cumulative counters, matching the
    /// GetSystemTimes convention the monitor differentiates against.
    /// </summary>
    private sealed class CumulativeScript
    {
        private readonly Queue<(long DeltaTicks, double Cpu, double Memory)> _steps;
        private long _idle;
        private long _busy;
        private long _ticks;

        internal CumulativeScript(params (long DeltaTicks, double Cpu, double Memory)[] steps)
        {
            _steps = new Queue<(long, double, double)>(steps);
        }

        internal RawSystemLoad Next()
        {
            var (delta, cpu, memory) = _steps.Dequeue();
            _ticks += delta;
            _idle += (long)Math.Round(delta * (1 - cpu));
            _busy += (long)Math.Round(delta * cpu);
            return new RawSystemLoad(_idle, _busy, memory, _ticks);
        }
    }

    private static SystemLoadMonitor MonitorOver(params (long DeltaTicks, double Cpu, double Memory)[] steps)
        => new(new CumulativeScript(steps).Next);

    [Fact]
    public void MapToLevel_CoversTheDocumentedBands()
    {
        Assert.Equal(0, SystemLoadMonitor.MapToLevel(0.10, 0.5));
        Assert.Equal(0, SystemLoadMonitor.MapToLevel(0.49, 0.9));
        Assert.Equal(1, SystemLoadMonitor.MapToLevel(0.50, 0.5));
        Assert.Equal(1, SystemLoadMonitor.MapToLevel(0.74, 0.5));
        Assert.Equal(2, SystemLoadMonitor.MapToLevel(0.75, 0.5));
        Assert.Equal(2, SystemLoadMonitor.MapToLevel(0.84, 0.5));
        Assert.Equal(3, SystemLoadMonitor.MapToLevel(0.85, 0.5));
        Assert.Equal(3, SystemLoadMonitor.MapToLevel(0.99, 0.5));
    }

    [Fact]
    public void MapToLevel_MemoryPressureDominates()
    {
        Assert.Equal(3, SystemLoadMonitor.MapToLevel(0.10, 0.07));
        Assert.Equal(3, SystemLoadMonitor.MapToLevel(0.05, 0.05));
    }

    [Fact]
    public void LevelDelays_SpanOneToSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SystemLoadMonitor.LevelDelays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), SystemLoadMonitor.LevelDelays[1]);
        Assert.Equal(TimeSpan.FromSeconds(15), SystemLoadMonitor.LevelDelays[2]);
        Assert.Equal(TimeSpan.FromSeconds(60), SystemLoadMonitor.LevelDelays[3]);
    }

    [Fact]
    public void Sample_ComputesCpuFromCumulativeDeltas()
    {
        var monitor = MonitorOver(
            (10_000_000, 0.10, 0.5),   // baseline window (no prior sample yet)
            (10_000_000, 0.90, 0.5));  // 90% busy delta window

        Assert.Equal(0, monitor.Sample());   // first call has no delta to differentiate
        Assert.Equal(3, monitor.Sample());   // 90% cpu maps to level 3
    }

    [Fact]
    public void Sample_RisesImmediatelyButFallsOnlyAfterThreeConsecutiveLowerLevels()
    {
        var monitor = MonitorOver(
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.90, 0.5),
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.10, 0.5));

        monitor.Sample();                     // baseline -> 0
        Assert.Equal(3, monitor.Sample());    // busy -> rises immediately
        Assert.Equal(3, monitor.Sample());    // calm streak 1
        Assert.Equal(3, monitor.Sample());    // calm streak 2: still held
    }

    [Fact]
    public void Sample_BusySampleResetsTheFallingStreak()
    {
        var monitor = MonitorOver(
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.90, 0.5),
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.90, 0.5),
            (10_000_000, 0.10, 0.5));

        monitor.Sample();                     // baseline
        monitor.Sample();                     // -> 3
        monitor.Sample();                     // streak 1
        monitor.Sample();                     // streak 2
        monitor.Sample();                     // busy again: streak resets
        Assert.Equal(3, monitor.Sample());    // single calm sample cannot fall yet
    }

    [Fact]
    public void Sample_EventuallyFallsAfterSustainedCalm()
    {
        var monitor = MonitorOver(
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.90, 0.5),
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.10, 0.5),
            (10_000_000, 0.10, 0.5));

        monitor.Sample();
        Assert.Equal(3, monitor.Sample());
        monitor.Sample();                     // streak 1
        monitor.Sample();                     // streak 2
        Assert.Equal(0, monitor.Sample());    // streak 3 completes the fall
    }
}
