using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Integration;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc37PropertyOrderingAndLibraryTests
{
    [TestMethod]
    public async Task Preferences_RoundTripOrderedNamesAndRemoveLegacyManualLibraryRoots()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            await File.WriteAllTextAsync(settings,
                """{"schemaVersion":4,"libraryRoots":["D:\\legacy"],"legacyToken":"keep"}""");
            var repository = new UserPreferencesRepository(
                settings,
                new AvailableIntegrations(),
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(200),
                Path.Combine(sandbox, "defaults.tsv"));

            var result = await repository.SaveAsync(new UserPreferencesSaveRequest(
                false,
                false,
                false,
                PropertyCardSourcePreference.Automatic,
                string.Empty,
                FilePropertiesMenu: false,
                PropertyDisplayOrder: ["图号", " 材料名称 ", "图号", "其他属性"]), CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.IsTrue(result.PreferencesSaved);
            CollectionAssert.AreEqual(
                new[] { "图号", "材料名称", "其他属性" },
                loaded.PropertyDisplayOrder!.ToArray());
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings));
            Assert.IsFalse(json.RootElement.TryGetProperty("libraryRoots", out _));
            Assert.AreEqual("keep", json.RootElement.GetProperty("legacyToken").GetString());
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [TestMethod]
    public void WebContract_AllowsOnlyOrderedPropertiesAndNoManualLibraryRoots()
    {
        using var valid = JsonDocument.Parse(
            """{"taskpane":true,"folderMenu":false,"filePropertiesMenu":true,"startup":false,"propertyDisplayOrder":["图号","材料名称"]}""");
        using var removed = JsonDocument.Parse(
            """{"taskpane":true,"folderMenu":false,"filePropertiesMenu":true,"startup":false,"libraryRoots":["D:\\legacy"]}""");

        Assert.IsTrue(WebMessageValidators.PreferencesSave(valid.RootElement).IsValid);
        Assert.IsFalse(WebMessageValidators.PreferencesSave(removed.RootElement).IsValid);
    }

    [TestMethod]
    public void SettingsUiRemovesManualLibrariesAndTaskpaneReadsSolidWorksDesignLibrary()
    {
        var root = FindRepositoryRoot();
        var appJs = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var library = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "TaskpaneLibrarySettings.cs"));
        var control = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "TaskpaneControl.cs"));

        StringAssert.Contains(appJs, "preferencePropertyDisplayOrder");
        Assert.IsFalse(appJs.Contains("preferenceLibraryRoots", StringComparison.Ordinal));
        Assert.IsFalse(appJs.Contains("文件库根路径", StringComparison.Ordinal));
        StringAssert.Contains(library, "GetUserPreferenceStringValue(DesignLibraryFileLocations)");
        Assert.IsFalse(library.Contains("settings.json", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(library.Contains("JavaScriptSerializer", StringComparison.Ordinal));
        StringAssert.Contains(control, "TaskpaneLibrarySettings.Load(swApplication)");
    }

    private static string CreateSandbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "swsearch-rc37-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private sealed class AvailableIntegrations : IUserPreferencesIntegrationBackend
    {
        private readonly Dictionary<UserPreferenceIntegrationTarget, bool> _states = new();

        public ValueTask<IntegrationStatus> ProbeAsync(
            UserPreferenceIntegrationTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IntegrationStatus(true, _states.GetValueOrDefault(target), "available"));

        public ValueTask SetEnabledAsync(
            UserPreferenceIntegrationTarget target,
            bool enabled,
            CancellationToken cancellationToken)
        {
            _states[target] = enabled;
            return ValueTask.CompletedTask;
        }
    }
}
