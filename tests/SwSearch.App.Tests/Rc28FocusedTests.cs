using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc28FocusedTests
{
    [TestMethod]
    public async Task RevisionBoundPaging_UsesBoundedViewportWindow_WithoutProductLimit()
    {
        var client = new PagedSearchClient(150_000, revision: 32);
        using var viewModel = new MainViewModel(client, new NoopFileActions(), TimeSpan.Zero);
        await viewModel.StartAsync();
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.Results.Count == MainViewModel.SearchPageSize);
        await WaitUntilAsync(() => viewModel.TotalResultCount == 150_000);

        Assert.AreEqual(MainViewModel.SearchPageSize, viewModel.Results.Count);
        Assert.AreEqual(1, client.QueryCount, "Desktop must not auto-enumerate all 150k matches.");
        Assert.AreEqual(0, viewModel.ResultBaseOffset);
        Assert.AreEqual(MainViewModel.SearchPageSize, client.Queries[0].Limit);

        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("fixture"));
        var resultEnvelopeSizes = new List<int>();
        bridge.StateReady += payload =>
        {
            var json = JsonSerializer.SerializeToElement(payload);
            if (json.TryGetProperty("results", out var results)) resultEnvelopeSizes.Add(results.GetArrayLength());
        };
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "main" }));
        Assert.IsTrue(resultEnvelopeSizes.All(size => size <= 500));

        viewModel.RequestResultWindow(149_800, MainViewModel.ContinuationPageSize);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.ResultBaseOffset == 149_800);
        Assert.AreEqual(MainViewModel.ContinuationPageSize, viewModel.Results.Count);
        Assert.AreEqual(2, client.QueryCount);
        Assert.AreEqual(149_800, client.Queries.Last().Offset);
        Assert.AreEqual(150_000, viewModel.TotalResultCount);
        Assert.IsTrue(viewModel.Results.Count <= MainViewModel.ContinuationPageSize);
        bridge.PublishState();
        Assert.IsTrue(resultEnvelopeSizes.All(size => size <= 500));
        Assert.IsTrue(bridge.PublishedResultDtoCount <= 320,
            $"Bridge serialized {bridge.PublishedResultDtoCount} DTOs for two viewport windows.");

        var root = FindRepositoryRoot();
        var appJs = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        StringAssert.Contains(appJs, "const MAIN_PAGE_CACHE_LIMIT = 4;");
        StringAssert.Contains(appJs, "payload.results = batch.slice(0, 500);");
        StringAssert.Contains(appJs, "document.createDocumentFragment()");
        StringAssert.Contains(appJs, "Math.floor(scrollTop / rowHeight) - 6");
    }

    [TestMethod]
    public async Task LatestQuery_CancelsObsoleteWindow_AndNeverPublishesOldRows()
    {
        var client = new LatestWinsSearchClient();
        using var viewModel = new MainViewModel(client, new NoopFileActions(), TimeSpan.Zero);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("fixture"));
        var publishedPaths = new List<string>();
        bridge.StateReady += payload =>
        {
            var json = JsonSerializer.SerializeToElement(payload);
            if (!json.TryGetProperty("results", out var results)) return;
            publishedPaths.AddRange(results.EnumerateArray().Select(item => item.GetProperty("fullPath").GetString()!));
        };
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "main" }));
        await viewModel.StartAsync();
        viewModel.ApplySearchTextFromPage("old");
        await client.OldQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ApplySearchTextFromPage("new");
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.Results.Count == 1);
        bridge.PublishState();

        Assert.AreEqual("new.SLDPRT", viewModel.Results[0].FileName);
        Assert.IsFalse(publishedPaths.Any(path => path.Contains("old", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(client.OldQueryCancelled);
    }

    [TestMethod]
    public void DesktopHasExactlyThreeModes_AndTaskpaneOwnsLibraryMode()
    {
        using var viewModel = new MainViewModel(new PagedSearchClient(0, 28), new NoopFileActions());
        CollectionAssert.AreEqual(
            new[] { SearchMode.All, SearchMode.FileName, SearchMode.Property },
            viewModel.SearchModes.Select(option => option.Value).ToArray());

        var root = FindRepositoryRoot();
        var appJs = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        StringAssert.Contains(appJs, "class=\"mode-group\" role=\"group\"");
        StringAssert.Contains(appJs, "data-mode=\"all\"");
        StringAssert.Contains(appJs, "data-mode=\"name\"");
        StringAssert.Contains(appJs, "data-mode=\"property\"");
        StringAssert.Contains(appJs, "? '<span id=\"modeButton\" class=\"mode mode-fixed\" role=\"status\" aria-label=\"搜索模式：文件库\">文件库</span>'");
        var taskpaneControl = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "TaskpaneControl.cs"));
        StringAssert.Contains(taskpaneControl, "private int _searchMode = 3;");
        StringAssert.Contains(taskpaneControl, "mode = \"library\"");
        var taskpaneBridge = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "TaskpaneWebBridge.cs"));
        Assert.IsFalse(taskpaneBridge.Contains("TryParseMode", StringComparison.Ordinal));
        Assert.IsFalse(taskpaneBridge.Contains("TryReadMode", StringComparison.Ordinal));
        var desktopBridge = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "DesktopWebBridge.cs"));
        Assert.IsFalse(desktopBridge.Contains("\"library\" => SearchMode.Library", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TrayLifetime_UserCloseHides_OpenRestores_ExplicitExitReallyExits()
    {
        var state = new TrayLifetimeState();
        Assert.AreEqual(TrayCloseDisposition.Hide, state.OnWindowClosing());
        Assert.IsFalse(state.WindowVisible);
        Assert.IsFalse(state.ExitRequested);

        state.Restore();
        Assert.IsTrue(state.WindowVisible);

        state.RequestExit();
        Assert.AreEqual(TrayCloseDisposition.Exit, state.OnWindowClosing());
        Assert.IsTrue(state.ExitRequested);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class PagedSearchClient(int count, long revision) : ISearchClient, ISearchCountClient
    {
        private readonly SearchHit[] _hits = Enumerable.Range(0, count).Select(index => new SearchHit(
            $@"C:\Fixture\Part-{index:D6}.SLDPRT", SolidWorksDocumentKind.Part,
            $"Part-{index:D6}.SLDPRT", null, null, null, 1, false)).ToArray();

        public List<SearchQuery> Queries { get; } = [];
        public IEnumerable<SearchQuery> ContinuationPages => Queries.Where(query => query.Offset > 0);
        public int QueryCount => Queries.Count;

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            lock (Queries) Queries.Add(query);
            var page = _hits.Skip(query.Offset).Take(query.Limit).ToArray();
            return ValueTask.FromResult(new SearchClientResult(
                page,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"),
                HasMore: query.Offset + page.Length < _hits.Length,
                IndexRevision: revision,
                PageFingerprint: SearchPagingFingerprint.Create(query)));
        }

        public ValueTask<long> CountAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult((long)_hits.Length);
    }

    private sealed class LatestWinsSearchClient : ISearchClient
    {
        public TaskCompletionSource OldQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OldQueryCancelled { get; private set; }

        public async ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken)
        {
            if (query.Text == "old")
            {
                OldQueryStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    OldQueryCancelled = true;
                    throw;
                }
            }
            var hits = query.Text == "new"
                ? new[] { new SearchHit(@"C:\Fixture\new.SLDPRT", SolidWorksDocumentKind.Part,
                    "new.SLDPRT", null, null, null, 1, false) }
                : Array.Empty<SearchHit>();
            return new SearchClientResult(hits,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"),
                HasMore: false,
                IndexRevision: 32,
                PageFingerprint: SearchPagingFingerprint.Create(query));
        }
    }

    private sealed class NoopFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
    }
}
