using System.Collections.Specialized;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc49ProgressAndRefreshTests
{
    [TestMethod]
    public async Task RevisionRefresh_KeepsOldWindowUntilChangedReplacementIsReady()
    {
        var client = new BlockingRevisionClient();
        using var viewModel = new MainViewModel(
            client,
            new NoOpFileActions(),
            TimeSpan.Zero,
            statePollInterval: TimeSpan.FromMilliseconds(10));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.Results.Count == 2);
        viewModel.SelectedResult = viewModel.Results[1];
        var selectedPath = viewModel.SelectedResult.FullPath;
        var initialResultSetId = viewModel.CurrentResultSetId;
        var resets = 0;
        var backgroundBusyTransitions = 0;
        viewModel.Results.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) Interlocked.Increment(ref resets);
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsBusy) && viewModel.IsBusy)
            {
                Interlocked.Increment(ref backgroundBusyTransitions);
            }
        };

        await client.RevisionSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, Volatile.Read(ref client.InitialSearchWasInteractive));
        Assert.AreEqual(0, Volatile.Read(ref client.RevisionSearchWasInteractive));

        Assert.AreEqual(2, viewModel.Results.Count, "后台重查尚未完成时不得清空旧结果。");
        Assert.AreEqual(initialResultSetId, viewModel.CurrentResultSetId, "结果完成前不得切换 resultSetId。");
        Assert.AreEqual(selectedPath, viewModel.SelectedResult?.FullPath);
        Assert.AreEqual(0, backgroundBusyTransitions, "后台 revision 重查不得触发可见的搜索忙碌状态。");

        client.ReleaseRevisionSearch.TrySetResult();
        await WaitUntilAsync(() => viewModel.Results.Count == 3);

        Assert.IsTrue(viewModel.CurrentResultSetId > initialResultSetId);
        Assert.AreEqual(1, resets, "变化后的结果应一次原子替换。");
        Assert.AreEqual(selectedPath, viewModel.SelectedResult?.FullPath, "仍存在的选中路径应被保留。");
    }

    [TestMethod]
    public async Task RevisionRefresh_WithIdenticalRows_DoesNotResetOrRotateResultSet()
    {
        var client = new StableRowsRevisionClient();
        using var viewModel = new MainViewModel(
            client,
            new NoOpFileActions(),
            TimeSpan.Zero,
            statePollInterval: TimeSpan.FromMilliseconds(10));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.Results.Count == 1);
        viewModel.SelectedResult = viewModel.Results[0];
        var selectedInstance = viewModel.SelectedResult;
        var initialResultSetId = viewModel.CurrentResultSetId;
        var resets = 0;
        viewModel.Results.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) Interlocked.Increment(ref resets);
        };

        await WaitUntilAsync(() => Volatile.Read(ref client.SearchCalls) >= 2);
        await Task.Delay(50);

        Assert.AreEqual(initialResultSetId, viewModel.CurrentResultSetId);
        Assert.AreEqual(0, resets);
        Assert.AreSame(selectedInstance, viewModel.SelectedResult);
    }

    [TestMethod]
    public void ProgressOnlyWebUpdate_HasNoGridOrRowRebuildCall()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        const string startToken = "function updateSearchStatusState()";
        const string endToken = "function updateIndexProgress()";
        var start = script.IndexOf(startToken, StringComparison.Ordinal);
        var end = script.IndexOf(endToken, start, StringComparison.Ordinal);

        Assert.IsTrue(start >= 0 && end > start);
        var progressOnlyBody = script[start..end];
        Assert.IsFalse(progressOnlyBody.Contains("renderGrid", StringComparison.Ordinal));
        Assert.IsFalse(progressOnlyBody.Contains("renderViewportRows", StringComparison.Ordinal));
        Assert.IsFalse(progressOnlyBody.Contains("innerHTML", StringComparison.Ordinal));
    }

    private static SearchHit Hit(string name, string? value = null) => new(
        $@"C:\Design\{name}",
        SolidWorksDocumentKind.Part,
        name,
        value is null ? null : "物料编码",
        value,
        null,
        1,
        false);

    private static BrokerStateSnapshot State(long revision) => new(
        "rc49-fixture",
        revision,
        BrokerRuntimePhase.Updating,
        CacheAvailable: true,
        BackgroundUpdating: true,
        FileCount: 2,
        LastUpdatedUtc: DateTimeOffset.UtcNow,
        new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class BlockingRevisionClient : ISearchClient, IBrokerStateClient
    {
        private int _searchCalls;
        private int _stateCalls;
        public int InitialSearchWasInteractive;
        public int RevisionSearchWasInteractive;
        public TaskCompletionSource RevisionSearchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRevisionSearch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _searchCalls);
            if (call == 1)
            {
                Volatile.Write(ref InitialSearchWasInteractive, query.EffectiveIsInteractive ? 1 : 0);
            }
            else
            {
                Volatile.Write(ref RevisionSearchWasInteractive, query.EffectiveIsInteractive ? 1 : 0);
            }
            if (call > 1)
            {
                RevisionSearchStarted.TrySetResult();
                await ReleaseRevisionSearch.Task.WaitAsync(cancellationToken);
            }
            var hits = call == 1
                ? new[] { Hit("A.SLDPRT", "A-1"), Hit("B.SLDPRT", "B-1") }
                : new[] { Hit("A.SLDPRT", "A-2"), Hit("B.SLDPRT", "B-1"), Hit("C.SLDPRT", "C-1") };
            return new SearchClientResult(
                hits,
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"),
                HasMore: false,
                IndexRevision: call);
        }

        public ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _stateCalls);
            return ValueTask.FromResult(State(call == 1 ? 1 : 2));
        }

        public void RetryConnection()
        {
        }
    }

    private sealed class StableRowsRevisionClient : ISearchClient, IBrokerStateClient
    {
        private int _stateCalls;
        public int SearchCalls;

        public ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref SearchCalls);
            return ValueTask.FromResult(new SearchClientResult(
                [Hit("Stable.SLDPRT", "S-1")],
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "ready"),
                HasMore: false,
                IndexRevision: call));
        }

        public ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _stateCalls);
            return ValueTask.FromResult(State(call == 1 ? 1 : 2));
        }

        public void RetryConnection()
        {
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
