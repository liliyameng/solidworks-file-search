using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using System.Diagnostics;
using System.Windows.Threading;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class AppSettingsTests
{
    [TestMethod]
    public void SettingsStore_RoundTripsVersionedColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swsearch-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonAppSettingsStore(
                Path.Combine(directory, "settings.json"),
                new DirectFileSettingsSaveExecutor());
            store.Save(AppSettings.Default with
            {
                Columns = [new ResultColumnLayout("fileName", "模型名称", 321, 0, true)],
                PropertyDisplayOrder = ["图号", "材料名称"]
            });
            var loaded = store.Load();
            Assert.AreEqual(3, loaded.SchemaVersion);
            Assert.AreEqual(
                ResultColumnDefaults.All.Single(column => column.Id == "fileName").DisplayName,
                loaded.NormalizedColumns.Single(column => column.Id == "fileName").DisplayName);
            Assert.AreEqual(ResultColumnDefaults.All.Count, loaded.NormalizedColumns.Count);
            CollectionAssert.AreEqual(
                new[] { "图号", "材料名称" },
                loaded.NormalizedPropertyDisplayOrder.ToArray());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task SettingsStore_PreparedSaveCommitsOnlyWhenLeaseOwnerCallsCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swsearch-settings-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonAppSettingsStore(path, new DirectFileSettingsSaveExecutor());
            using (var stale = await store.PrepareSaveAsync(
                       AppSettings.Default with { NetworkRoots = [@"\\stale\scope"] },
                       CancellationToken.None))
            {
            }
            Assert.IsFalse(File.Exists(path), "An invalidated staged save replaced the formal settings file.");

            using (var latest = await store.PrepareSaveAsync(
                       AppSettings.Default with { NetworkRoots = [@"\\latest\scope"] },
                       CancellationToken.None))
            {
                await latest.CommitAsync(CancellationToken.None);
            }
            Assert.AreEqual(SearchPath.NormalizeFolder(@"\\latest\scope"), store.Load().NetworkRoots.Single());
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void NetworkRoots_AreExplicitUncOnlyAndDeduplicated()
    {
        var roots = JsonAppSettingsStore.NormalizeNetworkRoots([@"\\server\design\", @"\\SERVER\design", "  "]);
        Assert.AreEqual(1, roots.Count);
        Assert.ThrowsException<ArgumentException>(() => JsonAppSettingsStore.NormalizeNetworkRoots([@"C:\Design"]));
    }

    [TestMethod]
    public async Task RuntimePanelShowsRealAggregatesAndPauseIsImmediate()
    {
        var client = new RuntimeClient();
        var settings = new MemorySettingsStore();
        using var viewModel = new MainViewModel(
            client,
            new NoOpFileActions(),
            settingsStore: settings,
            initialSettings: AppSettings.Default);

        await viewModel.RefreshRuntimeStatusAsync();

        StringAssert.Contains(viewModel.RuntimeSummary, "内置扫描");
        StringAssert.Contains(viewModel.RuntimeSummary, "属性完成 10");
        StringAssert.Contains(viewModel.RuntimeSummary, "待处理 4");
        StringAssert.Contains(viewModel.RuntimeSummary, "运行中");

        viewModel.TogglePauseCommand.Execute(null);
        await viewModel.SettingsPersistence.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(client.Paused);
        Assert.IsTrue(settings.LastSaved!.BackgroundPaused);
        StringAssert.Contains(viewModel.StatusText, "已暂停");
    }

    [TestMethod]
    public async Task BackgroundSettingsLoad_ReturnsImmediately_AndAppliesOnDispatcher()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            AppSettings? applied = null;
            var dispatcherThread = Environment.CurrentManagedThreadId;
            var appliedThread = -1;
            using var loader = new BackgroundSettingsLoader(
                () =>
                {
                    entered.Set();
                    release.Wait();
                    return AppSettings.Default with { DiscoveryPreference = DiscoveryPreference.BuiltInOnly };
                },
                dispatcher,
                settings =>
                {
                    applied = settings;
                    appliedThread = Environment.CurrentManagedThreadId;
                },
                () => true,
                TimeSpan.FromSeconds(2));

            var clock = Stopwatch.StartNew();
            loader.Start();
            clock.Stop();
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Settings Start blocked for {clock.Elapsed}.");
            Assert.IsTrue(await WaitHandleAsync(entered.WaitHandle, TimeSpan.FromSeconds(1)));
            Assert.IsNull(applied, "Settings were applied before the background load completed.");
            release.Set();
            await loader.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsNotNull(applied);
            Assert.AreEqual(DiscoveryPreference.BuiltInOnly, applied.DiscoveryPreference);
            Assert.AreEqual(dispatcherThread, appliedThread, "Settings were not applied through the UI dispatcher.");
        });
    }

    [TestMethod]
    public async Task LateSettingsSnapshot_NeverOverridesUserChangesClosedWindowOrNewGeneration()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            var client = new RuntimeClient();
            using var viewModel = new MainViewModel(client, new NoOpFileActions(), TimeSpan.FromMilliseconds(1));
            var capturedGeneration = viewModel.UserStateGeneration;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var applyCount = 0;
            using var loader = new BackgroundSettingsLoader(
                () =>
                {
                    entered.Set();
                    release.Wait();
                    return AppSettings.Default with
                    {
                        DiscoveryPreference = DiscoveryPreference.EverythingPreferred,
                        NetworkRoots = new[] { @"\\disk-snapshot\design" },
                    };
                },
                dispatcher,
                settings =>
                {
                    applyCount++;
                    viewModel.ApplyLoadedSettings(settings);
                },
                () => viewModel.UserStateGeneration == capturedGeneration,
                TimeSpan.FromSeconds(2));

            loader.Start();
            Assert.IsTrue(await WaitHandleAsync(entered.WaitHandle, TimeSpan.FromSeconds(1)));
            viewModel.NetworkRootsText = @"\\user-choice\library";
            viewModel.ColumnLayouts[0].Width += 17;
            viewModel.ApplyFolderScope(@"C:\UserScope");
            release.Set();
            await loader.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(0, applyCount, "Late disk settings overwrote a newer user edit.");
            Assert.AreEqual(@"\\user-choice\library", viewModel.NetworkRootsText);
            Assert.AreEqual(@"C:\UserScope", viewModel.CurrentScope.RootPath);

            using var firstEntered = new ManualResetEventSlim();
            using var firstRelease = new ManualResetEventSlim();
            var loadCall = 0;
            var newestApplyCount = 0;
            using var generations = new BackgroundSettingsLoader(
                () =>
                {
                    if (Interlocked.Increment(ref loadCall) == 1)
                    {
                        firstEntered.Set();
                        firstRelease.Wait();
                        return AppSettings.Default with { DiscoveryPreference = DiscoveryPreference.EverythingPreferred };
                    }
                    return AppSettings.Default with { DiscoveryPreference = DiscoveryPreference.BuiltInOnly };
                },
                dispatcher,
                settings =>
                {
                    newestApplyCount++;
                    viewModel.ApplyLoadedSettings(settings);
                },
                () => true,
                TimeSpan.FromSeconds(2));
            generations.Start();
            Assert.IsTrue(await WaitHandleAsync(firstEntered.WaitHandle, TimeSpan.FromSeconds(1)));
            generations.Start();
            await generations.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            firstRelease.Set();
            await Task.Delay(80);

            Assert.AreEqual(1, newestApplyCount, "An old settings generation reached the dispatcher.");
            Assert.AreEqual(DiscoveryPreference.BuiltInOnly, viewModel.SelectedDiscoveryOption.Value);

            using var closedEntered = new ManualResetEventSlim();
            using var closedRelease = new ManualResetEventSlim();
            var windowOpen = true;
            var closedApplyCount = 0;
            using var closed = new BackgroundSettingsLoader(
                () =>
                {
                    closedEntered.Set();
                    closedRelease.Wait();
                    return AppSettings.Default;
                },
                dispatcher,
                _ => closedApplyCount++,
                () => windowOpen,
                TimeSpan.FromSeconds(2));
            closed.Start();
            Assert.IsTrue(await WaitHandleAsync(closedEntered.WaitHandle, TimeSpan.FromSeconds(1)));
            windowOpen = false;
            closedRelease.Set();
            await closed.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(0, closedApplyCount, "Closed-window settings reached the dispatcher.");
        });
    }

    [TestMethod]
    public async Task BackgroundSettingsLoad_TimeoutAndDisposeAreBoundedAndNeverApply()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var applyCount = 0;
            using var loader = new BackgroundSettingsLoader(
                () =>
                {
                    entered.Set();
                    release.Wait();
                    return AppSettings.Default;
                },
                dispatcher,
                _ => applyCount++,
                () => true,
                TimeSpan.FromMilliseconds(40));

            loader.Start();
            Assert.IsTrue(await WaitHandleAsync(entered.WaitHandle, TimeSpan.FromSeconds(1)));
            var timeout = Stopwatch.StartNew();
            await loader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            timeout.Stop();
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromMilliseconds(500), $"Settings timeout was unbounded: {timeout.Elapsed}.");
            loader.Dispose();
            release.Set();
            await Task.Delay(80);
            Assert.AreEqual(0, applyCount);
        });
    }

    [TestMethod]
    public async Task BackgroundSettingsLoad_CompletionAndDisposeRaceIsIdempotent()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var loader = new BackgroundSettingsLoader(
                    () =>
                    {
                        release.Task.GetAwaiter().GetResult();
                        return AppSettings.Default;
                    },
                    dispatcher,
                    _ => { },
                    () => false,
                    TimeSpan.FromSeconds(1));
                loader.Start();
                release.TrySetResult();
                await Task.WhenAll(loader.Completion, Task.Run(loader.Dispose)).WaitAsync(TimeSpan.FromSeconds(2));
                loader.Dispose();
            }
        });
    }

    private static async Task RunOnDispatcherAsync(Func<Dispatcher, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action(dispatcher);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(2)), "Headless dispatcher thread did not exit.");
    }

    private static Task<bool> WaitHandleAsync(WaitHandle handle, TimeSpan timeout) =>
        Task.Run(() => handle.WaitOne(timeout));

    private sealed class RuntimeClient : ISearchClient, IRuntimeControlClient
    {
        public bool Paused { get; private set; }
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult([], new RuntimeCapability(CapabilityState.Available, "OK", "正常")));
        public ValueTask<RuntimeProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeProgressSnapshot(
                new MetadataProgressSnapshot(16, 10, 1, 4, 1),
                "内置扫描", "BUILTIN_DISCOVERY_READY", Paused, 2, 4 * 1024 * 1024, null));
        public void SetBackgroundPaused(bool paused) => Paused = paused;
    }

    private sealed class MemorySettingsStore : IAppSettingsStore
    {
        public AppSettings? LastSaved { get; private set; }
        public AppSettings Load() => LastSaved ?? AppSettings.Default;
        public void Save(AppSettings settings) => LastSaved = settings;
        public ValueTask<IPreparedAppSettingsSave> PrepareSaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IPreparedAppSettingsSave>(
                new DeferredAppSettingsSave(() => Save(settings)));
        }
    }

    private sealed class NoOpFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
    }

    private sealed class DirectFileSettingsSaveExecutor : ISettingsSaveExecutor
    {
        public async ValueTask ExecuteAsync(
            string settingsPath,
            string temporaryPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, settingsPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, settingsPath, overwrite: false);
            }
        }
    }
}
