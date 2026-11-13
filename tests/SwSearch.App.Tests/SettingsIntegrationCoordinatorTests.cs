using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class SettingsIntegrationCoordinatorTests
{
    [TestMethod]
    public async Task SlowProbes_StartUnder50MillisecondsAndTimeoutIndependently()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            var applied = new List<IntegrationStatus>();
            var completed = 0;
            using var coordinator = new SettingsIntegrationProbeCoordinator(
                dispatcher,
                static async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("unreachable");
                },
                (_, status) => applied.Add(status),
                () => completed++,
                TimeSpan.FromMilliseconds(40));

            var clock = Stopwatch.StartNew();
            var completion = coordinator.Start();
            clock.Stop();

            Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Probe start blocked for {clock.Elapsed}.");
            await completion.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(3, applied.Count);
            Assert.IsTrue(applied.All(status => !status.IsAvailable && status.Message.Contains("超时", StringComparison.Ordinal)));
            Assert.AreEqual(1, completed);
        });
    }

    [TestMethod]
    public async Task ProbeExceptionIsIsolatedAndOtherStatusesStillApply()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            var applied = new Dictionary<SettingsIntegrationTarget, IntegrationStatus>();
            var completed = 0;
            var recorded = 0;
            using var coordinator = new SettingsIntegrationProbeCoordinator(
                dispatcher,
                (target, _) => target == SettingsIntegrationTarget.Taskpane
                    ? Task.FromException<IntegrationStatus>(new InvalidDataException("sensitive fixture"))
                    : Task.FromResult(new IntegrationStatus(true, true, target.ToString())),
                (target, status) => applied[target] = status,
                () => completed++,
                TimeSpan.FromSeconds(1),
                _ => recorded++);

            await coordinator.Start().WaitAsync(TimeSpan.FromSeconds(1));
            await coordinator.Start().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(3, applied.Count);
            Assert.AreEqual(2, completed, "A completed probe generation left a disposed cancellation source behind.");
            Assert.AreEqual(2, recorded, "Probe exceptions were not recorded once per failed generation.");
            Assert.IsFalse(applied[SettingsIntegrationTarget.Taskpane].IsAvailable);
            Assert.IsTrue(applied[SettingsIntegrationTarget.Taskpane].Message.Contains(nameof(InvalidDataException), StringComparison.Ordinal));
            Assert.IsTrue(applied[SettingsIntegrationTarget.FolderMenu].IsAvailable);
            Assert.IsTrue(applied[SettingsIntegrationTarget.Startup].IsAvailable);
        });
    }

    [TestMethod]
    public async Task NewGenerationCancelsOldAndLateResultsNeverWriteBack()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            var oldRelease = new TaskCompletionSource<IntegrationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldCount = 0;
            var mode = 1;
            var applied = new List<string>();
            using var coordinator = new SettingsIntegrationProbeCoordinator(
                dispatcher,
                (_, _) =>
                {
                    if (Volatile.Read(ref mode) == 1)
                    {
                        if (Interlocked.Increment(ref oldCount) == 3) oldEntered.TrySetResult();
                        return oldRelease.Task;
                    }
                    return Task.FromResult(new IntegrationStatus(true, true, "new"));
                },
                (_, status) => applied.Add(status.Message),
                () => { },
                TimeSpan.FromSeconds(2));

            var first = coordinator.Start();
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Volatile.Write(ref mode, 2);
            var second = coordinator.Start();
            await second.WaitAsync(TimeSpan.FromSeconds(1));
            oldRelease.TrySetResult(new IntegrationStatus(true, false, "old"));
            await first.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(60);

            Assert.AreEqual(3, applied.Count);
            Assert.IsTrue(applied.All(message => message == "new"), "A late probe generation wrote back into the window.");
        });
    }

    [TestMethod]
    public async Task DisposeCancelsActiveGenerationAndPreventsClosedWindowWriteBack()
    {
        await RunOnDispatcherAsync(async dispatcher =>
        {
            var release = new TaskCompletionSource<IntegrationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var applied = 0;
            var coordinator = new SettingsIntegrationProbeCoordinator(
                dispatcher,
                (_, _) =>
                {
                    if (Interlocked.Increment(ref count) == 3) entered.TrySetResult();
                    return release.Task;
                },
                (_, _) => applied++,
                () => applied++,
                TimeSpan.FromSeconds(2));

            var completion = coordinator.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            coordinator.Dispose();
            release.TrySetResult(new IntegrationStatus(true, true, "late"));
            await completion.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(50);

            Assert.AreEqual(0, applied);
        });
    }

    [TestMethod]
    public async Task IntegrationAdapter_DisposeStopsOwnedNonceHelperBeforeReportingInactive()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepositoryRoot();
        var helper = Path.Combine(AppContext.BaseDirectory, "AppIsolationHelper", "SwSearch.Broker.exe");
        Assert.IsTrue(File.Exists(helper), "The fixed test-only native helper was not built.");
        var helperBefore = CaptureFileIdentity(helper);
        var helperSource = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "SwSearch.InstallerCoordinator.NativeHelper",
            "Program.cs"));
        Assert.IsFalse(helperSource.Contains("Process.Start", StringComparison.Ordinal));
        Assert.IsFalse(helperSource.Contains("--native-gate-child", StringComparison.Ordinal) &&
                       helperSource.Contains("ProcessStartInfo", StringComparison.Ordinal));

        var nonce = Guid.NewGuid().ToString("N");
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-helper-" + nonce);
        Directory.CreateDirectory(sandbox);
        var ready = Path.Combine(sandbox, "ready-" + nonce + ".txt");
        var late = Path.Combine(sandbox, "late-" + nonce + ".txt");
        var runner = new OwnedIsolatedProcessRunner();
        var lateStateWrites = 0;
        var adapter = new SettingsIntegrationOperationAdapter();
        try
        {
            var operation = adapter.RunAsync(
                async token =>
                {
                    var result = await runner.RunAsync(
                        helper,
                        ["--app-registry-noncooperative", nonce, ready, late],
                        string.Empty,
                        TimeSpan.FromSeconds(20),
                        token);
                    Interlocked.Increment(ref lateStateWrites);
                    return result.ExitCode;
                },
                CancellationToken.None);
            await WaitUntilAsync(() =>
                adapter.ActiveCount == 1 && runner.ActiveCount == 1 && File.Exists(ready));

            Assert.IsTrue(await adapter.StopAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(0, adapter.ActiveCount);
            Assert.AreEqual(0, runner.ActiveCount);
            try
            {
                await operation;
                Assert.Fail("Canceled owned helper completed successfully.");
            }
            catch (OperationCanceledException)
            {
            }
            Assert.AreEqual(0, lateStateWrites);
            Assert.IsFalse(File.Exists(late), "A terminated registry fixture produced a late write.");
            Assert.AreEqual(helperBefore, CaptureFileIdentity(helper), "The helper binary changed during the test.");
        }
        finally
        {
            adapter.Dispose();
            await adapter.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(0, runner.ActiveCount);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistenceCommit_BlockingOwnedHelperIsReapedWithinDeadlineWithoutLateFormalWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(AppContext.BaseDirectory, "AppIsolationHelper", "SwSearch.Broker.exe");
        var helperBefore = CaptureFileIdentity(helper);
        var nonce = Guid.NewGuid().ToString("N");
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-commit-" + nonce);
        Directory.CreateDirectory(sandbox);
        try
        {
            var executor = new BlockingOwnedHelperSaveExecutor(helper, sandbox, nonce);
            var store = new JsonAppSettingsStore(Path.Combine(sandbox, "settings.json"), executor);
            using var coordinator = new SettingsPersistenceCoordinator(
                store.PrepareSaveAsync,
                operationTimeout: TimeSpan.FromMilliseconds(250));
            var clock = Stopwatch.StartNew();
            var operation = coordinator.QueueAsync(AppSettings.Default, CancellationToken.None);
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queueClock = Stopwatch.StartNew();
            var latest = coordinator.QueueAsync(
                AppSettings.Default with { BackgroundPaused = true },
                CancellationToken.None);
            queueClock.Stop();
            Assert.IsTrue(queueClock.Elapsed < TimeSpan.FromMilliseconds(50), $"UI queue path blocked for {queueClock.Elapsed}.");
            Assert.IsTrue((await operation.WaitAsync(TimeSpan.FromSeconds(2))).Superseded);
            Assert.IsTrue(await coordinator.StopAsync(TimeSpan.FromSeconds(2)));
            clock.Stop();
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3));
            Assert.IsTrue((await latest.WaitAsync(TimeSpan.FromSeconds(2))).Superseded);
            Assert.AreEqual(0, coordinator.ActiveCount);
            Assert.AreEqual(0, executor.ActiveCount);
            Assert.IsFalse(File.Exists(Path.Combine(sandbox, "settings.json")));
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
            Assert.AreEqual(helperBefore, CaptureFileIdentity(helper));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task FlushAndStop_UsesOneWallClockDeadlineAndReservesOwnedHelperReapingBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(AppContext.BaseDirectory, "AppIsolationHelper", "SwSearch.Broker.exe");
        var helperBefore = CaptureFileIdentity(helper);
        var nonce = Guid.NewGuid().ToString("N");
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-total-deadline-" + nonce);
        Directory.CreateDirectory(sandbox);
        try
        {
            var executor = new BlockingOwnedHelperSaveExecutor(helper, sandbox, nonce);
            var store = new JsonAppSettingsStore(Path.Combine(sandbox, "settings.json"), executor);
            using var coordinator = new SettingsPersistenceCoordinator(
                store.PrepareSaveAsync,
                operationTimeout: TimeSpan.FromSeconds(10));
            var clock = Stopwatch.StartNew();
            var shutdown = coordinator.FlushLatestAndStopAsync(
                AppSettings.Default,
                TimeSpan.FromMilliseconds(900));
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Stop();
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(1200), $"Combined deadline was reset: {clock.Elapsed}.");
            Assert.IsTrue(result.Persistence.Superseded);
            Assert.IsInstanceOfType<TimeoutException>(result.Persistence.Error);
            Assert.IsTrue(result.WorkerStopped);
            Assert.AreEqual(0, coordinator.ActiveCount);
            Assert.AreEqual(0, executor.ActiveCount);
            Assert.IsFalse(File.Exists(store.SettingsPath));
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
            Assert.AreEqual(helperBefore, CaptureFileIdentity(helper));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task ColumnPersistenceIsOffUiThreadBoundedAndCoalescesLatestSnapshot()
    {
        using var store = new ControlledTransactionalStore(blockFirstPrepare: true);
        using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);

        var first = AppSettings.Default with { NetworkRoots = [@"\\first\library"] };
        var firstCompletion = coordinator.QueueAsync(first, CancellationToken.None);
        Assert.IsTrue(store.FirstPrepared.Wait(TimeSpan.FromSeconds(1)));
        var clock = Stopwatch.StartNew();
        var pending = new List<Task<SettingsPersistenceResult>>();
        for (var index = 0; index < 12; index++)
        {
            pending.Add(coordinator.QueueAsync(
                AppSettings.Default with { NetworkRoots = [$@"\\latest\library-{index}"] },
                CancellationToken.None));
        }
        clock.Stop();
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Queue blocked for {clock.Elapsed}.");
        store.ReleaseFirstPrepare.Set();
        var firstResult = await firstCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, store.CommitCount, "A superseded staged snapshot reached the formal commit.");
        Assert.AreEqual(@"\\latest\library-11", store.Committed.Single().NetworkRoots.Single());
        Assert.IsTrue(firstResult.Superseded, "A slow stale save was allowed to publish a late UI result.");
        Assert.IsTrue(results.Take(11).All(result => result.Superseded));
        Assert.IsTrue(results[^1].Saved && !results[^1].Superseded);
    }

    [TestMethod]
    public async Task PersistenceFailureIsReturnedAsNonFatalStatus()
    {
        using var coordinator = new SettingsPersistenceCoordinator(
            static (_, _) => ValueTask.FromException<IPreparedAppSettingsSave>(
                new IOException("fixture path must not escape")));

        var result = await coordinator.QueueAsync(AppSettings.Default, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsFalse(result.Saved);
        Assert.IsFalse(result.Superseded);
        Assert.IsInstanceOfType<IOException>(result.Error);
    }

    [TestMethod]
    public async Task CancellationSkipsPendingSaveAndSuppressesLateWriteBack()
    {
        using var store = new ControlledTransactionalStore(blockFirstPrepare: true);
        using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);

        var first = coordinator.QueueAsync(
            AppSettings.Default with { NetworkRoots = [@"\\slow\first"] },
            CancellationToken.None);
        Assert.IsTrue(store.FirstPrepared.Wait(TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();
        var canceled = coordinator.QueueAsync(
            AppSettings.Default with { NetworkRoots = [@"\\canceled\second"] },
            cancellation.Token);
        cancellation.Cancel();
        store.ReleaseFirstPrepare.Set();

        Assert.IsTrue((await first.WaitAsync(TimeSpan.FromSeconds(1))).Superseded);
        Assert.IsTrue((await canceled.WaitAsync(TimeSpan.FromSeconds(1))).Superseded);
        await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, store.CommitCount);
    }

    [TestMethod]
    public async Task ProductionJsonStore_CommitLeaseClosesFinalValidationReplacementRace()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var settingsPath = Path.Combine(sandbox, "settings.json");
            var executor = new FirstCommitCancellableExecutor();
            var store = new JsonAppSettingsStore(settingsPath, executor);
            using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);
            var stale = coordinator.QueueAsync(
                AppSettings.Default with { NetworkRoots = [@"\\stale\scope"] },
                CancellationToken.None);
            await executor.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var latest = coordinator.QueueAsync(
                AppSettings.Default with { NetworkRoots = [@"\\latest\scope"] },
                CancellationToken.None);
            Assert.IsTrue((await stale.WaitAsync(TimeSpan.FromSeconds(1))).Superseded);
            Assert.IsTrue((await latest.WaitAsync(TimeSpan.FromSeconds(1))).Saved);
            Assert.AreEqual(
                SearchPath.NormalizeFolder(@"\\latest\scope"),
                store.Load().NetworkRoots.Single());
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductionJsonStore_TimeoutAndStopCancelStagingWithoutFormalOrTempFile()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var writer = new BlockingCancellableSaveExecutor();
            var settingsPath = Path.Combine(sandbox, "settings.json");
            var store = new JsonAppSettingsStore(settingsPath, writer);
            using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);
            var flush = coordinator.FlushLatestAsync(
                AppSettings.Default with { NetworkRoots = [@"\\timeout\scope"] },
                TimeSpan.FromMilliseconds(50));
            await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var result = await flush.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(result.Superseded);
            Assert.IsInstanceOfType<TimeoutException>(result.Error);
            Assert.IsTrue(await coordinator.StopAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(0, coordinator.ActiveCount);
            Assert.AreEqual(0, writer.ActiveCount);
            Assert.IsFalse(File.Exists(settingsPath));
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductionJsonStore_StopCancelsActiveStagingWithoutManualRelease()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var writer = new BlockingCancellableSaveExecutor();
            var settingsPath = Path.Combine(sandbox, "settings.json");
            var store = new JsonAppSettingsStore(settingsPath, writer);
            using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);
            var operation = coordinator.QueueAsync(AppSettings.Default, CancellationToken.None);
            await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(await coordinator.StopAsync(TimeSpan.FromSeconds(1)));
            Assert.IsTrue((await operation.WaitAsync(TimeSpan.FromSeconds(1))).Superseded);
            Assert.AreEqual(0, coordinator.ActiveCount);
            Assert.AreEqual(0, writer.ActiveCount);
            Assert.IsFalse(File.Exists(settingsPath));
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductionJsonStore_StagingExceptionDeletesUniqueTempFile()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var settingsPath = Path.Combine(sandbox, "settings.json");
            var store = new JsonAppSettingsStore(settingsPath, new ThrowingSaveExecutor());
            using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);
            var result = await coordinator.QueueAsync(AppSettings.Default, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsInstanceOfType<IOException>(result.Error);
            Assert.IsFalse(File.Exists(settingsPath));
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task FlushLatest_CommitsFinalProductionSnapshotExactlyOnce()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var store = new JsonAppSettingsStore(
                Path.Combine(sandbox, "settings.json"),
                new DirectFileSettingsSaveExecutor());
            using var coordinator = new SettingsPersistenceCoordinator(store.PrepareSaveAsync);
            var result = await coordinator.FlushLatestAsync(
                AppSettings.Default with { NetworkRoots = [@"\\close\final"] },
                TimeSpan.FromSeconds(1));
            Assert.IsTrue(result.Saved);
            Assert.AreEqual(SearchPath.NormalizeFolder(@"\\close\final"), store.Load().NetworkRoots.Single());
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistenceDiagnostics_RecordTimeoutAndExceptionWithoutSensitiveMessages()
    {
        var sink = new RecordingDiagnosticSink();
        using var viewModel = new MainViewModel(
            new RuntimeControlClient(),
            new NoOpFileActions(),
            null,
            null,
            null,
            null,
            sink);
        await viewModel.ObserveClosePersistenceResultAsync(
            new SettingsPersistenceResult(
                false,
                true,
                new TimeoutException(@"secret \\server\scope")));
        await viewModel.ObserveClosePersistenceResultAsync(
            new SettingsPersistenceResult(
                false,
                false,
                new IOException(@"secret C:\private")));

        CollectionAssert.AreEqual(
            new[]
            {
                "SETTINGS_PERSISTENCE_FAILED|TimeoutException",
                "SETTINGS_PERSISTENCE_FAILED|IOException",
            },
            sink.Records.ToArray());
    }

    [TestMethod]
    public async Task PersistenceDiagnostics_FileSinkDrainsTimeoutAndOrdinaryFailureWithoutSensitiveData()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var log = Path.Combine(sandbox, "settings.log");
            using var sink = new FileSettingsPersistenceDiagnosticSink(log);
            using var viewModel = new MainViewModel(
                new RuntimeControlClient(),
                new NoOpFileActions(),
                null,
                null,
                null,
                null,
                sink);
            await viewModel.ObserveClosePersistenceResultAsync(
                new SettingsPersistenceResult(false, true, new TimeoutException(@"secret \\private\scope")));
            await viewModel.ObserveClosePersistenceResultAsync(
                new SettingsPersistenceResult(false, false, new IOException(@"secret C:\private")));
            Assert.IsTrue(await viewModel.DrainSettingsDiagnosticsAsync(TimeSpan.FromSeconds(2)));
            var lines = await File.ReadAllLinesAsync(log);
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains(lines[0], "SETTINGS_PERSISTENCE_FAILED|TimeoutException");
            StringAssert.Contains(lines[1], "SETTINGS_PERSISTENCE_FAILED|IOException");
            Assert.IsFalse(lines.Any(line => line.Contains("secret", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(lines.Any(line => line.Contains("private", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(lines.Any(line => line.Contains(sandbox, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [TestMethod]
    public async Task TemporaryCleanup_SharingConflictIsTrackedAndLaterSweepRemovesFile()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-settings-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var temporary = Path.Combine(sandbox, "settings.json." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, "fixture");
            await using (var lease = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.IsFalse(await RetriableTemporaryFileCleanup.TryDeleteAsync(
                    temporary,
                    CancellationToken.None,
                    attempts: 2));
                Assert.IsTrue(File.Exists(temporary), "A failed cleanup was not left as an observable pending file.");
            }
            Assert.IsTrue(await RetriableTemporaryFileCleanup.TryDeleteAsync(temporary, CancellationToken.None));
            Assert.IsFalse(File.Exists(temporary));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow("Taskpane")]
    [DataRow("FolderMenu")]
    [DataRow("Startup")]
    public async Task IsolatedHost_UsesEachProductionRegistrationServiceThroughRegistryBoundary(
        string targetName)
    {
        var target = Enum.Parse<SettingsIntegrationTarget>(targetName);
        var registry = new RecordingRegistryAccess();
        var nonce = Guid.NewGuid().ToString("N");
        var result = await AppIsolatedOperationHost.ExecuteAsync(
            new IsolatedOperationRequest(
                nonce,
                "integration-set",
                Target: target.ToString(),
                Enabled: true),
            registry,
            Path.GetTempPath(),
            CancellationToken.None);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(registry.WriteCount > 0, $"{target} did not reach IRegistryAccess.");
    }

    [TestMethod]
    public async Task TogglePauseCommand_SlowSaveReturnsImmediatelyAndPersistsOffUiThread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var saveThread = -1;
        AppSettings? storeSnapshot = null;
        var store = new DelegateSettingsStore(settings =>
        {
            saveThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait();
            storeSnapshot = settings;
        });
        var runtime = new RuntimeControlClient();
        using var viewModel = new MainViewModel(
            runtime,
            new NoOpFileActions(),
            settingsStore: store,
            initialSettings: AppSettings.Default);
        var uiThread = Environment.CurrentManagedThreadId;

        var clock = Stopwatch.StartNew();
        viewModel.TogglePauseCommand.Execute(null);
        clock.Stop();

        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Toggle blocked for {clock.Elapsed}.");
        Assert.IsTrue(viewModel.IsBackgroundPaused);
        Assert.IsTrue(runtime.Paused);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(1)));
        Assert.AreNotEqual(uiThread, saveThread, "Settings save ran on the command/UI thread.");
        release.Set();
        await viewModel.SettingsPersistence.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(storeSnapshot!.BackgroundPaused);
    }

    [TestMethod]
    public async Task TogglePauseCommand_SaveExceptionIsNonFatalAndSanitized()
    {
        var runtime = new RuntimeControlClient();
        using var viewModel = new MainViewModel(
            runtime,
            new NoOpFileActions(),
            settingsStore: new DelegateSettingsStore(_ => throw new IOException("sensitive fixture path")),
            initialSettings: AppSettings.Default);

        viewModel.TogglePauseCommand.Execute(null);
        await viewModel.SettingsPersistence.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => viewModel.StatusText.Contains(nameof(IOException), StringComparison.Ordinal));

        Assert.IsTrue(viewModel.IsBackgroundPaused, "Persistence failure rolled back the in-memory command state.");
        Assert.IsFalse(viewModel.StatusText.Contains("sensitive fixture path", StringComparison.Ordinal));
        StringAssert.Contains(viewModel.StatusText, "状态已生效");
    }

    [TestMethod]
    public async Task TogglePauseCommand_RapidChangesCoalesceToLatestSnapshot()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var saves = new ConcurrentQueue<AppSettings>();
        var store = new DelegateSettingsStore(settings =>
        {
            saves.Enqueue(settings);
            if (saves.Count == 1)
            {
                entered.Set();
                release.Wait();
            }
        });
        var runtime = new RuntimeControlClient();
        using var viewModel = new MainViewModel(
            runtime,
            new NoOpFileActions(),
            settingsStore: store,
            initialSettings: AppSettings.Default);

        viewModel.TogglePauseCommand.Execute(null);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(1)));
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < 12; index++) viewModel.TogglePauseCommand.Execute(null);
        clock.Stop();
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"Rapid commands blocked for {clock.Elapsed}.");
        release.Set();
        await viewModel.SettingsPersistence.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(2, saves.Count, "Rapid command snapshots were queued instead of coalesced.");
        Assert.AreEqual(viewModel.IsBackgroundPaused, saves.Last().BackgroundPaused);
        Assert.AreEqual(13, runtime.SetCalls);
    }

    [TestMethod]
    public async Task TogglePauseCommand_DisposeCancelsPendingAndSuppressesLateStatusWriteBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(AppContext.BaseDirectory, "AppIsolationHelper", "SwSearch.Broker.exe");
        var helperBefore = CaptureFileIdentity(helper);
        var nonce = Guid.NewGuid().ToString("N");
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-toggle-close-" + nonce);
        Directory.CreateDirectory(sandbox);
        try
        {
            var executor = new BlockingOwnedHelperSaveExecutor(helper, sandbox, nonce);
            var store = new JsonAppSettingsStore(Path.Combine(sandbox, "settings.json"), executor);
            var viewModel = new MainViewModel(
                new RuntimeControlClient(),
                new NoOpFileActions(),
                settingsStore: store,
                initialSettings: AppSettings.Default);

            viewModel.TogglePauseCommand.Execute(null);
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.TogglePauseCommand.Execute(null);
            var statusAtClose = viewModel.StatusText;
            var completion = viewModel.SettingsPersistence.Completion;
            viewModel.Dispose();
            await completion.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(30);

            Assert.AreEqual(0, executor.ActiveCount);
            Assert.IsFalse(File.Exists(store.SettingsPath));
            Assert.AreEqual(0, Directory.GetFiles(sandbox, "*.tmp").Length);
            Assert.AreEqual(statusAtClose, viewModel.StatusText, "A late persistence completion wrote back after close.");
            Assert.AreEqual(helperBefore, CaptureFileIdentity(helper));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(10);
        Assert.IsTrue(condition(), "Expected asynchronous state was not observed before the deadline.");
    }

    private static FileIdentity CaptureFileIdentity(string path)
    {
        var item = new FileInfo(path);
        using var stream = File.OpenRead(path);
        return new FileIdentity(item.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private readonly record struct FileIdentity(long Length, string Sha256);

    private sealed class DelegateSettingsStore(Action<AppSettings> save) : IAppSettingsStore
    {
        public AppSettings Load() => AppSettings.Default;
        public void Save(AppSettings settings) => save(settings);
        public async ValueTask<IPreparedAppSettingsSave> PrepareSaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => Save(settings), CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return new DeferredAppSettingsSave(static () => { });
        }
    }

    private sealed class ControlledTransactionalStore(bool blockFirstPrepare) : IDisposable
    {
        private int _prepared;
        internal ManualResetEventSlim FirstPrepared { get; } = new();
        internal ManualResetEventSlim ReleaseFirstPrepare { get; } = new(initialState: !blockFirstPrepare);
        internal ConcurrentQueue<AppSettings> Committed { get; } = new();
        internal int CommitCount => Committed.Count;

        internal async ValueTask<IPreparedAppSettingsSave> PrepareSaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _prepared) == 1)
            {
                FirstPrepared.Set();
                await Task.Run(
                    () =>
                    {
                        var signaled = WaitHandle.WaitAny(
                            [ReleaseFirstPrepare.WaitHandle, cancellationToken.WaitHandle]);
                        if (signaled == 1) cancellationToken.ThrowIfCancellationRequested();
                    },
                    cancellationToken);
            }
            return new Prepared(this, settings);
        }

        public void Dispose()
        {
            ReleaseFirstPrepare.Set();
            FirstPrepared.Dispose();
            ReleaseFirstPrepare.Dispose();
        }

        private sealed class Prepared(ControlledTransactionalStore owner, AppSettings settings) : IPreparedAppSettingsSave
        {
            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.Committed.Enqueue(settings);
                return ValueTask.CompletedTask;
            }

            public void Dispose() { }
        }
    }

    private sealed class BlockingCancellableSaveExecutor : ISettingsSaveExecutor
    {
        private int _activeCount;
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ActiveCount => Volatile.Read(ref _activeCount);

        public async ValueTask ExecuteAsync(
            string settingsPath,
            string temporaryPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
                Interlocked.Increment(ref _activeCount);
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeCount);
                }
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class BlockingOwnedHelperSaveExecutor(
        string helperPath,
        string sandbox,
        string nonce) : ISettingsSaveExecutor
    {
        private readonly OwnedIsolatedProcessRunner _runner = new();
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ActiveCount => _runner.ActiveCount;

        public async ValueTask ExecuteAsync(
            string settingsPath,
            string temporaryPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            var ready = Path.Combine(sandbox, "ready-" + nonce + ".txt");
            var operation = _runner.RunAsync(
                helperPath,
                ["--app-settings-noncooperative", nonce, ready, temporaryPath],
                string.Empty,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            try
            {
                await WaitUntilAsync(() => File.Exists(ready));
                Entered.TrySetResult();
                await operation.ConfigureAwait(false);
            }
            finally
            {
                for (var attempt = 0; attempt < 20 && File.Exists(temporaryPath); attempt++)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (File.Exists(temporaryPath)) await Task.Delay(20).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class RecordingDiagnosticSink : ISettingsPersistenceDiagnosticSink
    {
        internal ConcurrentQueue<string> Records { get; } = new();

        public bool TryWrite(string code, string exceptionType)
        {
            Records.Enqueue(code + "|" + exceptionType);
            return true;
        }

        public Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public void Dispose() { }
    }

    private sealed class ThrowingSaveExecutor : ISettingsSaveExecutor
    {
        public async ValueTask ExecuteAsync(
            string settingsPath,
            string temporaryPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
                throw new IOException("fixture staging failure");
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
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

    private sealed class FirstCommitCancellableExecutor : ISettingsSaveExecutor
    {
        private readonly DirectFileSettingsSaveExecutor _inner = new();
        private int _calls;
        internal TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ExecuteAsync(
            string settingsPath,
            string temporaryPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }
            await _inner.ExecuteAsync(settingsPath, temporaryPath, content, cancellationToken);
        }
    }

    private sealed class RecordingRegistryAccess : IRegistryAccess
    {
        private int _writeCount;
        internal int WriteCount => Volatile.Read(ref _writeCount);

        public object? GetValue(RegistryHive hive, string keyPath, string? valueName) => null;
        public bool KeyExists(RegistryHive hive, string keyPath) => true;
        public void SetString(RegistryHive hive, string keyPath, string? valueName, string value) =>
            Interlocked.Increment(ref _writeCount);
        public void SetDword(RegistryHive hive, string keyPath, string? valueName, int value) =>
            Interlocked.Increment(ref _writeCount);
        public void DeleteTree(RegistryHive hive, string keyPath) => Interlocked.Increment(ref _writeCount);
        public void DeleteValue(RegistryHive hive, string keyPath, string? valueName) =>
            Interlocked.Increment(ref _writeCount);
    }

    private sealed class RuntimeControlClient : ISearchClient, IRuntimeControlClient
    {
        internal bool Paused { get; private set; }
        internal int SetCalls { get; private set; }

        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult([], new RuntimeCapability(CapabilityState.Available, "OK", "ok")));

        public ValueTask<RuntimeProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeProgressSnapshot(
                new MetadataProgressSnapshot(0, 0, 0, 0, 0), "fixture", "OK", Paused, 0, 0, null));

        public void SetBackgroundPaused(bool paused)
        {
            Paused = paused;
            SetCalls++;
        }
    }

    private sealed class NoOpFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
