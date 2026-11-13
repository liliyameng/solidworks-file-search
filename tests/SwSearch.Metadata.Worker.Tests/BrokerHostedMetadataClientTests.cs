using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class BrokerHostedMetadataClientTests
{
    [Fact]
    public async Task UsesOneBelowNormalConsumerAndPrioritizesInteractiveRequests()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var order = new List<string>();
        var gate = new object();
        var active = 0;
        var maximumActive = 0;
        var consumerThread = 0;
        var priority = ThreadPriority.Normal;
        var engine = new FakeExtractionEngine((path, _) =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            consumerThread = Environment.CurrentManagedThreadId;
            priority = Thread.CurrentThread.Priority;
            if (path.EndsWith("first.SLDPRT", StringComparison.Ordinal))
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(3)));
            }

            lock (gate)
            {
                order.Add(Path.GetFileName(path));
            }
            Interlocked.Decrement(ref active);
            return Metadata(path);
        });
        await using var client = new BrokerHostedMetadataClient(engine);

        var first = client.ExtractAsync(Request("first.SLDPRT"), CancellationToken.None).AsTask();
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(3)));
        var background = client.ExtractAsync(Request("background.SLDPRT"), CancellationToken.None).AsTask();
        var interactive = client.ExtractAsync(
            Request("interactive.SLDPRT") with { WorkClass = MetadataWorkClass.Interactive },
            CancellationToken.None).AsTask();
        releaseFirst.Set();

        await Task.WhenAll(first, background, interactive).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, maximumActive);
        Assert.NotEqual(Environment.CurrentManagedThreadId, consumerThread);
        Assert.Equal(ThreadPriority.BelowNormal, priority);
        Assert.Equal(
            ["first.SLDPRT", "interactive.SLDPRT", "background.SLDPRT"],
            order);
    }

    [Fact]
    public async Task UnexpectedFileFailureDoesNotStopFollowingRequests()
    {
        var calls = 0;
        var engine = new FakeExtractionEngine((path, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("single-file fault");
            }

            return Metadata(path);
        });
        await using var client = new BrokerHostedMetadataClient(engine);

        var failed = await client.ExtractAsync(Request("bad.SLDPRT"), CancellationToken.None);
        var recovered = await client.ExtractAsync(Request("good.SLDPRT"), CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(MetadataErrorCodes.BrokerHostedInternalFault, failed.ErrorCode);
        Assert.True(failed.Retryable);
        Assert.NotNull(recovered.Metadata);
        Assert.Null(recovered.ErrorCode);
    }

    [Fact]
    public async Task ProbeDeclaresPureFileBrokerComposition()
    {
        await using var client = new BrokerHostedMetadataClient(
            new FakeExtractionEngine((path, _) => Metadata(path)));

        var capability = await client.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.Available, capability.State);
        Assert.Equal("BROKER_METADATA_HOSTED", capability.Code);
        Assert.Equal("1", capability.Details!["concurrency"]);
        Assert.Equal("disabled", capability.Details["documentManager"]);
        Assert.Equal("disabled", capability.Details["windowsShellPropertyStore"]);
        Assert.Equal("false", capability.Details["externalWorkerRequired"]);
    }

    private static MetadataRequest Request(string fileName) =>
        new(
            ContractVersions.MetadataPipe,
            Guid.NewGuid(),
            Path.Combine(@"C:\fixtures", fileName),
            ForceRefresh: true,
            TimeoutMilliseconds: 2_000);

    private static DocumentMetadata Metadata(string path) =>
        new(
            new FileCandidate(path, SolidWorksDocumentKind.Part, 1, DateTimeOffset.UnixEpoch),
            Array.Empty<DocumentProperty>(),
            Array.Empty<DocumentReference>(),
            MetadataSource.NoApi,
            DateTimeOffset.UtcNow);
}
