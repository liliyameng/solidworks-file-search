using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class SearchCandidatePromotionQueueTests
{
    [Fact]
    public async Task ConcurrentProducers_AreBoundedDeduplicatedAndUseOneConsumer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<string>();
        var processedGate = new object();
        var active = 0;
        var maximumActive = 0;
        await using var queue = new SearchCandidatePromotionQueue(
            4,
            async (query, cancellationToken) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, nowActive);
                try
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    lock (processedGate)
                    {
                        processed.Add(query.Text);
                    }

                    return 1;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        Assert.True(queue.TryEnqueue(new SearchQuery("same-path", SearchMode.All)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Parallel.For(0, 2_000, _ =>
            queue.TryEnqueue(new SearchQuery("same-path", SearchMode.All)));
        Parallel.For(0, 100, index =>
            queue.TryEnqueue(new SearchQuery("unique-" + index, SearchMode.All)));

        var blocked = queue.Snapshot;
        Assert.InRange(blocked.PendingCount, 0, 4);
        Assert.True(blocked.DeduplicatedCount >= 2_000);
        Assert.True(blocked.DroppedCount > 0);

        release.TrySetResult();
        await WaitUntilAsync(() => queue.Snapshot.CompletedCount == queue.Snapshot.AcceptedCount, TimeSpan.FromSeconds(2));
        var finished = queue.Snapshot;
        Assert.Equal(1, maximumActive);
        lock (processedGate)
        {
            Assert.Equal(processed.Count, processed.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task Dispose_CancelsBlockedConsumerAndLeavesNoTaskRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new SearchCandidatePromotionQueue(
            2,
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 1;
            });

        Assert.True(queue.TryEnqueue(new SearchQuery("cancel-me", SearchMode.All)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(queue.Snapshot.ConsumerCompleted);
        Assert.False(queue.TryEnqueue(new SearchQuery("after-dispose", SearchMode.All)));
    }

    [Fact]
    public async Task FailureIsIsolated_AndConsumerContinuesWithoutPublishingRevision()
    {
        var failures = 0;
        await using var queue = new SearchCandidatePromotionQueue(
            4,
            (query, _) => query.Text switch
            {
                "throws" => ValueTask.FromException<int>(new IOException("fixture")),
                "no-change" => ValueTask.FromResult(0),
                _ => ValueTask.FromResult(1),
            },
            (_, _) =>
            {
                Interlocked.Increment(ref failures);
                return ValueTask.CompletedTask;
            });

        Assert.True(queue.TryEnqueue(new SearchQuery("throws", SearchMode.All)));
        Assert.True(queue.TryEnqueue(new SearchQuery("no-change", SearchMode.All)));
        Assert.True(queue.TryEnqueue(new SearchQuery("committed", SearchMode.All)));
        await WaitUntilAsync(() => queue.Snapshot.CompletedCount == 3, TimeSpan.FromSeconds(1));

        Assert.Equal(1, failures);
        Assert.False(queue.Snapshot.ConsumerCompleted);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= stopAt)
            {
                throw new TimeoutException("Promotion queue did not become idle.");
            }

            await Task.Delay(10);
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
