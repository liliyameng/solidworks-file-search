using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc71NativeModeSelectorTests
{
    [TestMethod]
    public async Task WebModeIngress_DefaultsToAll_AndEachUserModeStartsOneCorrectSearch()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new NoOpFileActions(), TimeSpan.Zero);
        using var bridge = new DesktopWebBridge(
            viewModel,
            new UnavailableUserPreferencesRepository("test"));
        var publishedModes = new List<string>();
        bridge.StateReady += payload =>
        {
            var state = JsonSerializer.SerializeToElement(payload);
            if (state.TryGetProperty("mode", out var mode))
            {
                publishedModes.Add(mode.GetString()!);
            }
        };
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "maintoolbar" }));

        Assert.AreEqual(SearchMode.All, viewModel.SelectedMode.Value);
        Assert.AreEqual("all", publishedModes.Last());
        await viewModel.StartAsync();
        await client.WaitForCountAsync(1);

        bridge.HandleMessage("mode", JsonSerializer.SerializeToElement(new { mode = "property" }));
        var property = await client.WaitForCountAsync(2);
        Assert.AreEqual(SearchMode.Property, property.Mode);
        Assert.AreEqual(SearchMode.Property, viewModel.SelectedMode.Value);
        bridge.PublishState();
        Assert.AreEqual("property", publishedModes.Last());

        // Re-applying the selected button is a no-op, not a duplicate search.
        bridge.HandleMessage("mode", JsonSerializer.SerializeToElement(new { mode = "property" }));
        await Task.Delay(100);
        Assert.AreEqual(2, client.Count);

        bridge.HandleMessage("mode", JsonSerializer.SerializeToElement(new { mode = "name" }));
        var fileName = await client.WaitForCountAsync(3);
        Assert.AreEqual(SearchMode.FileName, fileName.Mode);

        bridge.HandleMessage("mode", JsonSerializer.SerializeToElement(new { mode = "all" }));
        var all = await client.WaitForCountAsync(4);
        Assert.AreEqual(SearchMode.All, all.Mode);
        Assert.AreEqual(SearchMode.All, viewModel.SelectedMode.Value);
    }

    [TestMethod]
    public void MainToolbar_HasOneInlineModeEntry_ThatCannotBeClippedByItsWebView()
    {
        var root = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml"));
        var js = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));

        Assert.IsFalse(window.Contains("ToolStripComboBox", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("NativeModeSelector", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("搜索方式：", StringComparison.Ordinal));

        StringAssert.Contains(js, "id=\"modeGroup\" class=\"mode-group\" role=\"group\"");
        Assert.AreEqual(1, js.Split("id=\"modeGroup\"", StringSplitOptions.None).Length - 1,
            "The shared desktop template must render one mode group per active view.");
        StringAssert.Contains(js, "data-mode=\"all\" aria-pressed=\"true\">全部</button>");
        StringAssert.Contains(js, "data-mode=\"name\" aria-pressed=\"false\">文件名</button>");
        StringAssert.Contains(js, "data-mode=\"property\" aria-pressed=\"false\">属性</button>");
        StringAssert.Contains(js, "option.getAttribute(\"aria-pressed\") === \"true\"");
        StringAssert.Contains(js, "post(\"mode\", { mode: option.dataset.mode });");
        StringAssert.Contains(js, "else updateDesktopModeButtons(mode);");
        Assert.IsFalse(js.Contains("modeMenu", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("aria-haspopup=\"listbox\"", StringComparison.Ordinal));

        StringAssert.Contains(css, "html[data-view=\"maintoolbar\"] .toolbar { min-height: 44px; max-height: 48px; flex-wrap: nowrap; overflow: hidden; }");
        StringAssert.Contains(css, ".mode-group { display: inline-flex;");
        Assert.IsFalse(css.Contains(".mode-menu", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "<RowDefinition Height=\"48\" />");
    }

    [TestMethod]
    public void MainToolbarReady_IsAcceptedExactly_WhileUnknownAndCaseVariantsRemainRejected()
    {
        var router = new WebMessageRouter();

        Assert.IsTrue(router.TryValidate(
            """{"type":"ready","payload":{"view":"maintoolbar"}}""",
            out var accepted,
            out var error),
            error);
        Assert.AreEqual("ready", accepted!.Type);

        Assert.IsFalse(router.TryValidate(
            """{"type":"ready","payload":{"view":"MainToolbar"}}""",
            out _,
            out _));
        Assert.IsFalse(router.TryValidate(
            """{"type":"ready","payload":{"view":"unknown"}}""",
            out _,
            out _));
    }

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

        public ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken)
        {
            _queries.Enqueue(query);
            return ValueTask.FromResult(new SearchClientResult(
                [],
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
