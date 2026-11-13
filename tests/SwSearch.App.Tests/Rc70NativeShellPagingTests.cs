using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc70NativeShellPagingTests
{
    [TestMethod]
    public async Task NativeShellSession_ReadsEveryBrokerPageWithoutMutatingInteractiveWindow()
    {
        var client = new PagedClient();
        using var session = new NativeShellSearchSession(
            client,
            new SearchQuery(
                "fixture",
                SearchMode.All,
                Kinds: [SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly],
                SortColumn: "fileName",
                SortDescending: false,
                IsInteractive: false),
            generation: 71,
            CancellationToken.None);

        var first = await session.ReadNextPageAsync(CancellationToken.None);
        var second = await session.ReadNextPageAsync(CancellationToken.None);
        var third = await session.ReadNextPageAsync(CancellationToken.None);

        Assert.AreEqual(500, first.Paths.Count);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(500, second.Paths.Count);
        Assert.IsTrue(second.HasMore);
        Assert.AreEqual(7, third.Paths.Count);
        Assert.IsFalse(third.HasMore);
        CollectionAssert.AreEqual(new[] { 0, 500, 1000 }, client.Queries.Select(query => query.Offset).ToArray());
        Assert.IsTrue(client.Queries.All(query => query.Limit == NativeShellSearchSession.PageSize));
        Assert.IsTrue(client.Queries.All(query => !query.IsInteractive.GetValueOrDefault(true)));
        Assert.IsTrue(client.Queries.Skip(1).All(query => query.ExpectedRevision == 91));
        Assert.IsTrue(client.Queries.Skip(1).All(query => query.PageFingerprint == "native-stable"));
    }

    [TestMethod]
    public async Task NativeShellSession_LeavesOffsetUntouchedForTransientBrokerCache()
    {
        var capability = new RuntimeCapability(CapabilityState.Unavailable, "BROKER_RECONNECTING", "retry");
        var client = new SequenceClient(
            new SearchClientResult([], capability, Availability: SearchResultAvailability.CacheTemporarilyUnavailable),
            new SearchClientResult([Hit(1)], ReadyCapability, HasMore: false, IndexRevision: 8, PageFingerprint: "stable"));
        using var session = new NativeShellSearchSession(
            client,
            new SearchQuery("fixture", SearchMode.All),
            generation: 72,
            CancellationToken.None);

        var unavailable = await session.ReadNextPageAsync(CancellationToken.None);
        var delivered = await session.ReadNextPageAsync(CancellationToken.None);

        Assert.IsTrue(unavailable.TemporarilyUnavailable);
        Assert.AreEqual(0, unavailable.Offset);
        Assert.AreEqual(0, delivered.Offset);
        CollectionAssert.AreEqual(new[] { 0, 0 }, client.Queries.Select(query => query.Offset).ToArray());
    }

    [TestMethod]
    public async Task NativeShellSession_RejectsAPageResetBeforeItCanBlendGenerations()
    {
        var client = new SequenceClient(
            new SearchClientResult(CreateHits(0, 500), ReadyCapability, HasMore: true, IndexRevision: 12, PageFingerprint: "one"),
            new SearchClientResult([], ReadyCapability, PageReset: true, IndexRevision: 13, PageFingerprint: "two"));
        using var session = new NativeShellSearchSession(
            client,
            new SearchQuery("fixture", SearchMode.All),
            generation: 73,
            CancellationToken.None);

        var first = await session.ReadNextPageAsync(CancellationToken.None);
        var reset = await session.ReadNextPageAsync(CancellationToken.None);

        Assert.IsTrue(first.HasMore);
        Assert.IsTrue(reset.Reset);
        Assert.AreEqual(500, reset.Offset);
        Assert.AreEqual(73L, reset.Generation);
    }

    private static readonly RuntimeCapability ReadyCapability =
        new(CapabilityState.Available, "INDEX_READY", "ready");

    private static SearchHit Hit(int index) => new(
        $@"C:\Fixture\Part-{index}.SLDPRT",
        SolidWorksDocumentKind.Part,
        $"Part-{index}.SLDPRT",
        null,
        null,
        null,
        index,
        false);

    private static IReadOnlyList<SearchHit> CreateHits(int start, int count) =>
        Enumerable.Range(start, count).Select(Hit).ToArray();

    private sealed class PagedClient : ISearchClient
    {
        public List<SearchQuery> Queries { get; } = [];

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            var count = query.Offset switch
            {
                0 or 500 => 500,
                1000 => 7,
                _ => 0,
            };
            return ValueTask.FromResult(new SearchClientResult(
                CreateHits(query.Offset, count),
                ReadyCapability,
                HasMore: query.Offset < 1000,
                IndexRevision: 91,
                PageFingerprint: "native-stable"));
        }
    }

    private sealed class SequenceClient(params SearchClientResult[] responses) : ISearchClient
    {
        private readonly Queue<SearchClientResult> _responses = new(responses);

        public List<SearchQuery> Queries { get; } = [];

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return ValueTask.FromResult(_responses.Dequeue());
        }
    }
}
