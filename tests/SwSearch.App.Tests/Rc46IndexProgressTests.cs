using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using System.Text.Json;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc46IndexProgressTests
{
    [TestMethod]
    public void KnownTotals_ProjectBoundedCompositePercentages()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                750, 1_000, RuntimeServiceState.Running, DateTimeOffset.UtcNow,
                "Everything", "DISCOVERY_RUNNING"),
            new MetadataServiceProgressSnapshot(
                400, 100, 400, 100, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"));

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(75d, projected.FilePercent);
        Assert.IsNull(projected.CachedCoveragePercent);
        Assert.AreEqual(60d, projected.CurrentMetadataPercent);
        Assert.AreEqual(
            "文件 75.00% · 可读取缓存 --% · 属性处理 60.00%",
            projected.Label);
        Assert.IsFalse(projected.IsMetadataServiceStalled);
    }

    [TestMethod]
    public void UnknownDiscoveryTotal_DoesNotInventAPercentage()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                42_000, null, RuntimeServiceState.Running, DateTimeOffset.UtcNow,
                "MFT", "TOTAL_UNKNOWN"),
            new MetadataServiceProgressSnapshot(
                10_000, 0, 32_000, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"));

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.IsNull(projected.FilePercent);
        StringAssert.StartsWith(projected.Label, "文件 --% · 可读取缓存 --% · 属性处理 ");
    }

    [TestMethod]
    public void MetadataPercent_UsesMetadataDiscoveredCount_NotMftRecordCount_WhenComplete()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                2_000_000, 2_000_000, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "MFT", "READY"),
            new MetadataServiceProgressSnapshot(
                147_000, 0, 0, 0, RuntimeServiceState.Idle,
                DateTimeOffset.UtcNow, "READY"),
            metadataDiscoveredFileCount: 147_000);

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(100d, projected.FilePercent);
        Assert.AreEqual(100d, projected.CurrentMetadataPercent);
        Assert.AreEqual(
            "文件 100.00% · 可读取缓存 --% · 属性处理 100.00%",
            projected.Label);
    }

    [TestMethod]
    public void MetadataPercent_UsesMetadataQueueDenominator_NotMftRecordCount_WhenHalfComplete()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                2_000_000, 2_000_000, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "MFT", "READY"),
            new MetadataServiceProgressSnapshot(
                73_500, 0, 73_500, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 147_000);

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(100d, projected.FilePercent);
        Assert.AreEqual(50d, projected.CurrentMetadataPercent);
        Assert.AreEqual(
            "文件 100.00% · 可读取缓存 --% · 属性处理 50.00%",
            projected.Label);
    }

    [TestMethod]
    public void MetadataPercent_UsesOnlyCurrentExtractorTerminalCounts_NotHistoricalReadableCache()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                2_000_000, 2_000_000, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "MFT", "READY"),
            new MetadataServiceProgressSnapshot(
                290, 3, 147_489, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 147_782,
            cachedReadableFileCount: 9_573);

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(100d, projected.FilePercent);
        Assert.AreEqual(6.48d, projected.CachedCoveragePercent);
        Assert.AreEqual(0.2d, projected.CurrentMetadataPercent);
        Assert.AreEqual(
            "文件 100.00% · 可读取缓存 6.48% · 属性处理 0.20%",
            projected.Label);
    }

    [TestMethod]
    public void HistoricalReadableCacheChanges_DoNotMoveCurrentExtractorProgress()
    {
        static RuntimeProgressSnapshot At(long cachedReadable) => Snapshot(
            new FileDiscoveryProgressSnapshot(
                2_000_000, 2_000_000, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "MFT", "READY"),
            new MetadataServiceProgressSnapshot(
                290, 3, 147_489, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 147_782,
            cachedReadableFileCount: cachedReadable);

        var before = MainViewModel.CreateIndexProgress(At(9_573));
        var after = MainViewModel.CreateIndexProgress(At(11_084));

        Assert.AreEqual(0.2d, before.CurrentMetadataPercent);
        Assert.AreEqual(before.CurrentMetadataPercent, after.CurrentMetadataPercent);
        Assert.AreEqual(6.48d, before.CachedCoveragePercent);
        Assert.AreEqual(7.5d, after.CachedCoveragePercent);
    }

    [TestMethod]
    public void CommittedCacheWithUnknownDiscoveryTotal_UsesPresentFilesAsCompletedBaseline()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                147_779, null, RuntimeServiceState.Running, DateTimeOffset.UtcNow,
                "MFT", "INCREMENTAL_RUNNING"),
            new MetadataServiceProgressSnapshot(
                9_602, 0, 138_177, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 147_779,
            cachedReadableFileCount: 9_602);

        var projected = MainViewModel.CreateIndexProgress(progress, hasCommittedFileIndex: true);

        Assert.AreEqual(100d, projected.FilePercent);
        Assert.AreEqual(
            "文件 100.00% · 可读取缓存 6.50% · 属性处理 6.50%",
            projected.Label);
    }

    [TestMethod]
    public void StalePersistedDiscovery_DoesNotPresentHistoricalCacheAsCompleteFileIndex()
    {
        // A failed first full discovery may leave historical cache rows in the
        // database. They remain searchable, but generation zero/no successful
        // snapshot must not be rendered as a finished file index.
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                60_000, 60_000, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "FallbackDiscoveryProvider", "DISCOVERY_QUERY_FAILED",
                DiscoveryGeneration: 0,
                LastSuccessfulDiscoveryUtc: null,
                SourceState: "stale"),
            new MetadataServiceProgressSnapshot(
                1_940, 0, 0, 0, RuntimeServiceState.Idle,
                DateTimeOffset.UtcNow, "READY"),
            metadataDiscoveredFileCount: 1_940,
            cachedReadableFileCount: 1_940);

        var projected = MainViewModel.CreateIndexProgress(progress, hasCommittedFileIndex: true);

        Assert.IsNull(projected.FilePercent);
        StringAssert.Contains(projected.Label, "--%");
    }

    [TestMethod]
    public void FirstEverythingSnapshot_IdleBeforeCommit_DoesNotDropToBlankPercentage()
    {
        // Everything has finished enumerating a coherent snapshot, while the
        // store has not yet published generation 1. Generation zero alone is
        // not durable state and must not turn the finished count into "--%".
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                138_251, 138_251, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "Everything", "EVERYTHING_SNAPSHOT_COMPLETE",
                DiscoveryGeneration: 0,
                LastSuccessfulDiscoveryUtc: null,
                SourceState: null),
            new MetadataServiceProgressSnapshot(
                0, 0, 0, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 0);

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(100d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "文件 100.00%");
    }

    [TestMethod]
    public void PartialPersistedDiscovery_ShowsCredibleLiveProviderPercentage()
    {
        // Provider enumeration is not durable completion yet, but its bounded
        // live estimate is the only truthful progress while discovery runs.
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                75_000, 100_000, RuntimeServiceState.Running, DateTimeOffset.UtcNow,
                "Everything", "DISCOVERY_PARTIAL",
                DiscoveryGeneration: 9,
                LastSuccessfulDiscoveryUtc: DateTimeOffset.UtcNow.AddHours(-1),
                SourceState: "partial"),
            new MetadataServiceProgressSnapshot(
                10, 0, 90, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 100);

        var projected = MainViewModel.CreateIndexProgress(progress, hasCommittedFileIndex: true);

        Assert.AreEqual(75d, projected.FilePercent);
        Assert.AreEqual(10d, projected.CurrentMetadataPercent);
    }

    [TestMethod]
    public void StalePersistedDiscovery_WithLiveRunningEstimate_ShowsProgressWithoutClaimingCompletion()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                25_000, 100_000, RuntimeServiceState.Running, DateTimeOffset.UtcNow,
                "FallbackDiscoveryProvider", "DISCOVERY_RUNNING",
                DiscoveryGeneration: 0,
                LastSuccessfulDiscoveryUtc: null,
                SourceState: "stale"),
            new MetadataServiceProgressSnapshot(
                100, 0, 900, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 1_000);

        var projected = MainViewModel.CreateIndexProgress(progress, hasCommittedFileIndex: true);

        Assert.AreEqual(25d, projected.FilePercent);
        Assert.AreNotEqual(100d, projected.FilePercent);
    }

    [DataTestMethod]
    [DataRow(RuntimeServiceState.Running)]
    [DataRow(RuntimeServiceState.Paused)]
    [DataRow(RuntimeServiceState.Stalled)]
    public void UncommittedLiveDiscovery_AtCurrentEstimate_RemainsBelowComplete(
        RuntimeServiceState state)
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                100, 100, state, DateTimeOffset.UtcNow,
                "FallbackDiscoveryProvider", "DISCOVERY_PARTIAL",
                DiscoveryGeneration: 0,
                LastSuccessfulDiscoveryUtc: null,
                SourceState: "partial"),
            new MetadataServiceProgressSnapshot(
                10, 0, 90, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_RUNNING"),
            metadataDiscoveredFileCount: 100);

        var projected = MainViewModel.CreateIndexProgress(progress, hasCommittedFileIndex: true);

        Assert.AreEqual(99.99d, projected.FilePercent);
    }

    [TestMethod]
    public void ReadyPersistedDiscovery_PreservesCompletedFileIndexPercentage()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                100, 100, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "Everything", "READY",
                DiscoveryGeneration: 1,
                LastSuccessfulDiscoveryUtc: DateTimeOffset.UtcNow,
                SourceState: "ready"),
            new MetadataServiceProgressSnapshot(
                100, 0, 0, 0, RuntimeServiceState.Idle,
                DateTimeOffset.UtcNow, "READY"),
            metadataDiscoveredFileCount: 100,
            cachedReadableFileCount: 100);

        var projected = MainViewModel.CreateIndexProgress(progress, hasCommittedFileIndex: true);

        Assert.AreEqual(100d, projected.FilePercent);
        Assert.AreEqual(100d, projected.CachedCoveragePercent);
        Assert.AreEqual(100d, projected.CurrentMetadataPercent);
    }

    [TestMethod]
    public void StalledMetadata_UsesRequiredVisibleDiagnostic()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                100, 100, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "cache", "READY"),
            new MetadataServiceProgressSnapshot(
                25, 0, 75, 0, RuntimeServiceState.Stalled,
                DateTimeOffset.UtcNow.AddSeconds(-30), "HEARTBEAT_STALE"));

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.IsTrue(projected.IsMetadataServiceStalled);
        Assert.AreEqual("文件 100.00% · 可读取缓存 --% · 属性处理 25.00%", projected.Label);
    }

    [TestMethod]
    public void WorkerRecoveryCode_ReportsAutomaticRecoveryWhileServiceRemainsRunning()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                100, 100, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "cache", "READY"),
            new MetadataServiceProgressSnapshot(
                25, 0, 75, 0, RuntimeServiceState.Running,
                DateTimeOffset.UtcNow, "METADATA_WORKER_RECOVERING"));

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.IsTrue(projected.IsMetadataServiceStalled);
        Assert.AreEqual("文件 100.00% · 可读取缓存 --% · 属性处理 25.00%", projected.Label);
        Assert.AreEqual(RuntimeServiceState.Running.ToString(), projected.MetadataState);
    }

    [TestMethod]
    public void UnavailableMetadataHost_UsesHostStartupDiagnosticOnly()
    {
        var progress = Snapshot(
            new FileDiscoveryProgressSnapshot(
                100, 100, RuntimeServiceState.Idle, DateTimeOffset.UtcNow,
                "cache", "READY"),
            new MetadataServiceProgressSnapshot(
                25, 0, 75, 0, RuntimeServiceState.Unavailable,
                DateTimeOffset.UtcNow, "METADATA_WORKER_UNAVAILABLE"));

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.IsTrue(projected.IsMetadataServiceStalled);
        Assert.AreEqual("文件 100.00% · 可读取缓存 --% · 属性处理 25.00%", projected.Label);
    }

    [TestMethod]
    public void MainWebUi_HasAccessibleMainOnlyCompositeProgressAndOneSecondPoll()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));
        var window = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "MainWindow.xaml.cs"));

        StringAssert.Contains(script, "id=\"indexProgress\"");
        StringAssert.Contains(script, "role=\"progressbar\"");
        StringAssert.Contains(script, "aria-valuetext");
        StringAssert.Contains(script, "view === \"taskpane\" ? \"\"");
        StringAssert.Contains(css, ".index-progress-fill.file");
        StringAssert.Contains(css, ".index-progress-fill.cached-coverage");
        StringAssert.Contains(css, ".index-progress-fill.current-metadata");
        StringAssert.Contains(window, "Interval = TimeSpan.FromSeconds(2)");
        StringAssert.Contains(window, "await RefreshRuntimeStatusAsync();");
    }

    [TestMethod]
    public async Task ProgressOnlyPublish_DoesNotRetransmitResultDtos()
    {
        var client = new ProgressClient(Snapshot(
            new FileDiscoveryProgressSnapshot(
                1, 2, RuntimeServiceState.Running, DateTimeOffset.UtcNow, "MFT", "RUNNING"),
            new MetadataServiceProgressSnapshot(
                0, 0, 1, 0, RuntimeServiceState.Running, DateTimeOffset.UtcNow, "RUNNING")));
        using var viewModel = new MainViewModel(client, new NoOpFileActions());
        viewModel.Results.ReplaceAll([
            new SearchResultItem(new SearchHit(
                @"C:\Design\Part.SLDPRT", SolidWorksDocumentKind.Part, "Part.SLDPRT",
                null, null, null, 1, false)),
        ]);
        using var bridge = new DesktopWebBridge(
            viewModel,
            new UnavailableUserPreferencesRepository("test"));
        object? lastState = null;
        bridge.StateReady += state => lastState = state;
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "main" }));
        Assert.AreEqual(1L, bridge.PublishedResultDtoCount);

        await viewModel.RefreshRuntimeStatusAsync();
        bridge.PublishState();

        Assert.AreEqual(1L, bridge.PublishedResultDtoCount);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(lastState));
        Assert.AreEqual("none", document.RootElement.GetProperty("resultsMode").GetString());
        Assert.AreEqual(0, document.RootElement.GetProperty("results").GetArrayLength());

        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        StringAssert.Contains(
            script,
            "if (view === \"main\" && payload.resultsMode === \"none\") updateSearchStatusState();");
        StringAssert.Contains(script, "function updateSearchStatusState()");
    }

    private static RuntimeProgressSnapshot Snapshot(
        FileDiscoveryProgressSnapshot discovery,
        MetadataServiceProgressSnapshot metadata,
        long? metadataDiscoveredFileCount = null,
        long? cachedReadableFileCount = null) => new(
        new MetadataProgressSnapshot(
            metadataDiscoveredFileCount ??
            metadata.CompletedCount + metadata.PartialCount + metadata.PendingCount + metadata.FailedCount,
            metadata.CompletedCount,
            metadata.PartialCount,
            metadata.PendingCount,
            metadata.FailedCount,
            CachedReadableFileCount: cachedReadableFileCount),
        discovery.Source,
        discovery.Code,
        false,
        0,
        0,
        null,
        Discovery: discovery,
        MetadataService: metadata);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ProgressClient(RuntimeProgressSnapshot progress) : ISearchClient, IRuntimeControlClient
    {
        public ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                Array.Empty<SearchHit>(),
                new RuntimeCapability(CapabilityState.Available, "TEST", "test")));

        public ValueTask<RuntimeProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(progress);

        public void SetBackgroundPaused(bool paused)
        {
        }
    }

    private sealed class NoOpFileActions : IFileActions
    {
        public void Open(string fullPath) { }
        public void Reveal(string fullPath) { }
        public void CopyPath(string fullPath) { }
        public void ViewProperties(string fullPath) { }
    }
}
