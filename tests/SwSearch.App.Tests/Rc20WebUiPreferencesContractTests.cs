using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.WebUi;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc20WebUiPreferencesContractTests
{
    [TestMethod]
    public void PreferencesProtocol_AllowsIndependentModernFileMenuToggle()
    {
        var router = new WebMessageRouter();

        AssertAccepted(router, "preferences.open", new { });
        AssertAccepted(router, "preferences.close", new { });
        AssertAccepted(router, "preferences.save", new
        {
            taskpane = true,
            folderMenu = false,
            filePropertiesMenu = true,
            startup = true,
            propertyCardSource = "automatic",
            propertyCardPath = "",
        });
        AssertAccepted(router, "preferences.save", new
        {
            taskpane = false,
            folderMenu = true,
            filePropertiesMenu = false,
            startup = false,
            propertyCardSource = "custom",
            propertyCardPath = @"C:\坚果云\SWPropertyEditor\Data\plm-config.xlsx",
        });

        AssertRejected(router, "settings", new { });
        AssertRejected(router, "settings.save", new { });
        AssertRejected(router, "settings.close", new { });
        AssertRejected(router, "preferences.save", new
        {
            taskpane = true,
            folderMenu = true,
            filePropertiesMenu = true,
            startup = true,
            propertyCardSource = "custom",
            propertyCardPath = "",
        });
        AssertRejected(router, "preferences.save", new
        {
            taskpane = true,
            folderMenu = true,
            filePropertiesMenu = true,
            startup = true,
            propertyCardSource = "custom",
            propertyCardPath = @"C:\坚果云\config.txt",
        });
    }

    [TestMethod]
    public void MainAsset_UsesOneInPageModalAndPreservesPathActionsWithoutAPathColumn()
    {
        var js = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.js");
        var css = Read("src", "SwSearch.WebUi", "Assets", "webui", "app.css");

        StringAssert.Contains(js, "id=\"preferencesModal\"");
        StringAssert.Contains(js, "settingsButton.addEventListener(\"click\", openPreferences)");
        StringAssert.Contains(js, "post(\"preferences.open\", {})");
        StringAssert.Contains(js, "post(\"preferences.close\", {})");
        StringAssert.Contains(js, "post(\"preferences.save\"");
        StringAssert.Contains(js, "Object.hasOwn(message.payload, \"preferences\")");
        StringAssert.Contains(js, "column.id !== \"fullPath\"");
        StringAssert.Contains(js, "[\"copyPath\", \"复制完整路径\"]");
        StringAssert.Contains(js, "path: result.fullPath");
        StringAssert.Contains(css, ".preferences-modal[hidden]");
        Assert.IsFalse(js.Contains("renderSettingsShell", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("post(\"settings\"", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("settings.save", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("{ id: \"fullPath\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebUi_HasNoSettingsViewOrSecondCompositionControllerPath()
    {
        var root = FindRepositoryRoot();
        var host = Read("src", "SwSearch.WebUi", "CompositionWebViewHost.cs");

        CollectionAssert.DoesNotContain(Enum.GetNames<WebUiView>(), "Settings");
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "settings.html")));
        Assert.AreEqual(
            1,
            Count(host, "CreateCoreWebView2CompositionControllerAsync("),
            "The WebUi host must create exactly one composition controller.");
        Assert.IsFalse(host.Contains("settings.html", StringComparison.Ordinal));

        var router = new WebMessageRouter();
        AssertRejected(router, "ready", new { view = "settings" });
        AssertRejected(router, "columnWidth", new { column = "fullPath", width = 420 });
    }

    private static void AssertAccepted(WebMessageRouter router, string type, object payload)
    {
        var json = JsonSerializer.Serialize(new { type, payload });
        Assert.IsTrue(router.TryValidate(json, out var message, out var error), error);
        Assert.AreEqual(type, message!.Type);
    }

    private static void AssertRejected(WebMessageRouter router, string type, object payload)
    {
        var json = JsonSerializer.Serialize(new { type, payload });
        Assert.IsFalse(router.TryValidate(json, out _, out _), $"Unexpectedly accepted {type}: {json}");
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
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
