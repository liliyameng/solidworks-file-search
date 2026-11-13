using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.WebUi.Native;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc73NativeColumnContractTests
{
    [TestMethod]
    public void NativeShellColumns_HaveOneStrictHeaderToShellPropertyContract()
    {
        var columns = NativeShellColumnContract.Columns;

        Assert.AreEqual(3, columns.Count);
        CollectionAssert.AreEqual(
            new[] { "fileName", "kind", "directory" },
            columns.Select(column => column.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "文件名", "文件类型", "所在文件夹" },
            columns.Select(column => column.Label).ToArray());
        Assert.IsTrue(columns.All(column => column.MinimumWidth > 0));
        Assert.IsTrue(columns.All(column => column.Weight > 0));
    }

    [TestMethod]
    public void NativeShellColumnClick_CyclesAscendingDescendingThenDefault()
    {
        Assert.AreEqual("asc", NativeShellSortCycle.NextDirection(null, false, "fileName"));
        Assert.AreEqual("desc", NativeShellSortCycle.NextDirection("fileName", false, "fileName"));
        Assert.IsNull(NativeShellSortCycle.NextDirection("fileName", true, "fileName"));
        Assert.AreEqual("asc", NativeShellSortCycle.NextDirection("kind", true, "directory"));
    }

    [TestMethod]
    public void MainWindow_UsesClickableColumnHeadersWithoutASeparateSortToolbar()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml.cs"));
        var shellHost = File.ReadAllText(
            Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "TaskpaneShellResultsHost.cs"));

        StringAssert.Contains(mainWindow, "columnHeader.ColumnInvoked");
        StringAssert.Contains(mainWindow, "_bridge.ApplyNativeSort(column, direction)");
        StringAssert.Contains(mainWindow, "ConfigureNativeShellColumns()");
        Assert.IsFalse(mainWindow.Contains("AddNativeSortButton", StringComparison.Ordinal));
        Assert.IsFalse(mainWindow.Contains("_nativeClearSortButton", StringComparison.Ordinal));
        Assert.IsFalse(mainWindow.Contains("new Forms.ToolStripLabel(\"排序：\")", StringComparison.Ordinal));
        Assert.IsFalse(mainWindow.Contains("取消排序", StringComparison.Ordinal));

        // The Shell header is hidden only because its built-in click would
        // re-sort the currently materialized page. The custom header is
        // truthful because these exact three columns are installed into the
        // real DefView through IColumnManager.
        StringAssert.Contains(shellHost, "FwfNoColumnHeader");
        StringAssert.Contains(shellHost, "manager.SetColumns(keys, (uint)keys.Length)");
        StringAssert.Contains(shellHost, "manager.SetColumnInfo(ref key, ref info)");
        StringAssert.Contains(shellHost, "B725F130-47EF-101A-A5F1-02608C9EEBAC\"), 10u");
        StringAssert.Contains(shellHost, "B725F130-47EF-101A-A5F1-02608C9EEBAC\"), 4u");
        StringAssert.Contains(shellHost, "E3E0584C-B788-4A5A-BB20-7F5A44C9ACDD\"), 6u");
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
