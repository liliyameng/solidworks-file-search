using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc24PreferencesSaveTests
{
    private const string ScreenshotPath = @"D:\TC模板库\SolidWorks\插件模板-PDM\plm-config.xlsx";

    [TestMethod]
    public async Task ScreenshotPathAndIntentPersistBeforePartialIntegrationFailure()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            var integrations = new PartialFailureIntegrations(settings);
            var repository = CreateRepository(settings, integrations, sandbox);

            var result = await repository.SaveAsync(new UserPreferencesSaveRequest(
                true,
                true,
                true,
                PropertyCardSourcePreference.Automatic,
                ScreenshotPath), CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.IsTrue(result.PreferencesSaved);
            Assert.IsTrue(integrations.SettingsExistedBeforeFirstIntegration);
            Assert.AreEqual(1, result.Integrations.Count(item => !item.Succeeded));
            Assert.IsTrue(result.Integrations.Single(item => item.Target == UserPreferenceIntegrationTarget.FolderMenu).Message.Contains("Windows 11 helper missing", StringComparison.Ordinal));
            Assert.AreEqual(Path.GetFullPath(ScreenshotPath), loaded.PropertyCardPath);
            Assert.IsTrue(loaded.Taskpane.IsEnabled);
            Assert.IsTrue(loaded.FolderMenu.IsEnabled, "Saved intent must survive an external integration failure.");
            Assert.IsTrue(loaded.Startup.IsEnabled);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings));
            Assert.AreEqual(ScreenshotPath, json.RootElement.GetProperty("propertyCardPath").GetString());
            Assert.IsTrue(json.RootElement.GetProperty("folderMenuEnabled").GetBoolean());
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [TestMethod]
    public async Task CorruptOldJsonIsRebuiltAtomicallyAndReloads()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            await File.WriteAllTextAsync(settings, "{ broken json");
            var repository = CreateRepository(settings, new AvailableIntegrations(), sandbox);

            var result = await repository.SaveAsync(new UserPreferencesSaveRequest(
                false,
                false,
                false,
                PropertyCardSourcePreference.Custom,
                Path.Combine(sandbox, "中文 空格", "不存在.xlsx")), CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.IsTrue(result.PreferencesSaved);
            Assert.AreEqual(PropertyCardSourcePreference.Custom, loaded.PropertyCardSource);
            StringAssert.EndsWith(loaded.PropertyCardPath, @"中文 空格\不存在.xlsx");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settings));
            Assert.AreEqual(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [TestMethod]
    public async Task UnwritableRepositoryReturnsSpecificFailureWithoutRunningIntegrations()
    {
        var sandbox = CreateSandbox();
        try
        {
            var blocker = Path.Combine(sandbox, "not-a-directory");
            await File.WriteAllTextAsync(blocker, "block");
            var integrations = new CountingIntegrations();
            var repository = CreateRepository(Path.Combine(blocker, "settings.json"), integrations, sandbox);

            var result = await repository.SaveAsync(new UserPreferencesSaveRequest(
                true, true, true, PropertyCardSourcePreference.Automatic, ScreenshotPath), CancellationToken.None);

            Assert.IsFalse(result.PreferencesSaved);
            StringAssert.Contains(result.PersistenceError, "本地偏好未保存");
            Assert.AreEqual(0, integrations.ProbeCount);
            Assert.AreEqual(0, result.Integrations.Count);
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [TestMethod]
    public async Task BrowserMessageContainsSpecificPerIntegrationResult()
    {
        using var viewModel = new MainViewModel(new EmptySearchClient(), new NoFileActions());
        using var bridge = new DesktopWebBridge(viewModel, new PartialResultRepository());
        using var empty = JsonDocument.Parse("{}");
        using var save = JsonDocument.Parse(
            """{"taskpane":true,"folderMenu":true,"startup":true,"propertyCardSource":"automatic","propertyCardPath":"D:\\TC模板库\\SolidWorks\\插件模板-PDM\\plm-config.xlsx"}""");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.StateReady += state =>
        {
            using var document = JsonSerializer.SerializeToDocument(state);
            if (!document.RootElement.TryGetProperty("preferences", out var preferences) ||
                !preferences.TryGetProperty("saveResults", out var results) ||
                results.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            completion.TrySetResult(preferences.Clone());
        };

        bridge.HandleMessage("ready", empty.RootElement);
        bridge.HandleMessage("preferences.open", empty.RootElement);
        await Task.Delay(30);
        bridge.HandleMessage("preferences.save", save.RootElement);
        var payload = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var folder = payload.GetProperty("saveResults").GetProperty("folderMenu");
        Assert.IsFalse(folder.GetProperty("succeeded").GetBoolean());
        StringAssert.Contains(folder.GetProperty("message").GetString(), "Windows 11 helper missing");
        StringAssert.Contains(payload.GetProperty("error").GetString(), "Windows 11 helper missing");
    }

    private static UserPreferencesRepository CreateRepository(
        string settings,
        IUserPreferencesIntegrationBackend integrations,
        string sandbox) => new(
            settings,
            integrations,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500),
            Path.Combine(sandbox, "defaults.tsv"));

    private static string CreateSandbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "swsearch-rc24-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private class AvailableIntegrations : IUserPreferencesIntegrationBackend
    {
        protected readonly Dictionary<UserPreferenceIntegrationTarget, bool> States = new();
        public virtual ValueTask<IntegrationStatus> ProbeAsync(UserPreferenceIntegrationTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IntegrationStatus(true, States.GetValueOrDefault(target), "available"));
        public virtual ValueTask SetEnabledAsync(UserPreferenceIntegrationTarget target, bool enabled, CancellationToken cancellationToken)
        {
            States[target] = enabled;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PartialFailureIntegrations(string settingsPath) : AvailableIntegrations
    {
        internal bool SettingsExistedBeforeFirstIntegration { get; private set; }
        public override ValueTask SetEnabledAsync(UserPreferenceIntegrationTarget target, bool enabled, CancellationToken cancellationToken)
        {
            SettingsExistedBeforeFirstIntegration |= File.Exists(settingsPath);
            if (target == UserPreferenceIntegrationTarget.FolderMenu)
            {
                throw new InvalidOperationException("Windows 11 helper missing");
            }
            return base.SetEnabledAsync(target, enabled, cancellationToken);
        }
    }

    private sealed class CountingIntegrations : AvailableIntegrations
    {
        internal int ProbeCount { get; private set; }
        public override ValueTask<IntegrationStatus> ProbeAsync(UserPreferenceIntegrationTarget target, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return base.ProbeAsync(target, cancellationToken);
        }
    }

    private sealed class PartialResultRepository : IUserPreferencesRepository
    {
        private static readonly UserPreferenceIntegrationState Enabled = new(true, true, "saved intent");
        public Task<UserPreferencesSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(
            new UserPreferencesSnapshot(Enabled, Enabled, Enabled, PropertyCardSourcePreference.Automatic, ScreenshotPath));
        public Task<UserPreferencesSaveResult> SaveAsync(UserPreferencesSaveRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new UserPreferencesSaveResult(true, null, request, new[]
            {
                new UserPreferenceIntegrationSaveResult(UserPreferenceIntegrationTarget.Taskpane, true, true, "enabled"),
                new UserPreferenceIntegrationSaveResult(UserPreferenceIntegrationTarget.FolderMenu, true, false, "Windows 11 helper missing"),
                new UserPreferenceIntegrationSaveResult(UserPreferenceIntegrationTarget.Startup, true, true, "enabled"),
            }));
    }

    private sealed class EmptySearchClient : ISearchClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult([], new RuntimeCapability(CapabilityState.Available, "READY", "ready")));
    }

    private sealed class NoFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
    }
}
