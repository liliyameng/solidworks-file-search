using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc69FolderLocateTests
{
    [TestMethod]
    public void DesktopBridge_RevealUsesOnlyTheExactCurrentIndexedBrokerPath()
    {
        const string firstPath = @"C:\Design\first.SLDPRT";
        const string selectedPath = @"D:\客户项目\Space & (fixture)\selected.SLDASM";
        var actions = new RecordingFileActions();
        using var viewModel = CreateViewModel(actions, firstPath, selectedPath);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));

        bridge.HandleMessage("contextAction", ActionPayload(
            ResultActionProtocol.Reveal,
            selectedPath,
            viewModel,
            resultIndex: 1));

        CollectionAssert.AreEqual(
            new[] { (ResultActionProtocol.Reveal, selectedPath) },
            actions.Calls.ToArray());
        Assert.AreSame(viewModel.Results[1], viewModel.SelectedResult);
    }

    [TestMethod]
    public void DesktopBridge_RejectsStaleUnknownAndPathMismatchedRevealRequests()
    {
        const string firstPath = @"C:\Design\first.SLDPRT";
        const string selectedPath = @"D:\Design\second.SLDASM";
        var actions = new RecordingFileActions();
        using var viewModel = CreateViewModel(actions, firstPath, selectedPath);
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));

        bridge.HandleMessage("contextAction", ActionPayload(
            ResultActionProtocol.Reveal,
            selectedPath,
            viewModel,
            resultSetId: viewModel.CurrentResultSetId + 1,
            resultIndex: 1));
        Assert.AreEqual(0, actions.Calls.Count, "A stale result set must not reach Explorer.");

        bridge.HandleMessage("contextAction", ActionPayload(
            ResultActionProtocol.Reveal,
            firstPath,
            viewModel,
            resultIndex: 1));
        Assert.AreEqual(0, actions.Calls.Count, "A path from a different current row must be rejected.");

        bridge.HandleMessage("contextAction", ActionPayload(
            ResultActionProtocol.Reveal,
            firstPath,
            viewModel,
            resultIndex: 2));
        Assert.AreEqual(0, actions.Calls.Count, "An unknown row index must not reach Explorer.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.StatusText), "The rejected action must surface a failure status.");
    }

    [TestMethod]
    public void ResultActionProtocol_RequiresCompleteCurrentResultIdentity()
    {
        const string path = @"C:\Design\fixture.SLDPRT";
        var router = new WebMessageRouter();
        var valid = JsonSerializer.Serialize(new
        {
            type = "contextAction",
            payload = new
            {
                schema = ResultActionProtocol.CurrentSchemaVersion,
                action = ResultActionProtocol.Reveal,
                path,
                resultSetId = 9L,
                resultRevision = 12L,
                resultStart = 200,
                resultIndex = 4,
            },
        });
        var missingIdentity = JsonSerializer.Serialize(new
        {
            type = "contextAction",
            payload = new
            {
                schema = ResultActionProtocol.CurrentSchemaVersion,
                action = ResultActionProtocol.Reveal,
                path,
                resultSetId = 9L,
                resultRevision = 12L,
                resultStart = 200,
            },
        });
        var legacyPathOnly = JsonSerializer.Serialize(new
        {
            type = "contextAction",
            payload = new { schema = 1, action = ResultActionProtocol.Reveal, path },
        });

        Assert.IsTrue(router.TryValidate(valid, out _, out var validError), validError);
        Assert.IsFalse(router.TryValidate(missingIdentity, out _, out _));
        Assert.IsFalse(router.TryValidate(legacyPathOnly, out _, out _));
    }

    [TestMethod]
    public void ExplorerReveal_UsesSeparateSafeSelectAndExactPathArguments()
    {
        const string path = @"D:\客户项目\Space & (fixture)\selected file.SLDPRT";
        ProcessStartInfo startInfo = WindowsFileActions.CreateRevealStartInfo(path);

        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            startInfo.FileName);
        Assert.IsFalse(startInfo.UseShellExecute);
        CollectionAssert.AreEqual(new[] { "/select,", path }, startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.Arguments.Contains(path, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExplorerReveal_RejectsMissingAndRelativeTargetsBeforeLaunch()
    {
        var actions = new WindowsFileActions();
        var missingPath = Path.Combine(Path.GetTempPath(), "SwSearch-Rc69-" + Guid.NewGuid().ToString("N") + ".SLDPRT");

        Assert.IsFalse(File.Exists(missingPath));
        Assert.ThrowsException<FileNotFoundException>(() => actions.Reveal(missingPath));
        Assert.ThrowsException<ArgumentException>(() => actions.Reveal("relative.SLDPRT"));
    }

    [TestMethod]
    public void MainWebUi_BindsDesktopActionsToTheCurrentResultWindowAndRow()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));

        StringAssert.Contains(script, "function createDesktopResultReference");
        StringAssert.Contains(script, "schema: 2, path, resultSetId, resultRevision, resultStart, resultIndex");
        StringAssert.Contains(script, "post(\"contextAction\", { ...desktopReference, action })");
        StringAssert.Contains(script, "postResultAction(\"open\", result, index - resultBaseOffset)");
        StringAssert.Contains(script, "showContextMenu(event, result, index - resultBaseOffset)");
        StringAssert.Contains(script, "Cached rows are display-only until the desktop model");
        StringAssert.Contains(script, "requestedWindowOffset = offset;");
    }

    private static MainViewModel CreateViewModel(RecordingFileActions actions, params string[] paths)
    {
        var viewModel = new MainViewModel(new EmptySearchClient(), actions);
        foreach (var path in paths)
        {
            viewModel.Results.Add(new SearchResultItem(new SearchHit(
                path,
                path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)
                    ? SolidWorksDocumentKind.Assembly
                    : SolidWorksDocumentKind.Part,
                Path.GetFileName(path),
                null,
                null,
                null,
                1,
                false)));
        }
        return viewModel;
    }

    private static JsonElement ActionPayload(
        string action,
        string path,
        MainViewModel viewModel,
        long? resultSetId = null,
        int resultIndex = 0) =>
        JsonSerializer.SerializeToElement(new
        {
            schema = ResultActionProtocol.CurrentSchemaVersion,
            action,
            path,
            resultSetId = resultSetId ?? viewModel.CurrentResultSetId,
            resultRevision = viewModel.CurrentPageRevision,
            resultStart = viewModel.ResultBaseOffset,
            resultIndex,
        });

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class EmptySearchClient : ISearchClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                [],
                new RuntimeCapability(CapabilityState.Available, "TEST", "test")));
    }

    private sealed class RecordingFileActions : IFileActions
    {
        public List<(string Action, string Path)> Calls { get; } = [];
        public void Open(string fullPath) => Calls.Add((ResultActionProtocol.Open, fullPath));
        public void Reveal(string fullPath) => Calls.Add((ResultActionProtocol.Reveal, fullPath));
        public void CopyPath(string fullPath) => Calls.Add((ResultActionProtocol.CopyPath, fullPath));
        public void ViewProperties(string fullPath) => Calls.Add((ResultActionProtocol.ViewProperties, fullPath));
    }
}
