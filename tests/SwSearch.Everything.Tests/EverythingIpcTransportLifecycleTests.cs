using SwSearch.Everything;

namespace SwSearch.Everything.Tests;

public sealed class EverythingIpcTransportLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_WaitsForInflightQueryAndRejectsNewQueries()
    {
        await using var transport = new EverythingIpcTransport(TimeSpan.FromSeconds(30));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterQueryGateAcquiredForTesting = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var query = transport.QueryPageAsync(
            SolidWorksEverythingQuery.Search,
            0,
            1,
            CancellationToken.None).AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var dispose = transport.DisposeAsync().AsTask();

        // Dispose must remain pending while the admitted query is still running.
        var first = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(dispose, first);

        // New queries are rejected atomically once dispose has been requested.
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transport.QueryPageAsync(SolidWorksEverythingQuery.Search, 0, 1, CancellationToken.None).AsTask());

        // Release the in-flight query through cancellation; dispose then drains it.
        release.TrySetCanceled();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndCompletesWhenIdle()
    {
        await using var transport = new EverythingIpcTransport(TimeSpan.FromSeconds(30));

        var first = transport.DisposeAsync().AsTask();
        var second = transport.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transport.QueryPageAsync(SolidWorksEverythingQuery.Search, 0, 1, CancellationToken.None).AsTask());
    }
}
