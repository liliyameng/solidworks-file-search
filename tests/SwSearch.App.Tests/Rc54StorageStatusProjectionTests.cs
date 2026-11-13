using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc54StorageStatusProjectionTests
{
    [TestMethod]
    public void CapacityReasonWithoutPausedWrites_DoesNotFlashStorageBlocked()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            metadataWritesPaused: false,
            storagePauseReason: "DATABASE_PHYSICAL_SAFETY_LIMIT",
            storageMaintenancePhase: "idle"));

        Assert.AreEqual("active", projected.StatusKind);
        Assert.AreEqual("文件 100.00% · 可读取缓存 28.46% · 属性处理 5.34%", projected.Label);
        Assert.IsFalse(projected.Label.Contains("索引空间不足", StringComparison.Ordinal));
        Assert.IsFalse(projected.IsMetadataServiceStalled);
    }

    [TestMethod]
    public void PausedWritesAndPhysicalSafetyLimit_ProjectCapacityPause()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            metadataWritesPaused: true,
            storagePauseReason: "DATABASE_PHYSICAL_SAFETY_LIMIT",
            storageMaintenancePhase: "idle"));

        Assert.AreEqual("storage-blocked", projected.StatusKind);
        Assert.AreEqual("文件 100.00% · 可读取缓存 28.46% · 属性处理 5.34%", projected.Label);
        Assert.IsTrue(projected.IsMetadataServiceStalled);
    }

    [TestMethod]
    public void MetadataStorageBlockedCodeWithoutPausedWrites_DoesNotClaimCapacityPause()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            metadataWritesPaused: false,
            storagePauseReason: null,
            storageMaintenancePhase: "idle",
            metadataCode: "METADATA_STORAGE_BLOCKED"));

        Assert.AreEqual("active", projected.StatusKind);
        Assert.AreEqual("文件 100.00% · 可读取缓存 28.46% · 属性处理 5.34%", projected.Label);
        Assert.IsFalse(projected.Label.Contains("索引空间不足", StringComparison.Ordinal));
        Assert.IsFalse(projected.IsMetadataServiceStalled);
    }

    [TestMethod]
    public void RecoveryFailed_IsRetryingMaintenance_NotCapacityPause()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            metadataWritesPaused: true,
            storagePauseReason: "DATABASE_HARD_LIMIT",
            storageMaintenancePhase: "recovery-failed"));

        Assert.AreEqual("storage-maintenance", projected.StatusKind);
        Assert.AreEqual("文件 100.00% · 可读取缓存 28.46% · 属性处理 5.34%", projected.Label);
        Assert.IsFalse(projected.Label.Contains("索引空间不足", StringComparison.Ordinal));
        Assert.IsFalse(projected.IsMetadataServiceStalled);
    }

    [TestMethod]
    public void ProgressProjection_PreservesDualProgressValues()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            metadataWritesPaused: false,
            storagePauseReason: "DATABASE_PHYSICAL_SAFETY_LIMIT",
            storageMaintenancePhase: "idle"));

        Assert.AreEqual(28.46d, projected.CachedCoveragePercent);
        Assert.AreEqual(5.34d, projected.CurrentMetadataPercent);
        StringAssert.Contains(projected.Label, "可读取缓存 28.46%");
        StringAssert.Contains(projected.Label, "属性处理 5.34%");
    }

    private static RuntimeProgressSnapshot Snapshot(
        bool metadataWritesPaused,
        string? storagePauseReason,
        string? storageMaintenancePhase,
        string metadataCode = "METADATA_UPDATING")
    {
        const long discovered = 100_000;
        const long completed = 5_340;
        const long cached = 28_460;
        var metadataService = new MetadataServiceProgressSnapshot(
            CompletedCount: completed,
            PartialCount: 0,
            PendingCount: discovered - completed,
            FailedCount: 0,
            State: RuntimeServiceState.Running,
            LastHeartbeatUtc: DateTimeOffset.UtcNow,
            Code: metadataCode);
        return new RuntimeProgressSnapshot(
            Metadata: new MetadataProgressSnapshot(
                DiscoveredFileCount: discovered,
                CompletedCount: completed,
                PartialCount: 0,
                PendingCount: discovered - completed,
                FailedCount: 0,
                CachedReadableFileCount: cached),
            DiscoverySource: "EVERYTHING_IPC_READY",
            DiscoveryCode: "READY",
            IsPaused: false,
            MaximumFilesPerSecond: 0,
            MaximumReadBytesPerSecond: 0,
            EstimatedRemaining: null,
            MetadataWritesPaused: metadataWritesPaused,
            StoragePauseReason: storagePauseReason,
            Discovery: new FileDiscoveryProgressSnapshot(
                IndexedFileCount: discovered,
                EstimatedTotalCount: discovered,
                State: RuntimeServiceState.Idle,
                LastHeartbeatUtc: DateTimeOffset.UtcNow,
                Source: "EVERYTHING_IPC_READY",
                Code: "READY"),
            MetadataService: metadataService,
            StorageMaintenancePhase: storageMaintenancePhase);
    }
}
