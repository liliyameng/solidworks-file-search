using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc20PreferencesPanelTests
{
    [TestMethod]
    public async Task Repository_WritesFourPreferenceGroupsAndPreservesExistingDocument()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            await File.WriteAllTextAsync(settings,
                """{"schemaVersion":2,"networkRoots":["\\\\server\\share"],"columns":[{"id":"fileName","width":321}],"legacyToken":"keep"}""");
            var integrations = new MemoryIntegrations();
            var repository = new UserPreferencesRepository(
                settings,
                integrations,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                Path.Combine(sandbox, "defaults.tsv"));
            var customPath = Path.Combine(sandbox, "属性卡.xlsx");

            await repository.SaveAsync(new UserPreferencesSaveRequest(
                true,
                true,
                true,
                PropertyCardSourcePreference.Custom,
                customPath), CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.IsTrue(loaded.Taskpane.IsEnabled);
            Assert.IsTrue(loaded.FolderMenu.IsEnabled);
            Assert.IsTrue(loaded.Startup.IsEnabled);
            Assert.AreEqual(PropertyCardSourcePreference.Custom, loaded.PropertyCardSource);
            Assert.AreEqual(Path.GetFullPath(customPath), loaded.PropertyCardPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settings));
            Assert.AreEqual("keep", document.RootElement.GetProperty("legacyToken").GetString());
            Assert.IsTrue(document.RootElement.TryGetProperty("columns", out _));
            Assert.AreEqual(1, document.RootElement.GetProperty("propertyCardSource").GetInt32());
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task AutomaticSource_ReadsCurrentNutstorePathFromPluginDefaults()
    {
        var sandbox = CreateSandbox();
        try
        {
            var current = Path.Combine(sandbox, "坚果云", "当前配置.xlsx");
            var defaults = Path.Combine(sandbox, "defaults.tsv");
            await File.WriteAllTextAsync(defaults,
                "__ConfigFilePath\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(current)));
            var repository = new UserPreferencesRepository(
                Path.Combine(sandbox, "settings.json"),
                new MemoryIntegrations(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                defaults);

            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.AreEqual(PropertyCardSourcePreference.Automatic, loaded.PropertyCardSource);
            Assert.AreEqual(Path.GetFullPath(current), loaded.PropertyCardPath);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task Panel_ReopensAndSaveFailureBecomesReadableState()
    {
        using var viewModel = new MainViewModel(new EmptySearchClient(), new NoFileActions());
        using var bridge = new DesktopWebBridge(viewModel, new FailingSaveRepository());
        using var empty = JsonDocument.Parse("{}");
        using var save = JsonDocument.Parse(
            """{"taskpane":false,"folderMenu":false,"startup":false,"propertyCardSource":"automatic","propertyCardPath":""}""");
        var loaded = 0;
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.StateReady += state =>
        {
            using var json = JsonSerializer.SerializeToDocument(state);
            if (!json.RootElement.TryGetProperty("preferences", out var preferences)) return;
            if (preferences.TryGetProperty("busy", out var busy) && !busy.GetBoolean() &&
                preferences.TryGetProperty("isOpen", out var open) && open.GetBoolean())
            {
                Interlocked.Increment(ref loaded);
            }
            if (preferences.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                failure.TrySetResult(error.GetString()!);
            }
        };

        bridge.HandleMessage("ready", empty.RootElement);
        bridge.HandleMessage("preferences.open", empty.RootElement);
        await WaitUntilAsync(() => Volatile.Read(ref loaded) >= 1);
        bridge.HandleMessage("preferences.close", empty.RootElement);
        bridge.HandleMessage("preferences.open", empty.RootElement);
        await WaitUntilAsync(() => Volatile.Read(ref loaded) >= 2);
        bridge.HandleMessage("preferences.save", save.RootElement);

        var error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        StringAssert.Contains(error, "无法读写");
    }

    [TestMethod]
    public void DesktopEntry_HasOneHostAndFormerWindowIsUnreachable()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml.cs"));
        var bridge = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "DesktopWebBridge.cs"));

        // Since the standalone preferences window landed, MainWindow creates
        // exactly two composition WebView hosts: the main toolbar plus the
        // on-demand settings window. The former single-host invariant is stale.
        Assert.AreEqual(2, Count(mainWindow, "new CompositionWebViewHost"));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "SwSearch.App", "SettingsWindow.xaml")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "SwSearch.App", "SettingsWindow.xaml.cs")));
        Assert.IsFalse(mainWindow.Contains("SettingsWindow", StringComparison.Ordinal));
        Assert.IsFalse(mainWindow.Contains("CreateSettingsWindow", StringComparison.Ordinal));
        Assert.IsFalse(bridge.Contains("IAppIsolationClient", StringComparison.Ordinal));
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("preferences.open"));
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("preferences.save"));
        Assert.IsTrue(DesktopWebBridge.IsAllowedCommand("preferences.close"));
        Assert.IsFalse(DesktopWebBridge.IsAllowedCommand("settings"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string CreateSandbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "swsearch-rc20-" + Guid.NewGuid().ToString("N"));
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

    private sealed class MemoryIntegrations : IUserPreferencesIntegrationBackend
    {
        private readonly Dictionary<UserPreferenceIntegrationTarget, bool> _states = new();

        public ValueTask<IntegrationStatus> ProbeAsync(
            UserPreferenceIntegrationTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IntegrationStatus(
                true,
                _states.GetValueOrDefault(target),
                "可用"));

        public ValueTask SetEnabledAsync(
            UserPreferenceIntegrationTarget target,
            bool enabled,
            CancellationToken cancellationToken)
        {
            _states[target] = enabled;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSaveRepository : IUserPreferencesRepository
    {
        private static readonly UserPreferenceIntegrationState Available = new(true, false, "可用");

        public Task<UserPreferencesSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UserPreferencesSnapshot(
                Available,
                Available,
                Available,
                PropertyCardSourcePreference.Automatic,
                string.Empty));

        public Task<UserPreferencesSaveResult> SaveAsync(
            UserPreferencesSaveRequest request,
            CancellationToken cancellationToken) => Task.FromResult(new UserPreferencesSaveResult(
                false,
                "设置文件无法读写：locked",
                request,
                Array.Empty<UserPreferenceIntegrationSaveResult>()));
    }

    private sealed class EmptySearchClient : ISearchClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                [],
                new RuntimeCapability(CapabilityState.Available, "INDEX_READY", "缓存已就绪")));
    }

    private sealed class NoFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
    }
}
