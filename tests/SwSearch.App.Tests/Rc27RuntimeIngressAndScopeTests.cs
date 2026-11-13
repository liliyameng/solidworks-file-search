using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.WebUi;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc27RuntimeIngressAndScopeTests
{
    [TestMethod]
    public void FolderActivation_IsInjectedBeforeWindowConstructionAndShow()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.App", "App.xaml.cs"));
        var apply = source.IndexOf("viewModel.ApplyFolderScope(scopeFolder);", StringComparison.Ordinal);
        var construct = source.IndexOf("var window = new MainWindow(viewModel);", StringComparison.Ordinal);
        var show = source.IndexOf("window.Show();", StringComparison.Ordinal);
        Assert.IsTrue(apply >= 0 && apply < construct && construct < show);
    }

    [TestMethod]
    public void ProductionHostRouterAndDesktopDispatch_AcceptExactAppJsEnvelope_ForAllFourActions()
    {
        const string path = @"\\?\UNC\server\共享 & (设计)\尾部\零件.SLDPRT ";
        var actions = new RecordingFileActions();
        using var viewModel = new MainViewModel(new RecordingSearchClient(), actions);
        viewModel.Results.Add(new SearchResultItem(new SearchHit(
            path, SolidWorksDocumentKind.Part, "零件.SLDPRT ", null, null, null, 1, false)));
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        var router = new WebMessageRouter();

        foreach (var action in new[] { "open", "reveal", "copyPath", "viewProperties" })
        {
            // This is the exact JSON shape produced by production app.js post(type, payload).
            var json = JsonSerializer.Serialize(new
            {
                type = "contextAction",
                payload = new
                {
                    schema = ResultActionProtocol.CurrentSchemaVersion,
                    action,
                    path,
                    resultSetId = viewModel.CurrentResultSetId,
                    resultRevision = viewModel.CurrentPageRevision,
                    resultStart = viewModel.ResultBaseOffset,
                    resultIndex = 0,
                },
            });
            Assert.IsTrue(
                CompositionWebViewHost.TryValidateMessage(router, json, out var message, out var error),
                $"{action}: {error}");
            Assert.IsNotNull(message);
            bridge.HandleMessage(message.Type, message.Payload); // cloned JsonElement survives parser disposal
        }

        CollectionAssert.AreEqual(
            new[] { "open", "reveal", "copyPath", "viewProperties" },
            actions.Calls.Select(static call => call.Action).ToArray());
        Assert.IsTrue(actions.Calls.All(call => call.Path == path));

        var rc27Json = JsonSerializer.Serialize(new
        {
            type = "contextAction",
            payload = new
            {
                schema = ResultActionProtocol.CurrentSchemaVersion,
                action = "open",
                path,
                resultSetId = viewModel.CurrentResultSetId,
                resultRevision = viewModel.CurrentPageRevision,
                resultStart = viewModel.ResultBaseOffset,
                resultIndex = 0,
            },
        });
        Assert.IsTrue(CompositionWebViewHost.TryValidateMessage(
            router, rc27Json, out var rc27Message, out var rc27Error), rc27Error);
        Assert.AreEqual(ResultActionProtocol.CurrentSchemaVersion, rc27Message!.Payload.GetProperty("schema").GetInt32());
    }

    [TestMethod]
    public void RejectionAudit_RecordsOnlySafeEnvelopeMetadata_NotThePath()
    {
        const string secretPath = @"D:\客户甲\保密项目\零件.SLDPRT";
        var json = JsonSerializer.Serialize(new
        {
            type = "contextAction",
            payload = new { action = "Open", path = secretPath, schema = 1 },
        });
        Assert.IsFalse(CompositionWebViewHost.TryValidateMessage(
            new WebMessageRouter(), json, out _, out var error));
        StringAssert.Contains(error, "[action]");
        var audit = WebMessageRejectionAudit.FromJson(json);
        Assert.AreEqual("contextAction", audit.MessageType);
        Assert.AreEqual("Open", audit.Action);
        Assert.AreEqual(1, audit.SchemaVersion);
        Assert.IsFalse(audit.ToString().Contains(secretPath, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExplorerScope_ArgvToWebStateAndBrokerQuery_PreservesOneNormalizedPathAndKeyword()
    {
        const string input = @"\\server\共享 & (设计)\尾部\";
        var normalized = App.ReadScopeFolder(["--scope-folder", input]);
        Assert.IsNotNull(normalized);
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.Zero);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        string? stateJson = null;
        bridge.StateReady += value => stateJson = JsonSerializer.Serialize(value);
        using (var ready = JsonDocument.Parse("{\"view\":\"main\"}"))
        {
            bridge.HandleMessage("ready", ready.RootElement);
        }

        viewModel.ApplyFolderScope(input);
        var initial = await client.WaitForQueryAsync(query => query.EffectiveScope.Kind == SearchScopeKind.Folder);
        Assert.AreEqual(normalized, initial.EffectiveScope.RootPath);
        Assert.IsTrue(initial.EffectiveScope.Recursive);
        bridge.PublishState();
        using (var state = JsonDocument.Parse(stateJson!))
        {
            var scope = state.RootElement.GetProperty("scope");
            Assert.IsTrue(scope.GetProperty("folder").GetBoolean());
            Assert.AreEqual("folder", scope.GetProperty("kind").GetString());
            Assert.AreEqual(normalized, scope.GetProperty("rootPath").GetString());
            Assert.AreEqual(
                typeof(DesktopWebBridge).Assembly.GetName().Version?.ToString(3),
                state.RootElement.GetProperty("version").GetString());
        }

        viewModel.ApplySearchTextFromPage("阀体");
        var keyword = await client.WaitForQueryAsync(query => query.Text == "阀体");
        Assert.AreEqual(normalized, keyword.EffectiveScope.RootPath);
        viewModel.ApplySearchTextFromPage(string.Empty);
        var cleared = await client.WaitForQueryAsync(query => query.Text.Length == 0);
        Assert.AreEqual(normalized, cleared.EffectiveScope.RootPath, "Clearing the keyword must retain scope.");

        using (var empty = JsonDocument.Parse("{}")) bridge.HandleMessage("restoreScope", empty.RootElement);
        var allMachine = await client.WaitForQueryAsync(query => query.EffectiveScope.Kind == SearchScopeKind.AllMachine);
        Assert.AreEqual(SearchScopeKind.AllMachine, allMachine.EffectiveScope.Kind);
        bridge.PublishState();
        using (var state = JsonDocument.Parse(stateJson!))
        {
            var scope = state.RootElement.GetProperty("scope");
            Assert.IsFalse(scope.GetProperty("folder").GetBoolean());
            Assert.AreEqual("all", scope.GetProperty("kind").GetString());
            Assert.AreEqual(JsonValueKind.Null, scope.GetProperty("rootPath").ValueKind);
        }

        var explorerSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.ModernContextMenu", "ExplorerCommand.cpp"));
        StringAssert.Contains(explorerSource, "commandLine += L\" --scope-folder \"");
        StringAssert.Contains(explorerSource, "QuoteCommandLineArgument(folder)");
        var pageScript = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        StringAssert.Contains(pageScript, "state.scope.folder === true");
        StringAssert.Contains(pageScript, "typeof state.scope.rootPath === \"string\"");
        StringAssert.Contains(pageScript, "post(\"restoreScope\", {})");
    }

    [TestMethod]
    public async Task LegacyTypePayload_IsIgnoredAndPublishedStateHasNoTypeFilters()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.Zero);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        string? stateJson = null;
        bridge.StateReady += value => stateJson = JsonSerializer.Serialize(value);

        const string json = """
            {"type":"search","payload":{"query":"legacy","types":{"part":false,"assembly":false,"drawing":true}}}
            """;
        var router = new WebMessageRouter();
        Assert.IsTrue(CompositionWebViewHost.TryValidateMessage(router, json, out var message, out var error), error);
        bridge.HandleMessage(message!.Type, message.Payload);

        var query = await client.WaitForQueryAsync(value => value.Text == "legacy");
        CollectionAssert.AreEquivalent(
            new[] { SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly },
            query.Kinds!.ToArray());

        using (var ready = JsonDocument.Parse("{\"view\":\"main\"}")) bridge.HandleMessage("ready", ready.RootElement);
        bridge.PublishState();
        using var state = JsonDocument.Parse(stateJson!);
        Assert.IsFalse(state.RootElement.TryGetProperty("filters", out _));
    }

    [TestMethod]
    public async Task TenRapidInputs_AreLatestWins_AndFirstPagePayloadIsBounded()
    {
        var client = new BurstSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.Zero);
        for (var index = 0; index < 10; index++) viewModel.ApplySearchTextFromPage($"query-{index}");

        await client.LatestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var retry = 0; retry < 100 && (viewModel.IsBusy || viewModel.Results.Count == 0); retry++)
            await Task.Delay(10);

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(MainViewModel.SearchPageSize, viewModel.Results.Count);
        Assert.IsTrue(viewModel.Results.All(item => item.FullPath.Contains("query-9", StringComparison.Ordinal)));
        Assert.IsTrue(client.CanceledCount >= 8, $"Only {client.CanceledCount} old queries were cancelled.");

        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        string? stateJson = null;
        bridge.StateReady += value => stateJson = JsonSerializer.Serialize(value);
        using (var ready = JsonDocument.Parse("{\"view\":\"main\"}")) bridge.HandleMessage("ready", ready.RootElement);
        using var state = JsonDocument.Parse(stateJson!);
        Assert.AreEqual(MainViewModel.SearchPageSize, state.RootElement.GetProperty("results").GetArrayLength());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class RecordingSearchClient : ISearchClient
    {
        private readonly ConcurrentQueue<SearchQuery> _queries = new();
        private readonly SemaphoreSlim _available = new(0);

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            _queries.Enqueue(query);
            _available.Release();
            return ValueTask.FromResult(new SearchClientResult(
                [], new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready")));
        }

        public async Task<SearchQuery> WaitForQueryAsync(Func<SearchQuery, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                await _available.WaitAsync(timeout.Token);
                while (_queries.TryDequeue(out var query))
                    if (predicate(query)) return query;
            }
        }
    }

    private sealed class BurstSearchClient : ISearchClient
    {
        public int CanceledCount;
        public TaskCompletionSource LatestCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(query.Text == "query-9" ? 5 : 250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref CanceledCount);
                throw;
            }
            var hits = Enumerable.Range(0, query.Limit).Select(index => new SearchHit(
                $@"C:\Synthetic\{query.Text}\Part-{index:D3}.SLDPRT",
                SolidWorksDocumentKind.Part,
                $"Part-{index:D3}.SLDPRT",
                null, null, null, 1, false)).ToArray();
            if (query.Text == "query-9") LatestCompleted.TrySetResult();
            return new SearchClientResult(
                hits, new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"));
        }
    }

    private sealed class RecordingFileActions : IFileActions
    {
        public List<(string Action, string Path)> Calls { get; } = [];
        public void Open(string fullPath) => Calls.Add(("open", fullPath));
        public void Reveal(string fullPath) => Calls.Add(("reveal", fullPath));
        public void CopyPath(string fullPath) => Calls.Add(("copyPath", fullPath));
        public void ViewProperties(string fullPath) => Calls.Add(("viewProperties", fullPath));
    }
}
