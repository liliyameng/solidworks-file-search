using SwSearch.Indexer;

namespace SwSearch.Indexer.Tests;

public sealed class DiscoveryIoPacingTests
{
    [Fact]
    public void IdleSystemRunsAtFullSpeed()
    {
        Assert.Equal(TimeSpan.Zero, DiscoveryIoPacing.DelayFor(0));
    }

    [Fact]
    public void DelaysGrowMonotonicallyWithLoadLevel()
    {
        for (var level = 1; level < DiscoveryIoPacing.ChunkDelays.Length; level++)
        {
            Assert.True(
                DiscoveryIoPacing.DelayFor(level) > DiscoveryIoPacing.DelayFor(level - 1),
                $"level {level} must pace more than {level - 1}");
        }
    }

    [Fact]
    public void LevelIsClampedToTheTable()
    {
        var max = DiscoveryIoPacing.DelayFor(DiscoveryIoPacing.ChunkDelays.Length - 1);
        Assert.Equal(max, DiscoveryIoPacing.DelayFor(99));
        Assert.Equal(TimeSpan.Zero, DiscoveryIoPacing.DelayFor(-1));
    }

    [Fact]
    public void BusyLevelTargetsApproximatelyFiveMegabytesPerSecond()
    {
        // 1024 MFT records ~= 1 MB of metadata reads; 200 ms per chunk caps a
        // level-3 storm at roughly 5 MB/s instead of the observed 240 MB/s.
        var level3 = DiscoveryIoPacing.DelayFor(3);
        Assert.True(level3 >= TimeSpan.FromMilliseconds(150), level3.ToString());
        Assert.True(level3 <= TimeSpan.FromMilliseconds(300), level3.ToString());
    }
}
