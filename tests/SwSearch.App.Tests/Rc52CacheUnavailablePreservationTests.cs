using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc52CacheUnavailablePreservationTests
{
    [TestMethod]
    public async Task CacheTemporarilyUnavailable_PreservesVisibleResults_AndRetrySuccessReplacesThem()
    {
        var initialHits = new[]
        {
            Hit("initial-a.SLDPRT"),
            Hit("initial-b.SLDPRT"),
        };
        var client = new ScriptedSearchClient(
            Available(initialHits, revision: 7, fingerprint: "initial-page"),
            new SearchClientResult(
                [],
                new RuntimeCapability(
                    CapabilityState.Degraded,
                    "LOCAL_INDEX_UNAVAILABLE",
                    "fixture cache migration"),
                HasMore: false,
                Availability: SearchResultAvailability.CacheTemporarilyUnavailable),
            Available([Hit("recovered.SLDPRT")], revision: 8, fingerprint: "recovered-page"));
        using var viewModel = new MainViewModel(
            client,
            new NoOpFileActions(),
            TimeSpan.FromMilliseconds(1));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.CallCount == 1 && viewModel.Results.Count == 2);
        var originalPaths = viewModel.Results.Select(static item => item.FullPath).ToArray();
        var originalTotal = viewModel.TotalResultCount;
        var originalRevision = viewModel.CurrentPageRevision;

        viewModel.RetryConnectionCommand.Execute(null);
        await WaitUntilAsync(() => client.CallCount == 2 && !viewModel.IsBusy);

        CollectionAssert.AreEqual(originalPaths, viewModel.Results.Select(static item => item.FullPath).ToArray());
        Assert.AreEqual(originalTotal, viewModel.TotalResultCount);
        Assert.AreEqual(originalRevision, viewModel.CurrentPageRevision);
        Assert.IsTrue(viewModel.IsRetryVisible);
        StringAssert.Contains(viewModel.StatusText, "保留当前结果");
        StringAssert.Contains(viewModel.StatusText, "自动重试");

        viewModel.RetryConnectionCommand.Execute(null);
        await WaitUntilAsync(() =>
            client.CallCount == 3 &&
            !viewModel.IsBusy &&
            viewModel.Results.Count == 1 &&
            viewModel.Results[0].FileName == "recovered.SLDPRT");

        Assert.AreEqual(1, viewModel.TotalResultCount);
        Assert.AreEqual(8L, viewModel.CurrentPageRevision);
        Assert.IsFalse(viewModel.IsRetryVisible);
    }

    [TestMethod]
    public async Task ReconnectingEmptyPage_PreservesVisibleResultsUntilAnAuthoritativePageArrives()
    {
        var initialHits = new[]
        {
            Hit("initial-a.SLDPRT"),
            Hit("initial-b.SLDPRT"),
        };
        var client = new ScriptedSearchClient(
            Available(initialHits, revision: 7, fingerprint: "initial-page"),
            new SearchClientResult(
                [],
                new RuntimeCapability(
                    CapabilityState.Degraded,
                    "BROKER_RECONNECTING",
                    "fixture reconnecting"),
                HasMore: false,
                IndexRevision: 8,
                PageFingerprint: "transitional-empty-page"),
            Available([Hit("recovered.SLDPRT")], revision: 9, fingerprint: "recovered-page"));
        using var viewModel = new MainViewModel(
            client,
            new NoOpFileActions(),
            TimeSpan.FromMilliseconds(1));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.CallCount == 1 && viewModel.Results.Count == 2);
        var originalPaths = viewModel.Results.Select(static item => item.FullPath).ToArray();
        var originalTotal = viewModel.TotalResultCount;
        var originalRevision = viewModel.CurrentPageRevision;
        var originalResultSet = viewModel.CurrentResultSetId;

        viewModel.RetryConnectionCommand.Execute(null);
        await WaitUntilAsync(() => client.CallCount == 2 && viewModel.IsRetryVisible);

        CollectionAssert.AreEqual(originalPaths, viewModel.Results.Select(static item => item.FullPath).ToArray());
        Assert.AreEqual(originalTotal, viewModel.TotalResultCount);
        Assert.AreEqual(originalRevision, viewModel.CurrentPageRevision);
        Assert.AreEqual(originalResultSet, viewModel.CurrentResultSetId);

        viewModel.RetryConnectionCommand.Execute(null);
        await WaitUntilAsync(() =>
            client.CallCount == 3 &&
            viewModel.Results.Count == 1 &&
            viewModel.Results[0].FileName == "recovered.SLDPRT");

        Assert.IsFalse(viewModel.IsRetryVisible);
        Assert.IsTrue(viewModel.CurrentResultSetId > originalResultSet);
    }

    [TestMethod]
    public async Task SuccessfulZeroResult_IsAuthoritativeAndClearsPreviouslyVisibleResults()
    {
        var client = new ScriptedSearchClient(
            Available([Hit("visible-before-zero.SLDPRT")], revision: 10, fingerprint: "nonempty"),
            Available([], revision: 11, fingerprint: "empty"));
        using var viewModel = new MainViewModel(
            client,
            new NoOpFileActions(),
            TimeSpan.FromMilliseconds(1));

        await viewModel.StartAsync();
        await WaitUntilAsync(() => client.CallCount == 1 && viewModel.Results.Count == 1);

        viewModel.RetryConnectionCommand.Execute(null);
        await WaitUntilAsync(() => client.CallCount == 2 && !viewModel.IsBusy);

        Assert.AreEqual(0, viewModel.Results.Count);
        Assert.AreEqual(0, viewModel.TotalResultCount);
        Assert.AreEqual(11L, viewModel.CurrentPageRevision);
        Assert.IsFalse(viewModel.IsRetryVisible);
    }

    private static SearchClientResult Available(
        IReadOnlyList<SearchHit> hits,
        long revision,
        string fingerprint) =>
        new(
            hits,
            new RuntimeCapability(CapabilityState.Available, "LOCAL_INDEX_READY", "ready"),
            HasMore: false,
            IndexRevision: revision,
            PageFingerprint: fingerprint);

    private static SearchHit Hit(string fileName) =>
        new(
            Path.Combine(@"C:\Models", fileName),
            SolidWorksDocumentKind.Part,
            fileName,
            null,
            null,
            null,
            1,
            false);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the view-model state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ScriptedSearchClient(params SearchClientResult[] results) : ISearchClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _callCount) - 1;
            if (index >= results.Length)
            {
                throw new InvalidOperationException("Unexpected search call.");
            }

            return ValueTask.FromResult(results[index]);
        }
    }

    private sealed class NoOpFileActions : IFileActions
    {
        public void Open(string fullPath)
        {
        }

        public void Reveal(string fullPath)
        {
        }

        public void CopyPath(string fullPath)
        {
        }

        public void ViewProperties(string fullPath)
        {
        }
    }
}
