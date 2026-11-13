using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    // Queue timestamps are stored as DateTimeOffset.UtcTicks.  This is SQLite's
    // UTC clock in the same epoch, evaluated by SQLite for each statement.  Do
    // not substitute a caller-side timestamp here: a task can wait behind the
    // process write gate or SQLite's write lock long enough for that value to
    // make an already-expired lease appear valid.
    private const string DatabaseUtcTicksSql =
        "CAST((julianday('now') - 1721425.5) * 864000000000.0 AS INTEGER)";

    private const long MaximumMetadataLeaseDurationTicks = 15L * TimeSpan.TicksPerMinute;

    // Keep the pre-update seam inside the same transaction.  It is test-only:
    // queue-claim tests use it to mutate the selected row before the UPDATE and
    // prove that a stale candidate cannot be returned as a lease.
    internal Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>?
        BeforeMetadataLeaseUpdateForTestingAsync { get; set; }

    // Test-only admission marker.  It intentionally sits at the boundary
    // where an older implementation captured its caller-side clock, allowing
    // queue tests to prove that a later write-gate admission cannot create an
    // already-expired lease.
    internal Func<CancellationToken, ValueTask>? BeforeMetadataLeaseWriteGateForTestingAsync { get; set; }

    public async ValueTask<bool> ContainsFileAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        var pathNorm = IndexNormalization.PathKey(fullPath);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM files WHERE path_norm=@path AND state=0 AND kind IN (0,1));";
        command.Parameters.AddWithValue("@path", pathNorm);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async ValueTask<IReadOnlyList<MetadataWorkLease>> LeaseMetadataAsync(
        int maximumItems,
        TimeSpan leaseDuration,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var storage = await GetStorageStatusAsync(cancellationToken).ConfigureAwait(false);
        if (storage.MetadataWritesPaused)
        {
            storage = await MaintainStorageAsync(cancellationToken).ConfigureAwait(false);
            if (storage.MetadataWritesPaused)
            {
                return Array.Empty<MetadataWorkLease>();
            }
        }

        var leases = new List<MetadataWorkLease>();
        if (BeforeMetadataLeaseWriteGateForTestingAsync is { } beforeWriteGate)
        {
            await beforeWriteGate(cancellationToken).ConfigureAwait(false);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // A lease claim must acquire SQLite's write transaction before it
            // reads candidates.  A deferred transaction can SELECT an older
            // WAL snapshot, then fail with SQLITE_BUSY_SNAPSHOT if an external
            // writer commits before the CAS UPDATE.  BEGIN IMMEDIATE waits for
            // that writer first, then gives the SELECT and statement-time CAS
            // UPDATE/RETURNING one fresh write transaction.  The complete CAS
            // below remains the transition authority.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var deferredReplay = await ApplyDeferredDiscoveryChangesCoreAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await using (var recoverLegacyCloudCooldown = Command(connection, transaction, $"""
                -- Pre-RC88 terminal placeholders have no retained queue row.
                -- Admit at most one normal scheduler batch after their durable
                -- cooldown; current rows already retain their own due_utc.
                INSERT INTO index_queue(
                    file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'CloudPlaceholderCooldown',5,{DatabaseUtcTicksSql},0,0,
                       d.fingerprint,d.extractor_version
                  FROM files f INDEXED BY ix_files_extractor
                  JOIN dead_letter d ON d.file_id=f.id
                 WHERE f.state=0 AND f.extractor_version=@extractor AND f.kind IN (0,1)
                   AND f.metadata_state=3
                   AND f.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                   AND d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                   AND d.extractor_version=@extractor
                   AND d.failed_utc<={DatabaseUtcTicksSql}-@cooldown
                   AND NOT EXISTS(SELECT 1 FROM index_queue q WHERE q.file_id=f.id)
                 LIMIT 100
                ON CONFLICT(file_id) DO NOTHING;
                """))
            {
                recoverLegacyCloudCooldown.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
                recoverLegacyCloudCooldown.Parameters.AddWithValue(
                    "@cooldown",
                    CloudPlaceholderRediscoveryCooldown.Ticks);
                await recoverLegacyCloudCooldown.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var candidates = new List<MetadataLeaseClaimCandidate>();
            await using (var select = Command(connection, transaction, $"""
                SELECT q.file_id,f.path,q.reason,q.attempt,q.priority,q.fingerprint,f.size,
                       q.extractor_version,q.due_utc,q.lease_until_utc,
                       f.path_norm,f.kind,f.modified_utc,f.file_identity,
                       q.failure_count,q.last_error,q.lease_owner
                  FROM index_queue q INDEXED BY ix_queue_schedule
                  CROSS JOIN files f ON f.id=q.file_id AND f.state=0 AND f.kind IN (0,1)
                 WHERE q.due_utc<={DatabaseUtcTicksSql}
                   AND (q.lease_until_utc IS NULL OR q.lease_until_utc<={DatabaseUtcTicksSql})
                   AND NOT EXISTS(SELECT 1 FROM dead_letter d WHERE d.file_id=q.file_id
                                  AND d.fingerprint=q.fingerprint AND d.extractor_version=q.extractor_version
                                  AND NOT (d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                                           AND d.failed_utc<={DatabaseUtcTicksSql}-@cooldown))
                 ORDER BY q.priority,
                          q.schedule_kind_rank,
                          CASE WHEN q.priority BETWEEN 2 AND 4
                               THEN q.schedule_modified_utc ELSE 0 END DESC,
                          q.due_utc,q.attempt,q.file_id LIMIT @limit;
                """))
            {
                select.Parameters.AddWithValue("@limit", maximumItems);
                select.Parameters.AddWithValue("@cooldown", CloudPlaceholderRediscoveryCooldown.Ticks);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add(ReadMetadataLeaseClaimCandidate(reader));
                }
            }

            foreach (var candidate in candidates)
            {
                if (BeforeMetadataLeaseUpdateForTestingAsync is { } beforeUpdate)
                {
                    await beforeUpdate(connection, transaction, cancellationToken).ConfigureAwait(false);
                }

                await using var update = CreateMetadataLeaseClaimUpdate(
                    connection,
                    transaction,
                    candidate,
                    leaseDuration,
                    leaseOwner,
                    includeDeadLetterCheck: true);
                await using var returned = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await returned.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    leases.Add(ReadMetadataLeaseClaimResult(returned, leaseOwner));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (deferredReplay.Changed)
            {
                MarkSearchContentChanged();
                MarkMetadataProgressBatchPublished();
            }
            return leases;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // A background batch may be deliberately held while the resource governor
    // admits a slow read or a single SQLite write.  Renewal is intentionally
    // narrow: it can extend only the exact, still-current lease that was
    // originally claimed by this owner.  In particular, this must never revive
    // an expired row or take a row that another owner has since claimed.
    internal async ValueTask<IReadOnlySet<long>> RenewMetadataLeasesAsync(
        IReadOnlyList<MetadataWorkLease> leases,
        DateTimeOffset renewUntilUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(leases));
        }

        var distinctIds = new HashSet<long>();
        foreach (var lease in leases)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentException.ThrowIfNullOrWhiteSpace(lease.LeaseOwner);
            ArgumentException.ThrowIfNullOrWhiteSpace(lease.Fingerprint);
            if (!distinctIds.Add(lease.FileId))
            {
                throw new ArgumentException("Lease renewal requires distinct file IDs.", nameof(leases));
            }
        }

        var renewed = new HashSet<long>();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            // This timestamp is only an API-boundary validation.  The actual
            // expiration and requested-deadline tests are deliberately in the
            // UPDATE below, so a lock wait after this point cannot resurrect a
            // lease that expired meanwhile.
            var validationNow = DateTimeOffset.UtcNow;
            if (renewUntilUtc > validationNow.AddMinutes(15))
            {
                throw new ArgumentOutOfRangeException(nameof(renewUntilUtc));
            }

            await using var update = Command(connection, transaction, $"""
                UPDATE index_queue
                   SET lease_until_utc=max(lease_until_utc,@until)
                 WHERE file_id=@file
                   AND lease_owner=@owner
                   AND fingerprint=@fingerprint
                   AND attempt=@attempt
                   AND extractor_version=@extractor
                   AND lease_until_utc>{DatabaseUtcTicksSql}
                   AND @until>{DatabaseUtcTicksSql}
                   AND @until<={DatabaseUtcTicksSql}+@maximumDuration;
                """);
            foreach (var lease in leases)
            {
                update.Parameters.Clear();
                update.Parameters.AddWithValue("@until", renewUntilUtc.UtcTicks);
                update.Parameters.AddWithValue("@file", lease.FileId);
                update.Parameters.AddWithValue("@owner", lease.LeaseOwner);
                update.Parameters.AddWithValue("@fingerprint", lease.Fingerprint);
                update.Parameters.AddWithValue("@attempt", lease.Attempt);
                update.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
                update.Parameters.AddWithValue("@maximumDuration", MaximumMetadataLeaseDurationTicks);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    renewed.Add(lease.FileId);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return renewed;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<int> ReleaseMetadataLeasesAsync(
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE index_queue
                   SET lease_until_utc=NULL,lease_owner=NULL,
                       attempt=CASE WHEN attempt>0 THEN attempt-1 ELSE 0 END
                 WHERE lease_owner=@owner;
                """;
            command.Parameters.AddWithValue("@owner", leaseOwner);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<MetadataWorkLease?> TryLeaseMetadataPathAsync(
        string fullPath,
        TimeSpan leaseDuration,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        var pathNorm = IndexNormalization.PathKey(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (BeforeMetadataLeaseWriteGateForTestingAsync is { } beforeWriteGate)
        {
            await beforeWriteGate(cancellationToken).ConfigureAwait(false);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // See LeaseMetadataAsync: take BEGIN IMMEDIATE before SELECT so an
            // external writer cannot invalidate a deferred read snapshot
            // between candidate selection and this path's CAS UPDATE.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var deferredReplay = await ApplyDeferredDiscoveryChangesCoreAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            MetadataLeaseClaimCandidate candidate;
            await using (var select = Command(connection, transaction, $"""
                SELECT q.file_id,f.path,q.reason,q.attempt,q.priority,q.fingerprint,f.size,
                       q.extractor_version,q.due_utc,q.lease_until_utc,
                       f.path_norm,f.kind,f.modified_utc,f.file_identity,
                       q.failure_count,q.last_error,q.lease_owner
                  FROM index_queue q JOIN files f ON f.id=q.file_id AND f.state=0 AND f.kind IN (0,1)
                 WHERE f.path_norm=@path AND q.due_utc<={DatabaseUtcTicksSql}
                   AND (q.lease_until_utc IS NULL OR q.lease_until_utc<={DatabaseUtcTicksSql});
                """))
            {
                select.Parameters.AddWithValue("@path", pathNorm);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (deferredReplay.Changed)
                    {
                        MarkSearchContentChanged();
                        MarkMetadataProgressBatchPublished();
                    }
                    return null;
                }

                candidate = ReadMetadataLeaseClaimCandidate(reader);
            }

            if (BeforeMetadataLeaseUpdateForTestingAsync is { } beforeUpdate)
            {
                await beforeUpdate(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            await using var update = CreateMetadataLeaseClaimUpdate(
                connection,
                transaction,
                candidate,
                leaseDuration,
                leaseOwner,
                includeDeadLetterCheck: false);
            MetadataWorkLease lease;
            await using (var returned = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await returned.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (deferredReplay.Changed)
                    {
                        MarkSearchContentChanged();
                        MarkMetadataProgressBatchPublished();
                    }
                    return null;
                }

                lease = ReadMetadataLeaseClaimResult(returned, leaseOwner);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (deferredReplay.Changed)
            {
                MarkSearchContentChanged();
                MarkMetadataProgressBatchPublished();
            }
            return lease;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static MetadataLeaseClaimCandidate ReadMetadataLeaseClaimCandidate(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            (MetadataQueuePriority)reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt32(11),
            reader.GetInt64(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));

    private static MetadataWorkLease ReadMetadataLeaseClaimResult(
        SqliteDataReader reader,
        string leaseOwner) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            FromUtcTicks(reader.GetInt64(7)),
            leaseOwner,
            (MetadataQueuePriority)reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt64(6));

    private static SqliteCommand CreateMetadataLeaseClaimUpdate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MetadataLeaseClaimCandidate candidate,
        TimeSpan leaseDuration,
        string leaseOwner,
        bool includeDeadLetterCheck)
    {
        var deadLetterCheck = includeDeadLetterCheck
            ? $"""
                   AND NOT EXISTS(
                       SELECT 1 FROM dead_letter d
                        WHERE d.file_id=index_queue.file_id
                          AND d.fingerprint=index_queue.fingerprint
                          AND d.extractor_version=index_queue.extractor_version
                          AND NOT (d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                                   AND d.failed_utc<={DatabaseUtcTicksSql}-{CloudPlaceholderRediscoveryCooldown.Ticks}))
              """
            : string.Empty;
        var update = Command(connection, transaction, $"""
            UPDATE index_queue
               SET lease_until_utc={DatabaseUtcTicksSql}+@duration,
                   lease_owner=@owner,
                   attempt=attempt+1
             WHERE file_id=@file
               AND reason=@expectedReason
               AND priority=@expectedPriority
               AND due_utc=@expectedDue
               AND attempt=@expectedAttempt
               AND failure_count=@expectedFailureCount
               AND fingerprint=@expectedFingerprint
               AND extractor_version=@expectedExtractor
               AND lease_owner IS @expectedLeaseOwner
               AND last_error IS @expectedLastError
               AND (
                    (lease_until_utc IS NULL AND @expectedLeaseUntil IS NULL)
                    OR lease_until_utc=@expectedLeaseUntil)
               AND due_utc<={DatabaseUtcTicksSql}
               AND (lease_until_utc IS NULL OR lease_until_utc<={DatabaseUtcTicksSql})
               AND EXISTS(
                   SELECT 1 FROM files f
                    WHERE f.id=index_queue.file_id
                      AND f.state=0
                      AND f.path_norm=@expectedPathNorm
                      AND f.path=@expectedPath
                      AND f.kind=@expectedKind
                      AND f.size=@expectedSize
                      AND f.modified_utc=@expectedModified
                      AND f.file_identity IS @expectedIdentity)
               {deadLetterCheck}
             RETURNING file_id,
                       (SELECT f.path FROM files f WHERE f.id=file_id),
                       reason,
                       attempt,
                       priority,
                       fingerprint,
                       (SELECT f.size FROM files f WHERE f.id=file_id),
                       lease_until_utc;
            """);
        update.Parameters.AddWithValue("@duration", leaseDuration.Ticks);
        update.Parameters.AddWithValue("@owner", leaseOwner);
        update.Parameters.AddWithValue("@file", candidate.FileId);
        update.Parameters.AddWithValue("@expectedReason", candidate.Reason);
        update.Parameters.AddWithValue("@expectedPriority", (int)candidate.Priority);
        update.Parameters.AddWithValue("@expectedDue", candidate.DueUtcTicks);
        update.Parameters.AddWithValue("@expectedAttempt", candidate.Attempt);
        update.Parameters.AddWithValue("@expectedFailureCount", candidate.FailureCount);
        update.Parameters.AddWithValue("@expectedFingerprint", candidate.Fingerprint);
        update.Parameters.AddWithValue("@expectedExtractor", candidate.ExtractorVersion);
        update.Parameters.AddWithValue("@expectedLeaseOwner", candidate.LeaseOwner ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("@expectedLastError", candidate.LastError ?? (object)DBNull.Value);
        update.Parameters.AddWithValue(
            "@expectedLeaseUntil",
            candidate.LeaseUntilUtcTicks is { } leaseUntil ? leaseUntil : DBNull.Value);
        update.Parameters.AddWithValue("@expectedPathNorm", candidate.PathNorm);
        update.Parameters.AddWithValue("@expectedPath", candidate.FullPath);
        update.Parameters.AddWithValue("@expectedKind", candidate.Kind);
        update.Parameters.AddWithValue("@expectedSize", candidate.EstimatedReadBytes);
        update.Parameters.AddWithValue("@expectedModified", candidate.ModifiedUtcTicks);
        update.Parameters.AddWithValue("@expectedIdentity", candidate.FileIdentity ?? (object)DBNull.Value);
        return update;
    }

    private readonly record struct MetadataLeaseClaimCandidate(
        long FileId,
        string FullPath,
        string Reason,
        int Attempt,
        MetadataQueuePriority Priority,
        string Fingerprint,
        long EstimatedReadBytes,
        int ExtractorVersion,
        long DueUtcTicks,
        long? LeaseUntilUtcTicks,
        string PathNorm,
        int Kind,
        long ModifiedUtcTicks,
        string? FileIdentity,
        int FailureCount,
        string? LastError,
        string? LeaseOwner);

    public async ValueTask CompleteMetadataLeaseAsync(
        long fileId,
        string leaseOwner,
        bool succeeded,
        string? errorCode = null,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var delay = retryDelay ?? TimeSpan.FromSeconds(30);
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(nameof(retryDelay));
            }

            var now = DateTimeOffset.UtcNow;
            await using (var verify = Command(connection, transaction, """
                SELECT EXISTS(
                    SELECT 1
                      FROM index_queue
                     WHERE file_id=@file
                       AND lease_owner=@owner
                       AND lease_until_utc>@now);
                """))
            {
                verify.Parameters.AddWithValue("@file", fileId);
                verify.Parameters.AddWithValue("@owner", leaseOwner);
                verify.Parameters.AddWithValue("@now", now.UtcTicks);
                if (Convert.ToInt32(
                        await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                {
                    throw new InvalidOperationException(
                        "The metadata lease is missing, expired, or belongs to another owner.");
                }
            }

            await using var command = Command(connection, transaction, succeeded
                ? "DELETE FROM index_queue WHERE file_id=@file AND lease_owner=@owner;"
                : """
                    UPDATE files
                       SET metadata_state=CASE WHEN (
                               SELECT q.failure_count+1
                                 FROM index_queue q
                                WHERE q.file_id=@file AND q.lease_owner=@owner
                           )>=2 THEN 3 ELSE 2 END,
                           extractor_version=@extractor,
                           error_code=@error
                     WHERE id=@file AND EXISTS(
                           SELECT 1
                             FROM index_queue q
                            WHERE q.file_id=@file
                              AND q.lease_owner=@owner
                              AND q.lease_until_utc>@now);
                    INSERT INTO dead_letter(file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
                    SELECT file_id,fingerprint,extractor_version,failure_count+1,@error,@now
                      FROM index_queue
                     WHERE file_id=@file AND lease_owner=@owner
                       AND lease_until_utc>@now AND failure_count+1>=2
                    ON CONFLICT(file_id) DO UPDATE SET fingerprint=excluded.fingerprint,
                        extractor_version=excluded.extractor_version,failure_count=excluded.failure_count,
                        error_code=excluded.error_code,failed_utc=excluded.failed_utc;
                    UPDATE index_queue SET due_utc=@due,priority=5,failure_count=failure_count+1,
                           lease_until_utc=NULL,lease_owner=NULL,last_error=@error
                     WHERE file_id=@file AND lease_owner=@owner
                       AND lease_until_utc>@now AND failure_count+1<2;
                    DELETE FROM index_queue
                     WHERE file_id=@file AND lease_owner=@owner AND lease_until_utc>@now;
                    """);
            command.Parameters.AddWithValue("@file", fileId);
            command.Parameters.AddWithValue("@owner", leaseOwner);
            if (!succeeded)
            {
                command.Parameters.AddWithValue("@due", now.Add(delay).UtcTicks);
                command.Parameters.AddWithValue("@now", now.UtcTicks);
                command.Parameters.AddWithValue("@error", (object?)errorCode ?? "METADATA_FAILED");
                command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
            }

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (succeeded && changed != 1)
            {
                throw new InvalidOperationException("租约不存在、已过期或不属于当前 owner。");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async ValueTask ClearMetadataFailureForInteractiveAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        var pathNorm = IndexNormalization.PathKey(fullPath);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = Command(connection, transaction, """
                DELETE FROM dead_letter
                 WHERE file_id IN (
                       SELECT id FROM files WHERE path_norm=@path AND state=0 AND kind IN (0,1));
                UPDATE files
                   SET metadata_state=CASE
                           WHEN extracted_utc IS NULL THEN 1
                           ELSE metadata_state
                       END,
                       error_code=CASE
                           WHEN extracted_utc IS NULL THEN NULL
                           ELSE error_code
                       END
                 WHERE path_norm=@path AND state=0;
                """);
            command.Parameters.AddWithValue("@path", pathNorm);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DeferMetadataLeaseAsync(
        long fileId,
        string leaseOwner,
        DateTimeOffset dueUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            var now = DateTimeOffset.UtcNow;
            command.CommandText = """
                UPDATE index_queue
                   SET due_utc=@due,lease_until_utc=NULL,lease_owner=NULL,last_error=@reason
                 WHERE file_id=@file AND lease_owner=@owner AND lease_until_utc>@now;
                """;
            command.Parameters.AddWithValue("@due", dueUtc.UtcTicks);
            command.Parameters.AddWithValue("@reason", reason);
            command.Parameters.AddWithValue("@file", fileId);
            command.Parameters.AddWithValue("@owner", leaseOwner);
            command.Parameters.AddWithValue("@now", now.UtcTicks);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "The metadata lease is missing, expired, or belongs to another owner.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // A cloud provider can leave a placeholder visible forever (for example a
    // retired sync-conflict directory). Such a row is neither a successful
    // property read nor immediate work once it has exhausted its bounded
    // attempt. Keep the terminal fact durable, then retain one future queue
    // row for the next bounded cooldown probe. Discovery change and interactive
    // reads still bypass that wait with stronger evidence.
    internal async ValueTask FinalizeDeferredMetadataLeaseAsync(
        long fileId,
        string leaseOwner,
        string terminalErrorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalErrorCode);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await using (var verify = Command(connection, transaction, """
                SELECT EXISTS(
                    SELECT 1
                      FROM index_queue
                     WHERE file_id=@file
                       AND lease_owner=@owner
                       AND lease_until_utc>@now);
                """))
            {
                verify.Parameters.AddWithValue("@file", fileId);
                verify.Parameters.AddWithValue("@owner", leaseOwner);
                verify.Parameters.AddWithValue("@now", now.UtcTicks);
                if (Convert.ToInt32(
                        await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                {
                    throw new InvalidOperationException(
                        "The metadata lease is missing, expired, or belongs to another owner.");
                }
            }

            await using (var command = Command(connection, transaction, """
                INSERT INTO dead_letter(file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
                SELECT file_id,fingerprint,extractor_version,
                       max(attempt,failure_count+1),@error,@now
                  FROM index_queue
                 WHERE file_id=@file AND lease_owner=@owner AND lease_until_utc>@now
                ON CONFLICT(file_id) DO UPDATE SET fingerprint=excluded.fingerprint,
                    extractor_version=excluded.extractor_version,failure_count=excluded.failure_count,
                    error_code=excluded.error_code,failed_utc=excluded.failed_utc;
                UPDATE files
                   SET metadata_state=3,extractor_version=@extractor,error_code=@error
                 WHERE id=@file AND EXISTS(
                       SELECT 1 FROM index_queue q
                        WHERE q.file_id=@file AND q.lease_owner=@owner
                          AND q.lease_until_utc>@now);
                UPDATE index_queue
                   SET reason='CloudPlaceholderCooldown',priority=5,due_utc=@cooldownDue,
                       attempt=0,failure_count=0,lease_until_utc=NULL,lease_owner=NULL,last_error=@error
                 WHERE @cloudTerminal=1
                   AND file_id=@file AND lease_owner=@owner AND lease_until_utc>@now;
                DELETE FROM index_queue
                 WHERE @cloudTerminal=0
                   AND file_id=@file AND lease_owner=@owner AND lease_until_utc>@now;
                """))
            {
                command.Parameters.AddWithValue("@file", fileId);
                command.Parameters.AddWithValue("@owner", leaseOwner);
                command.Parameters.AddWithValue("@now", now.UtcTicks);
                command.Parameters.AddWithValue(
                    "@cooldownDue",
                    now.Add(CloudPlaceholderRediscoveryCooldown).UtcTicks);
                command.Parameters.AddWithValue("@error", terminalErrorCode);
                command.Parameters.AddWithValue(
                    "@cloudTerminal",
                    terminalErrorCode == "CLOUD_PLACEHOLDER_RETRY_EXHAUSTED" ? 1 : 0);
                command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

    }

    // Reclaim only abandoned/deferred rows -- never an active lease owned by a
    // live parser.  This startup recovery lets an upgraded runtime close the
    // historical rows that were created before the bounded-defer policy existed.
    internal async ValueTask<int> FinalizeExhaustedDeferredMetadataAsync(
        string deferredErrorCode,
        int minimumAttempt,
        string terminalErrorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deferredErrorCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAttempt, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalErrorCode);
        var finalizedCount = 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var fileIds = new List<long>();
            await using (var select = Command(connection, transaction, """
                SELECT q.file_id
                  FROM index_queue q
                  JOIN files f ON f.id=q.file_id AND f.state=0 AND f.kind IN (0,1)
                 WHERE q.last_error=@deferred
                   AND q.attempt>=@attempt
                   AND q.extractor_version=@extractor
                   AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now);
                """))
            {
                select.Parameters.AddWithValue("@deferred", deferredErrorCode);
                select.Parameters.AddWithValue("@attempt", minimumAttempt);
                select.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
                select.Parameters.AddWithValue("@now", now.UtcTicks);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    fileIds.Add(reader.GetInt64(0));
                }
            }

            await using (var command = Command(connection, transaction, """
                INSERT INTO dead_letter(file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
                SELECT q.file_id,q.fingerprint,q.extractor_version,
                       max(q.attempt,q.failure_count+1),@terminal,@now
                  FROM index_queue q
                  JOIN files f ON f.id=q.file_id AND f.state=0 AND f.kind IN (0,1)
                 WHERE q.last_error=@deferred
                   AND q.attempt>=@attempt
                   AND q.extractor_version=@extractor
                   AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now)
                ON CONFLICT(file_id) DO UPDATE SET fingerprint=excluded.fingerprint,
                    extractor_version=excluded.extractor_version,failure_count=excluded.failure_count,
                    error_code=excluded.error_code,failed_utc=excluded.failed_utc;
                UPDATE files
                   SET metadata_state=3,extractor_version=@extractor,error_code=@terminal
                 WHERE state=0 AND id IN (
                       SELECT q.file_id FROM index_queue q
                        WHERE q.last_error=@deferred
                          AND q.attempt>=@attempt
                          AND q.extractor_version=@extractor
                          AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now));
                UPDATE index_queue
                   SET reason='CloudPlaceholderCooldown',priority=5,due_utc=@cooldownDue,
                       attempt=0,failure_count=0,lease_until_utc=NULL,lease_owner=NULL,last_error=@terminal
                 WHERE @cloudTerminal=1
                   AND last_error=@deferred
                   AND attempt>=@attempt
                   AND extractor_version=@extractor
                   AND (lease_until_utc IS NULL OR lease_until_utc<=@now);
                DELETE FROM index_queue
                 WHERE @cloudTerminal=0
                   AND last_error=@deferred
                   AND attempt>=@attempt
                   AND extractor_version=@extractor
                   AND (lease_until_utc IS NULL OR lease_until_utc<=@now);
                """))
            {
                command.Parameters.AddWithValue("@deferred", deferredErrorCode);
                command.Parameters.AddWithValue("@attempt", minimumAttempt);
                command.Parameters.AddWithValue("@terminal", terminalErrorCode);
                command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
                command.Parameters.AddWithValue("@now", now.UtcTicks);
                command.Parameters.AddWithValue(
                    "@cooldownDue",
                    now.Add(CloudPlaceholderRediscoveryCooldown).UtcTicks);
                command.Parameters.AddWithValue(
                    "@cloudTerminal",
                    terminalErrorCode == "CLOUD_PLACEHOLDER_RETRY_EXHAUSTED" ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            finalizedCount = fileIds.Count;
        }
        finally
        {
            _writeGate.Release();
        }

        return finalizedCount;
    }

    // Older builds could create a FILE_NOT_FOUND dead-letter row but leave its
    // matching queue row behind.  The scheduler correctly refuses that row,
    // while progress used to count it as pending forever.  Close only the
    // exact, unleased terminal pair.  A later discovery of the same path
    // clears FILE_NOT_FOUND and requeues it in FinalizeDiscoveryRunAsync.
    private static async ValueTask FinalizeLegacyFileNotFoundQueueAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.UtcTicks;
        await using var command = Command(connection, transaction, """
            UPDATE files AS f
               SET metadata_state=3,extractor_version=@extractor,error_code='FILE_NOT_FOUND'
             WHERE f.state=0
               AND EXISTS(
                   SELECT 1
                     FROM index_queue q JOIN dead_letter d ON d.file_id=q.file_id
                    WHERE q.file_id=f.id
                      AND q.extractor_version=@extractor
                      AND d.extractor_version=q.extractor_version
                      AND d.fingerprint=q.fingerprint
                      AND d.error_code='FILE_NOT_FOUND'
                      AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now));
            DELETE FROM index_queue
             WHERE extractor_version=@extractor
               AND (lease_until_utc IS NULL OR lease_until_utc<=@now)
               AND EXISTS(
                   SELECT 1 FROM dead_letter d
                    WHERE d.file_id=index_queue.file_id
                      AND d.extractor_version=index_queue.extractor_version
                      AND d.fingerprint=index_queue.fingerprint
                      AND d.error_code='FILE_NOT_FOUND');
            """);
        command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SkipMetadataLeaseAsync(
        long fileId,
        string leaseOwner,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await using var command = Command(connection, transaction, """
                UPDATE files
                   SET metadata_state=0,extractor_version=@extractor,
                       extracted_utc=@now,error_code=@reason
                 WHERE id=@file AND EXISTS(
                       SELECT 1 FROM index_queue q
                        WHERE q.file_id=@file AND q.lease_owner=@owner
                          AND q.lease_until_utc>@now);
                DELETE FROM index_queue
                 WHERE file_id=@file AND lease_owner=@owner AND lease_until_utc>@now;
                """);
            command.Parameters.AddWithValue("@file", fileId);
            command.Parameters.AddWithValue("@owner", leaseOwner);
            command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
            command.Parameters.AddWithValue("@now", now.UtcTicks);
            command.Parameters.AddWithValue("@reason", reason);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException(
                    "The metadata lease is missing, expired, or belongs to another owner.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask EnqueueMetadataAsync(
        string fullPath,
        string reason,
        DateTimeOffset? dueUtc = null,
        CancellationToken cancellationToken = default) =>
        EnqueueMetadataAsync(fullPath, reason, PriorityForReason(reason), dueUtc, cancellationToken);

    public async ValueTask EnqueueMetadataAsync(
        string fullPath,
        string reason,
        MetadataQueuePriority priority,
        DateTimeOffset? dueUtc = null,
        CancellationToken cancellationToken = default)
    {
        var norm = IndexNormalization.PathKey(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO index_queue(file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT id,@reason,@priority,@due,0,0,
                       printf('%lld:%lld:%s:%d',size,modified_utc,coalesce(file_identity,''),@extractor),@extractor
                  FROM files f WHERE path_norm=@path AND state=0
                   AND NOT EXISTS(SELECT 1 FROM dead_letter d WHERE d.file_id=f.id
                     AND d.fingerprint=printf('%lld:%lld:%s:%d',f.size,f.modified_utc,coalesce(f.file_identity,''),@extractor)
                     AND d.extractor_version=@extractor)
                ON CONFLICT(file_id) DO UPDATE SET reason=@reason,
                    priority=min(index_queue.priority,@priority),due_utc=min(index_queue.due_utc,@due),
                    last_error=NULL;
                """;
            command.Parameters.AddWithValue("@reason", reason);
            command.Parameters.AddWithValue("@priority", (int)priority);
            command.Parameters.AddWithValue("@due", (dueUtc ?? DateTimeOffset.UtcNow).UtcTicks);
            command.Parameters.AddWithValue("@path", norm);
            command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<int> RequeueForExtractorVersionAsync(
        int extractorVersion,
        CancellationToken cancellationToken = default)
    {
        if (extractorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extractorVersion));
        }

        var changed = 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = Command(connection, transaction, """
                DELETE FROM dead_letter WHERE extractor_version<@version;
                INSERT INTO index_queue(file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT id,'ExtractorUpgrade',4,@now,0,0,
                       printf('%lld:%lld:%s:%d',size,modified_utc,coalesce(file_identity,''),@version),@version
                  FROM files WHERE state=0 AND kind IN (0,1) AND extractor_version<@version
                ON CONFLICT(file_id) DO UPDATE SET reason='ExtractorUpgrade',priority=min(index_queue.priority,4),
                    due_utc=@now,attempt=0,failure_count=0,fingerprint=excluded.fingerprint,
                    extractor_version=@version,lease_until_utc=NULL,lease_owner=NULL,last_error=NULL;
                UPDATE files SET metadata_state=1 WHERE state=0 AND extractor_version<@version;
                UPDATE index_state SET extractor_version=@version WHERE singleton_id=1;
                """);
            command.Parameters.AddWithValue("@version", extractorVersion);
            var now = DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue("@now", now.UtcTicks);
            changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
        return changed;
    }

    // RC85 compatibility bridge.  This is intentionally constrained by
    // ExtractorVersions.CanAdoptCompletedCache rather than treating all future
    // extractor upgrades as cache-compatible.
    internal async ValueTask<int> AdoptCompletedCompatibleExtractorCacheAsync(
        int fromVersion,
        int toVersion,
        CancellationToken cancellationToken = default)
    {
        if (!ExtractorVersions.CanAdoptCompletedCache(fromVersion, toVersion))
        {
            return 0;
        }

        var adopted = 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var completed = Command(connection, transaction, """
                SELECT EXISTS(
                    SELECT 1 FROM index_maintenance_state WHERE key=@marker);
                """))
            {
                completed.Parameters.AddWithValue("@marker", ExtractorVersions.Rc86CompatibleCacheAdoptionMarker);
                if (Convert.ToInt32(
                        await completed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return 0;
                }
            }

            // Evaluate compatibility before the generic requeue.  Requeueing
            // first resets reason/attempt/failure/fingerprint and would erase
            // the evidence that a Modified/New/retry row must be re-extracted.
            await using (var candidates = Command(connection, transaction, """
                CREATE TEMP TABLE IF NOT EXISTS rc86_adoption_candidates(
                    file_id INTEGER PRIMARY KEY);
                DELETE FROM rc86_adoption_candidates;
                INSERT INTO rc86_adoption_candidates(file_id)
                SELECT f.id
                  FROM files f
                 WHERE f.state=0
                   AND f.kind IN (0,1)
                   AND f.extractor_version=@from
                   AND f.extracted_utc IS NOT NULL
                   AND f.error_code IS NULL
                   AND (
                       (f.metadata_state=1 AND EXISTS(
                           SELECT 1 FROM index_queue q
                            WHERE q.file_id=f.id
                              AND q.reason='ExtractorUpgrade'
                              AND q.extractor_version=@to
                              AND q.attempt=0
                              AND q.failure_count=0
                              AND q.lease_owner IS NULL
                              AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now)
                              AND q.fingerprint=printf('%lld:%lld:%s:%d',
                                  f.size,f.modified_utc,coalesce(f.file_identity,''),@to)))
                       OR
                       (f.metadata_state=0 AND NOT EXISTS(
                           SELECT 1 FROM index_queue q WHERE q.file_id=f.id)));
                UPDATE files
                   SET metadata_state=0,extractor_version=@to
                 WHERE id IN (SELECT file_id FROM rc86_adoption_candidates);
                """))
            {
                candidates.Parameters.AddWithValue("@from", fromVersion);
                candidates.Parameters.AddWithValue("@to", toVersion);
                candidates.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
                await candidates.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var count = Command(connection, transaction, """
                SELECT count(*) FROM rc86_adoption_candidates;
                """))
            {
                adopted = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }

            await using (var delete = Command(connection, transaction, """
                DELETE FROM index_queue
                 WHERE file_id IN (SELECT file_id FROM rc86_adoption_candidates);
                DELETE FROM rc86_adoption_candidates;
                INSERT INTO index_maintenance_state(key,value,updated_utc)
                VALUES(@marker,'complete',@now);
                """))
            {
                delete.Parameters.AddWithValue("@marker", ExtractorVersions.Rc86CompatibleCacheAdoptionMarker);
                delete.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        return adopted;
    }

    public async ValueTask RequeueChangedFileAsync(
        MetadataWorkLease lease,
        SwSearch.Contracts.FileCandidate current,
        CancellationToken cancellationToken = default)
    {
        _ = await RequeueChangedFileCoreAsync(lease, current, fileIdentity: null, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<bool> TryRequeueChangedFileAsync(
        MetadataWorkLease lease,
        StableFileEvidence current,
        CancellationToken cancellationToken = default) =>
        await RequeueChangedFileCoreAsync(lease, current.Candidate, current.FileIdentity, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<bool> RequeueChangedFileCoreAsync(
        MetadataWorkLease lease,
        SwSearch.Contracts.FileCandidate current,
        string? fileIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        IndexNormalization.ValidateCandidate(current);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await using var command = Command(connection, transaction, """
                DELETE FROM dead_letter
                 WHERE file_id=@file AND EXISTS(
                       SELECT 1 FROM index_queue q
                        WHERE q.file_id=@file AND q.lease_owner=@owner
                          AND q.lease_until_utc>@leaseNow);
                UPDATE files SET size=@size,modified_utc=@modified,
                       file_identity=coalesce(@identity,file_identity),metadata_state=1,
                       extracted_utc=NULL,error_code=NULL
                 WHERE id=@file AND EXISTS(
                       SELECT 1 FROM index_queue q
                        WHERE q.file_id=@file AND q.lease_owner=@owner
                          AND q.lease_until_utc>@leaseNow);
                UPDATE index_queue SET reason='Modified',priority=2,due_utc=@now,attempt=0,
                       failure_count=0,fingerprint=printf('%lld:%lld:%s:%d',@size,@modified,
                            coalesce((SELECT file_identity FROM files WHERE id=@file),''),@extractor),
                       extractor_version=@extractor,lease_until_utc=NULL,lease_owner=NULL,last_error=NULL
                 WHERE file_id=@file AND lease_owner=@owner AND lease_until_utc>@leaseNow;
                """);
            command.Parameters.AddWithValue("@file", lease.FileId);
            command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
            command.Parameters.AddWithValue("@size", current.Size);
            command.Parameters.AddWithValue("@modified", UtcTicks(current.LastWriteTimeUtc));
            command.Parameters.AddWithValue("@identity", (object?)fileIdentity ?? DBNull.Value);
            command.Parameters.AddWithValue("@now", now.UtcTicks);
            command.Parameters.AddWithValue("@leaseNow", now.UtcTicks);
            command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
            var requeued = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return requeued;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<int> EnqueueSearchCandidatesAsync(
        SwSearch.Contracts.SearchQuery query,
        int maximumItems = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (maximumItems is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        var term = IndexNormalization.Text(query.Text);
        if (term.Length == 0)
        {
            return 0;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO index_queue(file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'ActiveSearch',1,@now,0,0,
                       printf('%lld:%lld:%s:%d',f.size,f.modified_utc,coalesce(f.file_identity,''),@extractor),@extractor
                 FROM files f
                 WHERE f.state=0 AND f.kind IN (0,1) AND (
                       f.path_norm LIKE @contains ESCAPE '\'
                       OR EXISTS(
                           SELECT 1 FROM properties p
                            WHERE p.file_id=f.id
                              AND (p.name_norm LIKE @contains ESCAPE '\'
                                   OR p.value_norm LIKE @contains ESCAPE '\'))
                        )
                   AND (f.metadata_state<>0 OR f.extractor_version<@extractor)
                   AND NOT EXISTS(SELECT 1 FROM dead_letter d WHERE d.file_id=f.id
                       AND d.fingerprint=printf('%lld:%lld:%s:%d',f.size,f.modified_utc,coalesce(f.file_identity,''),@extractor)
                       AND d.extractor_version=@extractor)
                 ORDER BY CASE WHEN f.path_norm LIKE @contains ESCAPE '\' THEN 0 ELSE 1 END,f.modified_utc DESC
                 LIMIT @limit
                ON CONFLICT(file_id) DO UPDATE SET
                    reason=CASE WHEN index_queue.priority<1 THEN index_queue.reason ELSE 'ActiveSearch' END,
                    priority=min(index_queue.priority,1),
                    due_utc=@now,
                    attempt=CASE WHEN index_queue.failure_count>0 OR index_queue.last_error IS NOT NULL
                                      OR index_queue.fingerprint<>excluded.fingerprint
                                      OR index_queue.extractor_version<>excluded.extractor_version
                                 THEN 0 ELSE index_queue.attempt END,
                    failure_count=CASE WHEN index_queue.failure_count>0 OR index_queue.last_error IS NOT NULL
                                            OR index_queue.fingerprint<>excluded.fingerprint
                                            OR index_queue.extractor_version<>excluded.extractor_version
                                       THEN 0 ELSE index_queue.failure_count END,
                    fingerprint=excluded.fingerprint,
                    extractor_version=excluded.extractor_version,
                    lease_until_utc=CASE WHEN index_queue.failure_count>0 OR index_queue.last_error IS NOT NULL
                                             OR index_queue.fingerprint<>excluded.fingerprint
                                             OR index_queue.extractor_version<>excluded.extractor_version
                                        THEN NULL ELSE index_queue.lease_until_utc END,
                    lease_owner=CASE WHEN index_queue.failure_count>0 OR index_queue.last_error IS NOT NULL
                                         OR index_queue.fingerprint<>excluded.fingerprint
                                         OR index_queue.extractor_version<>excluded.extractor_version
                                    THEN NULL ELSE index_queue.lease_owner END,
                    last_error=NULL
                 WHERE index_queue.priority>1
                    OR index_queue.failure_count>0
                    OR index_queue.last_error IS NOT NULL
                    OR index_queue.fingerprint<>excluded.fingerprint
                    OR index_queue.extractor_version<>excluded.extractor_version;
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
            command.Parameters.AddWithValue("@contains", "%" + EscapeLike(term) + "%");
            command.Parameters.AddWithValue("@limit", maximumItems);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.discovery_generation,s.last_success_utc,s.source_state,
                    (SELECT COUNT(*) FROM files WHERE state=0 AND kind IN (0,1)),(SELECT COUNT(*) FROM index_queue)
              FROM index_state s WHERE singleton_id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("索引状态缺失。");
        }

        var sourceState = reader.GetString(2);
        return new IndexStatus(
            reader.GetInt64(0),
            !sourceState.Equals("ready", StringComparison.Ordinal),
            reader.IsDBNull(1) ? null : FromUtcTicks(reader.GetInt64(1)),
            sourceState,
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    public async ValueTask<MetadataProgressSnapshot> GetMetadataProgressAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            -- One exclusive outcome per active PRT/ASM row keeps every
            -- published batch partitioned: settled + pending = discovered.
            -- A terminal row remains name-searchable through the core index
            -- (and its sidecar projection), even when its metadata was not
            -- readable, so it is durable readable-cache coverage as well.
            -- A cloud placeholder parked in its rediscovery cooldown is
            -- settled work too (MetadataProgressSnapshot's documented
            -- contract: intentionally unread metadata is terminal work,
            -- distinct from parse failures). Counting it as pending kept
            -- the broker on its responsive base cadence forever and froze
            -- both user-visible percentages below 100% for files that can
            -- never be extracted while they stay cloud-only; the hourly
            -- cooldown retry continues regardless of this classification.
            WITH classified AS (
                SELECT f.extracted_utc,
                       CASE
                           WHEN f.extractor_version=@extractor
                                AND f.metadata_state=0
                                AND f.extracted_utc IS NOT NULL
                                AND f.error_code IS NULL THEN 1
                           WHEN f.extractor_version=@extractor
                                AND f.metadata_state=0
                                AND f.extracted_utc IS NOT NULL
                                AND f.error_code IS NOT NULL THEN 2
                           WHEN f.extractor_version=@extractor
                                AND (
                                    (f.metadata_state=3 AND f.error_code IN (
                                        'CLOUD_PLACEHOLDER_RETRY_EXHAUSTED',
                                        'DISCOVERY_SUBTREE_SKIPPED',
                                        'FILE_NOT_FOUND')) OR
                                    d.file_id IS NOT NULL) THEN 4
                           WHEN f.extractor_version=@extractor
                                AND f.metadata_state=3 THEN 3
                           ELSE 0
                       END AS outcome
                  FROM files f
                  LEFT JOIN index_queue q
                    ON q.file_id=f.id AND q.extractor_version=@extractor
                  LEFT JOIN dead_letter d
                    ON d.file_id=q.file_id
                   AND d.extractor_version=q.extractor_version
                   AND d.fingerprint=q.fingerprint
                   AND d.error_code='FILE_NOT_FOUND'
                 WHERE f.state=0 AND f.kind IN (0,1)
            )
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN outcome=1 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN outcome=2 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN outcome=3 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN outcome=0 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN extracted_utc IS NOT NULL OR outcome<>0
                                     THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN outcome=4 THEN 1 ELSE 0 END),0)
              FROM classified;
            """;
        command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new MetadataProgressSnapshot(0, 0, 0, 0, 0, 0);
        }

        return new MetadataProgressSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(4),
            reader.GetInt64(3),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    private static MetadataQueuePriority PriorityForReason(string reason) => reason switch
    {
        "PropertyViewer" => MetadataQueuePriority.PropertyViewer,
        "ActiveSearch" => MetadataQueuePriority.ActiveSearch,
        "Modified" or "DiscoveryChanged" => MetadataQueuePriority.Modified,
        "New" or "Discovered" => MetadataQueuePriority.New,
        "Retry" => MetadataQueuePriority.Retry,
        _ => MetadataQueuePriority.Historical,
    };
}
