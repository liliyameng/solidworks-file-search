using System.Reflection;

namespace SwSearch.Indexer.Tests;

public sealed class Rc88MetadataThroughputTests
{
    [Fact]
    public async Task WriteAdmissionDoesNotWaitForAnActiveReadReservation()
    {
        var governor = new ResourceGovernor(IndexingRuntimeOptions.Default);
        SetNextReservation(governor, isRead: true, DateTimeOffset.UtcNow.AddMilliseconds(700));

        var read = governor.WaitForReadAsync(0, CancellationToken.None).AsTask();
        try
        {
            await WaitUntilGateIsHeldAsync(GetGate(governor, isRead: true));

            await governor.WaitForWriteAsync(0, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            await read.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ReadReservationsRemainSerializedWithinTheirOwnDirection()
    {
        var governor = new ResourceGovernor(IndexingRuntimeOptions.Default);
        SetNextReservation(governor, isRead: true, DateTimeOffset.UtcNow.AddMilliseconds(500));

        var firstRead = governor.WaitForReadAsync(0, CancellationToken.None).AsTask();
        await WaitUntilGateIsHeldAsync(GetGate(governor, isRead: true));
        var secondRead = governor.WaitForReadAsync(0, CancellationToken.None).AsTask();

        await Task.Delay(125);
        Assert.False(secondRead.IsCompleted);

        await firstRead.WaitAsync(TimeSpan.FromSeconds(2));
        await secondRead.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledWriteReservationReleasesItsDirectionGate()
    {
        var governor = new ResourceGovernor(IndexingRuntimeOptions.Default);
        SetNextReservation(governor, isRead: false, DateTimeOffset.UtcNow.AddMilliseconds(500));
        using var cancellation = new CancellationTokenSource();

        var blockedWrite = governor.WaitForWriteAsync(0, cancellation.Token).AsTask();
        await WaitUntilGateIsHeldAsync(GetGate(governor, isRead: false));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blockedWrite);

        SetNextReservation(governor, isRead: false, DateTimeOffset.MinValue);
        await governor.WaitForWriteAsync(0, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Rc87RateAndPressureLimitsRemainUnchanged()
    {
        var options = IndexingRuntimeOptions.Default;
        Assert.Equal(12, options.MaximumFilesPerSecond);
        Assert.Equal(32 * 1024 * 1024, options.MaximumReadBytesPerSecond);
        Assert.Equal(8 * 1024 * 1024, options.MaximumWriteBytesPerSecond);
        Assert.Equal(0.25, options.InteractiveThrottleFactor);
        Assert.Equal(750, options.SlowExtractionMilliseconds);
        Assert.Equal(2, options.MaximumConcurrentExtractions);

        var interactiveGovernor = new ResourceGovernor(options);
        interactiveGovernor.NotifyInteractiveActivity();
        Assert.Equal(3, interactiveGovernor.CurrentFilesPerSecond);
        Assert.Equal(1, interactiveGovernor.RecommendedConcurrency(options.MaximumConcurrentExtractions));

        var pressureGovernor = new ResourceGovernor(options);
        pressureGovernor.RecordExtraction(TimeSpan.FromMilliseconds(options.SlowExtractionMilliseconds));
        Assert.Equal(6, pressureGovernor.CurrentFilesPerSecond);
        Assert.Equal(1, pressureGovernor.RecommendedConcurrency(options.MaximumConcurrentExtractions));
    }

    private static SemaphoreSlim GetGate(ResourceGovernor governor, bool isRead)
    {
        var type = typeof(ResourceGovernor);
        var field = type.GetField(
            isRead ? "_readGate" : "_writeGate",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            type.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SemaphoreSlim>(field?.GetValue(governor));
    }

    private static void SetNextReservation(
        ResourceGovernor governor,
        bool isRead,
        DateTimeOffset value)
    {
        var field = typeof(ResourceGovernor).GetField(
            isRead ? "_nextRead" : "_nextWrite",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(governor, value);
    }

    private static async Task WaitUntilGateIsHeldAsync(SemaphoreSlim gate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (gate.CurrentCount != 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, gate.CurrentCount);
    }
}
