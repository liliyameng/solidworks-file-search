using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc21BootstrapFailSoftTests
{
    [TestMethod]
    public async Task PreferencesConstructionFailure_ReturnsReadableUnavailableRepository()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            "swsearch-preferences-test-" + Guid.NewGuid().ToString("N") + ".log");
        var repository = UserPreferencesRepository.CreateFailSoft(
            () => throw new InvalidOperationException("registration failed"),
            logPath);

        var snapshot = await repository.LoadAsync(CancellationToken.None);

        Assert.IsFalse(snapshot.Taskpane.IsAvailable);
        Assert.IsFalse(snapshot.FolderMenu.IsAvailable);
        Assert.IsFalse(snapshot.Startup.IsAvailable);
        Assert.IsNotNull(snapshot.ConfigurationError);
        StringAssert.Contains(snapshot.ConfigurationError, "主搜索仍可使用");
        File.Delete(logPath);
    }

    [TestMethod]
    public void CorruptAndLegacySettings_FallBackWithoutBlockingBootstrap()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-rc21-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var corruptPath = Path.Combine(sandbox, "corrupt.json");
            File.WriteAllText(corruptPath, "{ this is not json");
            var corrupt = new JsonAppSettingsStore(corruptPath).Load();
            Assert.AreEqual(AppSettings.Default.SchemaVersion, corrupt.SchemaVersion);

            var legacyPath = Path.Combine(sandbox, "legacy.json");
            File.WriteAllText(legacyPath,
                """{"schemaVersion":1,"discoveryPreference":2,"networkRoots":[],"backgroundPaused":true,"columns":[{"id":"fullPath","width":900}]}""");
            var legacy = new JsonAppSettingsStore(legacyPath).Load();
            Assert.AreEqual(AppSettings.Default.SchemaVersion, legacy.SchemaVersion);
            Assert.IsFalse(legacy.BackgroundPaused);
            Assert.IsFalse(legacy.NormalizedColumns.Any(column => column.Id == "fullPath"));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void BootstrapAndSingleWebView_AreFailSoftAndOldSettingsChainIsAbsent()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml.cs"));
        var bootstrap = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "AppBootstrapFailure.cs"));

        StringAssert.Contains(app, "AppBootstrapFailurePolicy.IsRecoverable");
        StringAssert.Contains(app, "AppBootstrapFailureWindow.Create");
        StringAssert.Contains(main, "UserPreferencesRepository.CreateFailSoft()");
        StringAssert.Contains(bootstrap, "app-startup.log");
        Assert.AreEqual(1, Count(main, "new CompositionWebViewHost"));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "SwSearch.App", "SettingsWindow.xaml")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "SwSearch.App", "SettingsWindow.xaml.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "settings.html")));
        Assert.IsFalse(main.Contains("SettingsWindow", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StartupDiagnostics_NeverThrowsWhenLogPathIsInvalid()
    {
        AppStartupDiagnostics.Record(
            "test",
            new InvalidOperationException("boom"),
            "\0invalid");
    }

    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException();
    }
}
