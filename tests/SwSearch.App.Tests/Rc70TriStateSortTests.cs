using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc70TriStateSortTests
{
    [TestMethod]
    public void SortMessage_RequiresAnExplicitKnownThreeStateDirection()
    {
        var router = new WebMessageRouter();

        Assert.IsTrue(router.TryValidate(
            """{"type":"sort","payload":{"column":"fileName","direction":"none"}}""",
            out var cancelled,
            out var error),
            error);
        Assert.AreEqual("sort", cancelled!.Type);
        Assert.IsFalse(router.TryValidate(
            """{"type":"sort","payload":{"column":"fileName","direction":"reset"}}""",
            out _,
            out _));
        Assert.IsFalse(router.TryValidate(
            """{"type":"sort","payload":{"column":"fileName"}}""",
            out _,
            out _));
    }

    [TestMethod]
    public async Task SortCycle_UsesBrokerQueryStateAndCancelsBackToDefaultRelevance()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new NoOpFileActions(), TimeSpan.Zero);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        string? publishedState = null;
        bridge.StateReady += state => publishedState = JsonSerializer.Serialize(state);
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "main" }));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.Count >= 1 && !viewModel.IsBusy);

        var queryCount = client.Count;
        bridge.HandleMessage("sort", SortPayload("fileName", "asc"));
        var ascending = await client.WaitForCountAsync(++queryCount);
        Assert.AreEqual("fileName", ascending.SortColumn);
        Assert.IsFalse(ascending.SortDescending);
        Assert.AreEqual("fileName", viewModel.CurrentSortColumn);
        Assert.IsFalse(viewModel.CurrentSortDescending);

        bridge.HandleMessage("sort", SortPayload("fileName", "desc"));
        var descending = await client.WaitForCountAsync(++queryCount);
        Assert.AreEqual("fileName", descending.SortColumn);
        Assert.IsTrue(descending.SortDescending);
        Assert.IsTrue(viewModel.CurrentSortDescending);

        // Third click on the same column clears the explicit sort. The index
        // recognizes a null SortColumn as its relevance/default ordering.
        bridge.HandleMessage("sort", SortPayload("fileName", "none"));
        var defaultOrder = await client.WaitForCountAsync(++queryCount);
        Assert.IsNull(defaultOrder.SortColumn);
        Assert.IsFalse(defaultOrder.SortDescending);
        Assert.IsNull(viewModel.CurrentSortColumn);
        Assert.IsFalse(viewModel.CurrentSortDescending);

        bridge.PublishState();
        using var state = JsonDocument.Parse(publishedState!);
        Assert.AreEqual(JsonValueKind.Null, state.RootElement.GetProperty("sort").ValueKind);
    }

    [TestMethod]
    public async Task SortChangingColumn_StartsAscendingAndNativeClearUsesTheSameQueryContract()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new NoOpFileActions(), TimeSpan.Zero);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.Count >= 1 && !viewModel.IsBusy);

        var queryCount = client.Count;
        bridge.HandleMessage("sort", SortPayload("fileName", "asc"));
        await client.WaitForCountAsync(++queryCount);

        // Clicking another column begins a new ascending sort, never a local
        // re-sort of the current viewport.
        bridge.HandleMessage("sort", SortPayload("kind", "asc"));
        var changedColumn = await client.WaitForCountAsync(++queryCount);
        Assert.AreEqual("kind", changedColumn.SortColumn);
        Assert.IsFalse(changedColumn.SortDescending);
        Assert.AreEqual("kind", viewModel.CurrentSortColumn);

        // Native Shell headers use the exact same public-to-host bridge state.
        bridge.ApplyNativeSort("kind", "desc");
        var nativeDescending = await client.WaitForCountAsync(++queryCount);
        Assert.AreEqual("kind", nativeDescending.SortColumn);
        Assert.IsTrue(nativeDescending.SortDescending);

        bridge.ApplyNativeSort("kind", null);
        var nativeCancelled = await client.WaitForCountAsync(++queryCount);
        Assert.IsNull(nativeCancelled.SortColumn);
        Assert.IsFalse(nativeCancelled.SortDescending);
        Assert.IsNull(viewModel.CurrentSortColumn);
    }

    [TestMethod]
    public void MainWebGrid_HasTheExplicitThreeStateClickLoopAndVisualState()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));

        StringAssert.Contains(script, "function nextSortDirection(columnId)");
        StringAssert.Contains(script, "activeDirection === \"asc\" ? \"desc\" : activeDirection === \"desc\" ? \"none\" : \"asc\"");
        StringAssert.Contains(script, "cell.dataset.sortDirection = sortDirection || \"none\"");
        StringAssert.Contains(script, "cell.setAttribute(\"aria-sort\"");
        StringAssert.Contains(script, "post(\"sort\", { column: column.id, direction })");
        StringAssert.Contains(css, ".head-cell.sort-active");
    }

    private static JsonElement SortPayload(string column, string direction) =>
        JsonSerializer.SerializeToElement(new { column, direction });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingSearchClient : ISearchClient
    {
        private readonly ConcurrentQueue<SearchQuery> _queries = [];

        public int Count => _queries.Count;

        public async Task<SearchQuery> WaitForCountAsync(int expectedCount)
        {
            await WaitUntilAsync(() => Count >= expectedCount);
            return _queries.ToArray()[expectedCount - 1];
        }

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            _queries.Enqueue(query);
            return ValueTask.FromResult(new SearchClientResult(
                [new SearchHit(
                    @"C:\Fixture\Part.SLDPRT",
                    SolidWorksDocumentKind.Part,
                    "Part.SLDPRT",
                    null,
                    null,
                    null,
                    1,
                    false)],
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready")));
        }
    }

    private sealed class NoOpFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
    }
}
