using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Everything.Tests;

public sealed class EverythingDiscoveryProviderTests
{
    [Fact]
    public async Task DiscoverAsync_PaginatesByRawReturnedCount()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new PagingTransport(
        [
            new(2, 0, 1, [new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now)]),
            new(2, 1, 1, [new FileCandidate(@"D:\b.SLDASM", SolidWorksDocumentKind.Assembly, 2, now)]),
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 1);

        var results = new List<FileCandidate>();
        await foreach (var result in provider.DiscoverAsync(CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal(2, results.Count);
        Assert.All(transport.Searches, search => Assert.Equal(SolidWorksEverythingQuery.Search, search));
        Assert.Equal(2, provider.DiscoveryProgress.IndexedFileCount);
        Assert.Equal(2, provider.DiscoveryProgress.EstimatedTotalCount);
        Assert.Equal(RuntimeServiceState.Idle, provider.DiscoveryProgress.State);
        Assert.Equal("EVERYTHING_SNAPSHOT_COMPLETE", provider.DiscoveryProgress.Code);
        var diagnostics = provider.EverythingDiagnostics;
        Assert.True(diagnostics.Attempted);
        Assert.True(diagnostics.Available);
        Assert.Equal("EVERYTHING_QUERY_READY", diagnostics.AvailabilityCode);
        Assert.Equal(2, diagnostics.PageCount);
        Assert.Equal(2, diagnostics.ReturnedEntryCount);
        Assert.Equal(2, diagnostics.EmittedEntryCount);
        Assert.Null(diagnostics.FailureCode);
        Assert.True(diagnostics.Completed);
    }

    [Fact]
    public async Task ProbeAsync_RecordsUnavailableStateBeforeAnyQueryAttempt()
    {
        var transport = new ProbeTransport(new RuntimeCapability(
            CapabilityState.Degraded,
            "EVERYTHING_IPC_NOT_FOUND",
            "missing"));
        await using var provider = new EverythingDiscoveryProvider(transport);

        var capability = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal("EVERYTHING_IPC_NOT_FOUND", capability.Code);
        var diagnostics = provider.EverythingDiagnostics;
        Assert.False(diagnostics.Attempted);
        Assert.False(diagnostics.Available);
        Assert.Equal("EVERYTHING_IPC_NOT_FOUND", diagnostics.AvailabilityCode);
        Assert.Equal(0, diagnostics.PageCount);
        Assert.Equal(0, diagnostics.EmittedEntryCount);
        Assert.False(diagnostics.Completed);
    }

    [Fact]
    public async Task DiscoverAsync_SnapshotChange_RetriesAndSucceeds()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new RetryTransport(
        [
            // Attempt 0: unstable snapshot
            [
                new(2, 0, 1, [new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now)]),
                new(3, 1, 1, [new FileCandidate(@"D:\b.SLDASM", SolidWorksDocumentKind.Assembly, 2, now)]),
            ],
            // Attempt 1: stable snapshot
            [
                new(3, 0, 2,
                [
                    new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
                    new FileCandidate(@"C:\c.SLDASM", SolidWorksDocumentKind.Assembly, 3, now),
                ]),
                new(3, 2, 1, [new FileCandidate(@"D:\b.SLDASM", SolidWorksDocumentKind.Assembly, 2, now)]),
            ],
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 2);

        var results = new List<FileCandidate>();
        await foreach (var result in provider.DiscoverAsync(CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal(3, results.Count);
        Assert.Equal(RuntimeServiceState.Idle, provider.DiscoveryProgress.State);
        Assert.Equal("EVERYTHING_SNAPSHOT_COMPLETE", provider.DiscoveryProgress.Code);
        Assert.Equal(2, transport.AttemptCount);
        Assert.True(provider.EverythingDiagnostics.Completed);
    }

    [Fact]
    public async Task DiscoverAsync_SnapshotChange_ExhaustsRetries()
    {
        var now = DateTimeOffset.UtcNow;
        // Unstable page sequence that changes on every attempt.
        var transport = new AlwaysChangingTransport(
            new(2, 0, 1, [new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now)]));
        await using var provider = new EverythingDiscoveryProvider(transport, 1);

        var exception = await Assert.ThrowsAsync<EverythingQueryException>(async () =>
        {
            await foreach (var _ in provider.DiscoverAsync(CancellationToken.None))
            {
            }
        });

        Assert.Equal("EVERYTHING_SNAPSHOT_RETRIES_EXHAUSTED", exception.Code);
        Assert.Equal(RuntimeServiceState.Unavailable, provider.DiscoveryProgress.State);
        Assert.Equal("EVERYTHING_SNAPSHOT_INCOMPLETE", provider.DiscoveryProgress.Code);
        Assert.True(provider.EverythingDiagnostics.Attempted);
        Assert.False(provider.EverythingDiagnostics.Completed);
    }

    [Fact]
    public async Task DiscoverAsync_BuffersBeforeYield_NoPartialResults()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new RetryTransport(
        [
            // Attempt 0: snapshot changes after first page
            [
                new(3, 0, 1, [new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now)]),
                new(5, 1, 1, [new FileCandidate(@"D:\b.SLDASM", SolidWorksDocumentKind.Assembly, 2, now)]),
            ],
            // Attempt 1: stable snapshot
            [
                new(5, 0, 3,
                [
                    new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
                    new FileCandidate(@"C:\d.SLDASM", SolidWorksDocumentKind.Assembly, 4, now),
                    new FileCandidate(@"C:\e.SLDASM", SolidWorksDocumentKind.Assembly, 5, now),
                ]),
                new(5, 3, 2,
                [
                    new FileCandidate(@"C:\f.SLDPRT", SolidWorksDocumentKind.Part, 6, now),
                    new FileCandidate(@"D:\b.SLDASM", SolidWorksDocumentKind.Assembly, 2, now),
                ]),
            ],
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 3);

        var results = new List<FileCandidate>();
        await foreach (var result in provider.DiscoverAsync(CancellationToken.None))
        {
            results.Add(result);
        }

        // Only the stable snapshot's results are yielded (none from attempt 0).
        Assert.Equal(5, results.Count);
        Assert.Equal(2, transport.AttemptCount);
    }

