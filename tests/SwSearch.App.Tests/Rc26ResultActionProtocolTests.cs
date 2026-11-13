using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc26ResultActionProtocolTests
{
    [TestMethod]
    public void Router_AcceptsExactlyTheFourSharedActions_WithSpecialCharacterPath()
    {
        var router = new WebMessageRouter();
        const string path = @"D:\TC 模板库\空格 & (括号)\尾部\零件.SLDPRT";
        foreach (var action in ResultActionProtocol.Actions)
        {
            var json = JsonSerializer.Serialize(new { type = "contextAction", payload = DesktopAction(action, path) });
            Assert.IsTrue(router.TryValidate(json, out var message, out var error), $"{action}: {error}");
            Assert.AreEqual("contextAction", message!.Type);
        }
        var rejected = JsonSerializer.Serialize(new { type = "contextAction", payload = DesktopAction("Open", path) });
        Assert.IsFalse(router.TryValidate(rejected, out _, out _), "Action names remain ordinal and case-exact.");
    }

    [TestMethod]
    public void DesktopBridge_DispatchesEverySharedAction_ToTheCurrentRowOnly()
    {
        const string path = @"D:\TC 模板库\空格 & (括号)\尾部\零件.SLDPRT";
        var actions = new RecordingFileActions();
        using var viewModel = new MainViewModel(new EmptySearchClient(), actions);
        viewModel.Results.Add(new SearchResultItem(new SearchHit(
            path, SolidWorksDocumentKind.Part, "零件.SLDPRT", null, null, null, 1, false)));
        using var bridge = new DesktopWebBridge(viewModel, new UnavailableUserPreferencesRepository("test"));

        foreach (var action in ResultActionProtocol.Actions)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(DesktopAction(action, path)));
            bridge.HandleMessage("contextAction", document.RootElement);
        }

        CollectionAssert.AreEqual(
            new[] { "open", "reveal", "copyPath", "viewProperties" },
            actions.Calls.Select(call => call.Action).ToArray());
        Assert.IsTrue(actions.Calls.All(call => call.Path == path));
    }

    [TestMethod]
    public void BrandIcon_HasExactMultisizeFrames_TransparentCorners_AndTightSubjectBounds()
    {
        var root = FindRepositoryRoot();
        var ico = File.ReadAllBytes(Path.Combine(root, "src", "Assets", "Brand", "SwSearch.ico"));
        Assert.AreEqual(0, BitConverter.ToUInt16(ico, 0));
        Assert.AreEqual(1, BitConverter.ToUInt16(ico, 2));
        var count = BitConverter.ToUInt16(ico, 4);
        Assert.AreEqual(8, count);
        var sizes = Enumerable.Range(0, count)
            .Select(index => ico[6 + index * 16] == 0 ? 256 : (int)ico[6 + index * 16])
            .ToArray();
        CollectionAssert.AreEqual(new[] { 16, 20, 24, 32, 48, 64, 128, 256 }, sizes);

        using var stream = File.OpenRead(Path.Combine(root, "src", "Assets", "Brand", "SwSearch-256.png"));
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        Assert.AreEqual(0, pixels[3], "Top-left alpha must be transparent.");
        var minX = frame.PixelWidth;
        var minY = frame.PixelHeight;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < frame.PixelHeight; y++)
        for (var x = 0; x < frame.PixelWidth; x++)
        {
            if (pixels[(y * frame.PixelWidth + x) * 4 + 3] == 0) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        Assert.IsTrue(minX <= 12 && maxX >= 243, $"Horizontal subject bounds are too small: {minX}..{maxX}");
        Assert.IsTrue(minY <= 24 && maxY >= 231, $"Vertical subject bounds are too small: {minY}..{maxY}");
    }

    private static object DesktopAction(string action, string path) => new
    {
        schema = ResultActionProtocol.CurrentSchemaVersion,
        action,
        path,
        resultSetId = 0L,
        resultRevision = (long?)null,
        resultStart = 0,
        resultIndex = 0,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class EmptySearchClient : ISearchClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                [],
                new RuntimeCapability(CapabilityState.Unavailable, "TEST_UNAVAILABLE", "test")));
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
