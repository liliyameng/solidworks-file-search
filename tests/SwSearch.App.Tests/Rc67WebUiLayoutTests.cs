using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc67WebUiLayoutTests
{
    [TestMethod]
    public void MainShell_AssignsResultsAndStatusToSeparateFixedGridRows()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));

        StringAssert.Contains(
            css,
            ".shell { display: grid; grid-template-areas: \"toolbar\" \"results\" \"status\"; grid-template-rows: auto minmax(0,1fr) auto;");
        StringAssert.Contains(css, ".results { grid-area: results;");
        StringAssert.Contains(css, ".statusbar { grid-area: status;");
        Assert.IsFalse(
            css.Contains("grid-template-rows: auto auto minmax(0,1fr) auto;", StringComparison.Ordinal),
            "An extra auto row collapses the result grid and lets the status bar occupy the content area.");
    }

    [TestMethod]
    public void MainShell_MarksResultsBeforeTheBottomStatusBar()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var results = script.IndexOf("id=\"results\"", StringComparison.Ordinal);
        var status = script.IndexOf("class=\"statusbar\"", StringComparison.Ordinal);

        Assert.IsTrue(results >= 0, "The main page must include the scrollable result container.");
        Assert.IsTrue(status > results, "The status bar must follow the result container in the main shell.");
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
