using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc19WebUiContractTests
{
    [TestMethod]
    public void MainWebUi_HasNoCentralPromptOrBrowseScope_AndCanExitBoundScope()
    {
        var js = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");

        Assert.IsFalse(js.Contains("输入关键字开始搜索", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("browseScope", StringComparison.Ordinal));
        StringAssert.Contains(js, "post(\"restoreScope\", {})");
        Assert.IsFalse(js.Contains("选择目录", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("全部位置", StringComparison.Ordinal));
        StringAssert.Contains(js, "placeholder=\"键入文件名或属性，立即搜索\"");
        StringAssert.Contains(js, "id=\"rows\"");
    }

    [TestMethod]
    public void SearchMode_IsNeverBlankAndDefaultsToAll()
    {
        var js = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");

        StringAssert.Contains(js, "data-mode=\"all\" aria-pressed=\"true\">全部</button>");
        StringAssert.Contains(js, "data-mode=\"name\" aria-pressed=\"false\">文件名</button>");
        StringAssert.Contains(js, "data-mode=\"property\" aria-pressed=\"false\">属性</button>");
        StringAssert.Contains(js, "allowedModes.includes(state.mode) ? state.mode : \"all\"");
        StringAssert.Contains(js, "updateDesktopModeButtons(mode)");
        StringAssert.Contains(js, "search.addEventListener(\"input\"");
    }

    [TestMethod]
    public void GearCommand_UsesRc20InPagePreferencesAndCannotAliasRefresh()
    {
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("preferences.open"));
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("preferences.save"));
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("preferences.close"));
        Assert.IsFalse(DesktopWebBridge.IsAllowedCommand("settings"));
        Assert.IsFalse(DesktopWebBridge.IsAllowedCommand("refresh"));
        Assert.IsFalse(DesktopWebBridge.IsAllowedCommand("browseScope"));
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("restoreScope"));

        var router = new WebMessageRouter();
        Assert.IsTrue(router.TryValidate(
            """{"type":"restoreScope","payload":{}}""",
            out _,
            out var restoreError),
            restoreError);
        Assert.IsFalse(router.TryValidate(
            """{"type":"restoreScope","payload":{"rootPath":"C:\\Other"}}""",
            out _,
            out _),
            "restoreScope must retain its strict empty-payload boundary.");

        var js = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");
        StringAssert.Contains(js, "settingsButton.addEventListener(\"click\", openPreferences)");
        StringAssert.Contains(js, "post(\"preferences.open\", {})");
        Assert.IsFalse(File.Exists(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.App", "SettingsWindow.xaml")));
        Assert.IsFalse(File.Exists(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.App", "SettingsWindow.xaml.cs")));
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(segments).ToArray()));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

}
