using System.Text.Json;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.WebUi;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc68ShellFileResultsTests
{
    [TestMethod]
    public void FileDragProtocol_AcceptsOnlyTheCurrentSchemaAndExactShellPathFields()
    {
        const string gestureId = "0123456789abcdef0123456789abcdef";
        const string path = @"C:\Design\fixture.SLDPRT";
        var valid = JsonSerializer.Serialize(new
        {
            schema = 1,
            gestureId,
            resultSetId = 12L,
            resultRevision = 34L,
            path,
        });
        var invalid = JsonSerializer.Serialize(new
        {
            schema = 1,
            gestureId,
            resultSetId = 12L,
            resultRevision = 34L,
            path,
            extra = "replay",
        });

        var router = new WebMessageRouter();
        Assert.IsTrue(router.TryValidate($"{{\"type\":\"fileDrag.arm\",\"payload\":{valid}}}", out var message, out var error), error);
        Assert.AreEqual("fileDrag.arm", message!.Type);
        Assert.IsFalse(router.TryValidate($"{{\"type\":\"fileDrag.arm\",\"payload\":{invalid}}}", out _, out _));
    }

    [TestMethod]
    public void ShellFileDrop_UsesOneExactFileDropPayloadWithoutTextFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SwSearch-Rc68-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "exact name.SLDASM");
        try
        {
            File.WriteAllText(path, "fixture");

            Assert.IsTrue(ShellFileDrop.Exists(path));
            Assert.IsTrue(ShellFileDrop.TryCreateDataObject(path, out var dataObject));
            Assert.IsNotNull(dataObject);
            Assert.IsTrue(dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: false));
            var files = dataObject.GetData(DataFormats.FileDrop, autoConvert: false) as string[];
            CollectionAssert.AreEqual(new[] { path }, files!);
            Assert.IsFalse(dataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: false));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void DesktopBridge_ArmsOnlyCurrentResultSetAndKeepsBrokerPathUnchanged()
    {
        const string brokerPath = @"C:\Design\Original Case.SLDPRT";
        const string pagePath = @"c:\design\original case.sldprt";
        using var viewModel = new MainViewModel(new ImmediateSearchClient(), new NoOpFileActions());
        viewModel.Results.Add(new SearchResultItem(new SearchHit(
            brokerPath,
            SolidWorksDocumentKind.Part,
            "Original Case.SLDPRT",
            null,
            null,
            null,
            1,
            false)));
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        FileDragArmRequest? armed = null;
        bridge.FileDragArmed += request => armed = request;

        bridge.HandleMessage("fileDrag.arm", Payload(
            path: pagePath,
            resultSetId: viewModel.CurrentResultSetId,
            resultRevision: viewModel.CurrentPageRevision));

        Assert.IsNotNull(armed);
        Assert.AreEqual(brokerPath, armed!.Path, "The bridge must forward the broker-owned path, never a client rewrite.");

        armed = null;
        bridge.HandleMessage("fileDrag.arm", Payload(
            path: pagePath,
            resultSetId: viewModel.CurrentResultSetId + 1,
            resultRevision: viewModel.CurrentPageRevision));
        Assert.IsNull(armed, "A replay from a previous result window must not arm a drag.");
    }

    [TestMethod]
    public async Task QueryProgress_UsesMonotonicQueryUiIdAndNeverExposesInventedPercentages()
    {
        var client = new BlockingSearchClient();
        using var viewModel = new MainViewModel(client, new NoOpFileActions());
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));
        object? published = null;
        bridge.StateReady += payload => published = payload;
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "main" }));

        bridge.HandleMessage("search", JsonSerializer.SerializeToElement(new
        {
            query = "first",
            clientSequence = 41,
            inputDelayMs = 0,
        }));
        await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bridge.PublishState();
        var first = QueryProgress(published);
        Assert.AreEqual(1L, first.GetProperty("queryUiId").GetInt64());
        Assert.AreEqual(41, first.GetProperty("clientSequence").GetInt32());
        Assert.AreEqual("running", first.GetProperty("phase").GetString());
        Assert.IsFalse(first.TryGetProperty("percent", out _));

        bridge.HandleMessage("search", JsonSerializer.SerializeToElement(new
        {
            query = "second",
            clientSequence = 42,
            inputDelayMs = 0,
        }));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bridge.PublishState();
        var second = QueryProgress(published);
        Assert.AreEqual(2L, second.GetProperty("queryUiId").GetInt64());
        Assert.AreEqual(42, second.GetProperty("clientSequence").GetInt32());
        Assert.AreEqual("running", second.GetProperty("phase").GetString());

        client.ReleaseSecond.TrySetResult();
        await WaitUntilAsync(() => !viewModel.IsBusy);
        bridge.PublishState();
        var completed = QueryProgress(published);
        Assert.AreEqual(2L, completed.GetProperty("queryUiId").GetInt64());
        Assert.AreEqual("idle", completed.GetProperty("phase").GetString());
    }

    [TestMethod]
    public void MainWebUi_UsesDesktopOnlyGestureArmAndSeparateIndeterminateQueryOverlay()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));

        StringAssert.Contains(script, "message.type === \"fileDrag.gesture\" && view === \"main\"");
        StringAssert.Contains(script, "post(\"fileDrag.arm\"");
        StringAssert.Contains(script, "path: pending.path");
        StringAssert.Contains(script, "queryProgress");
        StringAssert.Contains(css, ".search-wrap.query-progress-active::after");
        StringAssert.Contains(css, "@keyframes query-progress-sweep");
        Assert.IsFalse(script.Contains("rowToken", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("armExternalFileDrag", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CompositionHost_FileDropStartsOnlyFromAValidatedHeldNativeGesture()
    {
        var host = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.WebUi", "CompositionWebViewHost.cs"));
        var callbackStart = host.IndexOf("private void WebViewOnWebMessageReceived", StringComparison.Ordinal);
        var callbackEnd = host.IndexOf("private bool TryForwardMouseMessage", callbackStart, StringComparison.Ordinal);
        Assert.IsTrue(callbackStart >= 0 && callbackEnd > callbackStart);
        var callback = host[callbackStart..callbackEnd];

        StringAssert.Contains(host, "QueueShellFileDragArm");
        StringAssert.Contains(host, "BeginNativeFileDragGesture");
        StringAssert.Contains(host, "SystemInformation.DragSize");
        StringAssert.Contains(host, "Task.Run(() => ShellFileDrop.Exists");
        StringAssert.Contains(host, "ShellFileDrop.TryCreateDataObject");
        StringAssert.Contains(host, "DoDragDrop(dataObject, DragDropEffects.Copy)");
        StringAssert.Contains(host, "NativeMethods.WmCaptureChanged");
        StringAssert.Contains(host, "CancelNativeFileDragGesture();");
        Assert.IsFalse(callback.Contains("DoDragDrop", StringComparison.Ordinal));
    }

    private static JsonElement Payload(string path, long resultSetId, long? resultRevision) =>
        JsonSerializer.SerializeToElement(new
        {
            schema = 1,
            gestureId = "0123456789abcdef0123456789abcdef",
            resultSetId,
            resultRevision,
            path,
        });

    private static JsonElement QueryProgress(object? payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return document.RootElement.GetProperty("queryProgress").Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ImmediateSearchClient : ISearchClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                [],
                new RuntimeCapability(CapabilityState.Available, "TEST", "test")));
    }

    private sealed class BlockingSearchClient : ISearchClient
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            if (query.Text == "first")
            {
                FirstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
            {
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }

            return new SearchClientResult(
                [],
                new RuntimeCapability(CapabilityState.Available, "TEST", "test"));
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
