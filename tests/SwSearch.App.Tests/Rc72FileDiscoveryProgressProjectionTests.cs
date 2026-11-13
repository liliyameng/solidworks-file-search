using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc72FileDiscoveryProgressProjectionTests
{
    [TestMethod]
    public void RunningBaselineFromCommittedStore_ReplacesUnknownPercent()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 135_000,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            partialCommitted: true));

        Assert.AreEqual(83.64d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "83.64%");
    }

    [TestMethod]
    public void PausedBaselineFromCommittedStore_KeepsAdvancingPercent()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 80_708,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Paused,
            baseline: 161_415,
            partialCommitted: true));

        Assert.AreEqual(50d, projected.FilePercent);
    }

    [TestMethod]
    public void StalledBaselineFromCommittedStore_KeepsKnownPercent()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 161_415,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Stalled,
            baseline: 161_415,
            partialCommitted: true));

        Assert.AreEqual(99.99d, projected.FilePercent);
    }

    [TestMethod]
    public void IndexedBeyondBaseline_CapsAtNinetyNineNinetyNineWithoutFakeCompletion()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 200_000,
            estimatedTotal: 200_000,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            partialCommitted: true));

        Assert.AreEqual(99.99d, projected.FilePercent);
        Assert.IsFalse(projected.Label.Contains("100.00%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FixedBaselineDenominator_KeepsPercentMonotonicAsIndexedClimbs()
    {
        var early = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 100_000,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            partialCommitted: true));
        var late = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 150_000,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            partialCommitted: true));

        Assert.AreEqual(61.95d, early.FilePercent);
        Assert.AreEqual(92.93d, late.FilePercent);
        Assert.IsTrue(late.FilePercent > early.FilePercent);
    }

    [TestMethod]
    public void LegacyBrokerBaselineOnly_StillProjectsLivePercent()
    {
        // An older broker never populates the runtime baseline; the committed
        // snapshot count must remain the fixed denominator for a live scan.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 80_708,
            estimatedTotal: null,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            partialCommitted: true));

        Assert.AreEqual(50d, projected.FilePercent);
    }

    [TestMethod]
    public void EmptyFirstRunRunning_KeepsUnknownPercent()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 0,
            estimatedTotal: null,
            discoveryState: RuntimeServiceState.Running,
            baseline: null));

        Assert.IsNull(projected.FilePercent);
        StringAssert.Contains(projected.Label, "--%");
    }

    [TestMethod]
    public void CommittedReadySnapshot_AllowsFullCompletion()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 161_415,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Idle,
            baseline: 161_415,
            committedComplete: true));

        Assert.AreEqual(100d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "100.00%");
    }

    [TestMethod]
    public void PartialCommittedIdle_NeverPromotesHistoricalRowsToCompletion()
    {
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 161_415,
            estimatedTotal: null,
            discoveryState: RuntimeServiceState.Idle,
            baseline: 161_415,
            partialCommitted: true));

        Assert.IsNull(projected.FilePercent);
    }

    [TestMethod]
    public void CommittedCompleteRunningWithoutLiveEstimate_CapsAtNinetyNineNinetyNine()
    {
        // When a committed complete snapshot exists but the provider has no live
        // estimate (EstimatedTotalCount is null) and discovery is Running, the
        // Running state represents a new uncommitted live scan. Even with a
        // committed ready snapshot, the percentage must cap at 99.99 -- never
        // show a fragile 100% during an uncommitted scan.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 161_415,
            estimatedTotal: null,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            committedComplete: true));

        Assert.AreEqual(99.99d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "99.99%");
    }

    [TestMethod]
    public void CommittedCompleteUnavailableWithoutLiveEstimate_ShowsFullCompletion()
    {
        // When the discovery service is Unavailable (non-live) but a committed
        // complete snapshot exists, the committed snapshot is authoritative
        // and the file index is settled. Show 100%.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 161_415,
            estimatedTotal: null,
            discoveryState: RuntimeServiceState.Unavailable,
            baseline: 161_415,
            committedComplete: true));

        Assert.AreEqual(100d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "100.00%");
    }

    [TestMethod]
    public void CommittedCompleteZeroIndexedUnavailable_ShowsFullCompletion()
    {
        // When the provider reports IndexedFileCount=0 in Unavailable state
        // (e.g. just after a reconnect) but a committed complete ready snapshot
        // exists with a nonzero baseline, the durable proof of completion must
        // still show 100% rather than falling to "--%".
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 0,
            estimatedTotal: null,
            discoveryState: RuntimeServiceState.Unavailable,
            baseline: 161_415,
            committedComplete: true));

        Assert.AreEqual(100d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "100.00%");
    }

    [TestMethod]
    public void CommittedCompleteEverythingIdleWithResetCounters_ShowsFullCompletion()
    {
        // Everything has already committed a complete snapshot. Even when the
        // provider restarts and its transient counters reset to zero while its
        // live estimate is still known, the durable snapshot must keep the file
        // bar at 100% instead of falling to 0% or "--%".
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 0,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Idle,
            baseline: 161_415,
            committedComplete: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_COMPLETE"));

        Assert.AreEqual(100d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "100.00%");
        StringAssert.StartsWith(projected.Label, "文件 100.00% ·");
    }

    [TestMethod]
    public void CommittedCompleteEverythingIdleWithStaleEstimate_ShowsFullCompletion()
    {
        // The provider's in-memory estimate may be older or narrower than the
        // committed snapshot. Once Everything has completed and persisted a full
        // snapshot, Idle must render 100 rather than a stale ratio.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 100_000,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Idle,
            baseline: 161_415,
            committedComplete: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_COMPLETE"));

        Assert.AreEqual(100d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "100.00%");
    }

    [TestMethod]
    public void CommittedCompleteEverythingRunningAtEstimate_NeverShowsFakeHundred()
    {
        // While a new Everything query is live, a known total shows the real
        // percentage, and reaching the estimate is capped below 100 because only
        // the next durable commit can prove completion.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 161_415,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            committedComplete: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_READING"));

        Assert.AreEqual(99.99d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "99.99%");
        Assert.IsFalse(projected.Label.Contains("100.00%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CommittedCompleteEverythingRunningWithKnownTotal_ShowsActualPercentage()
    {
        // A live Everything query with a known total must show the actual ratio,
        // not borrow the old completed snapshot's 100%.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 100_000,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            committedComplete: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_READING"));

        Assert.AreEqual(61.95d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "61.95%");
    }

    [TestMethod]
    public void CommittedCompleteLiveWithZeroIndexed_StaysAtLiveScanCap()
    {
        // When Everything starts a fresh query, IndexedFileCount resets to 0
        // while state=Running and EstimatedTotalCount still holds the old
        // committed baseline. The bar must stay at 99.99% (live-scan cap)
        // instead of regressing to 0%, because a live uncommitted scan can
        // never show a fragile 100%.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 0,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            committedComplete: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_READING"));

        Assert.AreEqual(99.99d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "99.99%");
        Assert.IsFalse(projected.Label.Contains("100.00%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PersistedPartialLiveWithZeroIndexed_StaysAtLiveScanCap()
    {
        // A persisted partial snapshot still contains usable file rows. When a
        // new Everything scan resets its transient counter, the file percentage
        // must not regress from the previous completion display to 0.00%.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 0,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            partialCommitted: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_READING"));

        Assert.AreEqual(99.99d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "99.99%");
        Assert.IsFalse(projected.Label.Contains("100.00%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CommittedCompleteLiveWithTemporarilyEmptyMetadata_StaysAtLiveScanCap()
    {
        // The durable ready marker is authoritative even if the independently
        // refreshed metadata count is briefly zero while Everything restarts.
        var projected = MainViewModel.CreateIndexProgress(Snapshot(
            indexed: 0,
            estimatedTotal: 161_415,
            discoveryState: RuntimeServiceState.Running,
            baseline: 161_415,
            committedComplete: true,
            source: "Everything",
            code: "EVERYTHING_SNAPSHOT_READING",
            discovered: 0));

        Assert.AreEqual(99.99d, projected.FilePercent);
        StringAssert.Contains(projected.Label, "99.99%");
    }

    private static RuntimeProgressSnapshot Snapshot(
        long indexed,
        long? estimatedTotal,
        RuntimeServiceState discoveryState,
        long? baseline,
        bool committedComplete = false,
        bool partialCommitted = false,
        string source = "Built-in directory scan",
        string code = "BUILTIN_DISCOVERY_SCANNING",
        long discovered = 161_415)
    {
        return new RuntimeProgressSnapshot(
            Metadata: new MetadataProgressSnapshot(
                DiscoveredFileCount: discovered,
                CompletedCount: 0,
                PartialCount: 0,
                PendingCount: discovered,
                FailedCount: 0),
            DiscoverySource: source,
            DiscoveryCode: code,
            IsPaused: false,
            MaximumFilesPerSecond: 0,
            MaximumReadBytesPerSecond: 0,
            EstimatedRemaining: null,
            Discovery: new FileDiscoveryProgressSnapshot(
                IndexedFileCount: indexed,
                EstimatedTotalCount: estimatedTotal,
                State: discoveryState,
                LastHeartbeatUtc: DateTimeOffset.UtcNow,
                Source: source,
                Code: code,
                DiscoveryGeneration: committedComplete || partialCommitted ? 206 : null,
                LastSuccessfulDiscoveryUtc: committedComplete ? DateTimeOffset.UtcNow : null,
                SourceState: committedComplete
                    ? "ready"
                    : partialCommitted
                        ? "partial"
                        : null,
                BaselineFileCount: baseline),
            MetadataService: new MetadataServiceProgressSnapshot(
                CompletedCount: 0,
                PartialCount: 0,
                PendingCount: discovered,
                FailedCount: 0,
                State: RuntimeServiceState.Running,
                LastHeartbeatUtc: DateTimeOffset.UtcNow,
                Code: "METADATA_RUNNING"));
    }
}
