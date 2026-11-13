using Microsoft.Data.Sqlite;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    // A maintenance transaction used to delete 2,000 rows while holding the
    // only SQLite writer.  On a fragmented production index that can turn one
    // low-priority orphan-prune slice into a multi-second metadata-commit wait.
    // Keep cleanup bounded to a short writer slice; the loop will continue in
    // the background until the durable orphan set is empty.
    private const int OrphanPruneBatchSize = 256;
    // A metadata rebuild can have hundreds of thousands of durable leases.
    // Do not make a complete COUNT(*) part of the maintenance hot loop just
    // to detect that condition: the bounded LIMIT/OFFSET probe below needs to
    // observe only this many queue rows.
    private const int LowPriorityMaintenanceBacklogHighWater = 128;
    private const int LowPriorityMaintenanceBacklogLowWater = 32;
    private static readonly TimeSpan MetadataCommitMaintenanceInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MetadataCheckpointRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LowPriorityMaintenanceBacklogPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LowPriorityMaintenanceFairnessInterval = TimeSpan.FromSeconds(30);

    // These fields deliberately belong to the single store instance.  A
    // metadata producer only requests maintenance; this owner is the sole
    // place that can checkpoint or prune, so metadata parsing concurrency is
    // unchanged.
    private long _nextMetadataCommitMaintenanceUtcTicks;
    private int _metadataCheckpointRequested;
    private long _metadataCommitMaintenanceScheduleCount;
    private long _storageMaintenanceStartRequestCount;
    private long _storageCheckpointCount;
    private int _lowPriorityMaintenanceBacklogDeferred;
    private long _nextLowPriorityMaintenanceFairnessUtcTicks;
    private long _lowPriorityMaintenanceBacklogDeferralCount;

    internal TimeSpan MetadataCommitMaintenanceIntervalForTesting { get; set; } =
        MetadataCommitMaintenanceInterval;

    internal long MetadataCommitMaintenanceScheduleCountForTesting =>
        Interlocked.Read(ref _metadataCommitMaintenanceScheduleCount);

    internal long StorageMaintenanceStartRequestCountForTesting =>
        Interlocked.Read(ref _storageMaintenanceStartRequestCount);

    internal long StorageCheckpointCountForTesting =>
        Interlocked.Read(ref _storageCheckpointCount);

    internal int LowPriorityMaintenanceBacklogHighWaterForTesting { get; set; } =
        LowPriorityMaintenanceBacklogHighWater;

    internal int LowPriorityMaintenanceBacklogLowWaterForTesting { get; set; } =
        LowPriorityMaintenanceBacklogLowWater;

    internal TimeSpan LowPriorityMaintenanceFairnessIntervalForTesting { get; set; } =
        LowPriorityMaintenanceFairnessInterval;

    internal long LowPriorityMaintenanceBacklogDeferralCountForTesting =>
        Interlocked.Read(ref _lowPriorityMaintenanceBacklogDeferralCount);

    /// <summary>
    /// Records a post-commit maintenance request without putting a checkpoint or
    /// orphan prune on the metadata writer's critical path.  A cheap WAL length
    /// read preserves the low/high-water safety behavior: crossing the low-water
    /// threshold wakes the owner immediately, while healthy commits are
    /// coalesced into at most one cleanup kick per interval.
    /// </summary>
    private void ScheduleStorageMaintenanceAfterMetadataCommit()
    {
        if (_readOnlyExisting || _disposed || _storageMaintenanceCancellation.IsCancellationRequested)
        {
            return;
        }

        var wakeForCheckpoint = false;
        try
        {
            var walBytes = ReadVolatileWalLength(
                DatabasePath + "-wal",
                static path => File.Exists(path),
                static path => new FileInfo(path).Length);
            if (walBytes >= _storagePolicy.PassiveCheckpointWalBytes)
            {
                // Only the transition from no pending request to pending wakes
                // the worker.  Further metadata commits are coalesced while it
                // owns the request, rather than creating one maintenance run per
                // 24/96-file property transaction.
                wakeForCheckpoint = Interlocked.Exchange(ref _metadataCheckpointRequested, 1) == 0;
            }
        }
        catch (IOException)
        {
            // The committed metadata is durable.  A transient file-length probe
            // must not turn it into a caller-visible commit failure; the next
            // lease admission/full status probe remains the hard safety gate.
        }
        catch (UnauthorizedAccessException)
        {
            // Same fail-safe behavior as a transient WAL handoff/ACL change.
        }

        if (!wakeForCheckpoint && !TryReserveMetadataCommitMaintenanceWindow())
        {
            return;
        }

        Interlocked.Increment(ref _metadataCommitMaintenanceScheduleCount);
        StartStorageMaintenance();
    }

    private bool TryReserveMetadataCommitMaintenanceWindow()
    {
        var interval = MetadataCommitMaintenanceIntervalForTesting;
        if (interval <= TimeSpan.Zero)
        {
            interval = MetadataCommitMaintenanceInterval;
        }

        var now = DateTimeOffset.UtcNow;
        var next = now.Add(interval).UtcTicks;
        while (true)
        {
            var observed = Volatile.Read(ref _nextMetadataCommitMaintenanceUtcTicks);
            if (observed > now.UtcTicks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _nextMetadataCommitMaintenanceUtcTicks,
                    next,
                    observed) == observed)
            {
                return true;
            }
        }
    }

    private void StartStorageMaintenance()
    {
        Interlocked.Increment(ref _storageMaintenanceStartRequestCount);
        if (_readOnlyExisting || _disposed)
        {
            return;
        }

        lock (_storageMaintenanceSync)
        {
            if (_disposed || _storageMaintenanceCancellation.IsCancellationRequested)
            {
                return;
            }

            if (_storageMaintenanceTask is { IsCompleted: false })
            {
                // Coalesce every kick received by the current generation into
                // one restart. This closes the empty-queue lost-wake race
                // without accumulating one continuation per metadata commit.
                _storageMaintenanceKickPending = true;
                return;
            }

            StartStorageMaintenanceWorkerUnderLock();
        }
    }

    private void StartStorageMaintenanceWorkerUnderLock()
    {
        _storageMaintenanceKickPending = false;
        _storageMaintenanceTask = Task.Run(RunStorageMaintenanceWorkerAsync);
    }

    private async Task RunStorageMaintenanceWorkerAsync()
    {
        try
        {
            await RunStorageMaintenanceLoopAsync(_storageMaintenanceCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_storageMaintenanceSync)
            {
                if (_storageMaintenanceKickPending &&
                    !_disposed &&
                    !_storageMaintenanceCancellation.IsCancellationRequested)
                {
                    StartStorageMaintenanceWorkerUnderLock();
                }
                else
                {
                    _storageMaintenanceKickPending = false;
                    _storageMaintenanceTask = null;
                }
            }
        }
    }

    private async Task RunStorageMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _metadataCheckpointRequested, 0) != 0)
                {
                    var checkpointed = await CheckpointStorageIfNeededAsync(
                        forceFresh: true,
                        cancellationToken).ConfigureAwait(false);
                    if (checkpointed.WalBytes >= _storagePolicy.PassiveCheckpointWalBytes)
                    {
                        // A long-lived reader can legitimately prevent a PASSIVE
                        // checkpoint.  Keep recovery live, but never spin or
                        // repeatedly steal the writer from property commits.
                        Interlocked.Exchange(ref _metadataCheckpointRequested, 1);
                        await Task.Delay(MetadataCheckpointRetryDelay, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                var admission = await GetLowPriorityMaintenanceAdmissionAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (admission == LowPriorityMaintenanceAdmission.DeferredForMetadataBacklog)
                {
                    // Checkpoint/storage protection above remains live.  Only
                    // rebuildable cleanup is delayed so the metadata writer
                    // gets the next gate turn while a large queue drains.
                    Volatile.Write(ref _maintenancePhase, "metadata-backlog");
                    await Task.Delay(LowPriorityMaintenanceBacklogPollDelay, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var pruned = await PruneOrphanBatchAsync(cancellationToken).ConfigureAwait(false);
                const int projected = 0;
                if (pruned == 0 && projected == 0)
                {
                    if (Volatile.Read(ref _lowPriorityMaintenanceBacklogDeferred) != 0)
                    {
                        // A fairness slot found no current cleanup work.  Keep
                        // the owner alive until the queue reaches the low
                        // water mark instead of treating the high backlog as
                        // an idle state and losing the automatic resume.
                        await Task.Delay(LowPriorityMaintenanceBacklogPollDelay, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (!string.Equals(
                            Volatile.Read(ref _maintenancePhase),
                            "recovery-failed",
                            StringComparison.Ordinal))
                    {
                        Volatile.Write(ref _maintenancePhase, "idle");
                    }
                    return;
                }

                // A queued metadata lease gets a chance to acquire the gate
                // before the next low-priority cleanup transaction.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal store shutdown.
        }
        catch
        {
            // The durable queues and orphan rows remain retryable on the next
            // maintenance trigger or process start.
            Volatile.Write(ref _maintenancePhase, "recovery-failed");
        }
    }

    private enum LowPriorityMaintenanceAdmission
    {
        Normal,
        DeferredForMetadataBacklog,
        FairnessSlice,
    }

    /// <summary>
    /// Gives durable metadata queue work priority over rebuildable maintenance
    /// without relying on a full queue count.  The high/low water marks form a
    /// hysteresis band: once deferred, maintenance stays out of the writer's
    /// way until the queue is genuinely small.  A bounded fairness interval
    /// still permits one tiny orphan-cleanup slice during a long rebuild.
    /// </summary>
    private async ValueTask<LowPriorityMaintenanceAdmission> GetLowPriorityMaintenanceAdmissionAsync(
        CancellationToken cancellationToken)
    {
        var highWater = Math.Max(1, LowPriorityMaintenanceBacklogHighWaterForTesting);
        var lowWater = Math.Clamp(
            LowPriorityMaintenanceBacklogLowWaterForTesting,
            0,
            Math.Max(0, highWater - 1));
        var wasDeferred = Volatile.Read(ref _lowPriorityMaintenanceBacklogDeferred) != 0;
        var requiredRows = wasDeferred ? lowWater + 1 : highWater;

        if (!await HasAtLeastMetadataQueueRowsAsync(requiredRows, cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _lowPriorityMaintenanceBacklogDeferred, 0);
            Interlocked.Exchange(ref _nextLowPriorityMaintenanceFairnessUtcTicks, 0);
            return LowPriorityMaintenanceAdmission.Normal;
        }

        if (!wasDeferred)
        {
            Volatile.Write(ref _lowPriorityMaintenanceBacklogDeferred, 1);
            Interlocked.Exchange(
                ref _nextLowPriorityMaintenanceFairnessUtcTicks,
                DateTimeOffset.UtcNow.Add(GetLowPriorityMaintenanceFairnessInterval()).UtcTicks);
            Interlocked.Increment(ref _lowPriorityMaintenanceBacklogDeferralCount);
            return LowPriorityMaintenanceAdmission.DeferredForMetadataBacklog;
        }

        if (TryReserveLowPriorityMaintenanceFairnessWindow())
        {
            return LowPriorityMaintenanceAdmission.FairnessSlice;
        }

        Interlocked.Increment(ref _lowPriorityMaintenanceBacklogDeferralCount);
        return LowPriorityMaintenanceAdmission.DeferredForMetadataBacklog;
    }

    private async ValueTask<bool> HasAtLeastMetadataQueueRowsAsync(
        int requiredRows,
        CancellationToken cancellationToken)
    {
        if (requiredRows <= 0)
        {
            return true;
        }

        // LIMIT/OFFSET is deliberately bounded by the water mark (128 / 32 in
        // production), unlike GetStatusAsync's complete COUNT(*).  This runs
        // on the maintenance owner's once-per-second backpressure poll, never
        // on the per-file metadata commit path.
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM index_queue LIMIT 1 OFFSET @offset);";
        command.Parameters.AddWithValue("@offset", requiredRows - 1);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private TimeSpan GetLowPriorityMaintenanceFairnessInterval()
    {
        var interval = LowPriorityMaintenanceFairnessIntervalForTesting;
        return interval > TimeSpan.Zero
            ? interval
            : LowPriorityMaintenanceFairnessInterval;
    }

    private bool TryReserveLowPriorityMaintenanceFairnessWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var next = now.Add(GetLowPriorityMaintenanceFairnessInterval()).UtcTicks;
        while (true)
        {
            var observed = Volatile.Read(ref _nextLowPriorityMaintenanceFairnessUtcTicks);
            if (observed > now.UtcTicks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _nextLowPriorityMaintenanceFairnessUtcTicks,
                    next,
                    observed) == observed)
            {
                return true;
            }
        }
    }

    internal async ValueTask<bool> ShouldDeferLowPriorityMaintenanceForTestingAsync(
        CancellationToken cancellationToken = default) =>
        await GetLowPriorityMaintenanceAdmissionAsync(cancellationToken).ConfigureAwait(false)
            == LowPriorityMaintenanceAdmission.DeferredForMetadataBacklog;

    private async ValueTask<int> PruneOrphanBatchAsync(CancellationToken cancellationToken)
    {
        if (_readOnlyExisting || _disposed)
        {
            return 0;
        }

        var wait = System.Diagnostics.Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        wait.Stop();
        var hold = System.Diagnostics.Stopwatch.StartNew();
        Volatile.Write(ref _lastMaintenanceGateWaitMilliseconds, (long)wait.Elapsed.TotalMilliseconds);
        Volatile.Write(ref _maintenancePhase, "orphan-prune");
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var affected = 0;
            affected += await ExecuteBoundedDeleteAsync(
                connection,
                transaction,
                """
                DELETE FROM properties WHERE id IN (
                    SELECT p.id
                      FROM files f INDEXED BY ix_files_state_kind_name
                     JOIN properties p ON p.file_id=f.id
                     WHERE f.state>0 LIMIT @limit);
                """,
                OrphanPruneBatchSize - affected,
                cancellationToken).ConfigureAwait(false);
            if (affected < OrphanPruneBatchSize)
            {
                affected += await ExecuteBoundedDeleteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM configurations WHERE rowid IN (
                        SELECT c.rowid
                          FROM files f INDEXED BY ix_files_state_kind_name
                          JOIN configurations c ON c.file_id=f.id
                         WHERE f.state>0 LIMIT @limit);
                    """,
                    OrphanPruneBatchSize - affected,
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        finally
        {
            hold.Stop();
            Volatile.Write(ref _lastMaintenanceGateHoldMilliseconds, (long)hold.Elapsed.TotalMilliseconds);
            if (!string.Equals(
                    Volatile.Read(ref _maintenancePhase),
                    "recovery-failed",
                    StringComparison.Ordinal))
            {
                Volatile.Write(ref _maintenancePhase, "idle");
            }
            _writeGate.Release();
        }
    }

    private static async ValueTask<int> ExecuteBoundedDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("@limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
