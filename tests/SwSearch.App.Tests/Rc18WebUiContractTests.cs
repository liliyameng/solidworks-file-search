using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.WebView2.Core;
using SwSearch.WebUi.Native;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc18WebUiContractTests
{
    [TestMethod]
    public void SettingsSurface_RemovesPauseDiscoveryAndColumnEditor()
    {
        var root = FindRepositoryRoot();
        // Settings moved into the single WebView shell; retain the contract
        // assertion without requiring the retired WPF/window asset names.
        var xaml = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml"));
        var html = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var combined = xaml + "\n" + html;

        Assert.IsFalse(combined.Contains("TogglePause", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("暂停后台", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("文件发现方式", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("结果列", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "WindowsFormsHost");
        StringAssert.Contains(html, "preferencesModal");
    }

    [TestMethod]
    public void SettingsSurface_RemainsAutomaticButBrokerHonorsExplicitDiscoveryCompatibilityValue()
    {
        var supplied = ResultColumnDefaults.All
            .Select(static column => column with
            {
                DisplayName = "用户重命名",
                Order = 99 - column.Order,
                Visible = !column.Visible,
                Width = column.Width + 31,
            });
        var normalized = ResultColumnDefaults.Normalize(supplied);

        CollectionAssert.AreEqual(
            ResultColumnDefaults.All.Select(static item => item.Id).ToArray(),
            normalized.Select(static item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            ResultColumnDefaults.All.Select(static item => item.DisplayName).ToArray(),
            normalized.Select(static item => item.DisplayName).ToArray());
        CollectionAssert.AreEqual(
            ResultColumnDefaults.All.Select(static item => item.Visible).ToArray(),
            normalized.Select(static item => item.Visible).ToArray());
        Assert.IsTrue(normalized.Zip(ResultColumnDefaults.All).All(pair => pair.First.Width == pair.Second.Width + 31));

        var root = FindRepositoryRoot();
        var broker = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Broker", "BrokerSettings.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Broker", "Program.cs"));
        StringAssert.Contains(broker, "FallbackDiscoveryProvider.CreateDefault(options)");
        StringAssert.Contains(broker, "ReadDiscoveryPreference");
        StringAssert.Contains(broker, "DiscoveryPreference.BuiltInOnly");
        StringAssert.Contains(program, "DISCOVERY_PROVIDER_SELECTED");
        StringAssert.Contains(program, "SetManualPause(false)");
    }

    [TestMethod]
    public void WebBridge_AllowsStructuredCommandsAndRejectsDangerousMessages()
    {
        var router = new WebMessageRouter();
        Assert.IsTrue(router.TryValidate(
            "{\"type\":\"ready\",\"payload\":{\"view\":\"main\"}}",
            out var ready,
            out _));
        Assert.AreEqual("ready", ready!.Type);

        Assert.IsFalse(router.TryValidate(
            "{\"type\":\"exec\",\"payload\":{\"command\":\"powershell\"}}",
            out _,
            out _));
        Assert.IsFalse(router.TryValidate(
            "{\"type\":\"openResult\",\"payload\":{\"path\":\"C:\\\\part.sldprt\",\"extra\":true}}",
            out _,
            out _));
        Assert.IsFalse(router.TryValidate(
            "{\"type\":\"property.load\",\"payload\":{\"path\":\"https://example.invalid/a.sldprt\"}}",
            out _,
            out _));
        Assert.IsFalse(router.TryValidate(
            "{\"type\":\"property.load\",\"payload\":{\"path\":\"C:\\\\legacy.slddrw\"}}",
            out _,
            out _));
        Assert.IsFalse(router.TryValidate(
            "{\"type\":\"filter\",\"payload\":{\"part\":true,\"assembly\":true,\"drawing\":true}}",
            out _,
            out _));
        Assert.IsFalse(DesktopWebBridge.IsAllowedCommand("filter"));
    }

    [TestMethod]
    public void HorizontalWheel_DecodesSignedDeltaKeysAndNegativeMonitorCoordinates()
    {
        static nint PackWheel(ushort keys, short delta) =>
            unchecked((nint)((uint)keys | ((uint)(ushort)delta << 16)));
        static nint PackPoint(short x, short y) =>
            unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16)));

        var positive = HorizontalWheelMessage.Decode(
            PackWheel((ushort)CoreWebView2MouseEventVirtualKeys.Control, 37),
            PackPoint(-1920, 240));
        Assert.AreEqual((short)37, positive.Delta);
        Assert.AreEqual(-1920, positive.ScreenPoint.X);
        Assert.AreEqual(240, positive.ScreenPoint.Y);
        Assert.AreEqual((uint)37, positive.WebViewMouseData);

        var negative = HorizontalWheelMessage.Decode(
            PackWheel((ushort)CoreWebView2MouseEventVirtualKeys.Shift, -91),
            PackPoint(400, -700));
        Assert.AreEqual((short)-91, negative.Delta);
        Assert.AreEqual(400, negative.ScreenPoint.X);
        Assert.AreEqual(-700, negative.ScreenPoint.Y);
        Assert.AreEqual(unchecked((uint)-91), negative.WebViewMouseData);
    }

    [TestMethod]
    public void WebAssets_UseDebouncedColumnPersistenceAndVirtualizedHorizontalViewport()
    {
        var root = FindRepositoryRoot();
        var js = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));
        StringAssert.Contains(js, "COLUMN_WIDTH_DEBOUNCE_MS");
        StringAssert.Contains(js, "columnWidth");
        StringAssert.Contains(js, "requestAnimationFrame");
        StringAssert.Contains(css, "overflow-x: auto");
    }

    [TestMethod]
    public void SearchAssets_HaveNoDocumentTypeControlsOrPayload()
    {
        var root = FindRepositoryRoot();
        var js = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var bridge = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "DesktopWebBridge.cs"));

        Assert.IsFalse(js.Contains("id=\"part\"", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("id=\"assembly\"", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("id=\"drawing\"", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains("types:", StringComparison.Ordinal));
        Assert.IsFalse(js.Contains(".SLDDRW", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(bridge.Contains("filters =", StringComparison.Ordinal));
    }

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
