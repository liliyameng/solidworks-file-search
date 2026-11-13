using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc75NativeShellMatchSelectionTests
{
    [TestMethod]
    public void NativeShellSearchPage_CarriesMatchEntriesForEveryFirstSeenPath()
    {
        var matchEntries = new[]
        {
            new NativeShellMatchEntry(@"C:\Design\bracket.SLDPRT", null, null),
            new NativeShellMatchEntry(@"C:\Design\housing.SLDASM", "材料", "铝合金"),
            new NativeShellMatchEntry(@"C:\Design\bracket.SLDPRT", "图号", "BRK-001"),
        };
        var page = new NativeShellSearchPage(
            1,
            0,
            new[] { @"C:\Design\bracket.SLDPRT", @"C:\Design\housing.SLDASM" },
            matchEntries,
            false,
            false,
            false,
            new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "索引服务正常"));

        Assert.AreEqual(2, page.Paths.Count);
        var bracketEntry = page.MatchEntries[0];
        Assert.AreEqual(@"C:\Design\bracket.SLDPRT", bracketEntry.Path);
        Assert.IsNull(bracketEntry.MatchedProperty);
        Assert.IsNull(bracketEntry.MatchedValue);
        var housingEntry = page.MatchEntries[1];
        Assert.AreEqual(@"C:\Design\housing.SLDASM", housingEntry.Path);
    }

    [TestMethod]
    public void NativeShellSearchPage_EmptyPagesProduceEmptyMatchEntries()
    {
        var completed = NativeShellSearchPage.Completed(1, 0);
        Assert.AreEqual(0, completed.MatchEntries.Count);
        var reset = NativeShellSearchPage.ResetRequired(1, 0,
            new RuntimeCapability(CapabilityState.Available, "OK", "ok"));
        Assert.AreEqual(0, reset.MatchEntries.Count);
        var unavailable = NativeShellSearchPage.Unavailable(1, 0,
            new RuntimeCapability(CapabilityState.Unavailable, "DOWN", "down"));
        Assert.AreEqual(0, unavailable.MatchEntries.Count);
    }

    [TestMethod]
    public void NativeShellMatchEntry_PreservesNullPropertyAndValue()
    {
        var entry = new NativeShellMatchEntry(@"C:\test.SLDPRT", null, null);
        Assert.AreEqual(@"C:\test.SLDPRT", entry.Path);
        Assert.IsNull(entry.MatchedProperty);
        Assert.IsNull(entry.MatchedValue);
    }

    [TestMethod]
    public void NativeShellMatchEntry_DisplayFallbackForFileNameOnlyHits()
    {
        // Filename-only hits (null MatchedProperty/MatchedValue) still produce entries.
        var entry = new NativeShellMatchEntry(@"C:\Design\bracket.SLDPRT", null, null);
        Assert.IsNotNull(entry);
        Assert.AreEqual(@"C:\Design\bracket.SLDPRT", entry.Path);
        // Consumers should display "命中属性：文件名 · 命中值：—" for this entry.
    }

    [TestMethod]
    public void GetDefaultMatchFromMap_EmptyMap_ReturnsNull()
    {
        var map = new Dictionary<string, (string? MatchedProperty, string? MatchedValue)>();
        var result = MainWindow.GetDefaultMatchFromMap(map);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetDefaultMatchFromMap_PrefersEntryWithMatchedProperty()
    {
        var map = new Dictionary<string, (string? MatchedProperty, string? MatchedValue)>
        {
            [@"C:\a.SLDPRT"] = (null, null),
            [@"C:\b.SLDASM"] = ("材料", "铝合金"),
            [@"C:\c.SLDPRT"] = ("图号", "BRK-001"),
        };
        var result = MainWindow.GetDefaultMatchFromMap(map);
        Assert.IsNotNull(result);
        // Should pick the first entry that has a non-null MatchedProperty.
        Assert.AreEqual("材料", result.Value.MatchedProperty);
        Assert.AreEqual("铝合金", result.Value.MatchedValue);
    }

    [TestMethod]
    public void GetDefaultMatchFromMap_AllFileNameOnly_ReturnsFirstEntry()
    {
        var map = new Dictionary<string, (string? MatchedProperty, string? MatchedValue)>
        {
            [@"C:\x.SLDPRT"] = (null, null),
            [@"C:\y.SLDASM"] = (null, null),
        };
        var result = MainWindow.GetDefaultMatchFromMap(map);
        Assert.IsNotNull(result);
        Assert.IsNull(result.Value.MatchedProperty);
        Assert.IsNull(result.Value.MatchedValue);
    }
}
