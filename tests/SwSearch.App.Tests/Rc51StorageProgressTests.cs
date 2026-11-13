using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc51StorageProgressTests
{
    [TestMethod]
    public void IdleMetadataWithPendingWork_NeverProjectsComplete()
    {
        var progress = Snapshot(
            completed: 100,
            pending: 1,
            metadataState: RuntimeServiceState.Idle,
            metadataCode: "METADATA_COMPLETE");

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(99.01d, projected.CurrentMetadataPercent);
        Assert.AreEqual(
            "文件 100.00% · 可读取缓存 --% · 属性处理 99.01%",
            projected.Label);
        Assert.AreEqual("pending", projected.StatusKind);
        Assert.IsFalse(projected.Label.Contains("已完成", StringComparison.Ordinal));
        Assert.IsFalse(projected.Label.Contains("未运行", StringComparison.Ordinal));
        var summary = MainViewModel.CreateRuntimeSummary(progress, projected);
        StringAssert.EndsWith(summary, "属性等待更新");
        Assert.IsFalse(summary.EndsWith("已完成", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HardLogicalLimitWithoutActiveMaintenance_ProjectsPausedCapacityStatus()
    {
        var progress = Snapshot(
            completed: 44_444,
            pending: 100_556,
            metadataState: RuntimeServiceState.Idle,
            metadataCode: "METADATA_COMPLETE",
            metadataWritesPaused: true,
            storagePauseReason: "DATABASE_HARD_LIMIT",
            storageMaintenancePhase: "idle");

        var projected = MainViewModel.CreateIndexProgress(progress);

        StringAssert.Contains(projected.Label, "文件 100.00% · 可读取缓存 --% · 属性处理 30.65%");
        Assert.IsFalse(projected.Label.Contains("索引空间不足", StringComparison.Ordinal));
        Assert.AreEqual("storage-blocked", projected.StatusKind);
        Assert.IsTrue(projected.IsMetadataServiceStalled);
        Assert.IsFalse(projected.Label.Contains("已完成", StringComparison.Ordinal));
        Assert.AreEqual(30.65d, projected.CurrentMetadataPercent);
    }

    [TestMethod]
    public void ActiveStorageMaintenance_HasDistinctAutomaticallyContinuingStatus()
    {
        var progress = Snapshot(
            completed: 44_444,
            pending: 100_556,
            metadataWritesPaused: true,
            storagePauseReason: "DATABASE_HARD_LIMIT",
            storageMaintenancePhase: "orphan-prune");

        var projected = MainViewModel.CreateIndexProgress(progress);

        StringAssert.Contains(projected.Label, "文件 100.00% · 可读取缓存 --% · 属性处理 30.65%");
        Assert.IsFalse(projected.Label.Contains("正在整理索引空间", StringComparison.Ordinal));
        Assert.AreEqual("storage-maintenance", projected.StatusKind);
        Assert.IsFalse(projected.IsMetadataServiceStalled);
        Assert.IsFalse(projected.Label.Contains("属性已完成", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailedStorageRecovery_ProjectsRetryingMaintenanceStatus()
    {
        var progress = Snapshot(
            completed: 44_444,
            pending: 100_556,
            metadataWritesPaused: true,
            storagePauseReason: "DATABASE_HARD_LIMIT",
            storageMaintenancePhase: "recovery-failed");

        var projected = MainViewModel.CreateIndexProgress(progress);

        StringAssert.Contains(projected.Label, "文件 100.00% · 可读取缓存 --% · 属性处理 30.65%");
        Assert.IsFalse(projected.Label.Contains("索引维护暂时失败", StringComparison.Ordinal));
        Assert.AreEqual("storage-maintenance", projected.StatusKind);
        Assert.IsFalse(projected.IsMetadataServiceStalled);
    }

    [TestMethod]
    public void LogicalStorageBlockedMetadataCode_AloneDoesNotClaimCapacityExhaustion()
    {
        var progress = Snapshot(
            completed: 44_444,
            pending: 100_556,
            metadataState: RuntimeServiceState.Unavailable,
            metadataCode: "METADATA_STORAGE_BLOCKED");

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.IsFalse(projected.Label.Contains("索引空间不足", StringComparison.Ordinal));
        StringAssert.Contains(projected.Label, "文件 100.00% · 可读取缓存 --% · 属性处理 30.65%");
        Assert.AreEqual("metadata-unavailable", projected.StatusKind);
    }

    [TestMethod]
    public void WalPressure_ProjectsCacheMaintenanceInsteadOfServiceFailure()
    {
        var progress = Snapshot(
            completed: 60,
            pending: 40,
            metadataWritesPaused: true,
            storagePauseReason: "WAL_PRESSURE",
            storageMaintenancePhase: "checkpoint");

        var projected = MainViewModel.CreateIndexProgress(progress);

        StringAssert.Contains(projected.Label, "文件 100.00% · 可读取缓存 --% · 属性处理 60.00%");
        Assert.IsFalse(projected.Label.Contains("正在整理索引缓存", StringComparison.Ordinal));
        Assert.AreEqual("storage-maintenance", projected.StatusKind);
        Assert.IsFalse(projected.IsMetadataServiceStalled);
        Assert.IsFalse(projected.Label.Contains("未运行", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProgressTooltip_UsesOnlyTheCanonicalThreeMetricText()
    {
        var progress = Snapshot(
            completed: 60,
            pending: 40,
            databaseBytes: 2L * 1024 * 1024 * 1024,
            logicalDatabaseBytes: 1536L * 1024 * 1024,
            reusableDatabaseBytes: 512L * 1024 * 1024);

        var projected = MainViewModel.CreateIndexProgress(progress);

        Assert.AreEqual(projected.Label, projected.Tooltip);
        Assert.AreEqual("文件 100.00% · 可读取缓存 --% · 属性处理 60.00%", projected.Tooltip);
        Assert.IsFalse(projected.Tooltip.Contains("GB", StringComparison.Ordinal));
        Assert.IsFalse(projected.Tooltip.Contains("MB", StringComparison.Ordinal));
        Assert.IsFalse(projected.Tooltip.Contains("索引", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeSummary_UsesProjectedStorageStatusWithoutRawReasonCode()
    {
        var progress = Snapshot(
            completed: 44_444,
            pending: 100_556,
            metadataWritesPaused: true,
            storagePauseReason: "DATABASE_HARD_LIMIT",
            storageMaintenancePhase: "idle");

        var summary = MainViewModel.CreateRuntimeSummary(progress);

        StringAssert.EndsWith(summary, "索引空间不足，属性更新已暂停");
        StringAssert.Contains(summary, "待处理 100,556");
        Assert.IsFalse(summary.Contains("DATABASE_HARD_LIMIT", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("运行中", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProgressOnlyWebProjection_UsesCanonicalProgressTextAndClassesWithoutGridRedraw()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.WebUi", "Assets", "webui", "app.js"));
        const string startToken = "function updateIndexProgress()";
        const string endToken = "function defaultColumns()";
        var start = script.IndexOf(startToken, StringComparison.Ordinal);
        var end = script.IndexOf(endToken, start, StringComparison.Ordinal);

        Assert.IsTrue(start >= 0 && end > start);
        var body = script[start..end];
        StringAssert.Contains(body, "root.title = progressLabel");
        Assert.IsFalse(body.Contains("progress.label", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("progress.tooltip", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("progress.statusKind", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("progress.metadataStalled", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("renderGrid", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("renderViewportRows", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("innerHTML", StringComparison.Ordinal));
    }

    private static RuntimeProgressSnapshot Snapshot(
        long completed,
        long pending,
        RuntimeServiceState metadataState = RuntimeServiceState.Running,
        string metadataCode = "METADATA_RUNNING",
        long? metadataDiscoveredFileCount = null,
        bool metadataWritesPaused = false,
        string? storagePauseReason = null,
        long databaseBytes = 0,
        long? logicalDatabaseBytes = null,
        long? reusableDatabaseBytes = null,
        string? storageMaintenancePhase = null)
    {
        var metadata = new MetadataServiceProgressSnapshot(
            completed,
            0,
            pending,
            0,
            metadataState,
            DateTimeOffset.UtcNow,
            metadataCode);
        return new RuntimeProgressSnapshot(
            Metadata: new MetadataProgressSnapshot(
                metadataDiscoveredFileCount ?? completed + pending,
                completed,
                0,
                pending,
                0),
            DiscoverySource: "MFT",
            DiscoveryCode: "READY",
            IsPaused: false,
            MaximumFilesPerSecond: 0,
            MaximumReadBytesPerSecond: 0,
            EstimatedRemaining: null,
            MetadataWritesPaused: metadataWritesPaused,
            StoragePauseReason: storagePauseReason,
            WalBytes: 0,
            Discovery: new FileDiscoveryProgressSnapshot(
                145_000,
                EstimatedTotalCount: 145_000,
                RuntimeServiceState.Idle,
                DateTimeOffset.UtcNow,
                "MFT",
                "READY"),
            MetadataService: metadata,
            DatabaseBytes: databaseBytes,
            LogicalDatabaseBytes: logicalDatabaseBytes,
            ReusableDatabaseBytes: reusableDatabaseBytes,
            StorageMaintenancePhase: storageMaintenancePhase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
