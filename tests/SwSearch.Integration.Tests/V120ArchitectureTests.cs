using Microsoft.Win32;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwSearch.App;
using SwSearch.Broker;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.Integration.Tests;

public sealed class V120ArchitectureTests
{
    [Fact]
    public void AppCommand_ParsesFolderScopeWithoutMakingItPersistent()
    {
        Assert.Equal(@"C:\设计 & 空格", SwSearch.App.App.ReadScopeFolder(["--scope-folder", @"C:\设计 & 空格\"]));
        Assert.Null(SwSearch.App.App.ReadScopeFolder(["--startup"]));
    }

    [Fact]
    public void ColumnLayout_NormalizesUnknownDuplicateAndUnsafeValues()
    {
        var normalized = ResultColumnDefaults.Normalize(
        [
            new("fileName", "模型", 10, 9, false),
            new("fileName", "重复", 100, 0, true),
            new("unknown", "未知", 100, 1, true),
            new("fullPath", "路径", 9999, -2, false),
        ]);
        Assert.Equal(8, normalized.Count);
        Assert.Equal(56, normalized.Single(column => column.Id == "fileName").Width);
        Assert.Equal(1600, normalized.Single(column => column.Id == "fullPath").Width);
        Assert.Contains(normalized, column => column.Visible);
        Assert.Equal(Enumerable.Range(0, 8), normalized.Select(column => column.Order));
    }

    [Fact]
    public async Task ThreeRegistrationSwitches_ProbeActualRegistryState()
    {
        var registry = new MemoryRegistry();
        var app = @"C:\Program Files\SwSearch\SwSearch.App.exe";
        var folder = new FolderContextMenuService(registry, useModern: false);
        var startup = new StartupRegistrationService(registry);
        var taskpane = new SolidWorksTaskpaneRegistrationService(registry);

        await folder.SetEnabledAsync(true, app, CancellationToken.None);
        await startup.SetEnabledAsync(true, app, CancellationToken.None);
        Assert.True((await folder.ProbeAsync(app, CancellationToken.None)).IsEnabled);
        Assert.True((await startup.ProbeAsync(app, CancellationToken.None)).IsEnabled);
        Assert.Equal(
            "\"C:\\Program Files\\SwSearch\\SwSearch.App.exe\" --startup",
            registry.GetValue(RegistryHive.CurrentUser, StartupRegistrationService.RunKey, StartupRegistrationService.ValueName));
        Assert.False((await taskpane.ProbeAsync(CancellationToken.None)).IsAvailable);

        registry.SetDword(RegistryHive.LocalMachine, SolidWorksTaskpaneRegistrationService.MachineAddinKey, null, 0);
        await taskpane.SetEnabledAsync(true, CancellationToken.None);
        Assert.True((await taskpane.ProbeAsync(CancellationToken.None)).IsEnabled);

        await folder.SetEnabledAsync(false, app, CancellationToken.None);
        await startup.SetEnabledAsync(false, app, CancellationToken.None);
        Assert.False((await folder.ProbeAsync(app, CancellationToken.None)).IsEnabled);
        Assert.False((await startup.ProbeAsync(app, CancellationToken.None)).IsEnabled);
    }

    [Fact]
    public void BrokerGuard_AllowsOnlyOneIndexOwner()
    {
        var name = @"Local\SwSearch.Broker.Test." + Guid.NewGuid().ToString("N");
        using var first = BrokerInstanceGuard.TryAcquire(name);
        using var second = BrokerInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task BrokerClient_RetriesAfterPipeDisconnectWithoutStartingAProductProcess()
    {
        var pipeName = "swsearch.test." + Guid.NewGuid().ToString("N");
        var dropped = RunDroppingServerAsync(pipeName);
        Task? recoveryServer = null;
        var starts = 0;
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "does-not-run.exe"),
            pipeName,
            _ =>
            {
                Interlocked.Increment(ref starts);
                recoveryServer = RunReplyServerAsync(pipeName);
                return ValueTask.CompletedTask;
            });

        var result = await client.SearchAsync(new SearchQuery("接头", SearchMode.All), CancellationToken.None);

        await dropped;
        if (recoveryServer is not null) await recoveryServer;
        Assert.Equal(1, starts);
        Assert.Equal(@"C:\模型\接头.SLDPRT", Assert.Single(result.Hits).FullPath);
    }

    [Fact]
    public async Task BrokerDisconnect_ReturnsLastMatchingClientCacheWhileReconnectIsBounded()
    {
        var pipeName = "swsearch.cached-disconnect." + Guid.NewGuid().ToString("N");
        var firstServer = RunReplyServerAsync(pipeName);
        var starts = 0;
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "does-not-run.exe"),
            pipeName,
            _ =>
            {
                Interlocked.Increment(ref starts);
                return ValueTask.CompletedTask;
            },
            new BrokerSearchClientOptions(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(10)));
        var query = new SearchQuery("缓存断线", SearchMode.All);

        var first = await client.SearchAsync(query, CancellationToken.None);
        await firstServer;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var retained = await client.SearchAsync(query, CancellationToken.None);
        clock.Stop();

        Assert.Equal(first.Hits, retained.Hits);
        Assert.Equal("BROKER_RECONNECTING", retained.Capability.Code);
        Assert.Equal(1, starts);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"cached reconnect fallback elapsed={clock.Elapsed}");
    }

    [Fact]
    public async Task BrokerClient_ProtocolMismatchIsExplicitAndNeverStartsOrFallsBack()
    {
        var pipeName = "swsearch.protocol." + Guid.NewGuid().ToString("N");
        var server = RunProtocolMismatchServerAsync(pipeName);
        var starts = 0;
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "does-not-run.exe"),
            pipeName,
            _ =>
            {
                Interlocked.Increment(ref starts);
                return ValueTask.CompletedTask;
            },
            FastBrokerOptions());

        var error = await Assert.ThrowsAsync<BrokerProtocolMismatchException>(() =>
            client.SearchAsync(new SearchQuery("范围", SearchMode.All), CancellationToken.None).AsTask());

        await server;
        Assert.Equal(0, starts);
        Assert.Contains("搜索服务版本不匹配", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingBroker_StartsOnlyOnceAndFailsWithinBoundedDeadline()
    {
        var starts = 0;
        var options = FastBrokerOptions();
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "中文 路径", "SwSearch.Broker.exe"),
            "swsearch.missing." + Guid.NewGuid().ToString("N"),
            _ =>
            {
                Interlocked.Increment(ref starts);
                return ValueTask.CompletedTask;
            },
            options);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var first = client.SearchAsync(new SearchQuery("一", SearchMode.All), CancellationToken.None).AsTask();
        var second = client.SearchAsync(new SearchQuery("二", SearchMode.All), CancellationToken.None).AsTask();
        await Assert.ThrowsAnyAsync<IOException>(() => Task.WhenAll(first, second));

        Assert.Equal(1, starts);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"elapsed={clock.Elapsed}");
    }

    [Fact]
    public async Task MissingBroker_IsStartedOnceAndThenConnects()
    {
        var pipeName = "swsearch.autostart." + Guid.NewGuid().ToString("N");
        var starts = 0;
        Task? server = null;
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "含 中文 空格", "SwSearch.Broker.exe"),
            pipeName,
            _ =>
            {
                Interlocked.Increment(ref starts);
                server = RunReplyServerAsync(pipeName);
                return ValueTask.CompletedTask;
            },
            ReliableBrokerOptions());

        var result = await client.SearchAsync(new SearchQuery("自动启动", SearchMode.All), CancellationToken.None);

        if (server is not null) await server;
        Assert.Equal(1, starts);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task ExplicitRetry_CanRecoverAfterBoundedConnectionFailure()
    {
        var pipeName = "swsearch.retry." + Guid.NewGuid().ToString("N");
        var starts = 0;
        Task? server = null;
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "does-not-run.exe"),
            pipeName,
            _ =>
            {
                if (Interlocked.Increment(ref starts) == 2)
                {
                    server = RunReplyServerAsync(pipeName);
                }
                return ValueTask.CompletedTask;
            },
            ReliableBrokerOptions());

        await Assert.ThrowsAsync<IOException>(() =>
            client.SearchAsync(new SearchQuery("失败", SearchMode.All), CancellationToken.None).AsTask());
        client.RetryConnection();
        var recovered = await client.SearchAsync(new SearchQuery("恢复", SearchMode.All), CancellationToken.None);

        if (server is not null) await server;
        Assert.Equal(2, starts);
        Assert.Single(recovered.Hits);
    }

    [Fact]
    public async Task FailedBrokerStart_IsAutomaticallyRetriedByNextMonitorOperation()
    {
        var pipeName = "swsearch.automatic-retry." + Guid.NewGuid().ToString("N");
        var starts = 0;
        Task? server = null;
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "does-not-run.exe"),
            pipeName,
            _ =>
            {
                if (Interlocked.Increment(ref starts) == 2)
                {
                    server = RunReplyServerAsync(pipeName);
                }
                return ValueTask.CompletedTask;
            },
            ReliableBrokerOptions());

        await Assert.ThrowsAsync<IOException>(() =>
            client.RefreshAsync(CancellationToken.None).AsTask());
        var recovered = await client.RefreshAsync(CancellationToken.None);

        if (server is not null) await server;
        Assert.Equal(2, starts);
        Assert.Equal("TEST", recovered.Code);
    }

    [Fact]
    public async Task ConnectedPipeWithoutResponse_TimesOutInsteadOfHangingForever()
    {
        var pipeName = "swsearch.noresponse." + Guid.NewGuid().ToString("N");
        var server = RunNonRespondingServerAsync(pipeName);
        var client = new BrokerSearchClient(
            Path.Combine(Path.GetTempPath(), "does-not-run.exe"),
            pipeName,
            _ => ValueTask.CompletedTask,
            FastBrokerOptions());
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() =>
            client.SearchAsync(new SearchQuery("response-timeout", SearchMode.All), CancellationToken.None).AsTask());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"elapsed={clock.Elapsed}");
        await server;
    }

    [Fact]
    public void BrokerStartInfo_PreservesUnicodeAndSpacesWithoutCommandLineQuoting()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "设计 文件", "搜索 服务", "SwSearch.Broker.exe"));

        var info = BrokerSearchClient.CreateBrokerStartInfo(path);

        Assert.Equal(path, info.FileName);
        Assert.Equal(Path.GetDirectoryName(path), info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal("--background", Assert.Single(info.ArgumentList));
    }

    [Fact]
    public void PublishAndTaskpane_UseSiblingBrokerAndEverythingStyleLiveQuery()
    {
        var root = FindRepoRoot();
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var package = File.ReadAllText(Path.Combine(root, "installer", "wix", "Package.wxs"));
        var taskpaneVerifier = File.ReadAllText(Path.Combine(root, "installer", "Verify-Taskpane.ps1"));
        var msiVerifier = File.ReadAllText(Path.Combine(root, "installer", "Verify-Msi.ps1"));
        var app = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainViewModel.cs"));
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SettingsWindow.xaml"));
        var appStartup = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "App.xaml.cs"));
        var backgroundSettings = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "BackgroundSettingsLoader.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml.cs"));
        var settingsWindow = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SettingsWindow.xaml.cs"));
        var settingsCoordinator = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SettingsIntegrationProbeCoordinator.cs"));
        var integrationAdapter = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SettingsIntegrationOperationAdapter.cs"));
        var appIsolation = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "AppIsolation.cs"));
        var appSettings = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "AppSettings.cs"));
        var diagnostics = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SettingsPersistenceDiagnostics.cs"));
        var settingsPersistence = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SettingsPersistenceCoordinator.cs"));
        var integrationServices = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Integration", "IntegrationServices.cs"));
        var candidateIlGate = File.ReadAllText(Path.Combine(root, "tests", "SwSearch.CandidateIlGate", "Program.cs"));
        var taskpaneFiles = Directory.GetFiles(Path.Combine(root, "src", "SwSearch.SolidWorksAddin"), "*.cs");
        var taskpane = taskpaneFiles
            .Select(File.ReadAllText).Aggregate(string.Concat);
        var taskpaneUi = taskpaneFiles
            .Where(path => !Path.GetFileName(path).Equals("AddinDiagnostics.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText).Aggregate(string.Concat);
        var taskpaneBuild = File.ReadAllText(Path.Combine(root, "installer", "Build-Taskpane.ps1"));
        var lexicalScope = File.ReadAllText(Path.Combine(root, "src", "Shared", "WindowsScopePath.cs"));
        var appTestsProject = File.ReadAllText(Path.Combine(root, "tests", "SwSearch.App.Tests", "SwSearch.App.Tests.csproj"));
        var nativeHelper = File.ReadAllText(Path.Combine(root, "tests", "SwSearch.InstallerCoordinator.NativeHelper", "Program.cs"));

        Assert.Contains("'SwSearch.Broker.exe'", publish, StringComparison.Ordinal);
        Assert.Contains("<ComponentGroupRef Id=\"ApplicationFiles\" />", package, StringComparison.Ordinal);
        Assert.Contains("'<StandardDirectory Id=\"ProgramFiles64Folder\">', 'SwSearch.Broker.exe', 'SwSearch.PropertyConfig.dll'", msiVerifier, StringComparison.Ordinal);
        Assert.Contains("Path.GetDirectoryName(typeof(BrokerProtocol).Assembly.Location)", taskpane, StringComparison.Ordinal);
        Assert.Contains("new LiveSearchScheduler(SubmitScheduledSearch, 80)", taskpane, StringComparison.Ordinal);
        Assert.Contains("debounceDelay ?? TimeSpan.FromMilliseconds(80)", app, StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding SearchNowCommand}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("WmImeStartComposition = 0x010D", taskpane, StringComparison.Ordinal);
        Assert.Contains("WmImeEndComposition = 0x010E", taskpane, StringComparison.Ordinal);
        Assert.Contains("_search.TextChanged", taskpane, StringComparison.Ordinal);
        Assert.Contains("_mode.SelectedIndexChanged", taskpane, StringComparison.Ordinal);
        Assert.Contains("CheckedChanged", taskpane, StringComparison.Ordinal);
        Assert.Contains("_scopeMode.SelectedIndexChanged", taskpane, StringComparison.Ordinal);
        Assert.Contains("_scopePath.TextChanged", taskpane, StringComparison.Ordinal);
        Assert.Contains("_scopePath.KeyDown", taskpane, StringComparison.Ordinal);
        Assert.Contains("_browseScopeButton.Click", taskpane, StringComparison.Ordinal);
        Assert.Contains("TaskpaneSearchRequestFactory", taskpane, StringComparison.Ordinal);
        Assert.Contains("WindowsScopePath", taskpane, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskpaneScopeSettings", taskpane, StringComparison.Ordinal);
        Assert.Contains("TaskpaneSearchScope.ContainsPath", taskpane, StringComparison.Ordinal);
        Assert.Contains("SearchScopeDto", taskpane, StringComparison.Ordinal);
        Assert.Contains("IncludeSubdirectories", taskpane, StringComparison.Ordinal);
        Assert.Contains("ProtocolVersion = 2", taskpane, StringComparison.Ordinal);
        Assert.Contains("搜索服务版本不匹配", taskpane, StringComparison.Ordinal);
        Assert.Contains("LiveSearchCoordinator", taskpane, StringComparison.Ordinal);
        Assert.Contains("_searchCoordinator.CancelCurrent()", taskpane, StringComparison.Ordinal);
        Assert.Contains("BrokerPipeTransport", taskpane, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.Register", taskpane, StringComparison.Ordinal);
        Assert.Contains("WaitHandle.WaitAny", taskpane, StringComparison.Ordinal);
        Assert.DoesNotContain("if (text.Length == 0) return", taskpane, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists", taskpaneUi, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectoryInfo", taskpaneUi, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", taskpaneUi, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", taskpaneUi, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog.SelectedPath =", taskpaneUi, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic", taskpane, StringComparison.Ordinal);
        Assert.Contains("private ISldWorks _swApplication", taskpane, StringComparison.Ordinal);
        Assert.Contains("private ITaskpaneView _taskpaneView", taskpane, StringComparison.Ordinal);
        Assert.Contains("/link:$SolidWorksInteropPath", taskpaneBuild, StringComparison.Ordinal);
        Assert.Contains("F3AF048BB4064B8FE7E3BB8E1B5D6E6AAB5DC78C04B3EBAA7927B3EE923F6279", taskpaneBuild, StringComparison.Ordinal);
        Assert.Contains("Required early-bound interface callvirt", taskpaneVerifier, StringComparison.Ordinal);
        Assert.Contains("UnsafeDynamicTaskpaneFixture", taskpaneVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists", app, StringComparison.Ordinal);
        Assert.Contains("initialSettings: AppSettings.Default", appStartup, StringComparison.Ordinal);
        Assert.Contains("new BackgroundSettingsLoader", appStartup, StringComparison.Ordinal);
        Assert.DoesNotContain("var settings = settingsStore.Load()", appStartup, StringComparison.Ordinal);
        Assert.Contains("Task.Run(_load", backgroundSettings, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", backgroundSettings, StringComparison.Ordinal);
        Assert.Contains("generation == _generation", backgroundSettings, StringComparison.Ordinal);
        Assert.Contains("new SettingsIntegrationProbeCoordinator", settingsWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("SettingsIntegrationOperationAdapter", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("IAppIsolationClient", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("_isolationClient.SetIntegrationEnabledAsync", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("_operationAdapter.StopAsync", settingsWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new WindowsRegistryAccess", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("Channel.CreateBounded", integrationAdapter, StringComparison.Ordinal);
        Assert.Contains("SingleReader = true", integrationAdapter, StringComparison.Ordinal);
        Assert.Contains("WaitAsync", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("generation == _generation", settingsCoordinator, StringComparison.Ordinal);
        Assert.Contains("_generationCancellation?.Cancel()", settingsCoordinator, StringComparison.Ordinal);
        Assert.Contains("RestoreDefaultColumnsInMemory", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("_settingsPersistence.QueueAsync", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("ColumnPersistenceStatusText", settingsWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_viewModel.RestoreDefaultColumns()", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("Task.Run(DrainAsync)", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("IsCurrentLocked(next)", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("item.Generation == _generation", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("lock (_sync)", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("await prepared.CommitAsync(next.Token)", settingsPersistence, StringComparison.Ordinal);
        Assert.DoesNotContain("prepared.Commit()", settingsPersistence, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"lock\s*\(_sync\)\s*\{[^}]*CommitAsync", RegexOptions.Singleline), settingsPersistence);
        Assert.Contains("linkedCancellation.CancelAfter(_operationTimeout)", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("new IsolatedSettingsSaveExecutor(new AppIsolationClient())", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Replace", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move(temporaryPath", appSettings, StringComparison.Ordinal);
        Assert.Contains("JobObjectLimitKillOnJobClose", appIsolation, StringComparison.Ordinal);
        Assert.Contains("TerminateJobObject", appIsolation, StringComparison.Ordinal);
        Assert.Contains("new SolidWorksTaskpaneRegistrationService(registry)", appIsolation, StringComparison.Ordinal);
        Assert.Contains("new FolderContextMenuService(registry, useModern: false", appIsolation, StringComparison.Ordinal);
        Assert.Contains("new StartupRegistrationService(registry)", appIsolation, StringComparison.Ordinal);
        Assert.Contains("Channel.CreateBounded", diagnostics, StringComparison.Ordinal);
        Assert.Contains("StopAsync", diagnostics, StringComparison.Ordinal);
        Assert.Contains("BuildAndCopyAppIsolationHelper", appTestsProject, StringComparison.Ordinal);
        Assert.Contains("SkipUnchangedFiles=\"false\"", appTestsProject, StringComparison.Ordinal);
        Assert.Contains("--app-settings-noncooperative", nativeHelper, StringComparison.Ordinal);
        Assert.Contains("--app-registry-noncooperative", nativeHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", nativeHelper, StringComparison.Ordinal);
        Assert.Contains("StopAsync", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("ActiveCount", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("FlushLatestAsync", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("MainWindow_OnClosing", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FlushLatestAndStopAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("operationDeadline.CancelAfter(totalTimeout - TimeSpan.FromMilliseconds(500))", settingsPersistence, StringComparison.Ordinal);
        Assert.Contains("_settingsSaver.StopAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ReportSettingsPersistenceFailureAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ObserveClosePersistenceResultAsync(shutdown.Persistence)", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("!result.Superseded && result.Error", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ReportSettingsPersistenceFailure", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("catch\n        {\n        }", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForExit(", integrationServices, StringComparison.Ordinal);
        Assert.Contains("OwnedHelperProcessRunner", integrationServices, StringComparison.Ordinal);
        Assert.Contains("process.Kill(entireProcessTree: false)", integrationServices, StringComparison.Ordinal);
        Assert.Contains("SettingsPersistence => _settingsPersistence", app, StringComparison.Ordinal);
        Assert.Contains("PersistSettingsInBackgroundAsync(_settings, generation)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySaveSettings", app, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(app, @"_settingsStore\?\.Save\(", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Contains("_settingsStore?.PrepareSaveAsync", app, StringComparison.Ordinal);
        Assert.Contains(": this(viewModel, viewModel.SettingsPersistence)", settingsWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsPersistenceCoordinator(viewModel.PersistSettingsSnapshot)", settingsWindow, StringComparison.Ordinal);
        var xamlCommands = Regex.Matches(appXaml + settingsXaml, "Command=\\\"\\{Binding ([A-Za-z][A-Za-z0-9_]*Command)\\}", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "RestoreAllMachineCommand", "RetryConnectionCommand", "TogglePauseCommand" }, xamlCommands);
        Assert.Contains("GetXamlCommandNames", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains(".baml", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("ResolveCommandTarget", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("OpCodes.Ldftn", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("CommandBindingResolverFixture", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("XAML commands=", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("ProbeHeadlessSettingsWindowFactory", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("CreateSettingsWindow", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("RestoreColumns_OnClick", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("Save_OnClick", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("AsyncStateMachineAttribute", candidateIlGate, StringComparison.Ordinal);
        Assert.Contains("AsyncDirect", candidateIlGate, StringComparison.Ordinal);
        var settingsHandlerStart = mainWindow.IndexOf("private void Settings_OnClick", StringComparison.Ordinal);
        var settingsHandlerEnd = mainWindow.IndexOf("private void Window_OnPreviewKeyDown", settingsHandlerStart, StringComparison.Ordinal);
        var settingsHandler = mainWindow[settingsHandlerStart..settingsHandlerEnd];
        Assert.DoesNotContain("PersistCurrentColumns", settingsHandler, StringComparison.Ordinal);
        Assert.True(settingsHandler.IndexOf("CreateSettingsWindow", StringComparison.Ordinal) <
                    settingsHandler.IndexOf("ShowDialog", StringComparison.Ordinal));
        Assert.Contains("按缓存范围搜索", app, StringComparison.Ordinal);
        Assert.Contains("SwSearch.Taskpane.BehaviorHarness", taskpaneVerifier, StringComparison.Ordinal);
        Assert.Contains("& $behaviorExe", taskpaneVerifier, StringComparison.Ordinal);
        Assert.Contains("set_SelectedPath", taskpaneVerifier, StringComparison.Ordinal);
        Assert.Contains("ForbiddenSynchronousIoFixture", taskpaneVerifier, StringComparison.Ordinal);
        Assert.Contains("SwSearch.CandidateIlGate", msiVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath", lexicalScope, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.IsPathRooted", lexicalScope, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizationForm", lexicalScope, StringComparison.Ordinal);
        Assert.Contains("MaximumLength = 32767", lexicalScope, StringComparison.Ordinal);
        Assert.Contains("value == '\\u00B9' || value == '\\u00B2' || value == '\\u00B3'", lexicalScope, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexer_UsesCreateModeOnlyForInitializationAndReadWriteForEveryOperationalOpen()
    {
        var root = FindRepoRoot();
        var indexerDirectory = Path.Combine(root, "src", "SwSearch.Indexer");
        var sources = Directory.GetFiles(indexerDirectory, "*.cs")
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        var store = sources["SqliteIndexStore.cs"];

        Assert.Equal(1, sources.Values.Sum(source =>
            source.Split("SqliteOpenMode.ReadWriteCreate", StringSplitOptions.None).Length - 1));
        Assert.Contains("new SqliteConnection(_initializationConnectionString)", store, StringComparison.Ordinal);
        Assert.Contains("new SqliteConnection(_operationalConnectionString)", store, StringComparison.Ordinal);
        Assert.Contains("Mode = SqliteOpenMode.ReadWrite,", store, StringComparison.Ordinal);

        foreach (var (file, source) in sources)
        {
            if (!source.Contains("new SqliteConnection(", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Equal("SqliteIndexStore.cs", file);
        }
    }

    [Fact]
    public void ReleaseEntryPoint_FreezesControlledFilesAndVerifierRechecksManifest()
    {
        var root = FindRepoRoot();
        var release = File.ReadAllText(Path.Combine(root, "installer", "Build-Release.ps1"));
        var freeze = File.ReadAllText(Path.Combine(root, "installer", "Source-Freeze.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "installer", "Verify-Msi.ps1"));

        Assert.Contains("Get-SwSearchControlledSourceSnapshot", release, StringComparison.Ordinal);
        Assert.Contains("Compare-SwSearchControlledSourceSnapshot", release, StringComparison.Ordinal);
        Assert.Contains("Test-SwSearchControlledSourceManifest", release, StringComparison.Ordinal);
        Assert.Contains("ModifiedAfterCandidateCount", freeze, StringComparison.Ordinal);
        Assert.Contains("HashChangedCount", freeze, StringComparison.Ordinal);
        Assert.Contains("CONTROLLED_SOURCE_FREEZE_FAILED", freeze, StringComparison.Ordinal);
        Assert.Contains("Test-SwSearchControlledSourceManifest", verifier, StringComparison.Ordinal);
        Assert.Contains("Controlled source files / modified after candidate", verifier, StringComparison.Ordinal);
        Assert.Contains("-ReleaseOrchestrated", release, StringComparison.Ordinal);
        Assert.Contains("must run through installer\\Build-Release.ps1", File.ReadAllText(
            Path.Combine(root, "installer", "Build-Msi.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    public void CachedSearchResponsePath_OnlyUsesNonBlockingPromotionProducer()
    {
        var root = FindRepoRoot();
        var runtime = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Indexer", "IndexingRuntime.cs"));
        var queue = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Indexer", "SearchCandidatePromotionQueue.cs"));

        Assert.Contains("_searchPromotions.TryEnqueue(query);", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("await _store.EnqueueSearchCandidatesAsync(query", runtime, StringComparison.Ordinal);
        Assert.Contains("_capacity", queue, StringComparison.Ordinal);
        Assert.Contains("_knownKeys", queue, StringComparison.Ordinal);
        Assert.Contains("Task.Run(ConsumeAsync)", queue, StringComparison.Ordinal);
    }

    [Fact]
    public void UiProcesses_UseOnlyTheFixedReadOnlyCacheBoundary()
    {
        var root = FindRepoRoot();
        var appProject = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SwSearch.App.csproj"));
        Assert.DoesNotContain("SwSearch.Indexer", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", appProject, StringComparison.OrdinalIgnoreCase);
        var integration = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Integration", "LocalCacheSearchReader.cs"));
        var viewer = File.ReadAllText(Path.Combine(root, "src", "SwSearch.PropertyViewer", "PropertyDataClient.cs"));
        var sqliteReader = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Indexer", "SqliteMetadataSnapshotReader.cs"));
        Assert.Contains("OpenReadOnlyExisting", integration, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeAsync", integration, StringComparison.Ordinal);
        Assert.Contains("SqliteMetadataSnapshotReader", viewer, StringComparison.Ordinal);
        Assert.Contains("PropertyCardLastGoodReader", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyCardConfigProvider", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("Mode = SqliteOpenMode.ReadOnly", sqliteReader, StringComparison.Ordinal);
        Assert.Contains("PRAGMA query_only=ON", sqliteReader, StringComparison.Ordinal);
        var addin = Directory.GetFiles(Path.Combine(root, "src", "SwSearch.SolidWorksAddin"), "*.cs")
            .Select(File.ReadAllText).Aggregate(string.Concat);
        Assert.Contains("DisplayWindowFromHandlex64", addin, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", addin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SwSearch.Indexer", addin, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppReleaseDeps_ContainExecutableFixedSqliteReadOnlyClosure()
    {
        var root = FindRepoRoot();
        var output = Path.Combine(root, "src", "SwSearch.App", "bin", "Release", "net8.0-windows");
        var deps = File.ReadAllText(Path.Combine(output, "SwSearch.App.deps.json"));
        foreach (var dependency in new[]
                 {
                     "SwSearch.Integration", "SwSearch.Indexer", "Microsoft.Data.Sqlite",
                     "SQLitePCLRaw.core", "SQLitePCLRaw.provider.e_sqlite3", "SQLitePCLRaw.batteries_v2",
                 })
        {
            Assert.Contains(dependency, deps, StringComparison.Ordinal);
        }
        Assert.True(File.Exists(Path.Combine(output, "e_sqlite3.dll")));

        var app = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SearchClient.cs"));
        Assert.Contains("LocalCacheSearchReader", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteIndexStore", app, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeAsync", app, StringComparison.Ordinal);
    }

    [Fact]
    public void BrandIcon_ContainsAllRequiredFramesAndPreservesMaster()
    {
        var root = FindRepoRoot();
        var icon = Path.Combine(root, "src", "Assets", "Brand", "SwSearch.ico");
        using var reader = new BinaryReader(File.OpenRead(icon));
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.Equal(9, count);
        var sizes = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            sizes.Add(width == 0 ? 256 : width);
            Assert.Equal(width, height);
            reader.ReadBytes(14);
        }
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, sizes);
        var source = File.ReadAllBytes(@"C:\Users\liliy\AppData\Local\Temp\codex-clipboard-80223c5c-4dee-4856-b2f7-6845635605b8.png");
        var master = File.ReadAllBytes(Path.Combine(root, "src", "Assets", "Brand", "ProductIcon-source.png"));
        Assert.Equal(source, master);
    }

    [Fact]
    public void TaskpaneIcons_AreEightBitIndexedPalettePngs()
    {
        var root = FindRepoRoot();
        foreach (var size in new[] { 20, 32, 40, 64, 96, 128 })
        {
            var path = Path.Combine(root, "src", "Assets", "Taskpane", $"SwSearch-{size}.png");
            var png = File.ReadAllBytes(path);
            Assert.True(png.Length >= 33, path);
            Assert.Equal((byte)8, png[24]);
            Assert.Equal((byte)3, png[25]);
            Assert.Contains("PLTE", System.Text.Encoding.ASCII.GetString(png), StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static async Task RunDroppingServerAsync(string pipeName)
    {
        await using var server = NewServer(pipeName);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _ = await reader.ReadLineAsync();
    }

    private static async Task RunReplyServerAsync(string pipeName)
    {
        await using var server = NewServer(pipeName);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var request = JsonSerializer.Deserialize<BrokerRequest>(
            await reader.ReadLineAsync() ?? throw new InvalidDataException(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException();
        var hit = new SearchHit(@"C:\模型\接头.SLDPRT", SolidWorksDocumentKind.Part, "接头.SLDPRT", null, null, null, 1, false);
        var response = new BrokerResponse(ContractVersions.BrokerPipe, request.RequestId, true, Hits: [hit],
            Capability: new RuntimeCapability(CapabilityState.Available, "TEST", "测试"));
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task RunProtocolMismatchServerAsync(string pipeName)
    {
        await using var server = NewServer(pipeName);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var request = JsonSerializer.Deserialize<BrokerRequest>(
            await reader.ReadLineAsync() ?? throw new InvalidDataException(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException();
        var response = new BrokerResponse(
            1,
            request.RequestId,
            false,
            "BROKER_PROTOCOL_MISMATCH",
            "old fixture");
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task RunNonRespondingServerAsync(string pipeName)
    {
        await using var server = NewServer(pipeName);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        await Task.Delay(300);
    }

    private static NamedPipeServerStream NewServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static BrokerSearchClientOptions FastBrokerOptions() => new(
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(10));

    private static BrokerSearchClientOptions ReliableBrokerOptions() => new(
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(25));

    private sealed class MemoryRegistry : IRegistryAccess
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        private static string Key(RegistryHive hive, string path) => $"{hive}|{path}";
        private static string ValueKey(RegistryHive hive, string path, string? name) => $"{Key(hive, path)}|{name ?? "(default)"}";
        public object? GetValue(RegistryHive hive, string keyPath, string? valueName) => _values.GetValueOrDefault(ValueKey(hive, keyPath, valueName));
        public bool KeyExists(RegistryHive hive, string keyPath) => _keys.Contains(Key(hive, keyPath));
        public void SetString(RegistryHive hive, string keyPath, string? valueName, string value) { _keys.Add(Key(hive, keyPath)); _values[ValueKey(hive, keyPath, valueName)] = value; }
        public void SetDword(RegistryHive hive, string keyPath, string? valueName, int value) { _keys.Add(Key(hive, keyPath)); _values[ValueKey(hive, keyPath, valueName)] = value; }
        public void DeleteTree(RegistryHive hive, string keyPath)
        {
            var prefix = Key(hive, keyPath);
            _keys.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            foreach (var key in _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray()) _values.Remove(key);
        }
        public void DeleteValue(RegistryHive hive, string keyPath, string? valueName) => _values.Remove(ValueKey(hive, keyPath, valueName));
    }
}