    [Fact]
    public async Task DiscoverAsync_ConsumerStopsEarly_DoesNotPublishCompleteProgress()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new PagingTransport(
        [
            new(2, 0, 2,
            [
                new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
                new FileCandidate(@"D:\b.SLDASM", SolidWorksDocumentKind.Assembly, 2, now),
            ]),
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 2);
        await using (var enumerator = provider.DiscoverAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.Equal(RuntimeServiceState.Unavailable, provider.DiscoveryProgress.State);
        // The iterator increments only after the consumer resumes past a
        // yielded item. Disposing at that suspension point must still leave
        // completion unpublished, regardless of the transient count.
        Assert.Equal(0, provider.DiscoveryProgress.IndexedFileCount);
        Assert.False(provider.EverythingDiagnostics.Completed);
    }

    [Fact]
    public async Task DiscoverAsync_PublishesLiveProgressWhileStableSnapshotIsConsumed()
    {
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, 1_025)
            .Select(index => new FileCandidate(
                $@"C:\live-{index}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                now))
            .ToArray();
        var transport = new PagingTransport(
        [
            new EverythingQueryPage(1_025, 0, 1_025, items),
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 1_025);
        await using var enumerator = provider.DiscoverAsync(CancellationToken.None).GetAsyncEnumerator();

        for (var index = 0; index < 1_025; index++)
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.Equal(1_024, provider.DiscoveryProgress.IndexedFileCount);
        Assert.Equal(1_025, provider.DiscoveryProgress.EstimatedTotalCount);
        Assert.Equal(RuntimeServiceState.Running, provider.DiscoveryProgress.State);
        Assert.Equal("EVERYTHING_SNAPSHOT_READING", provider.DiscoveryProgress.Code);
    }

    [Fact]
    public async Task DiscoverAsync_EmptyPageBeforeReportedTotal_RecordsTruncationInsteadOfSilentlyCompleting()
    {
        var transport = new PagingTransport(
        [
            new(1, 0, 0, Array.Empty<FileCandidate>()),
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 1);

        var exception = await Assert.ThrowsAsync<EverythingQueryException>(async () =>
        {
            await foreach (var _ in provider.DiscoverAsync(CancellationToken.None))
            {
            }
        });

        Assert.Equal("EVERYTHING_SNAPSHOT_TRUNCATED", exception.Code);
        var diagnostics = provider.EverythingDiagnostics;
        Assert.True(diagnostics.Attempted);
        Assert.Equal(1, diagnostics.PageCount);
        Assert.Equal(0, diagnostics.ReturnedEntryCount);
        Assert.Equal(0, diagnostics.EmittedEntryCount);
        Assert.Equal("EVERYTHING_SNAPSHOT_TRUNCATED", diagnostics.FailureCode);
        Assert.False(diagnostics.Completed);
    }

    [Fact]
    public async Task DiscoverAsync_InvalidPageBoundary_RecordsProtocolFailure()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new PagingTransport(
        [
            new EverythingQueryPage(
                TotalItems: 1,
                Offset: 0,
                ReturnedItems: 2,
                Items: [new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now)]),
        ]);
        await using var provider = new EverythingDiscoveryProvider(transport, 2);

        var exception = await Assert.ThrowsAsync<EverythingQueryException>(async () =>
        {
            await foreach (var _ in provider.DiscoverAsync(CancellationToken.None))
            {
            }
        });

        Assert.Equal("EVERYTHING_INVALID_PAGE", exception.Code);
        var diagnostics = provider.EverythingDiagnostics;
        Assert.Equal(1, diagnostics.PageCount);
        Assert.Equal(2, diagnostics.ReturnedEntryCount);
        Assert.Equal(0, diagnostics.EmittedEntryCount);
        Assert.Equal("EVERYTHING_INVALID_PAGE", diagnostics.FailureCode);
        Assert.False(diagnostics.Completed);
    }

    [Fact]
    public async Task DefaultPageSize_Is16384()
    {
        var transport = new PagingTransport([new EverythingQueryPage(0, 0, 0, Array.Empty<FileCandidate>())]);
        await using var provider = new EverythingDiscoveryProvider(transport);
        await foreach (var _ in provider.DiscoverAsync(CancellationToken.None))
        {
        }
        Assert.Equal((uint)16384, transport.LastPageSize);
    }

    /// <summary>Transport that returns pages from a single list, used for simple (non-retry) tests.</summary>
    private sealed class PagingTransport(EverythingQueryPage[] pages) : IEverythingQueryTransport
    {
        private int _index;
        internal List<string> Searches { get; } = [];
        internal uint LastPageSize { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "READY", "ready"));

        public ValueTask<EverythingQueryPage> QueryPageAsync(
            string search,
            uint offset,
            uint maximumResults,
            CancellationToken cancellationToken)
        {
            Searches.Add(search);
            LastPageSize = maximumResults;
            return ValueTask.FromResult(pages[_index++]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Transport that returns pages from a per-attempt list. When the provider
    /// retries (offset resets to 0 after a non-zero offset), the transport
    /// advances to the next attempt's pages.
    /// </summary>
    private sealed class RetryTransport(List<List<EverythingQueryPage>> attempts) : IEverythingQueryTransport
    {
        private int _attempt;
        private int _index;
        private uint _lastOffset;
        internal int AttemptCount => _attempt + 1;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "READY", "ready"));

        public ValueTask<EverythingQueryPage> QueryPageAsync(
            string search,
            uint offset,
            uint maximumResults,
            CancellationToken cancellationToken)
        {
            if (offset == 0 && _lastOffset > 0)
            {
                _attempt = Math.Min(_attempt + 1, attempts.Count - 1);
                _index = 0;
            }

            _lastOffset = offset;
            return ValueTask.FromResult(attempts[_attempt][_index++]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Transport where the second page always reports a different TotalItems,
    /// causing every attempt to detect a snapshot change.
    /// </summary>
    private sealed class AlwaysChangingTransport(EverythingQueryPage firstPage) : IEverythingQueryTransport
    {
        private int _calls;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "READY", "ready"));

        public ValueTask<EverythingQueryPage> QueryPageAsync(
            string search,
            uint offset,
            uint maximumResults,
            CancellationToken cancellationToken)
        {
            _calls++;
            return ValueTask.FromResult(
                new EverythingQueryPage(
                    TotalItems: (uint)(firstPage.TotalItems + _calls + 1),
                    Offset: offset,
                    ReturnedItems: 1,
                    Items: firstPage.Items));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProbeTransport(RuntimeCapability capability) : IEverythingQueryTransport
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(capability);

        public ValueTask<EverythingQueryPage> QueryPageAsync(
            string search,
            uint offset,
            uint maximumResults,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<EverythingQueryPage>(
                new EverythingQueryException("EVERYTHING_QUERY_NOT_EXPECTED", "query should not run"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
