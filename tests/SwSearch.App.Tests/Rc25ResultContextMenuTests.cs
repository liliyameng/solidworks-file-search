using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc25ResultContextMenuTests
{
    [TestMethod]
    public void FourActions_UseStableDataActionAndSingleRowPathPayload()
    {
        var script = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");

        foreach (var action in new[] { "open", "reveal", "copyPath", "viewProperties" })
            StringAssert.Contains(script, $"[\"{action}\"");
        StringAssert.Contains(script, "button.dataset.action = action");
        StringAssert.Contains(script, "menu.dataset.path = rowPath");
        StringAssert.Contains(script, "const path = menu.dataset.path");
        StringAssert.Contains(script, "post(\"contextAction\", { schema: 1, action, path })");
    }

    [TestMethod]
    public void ClickPostsToBridgeBeforeClosingAndStopsGlobalDismissal()
    {
        var script = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");
        var handler = Slice(script, "menu.addEventListener(\"click\"", "document.body.append(menu)");

        Assert.IsTrue(
            handler.IndexOf("post(\"contextAction\"", StringComparison.Ordinal) <
            handler.IndexOf("dismissContextMenu()", StringComparison.Ordinal));
        StringAssert.Contains(handler, "clickEvent.stopPropagation()");
        StringAssert.Contains(script, "pointerEvent => pointerEvent.stopPropagation()");
        StringAssert.Contains(handler, "showContextActionFeedback");
    }

    [TestMethod]
    public void MenuIsContentSizedAndClampedToBothViewportEdges()
    {
        var script = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");
        var css = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.css");

        StringAssert.Contains(css, "width: max-content");
        StringAssert.Contains(css, "min-width: 180px");
        StringAssert.Contains(css, "max-width: min(260px, calc(100vw - 16px))");
        StringAssert.Contains(script, "innerWidth - bounds.width - margin");
        StringAssert.Contains(script, "innerHeight - bounds.height - margin");
        StringAssert.Contains(script, "Math.max(margin, Math.min");
    }

    [TestMethod]
    public void DesktopBridgeReportsStaleOrInvalidRowAndUsesExistingSafeActions()
    {
        var bridge = Read("src", "SwSearch.App", "DesktopWebBridge.cs");
        var actions = Read("src", "SwSearch.App", "FileActions.cs");

        StringAssert.Contains(bridge, "_viewModel.ReportFileActionFailure");
        StringAssert.Contains(bridge, "_viewModel.Open");
        StringAssert.Contains(bridge, "_viewModel.Reveal");
        StringAssert.Contains(bridge, "_viewModel.CopyPath");
        StringAssert.Contains(bridge, "_viewModel.ViewProperties");
        StringAssert.Contains(actions, "startInfo.ArgumentList.Add");
        StringAssert.Contains(actions, "Clipboard.SetText(fullPath)");
        Assert.IsFalse(actions.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(actions.Contains("/c ", StringComparison.OrdinalIgnoreCase));
    }

    [DataTestMethod]
    [DataRow(@"D:\中文 目录\A&B (装配体)\尾部\零件.SLDPRT")]
    [DataRow(@"C:\space folder\assembly (v2)&final.SLDASM")]
    public void SpecialCharacterPathIsSerializedAsOneJsonField(string path)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { action = "open", path });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(path, document.RootElement.GetProperty("path").GetString());
        Assert.AreEqual(2, document.RootElement.EnumerateObject().Count());
    }

    private static string Slice(string value, string start, string end)
    {
        var first = value.IndexOf(start, StringComparison.Ordinal);
        var last = value.IndexOf(end, first, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && last > first);
        return value[first..last];
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
