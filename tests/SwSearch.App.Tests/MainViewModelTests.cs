using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.Testing;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public void ProductionDebounce_Is80MillisecondsAndNeedsNoSearchButton()
    {
        using var viewModel = new MainViewModel(new RecordingSearchClient(), new RecordingFileActions());
        var delay = (TimeSpan)typeof(MainViewModel)
            .GetField("_debounceDelay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;

        Assert.AreEqual(TimeSpan.FromMilliseconds(80), delay);
    }

    [TestMethod]
    public async Task SearchText_DebouncesAndCancelsPreviousQuery()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(5));

        viewModel.SearchText = "旧查询";
        await WaitUntilAsync(() => client.StartedTexts.Contains("旧查询"));
        viewModel.SearchText = "新查询";

        await WaitUntilAsync(() => viewModel.Results.Count == 1);

        CollectionAssert.Contains(client.StartedTexts, "旧查询");
        CollectionAssert.Contains(client.StartedTexts, "新查询");
        Assert.AreEqual("新查询.SLDPRT", viewModel.Results[0].FileName);
        Assert.IsTrue(client.CancelledTexts.Contains("旧查询"));
    }

    [TestMethod]
    public async Task TwelveRapidTextChanges_WithoutSearchCommand_SubmitAndDisplayOnlyFinalKeyword()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(30));

        for (var index = 0; index < 12; index++)
        {
            viewModel.SearchText = "即时-" + index;
        }

        await WaitUntilAsync(() => viewModel.Results.Count == 1);
        await Task.Delay(80);

        CollectionAssert.AreEqual(new[] { "即时-11" }, client.StartedTexts);
        Assert.AreEqual("即时-11.SLDPRT", viewModel.Results[0].FileName);
    }

    [TestMethod]
    public async Task EnterCommand_CancelsPendingDebounceAndSubmitsImmediatelyOnce()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(250));
        viewModel.SearchText = "回车立即";

        var started = DateTime.UtcNow;
        viewModel.SearchNowCommand.Execute(null);
        await WaitUntilAsync(() => client.LastCompletedQuery?.Text == "回车立即");
        var elapsed = DateTime.UtcNow - started;
        await Task.Delay(300);

        Assert.IsTrue(elapsed < TimeSpan.FromMilliseconds(200), $"Enter search took {elapsed}.");
        CollectionAssert.AreEqual(new[] { "回车立即" }, client.StartedTexts);
    }

    [TestMethod]
    public async Task ImeComposition_DoesNotSubmitPartialText_AndDebouncesFinalText()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(20));

        viewModel.BeginSearchTextComposition();
        viewModel.SearchText = "fa";
        viewModel.SearchText = "阀";
        viewModel.SearchText = "阀体";
        await Task.Delay(80);
        Assert.AreEqual(0, client.StartedTexts.Count, "IME partial composition leaked into search.");

        viewModel.CompleteSearchTextComposition();
        await WaitUntilAsync(() => client.LastCompletedQuery?.Text == "阀体");

        CollectionAssert.AreEqual(new[] { "阀体" }, client.StartedTexts);
    }

    [TestMethod]
    public async Task FixedKindsAndMode_ArePassedAsContractQuery()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1))
        {
            IsPropertyMode = true,
            SearchText = "PN-100"
        };

        await WaitUntilAsync(() => client.LastCompletedQuery?.Text == "PN-100");

        Assert.AreEqual(SearchMode.Property, client.LastCompletedQuery!.Mode);
        CollectionAssert.AreEquivalent(
            new[] { SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly },
            client.LastCompletedQuery.Kinds!.ToArray());
    }

    [TestMethod]
    public async Task Start_EmptyQueryImmediatelyShowsCachedFiles()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.LastCompletedQuery is not null && viewModel.Results.Count == 1);

        Assert.AreEqual(string.Empty, client.LastCompletedQuery!.Text);
        Assert.AreEqual(MainViewModel.SearchPageSize, client.LastCompletedQuery.Limit);
        Assert.AreEqual(0, client.LastCompletedQuery.Offset);
    }

    [TestMethod]
    public async Task Paging_LoadsEveryMatchPast500WithoutMaterializingAllAtStartup()
    {
        var client = new PagedSearchClient(625);
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));
        var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedWindows = new List<string>();

        void AssertAndCaptureWindow(int expectedOffset, int expectedCount)
        {
            Assert.AreEqual(expectedOffset, viewModel.ResultBaseOffset);
            Assert.AreEqual(expectedCount, viewModel.Results.Count);
            Assert.IsTrue(viewModel.Results.Count <= MainViewModel.ContinuationPageSize);
            observedWindows.Add($"{viewModel.ResultBaseOffset}/{viewModel.Results.Count}");
            observedPaths.UnionWith(viewModel.Results.Select(item => item.FullPath));
        }

        await viewModel.StartAsync();
        await WaitUntilAsync(() =>
            !viewModel.IsBusy && viewModel.ResultBaseOffset == 0 &&
            viewModel.Results.Count == MainViewModel.SearchPageSize);
        AssertAndCaptureWindow(0, MainViewModel.SearchPageSize);
        Assert.IsTrue(viewModel.HasMoreResults);

        viewModel.RequestLoadMore();
        await WaitUntilAsync(() =>
            !viewModel.IsBusy && viewModel.ResultBaseOffset == MainViewModel.SearchPageSize &&
            viewModel.Results.Count == MainViewModel.ContinuationPageSize);
        AssertAndCaptureWindow(120, MainViewModel.ContinuationPageSize);
        Assert.IsTrue(viewModel.HasMoreResults);

        viewModel.RequestLoadMore();
        await WaitUntilAsync(() =>
            !viewModel.IsBusy && viewModel.ResultBaseOffset == 320 &&
            viewModel.Results.Count == MainViewModel.ContinuationPageSize);
        AssertAndCaptureWindow(320, MainViewModel.ContinuationPageSize);
        Assert.IsTrue(viewModel.HasMoreResults);

        viewModel.RequestLoadMore();
        await WaitUntilAsync(() =>
            !viewModel.IsBusy && viewModel.ResultBaseOffset == 520 && viewModel.Results.Count == 105);
        AssertAndCaptureWindow(520, 105);

        CollectionAssert.AreEqual(new[] { "0/120", "120/200", "320/200", "520/105" }, observedWindows);
        CollectionAssert.AreEqual(new[] { 0, 120, 320, 520 }, client.Offsets);
        Assert.IsFalse(viewModel.HasMoreResults);
        Assert.IsFalse(viewModel.ResultSummary.Contains("最多显示", StringComparison.Ordinal));
        await WaitUntilAsync(() => viewModel.TotalResultCount == 625);
        Assert.AreEqual(625, viewModel.TotalResultCount);
        Assert.AreEqual(625, viewModel.LoadedResultCount);
        Assert.AreEqual(625, observedPaths.Count);
        Assert.AreEqual(105, viewModel.Results.Count, "Only the final viewport window should remain resident.");
    }

    [TestMethod]
    public async Task OutOfOrderResponse_NeverOverwritesLatestQuery()
    {
        var client = new OutOfOrderSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));

        viewModel.SearchText = "旧响应";
        await client.OldStarted.Task;
        viewModel.SearchText = "最终输入";
        await WaitUntilAsync(() => viewModel.Results.Count == 1 && viewModel.Results[0].FileName == "最终输入.SLDPRT");
        client.ReleaseOld.TrySetResult();
        await Task.Delay(50);

        Assert.AreEqual("最终输入.SLDPRT", viewModel.Results[0].FileName);
    }

    [TestMethod]
    public async Task FolderMode_ImmediatelyRequeriesWithSupportedKinds()
    {
        var folder = Path.Combine(Path.GetTempPath(), "swsearch-app-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var client = new RecordingSearchClient();
            using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));
            viewModel.SearchText = "阀体";
            await WaitUntilAsync(() => client.LastCompletedQuery?.Text == "阀体");

            viewModel.IsPropertyMode = true;
            viewModel.ApplyFolderScope(folder);
            await WaitUntilAsync(() => client.LastCompletedQuery?.EffectiveScope.Kind == SearchScopeKind.Folder &&
                                       client.LastCompletedQuery.Mode == SearchMode.Property &&
                                       client.LastCompletedQuery.Kinds!.Count == 2 &&
                                       client.LastCompletedQuery.Kinds.Contains(SolidWorksDocumentKind.Part) &&
                                       client.LastCompletedQuery.Kinds.Contains(SolidWorksDocumentKind.Assembly));

            Assert.AreEqual(Path.GetFullPath(folder), client.LastCompletedQuery!.EffectiveScope.RootPath);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task DisconnectedUncScope_ReturnsWithin50Milliseconds_AndQueriesHistoricalCache()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));
        const string unc = @"\\offline-swsearch.invalid\design library\历史缓存";

        var clock = Stopwatch.StartNew();
        viewModel.ApplyFolderScope(unc);
        clock.Stop();

        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"UNC scope blocked for {clock.Elapsed}.");
        await WaitUntilAsync(() => client.LastCompletedQuery?.EffectiveScope.Kind == SearchScopeKind.Folder);
        Assert.AreEqual(Path.GetFullPath(unc), client.LastCompletedQuery!.EffectiveScope.RootPath);
        Assert.IsTrue(client.LastCompletedQuery.EffectiveScope.IncludeSubdirectories);
        StringAssert.Contains(viewModel.StatusText, "按缓存范围搜索");
    }

    [TestMethod]
    public async Task MissingAbsoluteFolder_IsStillARecursiveCacheScope_AndNeverFallsBackToAllMachine()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));
        var missing = Path.Combine(Path.GetTempPath(), "swsearch-never-created-" + Guid.NewGuid().ToString("N"), "历史");

        var clock = Stopwatch.StartNew();
        viewModel.ApplyFolderScope(missing);
        clock.Stop();

        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Missing scope blocked for {clock.Elapsed}.");
        await WaitUntilAsync(() => client.LastCompletedQuery is not null);
        Assert.AreEqual(SearchScopeKind.Folder, client.LastCompletedQuery!.EffectiveScope.Kind);
        Assert.AreEqual(Path.GetFullPath(missing), client.LastCompletedQuery.EffectiveScope.RootPath);
    }

    [TestMethod]
    public async Task InvalidFolderSyntax_DoesNotSubmitOrFallBackToAllMachine()
    {
        var client = new RecordingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));

        viewModel.ApplyFolderScope(string.Empty);
        viewModel.ApplyFolderScope("relative-folder");
        viewModel.ApplyFolderScope("C:\\invalid\0folder");
        await Task.Delay(80);

        Assert.AreEqual(0, client.StartedTexts.Count);
        Assert.AreEqual(SearchScopeKind.AllMachine, viewModel.CurrentScope.Kind);
        StringAssert.Contains(viewModel.StatusText, "文件夹范围无效");
    }

    [TestMethod]
    public void FolderScopeGoldenMatrix_IsPureLexicalExactAndUnder50Milliseconds()
    {
        var clock = Stopwatch.StartNew();
        foreach (var item in WindowsScopePathGolden.Cases())
        {
            if (item.IsValid)
            {
                var scope = SearchScope.Folder(item.Input);
                Assert.AreEqual(item.Expected, scope.RootPath, item.Name);
            }
            else
            {
                Assert.ThrowsException<ArgumentException>(() => SearchScope.Folder(item.Input), item.Name);
            }
        }
        clock.Stop();
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"App lexical scope matrix blocked for {clock.Elapsed}.");
    }

    [TestMethod]
    public void FolderScopeConstructionAndApplyCallGraph_HasZeroSynchronousDirectoryProbes()
    {
        var roots = typeof(MainViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Append(typeof(MainViewModel).GetMethod(nameof(MainViewModel.ApplyFolderScope))!);
        var calls = ApplicationCallGraph(roots).ToArray();

        var forbidden = calls.Where(IsForbiddenSynchronousIo).ToArray();
        Assert.AreEqual(0, forbidden.Length,
            "MainViewModel construction/ApplyFolderScope call graph contains synchronous I/O: " +
            string.Join(", ", forbidden.Select(method => method.DeclaringType?.FullName + "." + method.Name)));
    }

    [TestMethod]
    public async Task Disconnect_NeverShowsPreviousQueryResults()
    {
        var client = new DisconnectingSearchClient();
        using var viewModel = new MainViewModel(client, new RecordingFileActions(), TimeSpan.FromMilliseconds(1));
        viewModel.SearchText = "缓存";
        await WaitUntilAsync(() => viewModel.Results.Count == 1);

        viewModel.SearchText = "断线";
        await WaitUntilAsync(() => viewModel.IsRetryVisible);

        Assert.AreEqual(0, viewModel.Results.Count);
        StringAssert.Contains(viewModel.StatusText, "后台更新暂不可用");
    }

    [TestMethod]
    public async Task ConnectionRecovery_AutomaticallyRefreshesCurrentQuery()
    {
        var client = new RecoveringStateClient();
        using var viewModel = new MainViewModel(
            client,
            new RecordingFileActions(),
            TimeSpan.FromMilliseconds(1),
            statePollInterval: TimeSpan.FromMilliseconds(10));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.Results.Count == 1 && client.SearchCalls >= 2);

        Assert.AreEqual("recovered.SLDPRT", viewModel.Results[0].FileName);
        Assert.IsFalse(viewModel.IsRetryVisible);
    }

    [TestMethod]
    public async Task RapidRevisionChanges_CoalesceToOneCurrentQueryRefresh()
    {
        var client = new RevisionSearchClient();
        using var viewModel = new MainViewModel(
            client,
            new RecordingFileActions(),
            TimeSpan.FromMilliseconds(1),
            statePollInterval: TimeSpan.FromMilliseconds(10));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.SearchCalls >= 2);
        await Task.Delay(250);

        Assert.AreEqual(2, client.SearchCalls, "初始缓存查询之后，快速 revision 只能合并成一次重查。");
    }

    [TestMethod]
    public async Task StableRevisionAcrossRepeatedPolls_NeverSelfTriggersSearch()
    {
        var client = new StableRevisionSearchClient();
        using var viewModel = new MainViewModel(
            client,
            new RecordingFileActions(),
            TimeSpan.FromMilliseconds(1),
            statePollInterval: TimeSpan.FromMilliseconds(10));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.StateCalls >= 10);
        await Task.Delay(200);

        Assert.AreEqual(1, client.SearchCalls, "Stable revision polls must not self-trigger searches.");
    }

    [TestMethod]
    public void FileActions_ReceivePathAsData()
    {
        var actions = new RecordingFileActions();
        using var viewModel = new MainViewModel(new RecordingSearchClient(), actions);
        var item = new SearchResultItem(new SearchHit(
            @"C:\含空格 & 引号\零件.SLDPRT", SolidWorksDocumentKind.Part, "零件.SLDPRT",
            null, null, null, 1, false));

        viewModel.Open(item);
        viewModel.Reveal(item);
        viewModel.CopyPath(item);
        viewModel.ViewProperties(item);

        Assert.AreEqual(4, actions.Paths.Count);
        Assert.IsTrue(actions.Paths.All(path => path == item.FullPath));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail("等待异步状态超时。");
            await Task.Delay(10);
        }
    }

    private static IEnumerable<MethodBase> ApplicationCallGraph(IEnumerable<MethodBase> roots)
    {
        var appAssembly = typeof(MainViewModel).Assembly;
        var contractsAssembly = typeof(SearchScope).Assembly;
        var pending = new Stack<MethodBase>(roots);
        var visited = new HashSet<MethodBase>();
        while (pending.Count != 0)
        {
            var method = pending.Pop();
            if (!visited.Add(method)) continue;
            foreach (var called in CalledMethods(method))
            {
                yield return called;
                var assembly = called.DeclaringType?.Assembly;
                if (assembly == appAssembly || assembly == contractsAssembly) pending.Push(called);
            }
        }
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var body = method.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes is null) yield break;
        var module = method.Module;
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var offset = 0; offset < bytes.Length;)
        {
            var first = bytes[offset++];
            var key = first == 0xFE ? (short)(0xFE00 | bytes[offset++]) : (short)first;
            var opcode = OpCodesByValue[key];
            if (opcode.OperandType == OperandType.InlineMethod &&
                (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj))
            {
                var token = BitConverter.ToInt32(bytes, offset);
                MethodBase? called = null;
                try { called = module.ResolveMethod(token, typeArguments, methodArguments); }
                catch (ArgumentException) { }
                if (called is not null) yield return called;
            }
            offset += OperandSize(opcode.OperandType, bytes, offset);
        }
    }

    private static int OperandSize(OperandType type, byte[] bytes, int offset) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
            OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
            OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, offset) * 4),
        _ => throw new InvalidOperationException("Unknown IL operand: " + type),
    };

    private static bool IsForbiddenSynchronousIo(MethodBase method)
    {
        var type = method.DeclaringType?.FullName ?? string.Empty;
        return type is "System.IO.DirectoryInfo" or "System.IO.DriveInfo" ||
            (type == "System.IO.Directory" &&
             (method.Name is "Exists" or "CreateDirectory" or "GetFiles" or "GetDirectories" or "GetFileSystemEntries" ||
              method.Name.StartsWith("Enumerate", StringComparison.Ordinal))) ||
            (type == "System.IO.File" &&
             (method.Name == "Exists" || method.Name.StartsWith("ReadAll", StringComparison.Ordinal) ||
              method.Name.StartsWith("WriteAll", StringComparison.Ordinal))) ||
            method.Name.StartsWith("WNet", StringComparison.Ordinal);
    }

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => opcode.Value);

    private sealed class RecordingSearchClient : ISearchClient
    {
        public List<string> StartedTexts { get; } = [];
        public List<string> CancelledTexts { get; } = [];
        public SearchQuery? LastCompletedQuery { get; private set; }

        public async ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            StartedTexts.Add(query.Text);
            try
            {
                if (query.Text == "旧查询") await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                else await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancelledTexts.Add(query.Text);
                throw;
            }

            LastCompletedQuery = query;
            IReadOnlyList<SearchHit> hits =
            [
                new($@"C:\模型\{query.Text}.SLDPRT", SolidWorksDocumentKind.Part, $"{query.Text}.SLDPRT",
                    "物料编码", query.Text, null, 1, false)
            ];
            return new SearchClientResult(hits, new RuntimeCapability(CapabilityState.Available, "OK", "索引服务正常"));
        }
    }

    private sealed class PagedSearchClient(int count) : ISearchClient, ISearchCountClient
    {
        private readonly SearchHit[] _hits = Enumerable.Range(0, count)
            .Select(index => new SearchHit(
                $@"C:\Models\Part-{index:D4}.SLDPRT",
                SolidWorksDocumentKind.Part,
                $"Part-{index:D4}.SLDPRT",
                null,
                null,
                null,
                1,
                false))
            .ToArray();

        public List<int> Offsets { get; } = [];

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            Offsets.Add(query.Offset);
            IReadOnlyList<SearchHit> page = _hits.Skip(query.Offset).Take(query.Limit).ToArray();
            return ValueTask.FromResult(new SearchClientResult(
                page,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready")));
        }

        public ValueTask<long> CountAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult((long)_hits.Length);
    }

    private sealed class OutOfOrderSearchClient : ISearchClient
    {
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            if (query.Text == "旧响应")
            {
                OldStarted.TrySetResult();
                await ReleaseOld.Task;
            }
            else
            {
                await Task.Delay(5);
            }
            return Result(query.Text);
        }
    }

    private sealed class DisconnectingSearchClient : ISearchClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            query.Text == "断线"
                ? ValueTask.FromException<SearchClientResult>(new IOException("fixture disconnected"))
                : ValueTask.FromResult(Result(query.Text));
    }

    private sealed class RevisionSearchClient : ISearchClient, IBrokerStateClient
    {
        private int _stateCalls;
        public int SearchCalls;

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SearchCalls);
            return ValueTask.FromResult(Result(query.Text));
        }

        public ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _stateCalls);
            var revision = Math.Min(3, call - 1);
            return ValueTask.FromResult(new BrokerStateSnapshot(
                "fixture-epoch",
                revision,
                BrokerRuntimePhase.Updating,
                CacheAvailable: true,
                BackgroundUpdating: true,
                FileCount: 1,
                LastUpdatedUtc: DateTimeOffset.UtcNow,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready")));
        }

        public void RetryConnection()
        {
        }
    }

    private sealed class RecoveringStateClient : ISearchClient, IBrokerStateClient
    {
        private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SearchCalls;

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref SearchCalls) == 1)
            {
                _firstFailure.TrySetResult();
                return ValueTask.FromException<SearchClientResult>(new IOException("fixture connecting"));
            }

            return ValueTask.FromResult(new SearchClientResult(
                [new SearchHit(
                    @"C:\Models\recovered.SLDPRT",
                    SolidWorksDocumentKind.Part,
                    "recovered.SLDPRT",
                    null,
                    null,
                    null,
                    1,
                    false)],
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready")));
        }

        public async ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
        {
            await _firstFailure.Task.WaitAsync(cancellationToken);
            return new BrokerStateSnapshot(
                "recovered-epoch",
                1,
                BrokerRuntimePhase.Ready,
                CacheAvailable: true,
                BackgroundUpdating: false,
                FileCount: 1,
                LastUpdatedUtc: DateTimeOffset.UtcNow,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"));
        }

        public void RetryConnection()
        {
        }
    }

    private sealed class StableRevisionSearchClient : ISearchClient, IBrokerStateClient
    {
        public int SearchCalls;
        public int StateCalls;

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SearchCalls);
            return ValueTask.FromResult(Result(query.Text));
        }

        public ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StateCalls);
            return ValueTask.FromResult(new BrokerStateSnapshot(
                "stable-epoch",
                42,
                BrokerRuntimePhase.Ready,
                CacheAvailable: true,
                BackgroundUpdating: false,
                FileCount: 1,
                LastUpdatedUtc: DateTimeOffset.UtcNow,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready")));
        }

        public void RetryConnection()
        {
        }
    }

    private static SearchClientResult Result(string text)
    {
        var fileName = text + ".SLDPRT";
        return new SearchClientResult(
            [new SearchHit(@"C:\模型\" + fileName, SolidWorksDocumentKind.Part, fileName, null, null, null, 1, false)],
            new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"));
    }

    private sealed class RecordingFileActions : IFileActions
    {
        public List<string> Paths { get; } = [];
        public void Open(string fullPath) => Paths.Add(fullPath);
        public void Reveal(string fullPath) => Paths.Add(fullPath);
        public void CopyPath(string fullPath) => Paths.Add(fullPath);
        public void ViewProperties(string fullPath) => Paths.Add(fullPath);
    }

}
