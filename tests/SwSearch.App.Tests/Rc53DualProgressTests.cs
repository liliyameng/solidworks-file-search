using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc53DualProgressTests
{
    [TestMethod]
    public void CurrentExtractorRequeue_CanRetreatWithoutChangingCachedCoverage()
    {
        var before = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 10_000, cachedReadable: 8_000, terminal: 7_000));
        var after = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 10_000, cachedReadable: 8_000, terminal: 1_000));

        Assert.AreEqual(80d, before.CachedCoveragePercent);
        Assert.AreEqual(before.CachedCoveragePercent, after.CachedCoveragePercent);
        Assert.AreEqual(70d, before.CurrentMetadataPercent);
        Assert.AreEqual(10d, after.CurrentMetadataPercent);
    }

    [TestMethod]
    public void DiscoveredDenominatorGrowth_RecalculatesBothRatiosWithoutMonotonicClamping()
    {
        var before = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 100, cachedReadable: 80, terminal: 60));
        var after = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 200, cachedReadable: 80, terminal: 60));

        Assert.AreEqual(80d, before.CachedCoveragePercent);
        Assert.AreEqual(40d, after.CachedCoveragePercent);
        Assert.AreEqual(60d, before.CurrentMetadataPercent);
        Assert.AreEqual(30d, after.CurrentMetadataPercent);
    }

    [TestMethod]
    public async Task PublishedMetadataEpoch_AllowsOneDropThenOnlyMonotonicRecovery()
    {
        const string brokerSession = "broker-a";
        var snapshots = new[]
        {
            Snapshot(discovered: 100, cachedReadable: 100, terminal: 100, metadataProgressEpoch: 1, metadataProgressEpochSession: brokerSession),
            // A committed new work batch intentionally begins a new epoch.
            Snapshot(discovered: 100, cachedReadable: 90, terminal: 60, metadataProgressEpoch: 2, metadataProgressEpochSession: brokerSession),
            Snapshot(discovered: 100, cachedReadable: 90, terminal: 70, metadataProgressEpoch: 2, metadataProgressEpochSession: brokerSession),
            // Late watcher/USN work in the same epoch must not make the
            // visible attribute bar retreat again.
            Snapshot(discovered: 100, cachedReadable: 80, terminal: 65, metadataProgressEpoch: 2, metadataProgressEpochSession: brokerSession),
            Snapshot(discovered: 100, cachedReadable: 80, terminal: 80, metadataProgressEpoch: 2, metadataProgressEpochSession: brokerSession),
            // A later committed batch is a distinct, allowed retreat.
            Snapshot(discovered: 100, cachedReadable: 75, terminal: 50, metadataProgressEpoch: 3, metadataProgressEpochSession: brokerSession),
        };
        using var viewModel = new MainViewModel(
            new SequentialProgressClient(snapshots),
            new NoOpFileActions());

        var visible = new List<double?>();
        var cachedCoverage = new List<double?>();
        foreach (var _ in snapshots)
        {
            await viewModel.RefreshRuntimeStatusAsync();
            visible.Add(viewModel.IndexProgress.CurrentMetadataPercent);
            cachedCoverage.Add(viewModel.IndexProgress.CachedCoveragePercent);
        }

        CollectionAssert.AreEqual(new double?[] { 100d, 60d, 70d, 70d, 80d, 50d }, visible);
        // The epoch protection is deliberately scoped to attribute work; it
        // must not invent cache coverage that the durable snapshot did not have.
        CollectionAssert.AreEqual(new double?[] { 100d, 90d, 90d, 80d, 80d, 75d }, cachedCoverage);
    }

    [TestMethod]
    public async Task EachCommittedEpoch_AllowsAnotherDropBeforePriorBatchDrains_AndRejectsStaleResponses()
    {
        var snapshots = new[]
        {
            Snapshot(discovered: 100, cachedReadable: 100, terminal: 100, metadataProgressEpoch: 1, metadataProgressEpochSession: "broker-a"),
            Snapshot(discovered: 100, cachedReadable: 90, terminal: 70, metadataProgressEpoch: 2, metadataProgressEpochSession: "broker-a"),
            // A second completed discovery snapshot can publish while the
            // prior metadata batch is still draining. It owns a new allowed
            // retreat, rather than being hidden behind epoch 2's high-water.
            Snapshot(discovered: 100, cachedReadable: 80, terminal: 45, metadataProgressEpoch: 3, metadataProgressEpochSession: "broker-a"),
            Snapshot(discovered: 100, cachedReadable: 80, terminal: 55, metadataProgressEpoch: 3, metadataProgressEpochSession: "broker-a"),
            // A delayed epoch-2 RPC response must be rejected altogether.
            Snapshot(discovered: 100, cachedReadable: 90, terminal: 95, metadataProgressEpoch: 2, metadataProgressEpochSession: "broker-a"),
            // A broker restart establishes a new trusted session baseline.
            Snapshot(discovered: 100, cachedReadable: 70, terminal: 30, metadataProgressEpoch: 1, metadataProgressEpochSession: "broker-b"),
        };
        using var viewModel = new MainViewModel(
            new SequentialProgressClient(snapshots),
            new NoOpFileActions());

        var visible = new List<double?>();
        var cachedCoverage = new List<double?>();
        foreach (var _ in snapshots)
        {
            await viewModel.RefreshRuntimeStatusAsync();
            visible.Add(viewModel.IndexProgress.CurrentMetadataPercent);
            cachedCoverage.Add(viewModel.IndexProgress.CachedCoveragePercent);
        }

        CollectionAssert.AreEqual(new double?[] { 100d, 70d, 45d, 55d, 55d, 30d }, visible);
        CollectionAssert.AreEqual(new double?[] { 100d, 90d, 80d, 80d, 80d, 70d }, cachedCoverage);
    }

    [TestMethod]
    public void ZeroDiscoveredFiles_ProjectsUnknownRatiosWithoutNaNOrInventedCompletion()
    {
        var projected = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 0, cachedReadable: 0, terminal: 0));

        Assert.IsNull(projected.CachedCoveragePercent);
        Assert.IsNull(projected.CurrentMetadataPercent);
        StringAssert.Contains(projected.Label, "可读取缓存 --% · 属性处理 --%");
        Assert.IsFalse(projected.Label.Contains("NaN", StringComparison.Ordinal));
        Assert.IsFalse(projected.Tooltip.Contains("Infinity", StringComparison.Ordinal));
    }

    [TestMethod]
    public void KnownFilesWithZeroCounts_ProjectRealZeroWhileUnknownCacheStaysUnknown()
    {
        var knownZero = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 100, cachedReadable: 0, terminal: 0));
        var unknownCache = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 100, cachedReadable: null, terminal: 50));

        Assert.AreEqual(0d, knownZero.CachedCoveragePercent);
        Assert.AreEqual(0d, knownZero.CurrentMetadataPercent);
        StringAssert.Contains(knownZero.Label, "可读取缓存 0.00% · 属性处理 0.00%");
        Assert.IsNull(unknownCache.CachedCoveragePercent);
        Assert.AreEqual(50d, unknownCache.CurrentMetadataPercent);
        StringAssert.Contains(unknownCache.Label, "可读取缓存 --% · 属性处理 50.00%");
    }

    [TestMethod]
    public void LabelAndTooltip_AreTheSameStrictThreeMetricText()
    {
        var projected = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 147_782, cachedReadable: 9_573, terminal: 293));

        Assert.AreEqual(
            "文件 100.00% · 可读取缓存 6.48% · 属性处理 0.20%",
            projected.Label);
        Assert.AreEqual(projected.Label, projected.Tooltip);
        Assert.IsFalse(projected.Tooltip.Contains('\n'));
        Assert.IsFalse(projected.Tooltip.Contains("GB", StringComparison.Ordinal));
        Assert.AreEqual("active", projected.StatusKind);
    }

    [TestMethod]
    public void TerminalSkippedFiles_AreCountedAsDurableSearchableCacheRows()
    {
        var projected = MainViewModel.CreateIndexProgress(
            Snapshot(discovered: 100, cachedReadable: 100, terminal: 99, skipped: 1));

        Assert.AreEqual(100d, projected.CachedCoveragePercent);
        Assert.AreEqual(100d, projected.CurrentMetadataPercent);
        StringAssert.Contains(projected.Label, "可读取缓存 100.00%");
        StringAssert.Contains(projected.Label, "属性处理 100.00%");
    }

    [TestMethod]
    public void DiscoveryStatusAndSummary_AreConciseChineseInsteadOfRawProviderText()
    {
        var message = MainViewModel.NormalizeCapabilityMessage(
            new RuntimeCapability(
                CapabilityState.Degraded,
                "DISCOVERY_PARTIAL",
                "Some discovery subtrees could not be enumerated"));
        var summary = MainViewModel.CreateRuntimeSummary(
            Snapshot(discovered: 10, cachedReadable: 10, terminal: 10));

        Assert.AreEqual("部分文件夹暂时无法读取，已保留原有索引，其余文件夹已更新。", message);
        StringAssert.StartsWith(summary, "快速索引 ·");
        Assert.IsFalse(summary.Contains("MFT", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DesktopBridge_UsesExplicitDualProgressDto()
    {
        using var viewModel = new MainViewModel(
            new ProgressClient(Snapshot(discovered: 100, cachedReadable: 80, terminal: 10)),
            new NoOpFileActions());
        await viewModel.RefreshRuntimeStatusAsync();

        using var bridge = new DesktopWebBridge(
            viewModel,
            new UnavailableUserPreferencesRepository("test"));
        object? state = null;
        bridge.StateReady += payload => state = payload;
        bridge.HandleMessage("ready", JsonSerializer.SerializeToElement(new { view = "main" }));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(state));
        var progress = document.RootElement.GetProperty("indexProgress");
        Assert.AreEqual(80d, progress.GetProperty("cachedCoveragePercent").GetDouble());
        Assert.AreEqual(10d, progress.GetProperty("currentMetadataPercent").GetDouble());
        Assert.IsFalse(progress.TryGetProperty("metadataPercent", out _));
        Assert.AreEqual(100d, progress.GetProperty("filePercent").GetDouble());
        Assert.IsFalse(progress.TryGetProperty("label", out _));
        Assert.IsFalse(progress.TryGetProperty("tooltip", out _));
        Assert.IsFalse(progress.TryGetProperty("metadataStalled", out _));
        Assert.IsFalse(progress.TryGetProperty("fileState", out _));
        Assert.IsFalse(progress.TryGetProperty("metadataState", out _));
        Assert.IsFalse(progress.TryGetProperty("statusKind", out _));
        var propertyCount = 0;
        foreach (var property in progress.EnumerateObject())
        {
            Assert.IsTrue(
                property.Name is "filePercent" or "cachedCoveragePercent" or "currentMetadataPercent",
                $"Unexpected visible progress bridge field: {property.Name}");
            propertyCount++;
        }
        Assert.AreEqual(3, propertyCount);
    }

    [TestMethod]
    public void WebProjection_UpdatesThreeFillsWithoutRefreshingTheTable()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        const string startToken = "function updateIndexProgress()";
        const string endToken = "function defaultColumns()";
        var start = script.IndexOf(startToken, StringComparison.Ordinal);
        var end = script.IndexOf(endToken, start, StringComparison.Ordinal);

        Assert.IsTrue(start >= 0 && end > start);
        var body = script[start..end];
        StringAssert.Contains(body, "progress.cachedCoveragePercent");
        StringAssert.Contains(body, "progress.currentMetadataPercent");
        StringAssert.Contains(body, "cachedCoverageProgressFill");
        StringAssert.Contains(body, "currentMetadataProgressFill");
        Assert.IsFalse(body.Contains("progress.metadataPercent", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("renderGrid", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("renderViewportRows", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("innerHTML", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebProgressLabel_UsesOnlyThreeTwoDecimalMetricsAndTransparentContrastLayers()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.WebUi", "Assets", "webui", "app.css"));
        const string startToken = "function updateIndexProgress()";
        const string endToken = "function defaultColumns()";
        var start = script.IndexOf(startToken, StringComparison.Ordinal);
        var end = script.IndexOf(endToken, start, StringComparison.Ordinal);

        Assert.IsTrue(start >= 0 && end > start);
        var body = script[start..end];
        StringAssert.Contains(script, "const formatProgressPercent = value => value == null ? \"--%\" : `${value.toFixed(2)}%`;");
        // RC93 i18n: the label is composed from the localized index.label template.
        StringAssert.Contains(body, "window.SW_FORMAT(t(\"index.label\")");
        StringAssert.Contains(body, "formatProgressPercent(filePercent)");
        StringAssert.Contains(body, "formatProgressPercent(cachedCoveragePercent)");
        StringAssert.Contains(body, "formatProgressPercent(currentMetadataPercent)");
        StringAssert.Contains(body, "--index-progress-dark-end");
        Assert.IsFalse(body.Contains("progress.label", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("progress.tooltip", StringComparison.Ordinal));
        StringAssert.Contains(script, "id=\"indexProgressLabelOnDark\"");
        StringAssert.Contains(css, ".index-progress-label.on-dark");
        StringAssert.Contains(css, "clip-path: inset(0 calc(100% - var(--index-progress-dark-end)) 0 0)");

        const string labelRuleStart = ".index-progress-label {";
        var labelStart = css.IndexOf(labelRuleStart, StringComparison.Ordinal);
        var labelEnd = css.IndexOf('}', labelStart);
        Assert.IsTrue(labelStart >= 0 && labelEnd > labelStart);
        var labelRule = css[labelStart..labelEnd];
        StringAssert.Contains(labelRule, "color: #102a43");
        StringAssert.Contains(labelRule, "background: transparent");
        StringAssert.Contains(labelRule, "border: 0");
        Assert.IsFalse(labelRule.Contains("background: #fff", StringComparison.Ordinal));
    }

    private static RuntimeProgressSnapshot Snapshot(
        long discovered,
        long? cachedReadable,
        long terminal,
        long skipped = 0,
        long metadataProgressEpoch = 0,
        string? metadataProgressEpochSession = null)
    {
        var pending = Math.Max(0, discovered - terminal - skipped);
        return new RuntimeProgressSnapshot(
            Metadata: new MetadataProgressSnapshot(
                discovered,
                terminal,
                0,
                pending,
                0,
                CachedReadableFileCount: cachedReadable,
                SkippedCount: skipped),
            DiscoverySource: "MFT",
            DiscoveryCode: "READY",
            IsPaused: false,
            MaximumFilesPerSecond: 0,
            MaximumReadBytesPerSecond: 0,
            EstimatedRemaining: null,
            Discovery: new FileDiscoveryProgressSnapshot(
                Math.Max(0, discovered),
                Math.Max(0, discovered),
                RuntimeServiceState.Idle,
                DateTimeOffset.UtcNow,
                "MFT",
                "READY"),
            MetadataService: new MetadataServiceProgressSnapshot(
                terminal,
                0,
                pending,
                0,
                RuntimeServiceState.Running,
                DateTimeOffset.UtcNow,
                "METADATA_RUNNING"),
            MetadataProgressEpoch: metadataProgressEpoch,
            MetadataProgressEpochSession: metadataProgressEpochSession);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
               throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ProgressClient(RuntimeProgressSnapshot progress)
        : ISearchClient, IRuntimeControlClient
    {
        public ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                Array.Empty<SearchHit>(),
                new RuntimeCapability(CapabilityState.Available, "TEST", "test")));

        public ValueTask<RuntimeProgressSnapshot> GetProgressAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(progress);

        public void SetBackgroundPaused(bool paused)
        {
        }
    }

    private sealed class SequentialProgressClient(IEnumerable<RuntimeProgressSnapshot> snapshots)
        : ISearchClient, IRuntimeControlClient
    {
        private readonly Queue<RuntimeProgressSnapshot> _snapshots = new(snapshots);
        private RuntimeProgressSnapshot? _last;

        public ValueTask<SearchClientResult> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SearchClientResult(
                Array.Empty<SearchHit>(),
                new RuntimeCapability(CapabilityState.Available, "TEST", "test")));

        public ValueTask<RuntimeProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken)
        {
            if (_snapshots.Count > 0)
            {
                _last = _snapshots.Dequeue();
            }

            return ValueTask.FromResult(_last ?? throw new InvalidOperationException("No progress snapshot."));
        }

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
