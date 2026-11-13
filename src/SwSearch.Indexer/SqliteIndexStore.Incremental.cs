using System.Globalization;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    public async ValueTask<DiscoveryJournalCheckpoint?> LoadLatestDiscoveryCheckpointAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        string? source;
        await using (var latest = connection.CreateCommand())
        {
            latest.CommandText = """
                SELECT source FROM discovery_journal_checkpoints
                 ORDER BY captured_utc DESC,source LIMIT 1;
                """;
            source = Convert.ToString(await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var cursors = new List<DiscoveryVolumeCursor>();
        long capturedUtc = 0;
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT volume_root,volume_serial,journal_id,next_usn,captured_utc
              FROM discovery_journal_checkpoints WHERE source=@source
             ORDER BY volume_root COLLATE NOCASE;
            """;
        read.Parameters.AddWithValue("@source", source);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            capturedUtc = Math.Max(capturedUtc, reader.GetInt64(4));
            cursors.Add(new DiscoveryVolumeCursor(
                reader.GetString(0),
                ulong.Parse(reader.GetString(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                reader.GetInt64(3),
                checked((uint)reader.GetInt64(1))));
        }

        return cursors.Count == 0
            ? null
            : new DiscoveryJournalCheckpoint(source, cursors, new DateTimeOffset(capturedUtc, TimeSpan.Zero));
    }

    public async ValueTask SaveDiscoveryCheckpointAsync(
        DiscoveryJournalCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.Source);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var delete = Command(
                connection,
                transaction,
                "DELETE FROM discovery_journal_checkpoints WHERE source=@source;"))
            {
                delete.Parameters.AddWithValue("@source", checkpoint.Source);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var insert = Command(connection, transaction, """
                INSERT INTO discovery_journal_checkpoints(
                    source,volume_root,volume_serial,journal_id,next_usn,captured_utc)
                VALUES(@source,@root,@serial,@journal,@next,@captured);
                """);
            foreach (var cursor in checkpoint.Volumes)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@source", checkpoint.Source);
                insert.Parameters.AddWithValue("@root", DiscoveryPath.NormalizeKey(cursor.VolumeRoot));
                insert.Parameters.AddWithValue("@serial", cursor.VolumeSerialNumber);
                insert.Parameters.AddWithValue("@journal", cursor.JournalId.ToString("X16", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("@next", cursor.NextUsn);
                insert.Parameters.AddWithValue("@captured", checkpoint.CapturedAtUtc.UtcTicks);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask ApplyDiscoveryDeletionHintAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        _ = await ApplyDiscoveryDeletionHintBatchAsync([fullPath], cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<DiscoveryHintBatchResult> ApplyDiscoveryDeletionHintBatchAsync(
        IReadOnlyList<string> fullPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        if (fullPaths.Count == 0)
        {
            return default;
        }

        var changed = false;
        var deferredForActiveLease = false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var fullPath in fullPaths)
            {
                var result = await ApplyDiscoveryDeletionHintCoreAsync(
                        connection,
                        transaction,
                        fullPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                changed |= result.Changed;
                deferredForActiveLease |= result.DeferredForActiveLease;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        if (changed)
        {
            MarkSearchContentChanged();
            MarkMetadataProgressBatchPublished();
        }

        return new DiscoveryHintBatchResult(changed, deferredForActiveLease);
    }

    internal async ValueTask<DiscoveryHintBatchResult> ApplyDiscoveryDeltaBatchAsync(
        IReadOnlyList<DiscoveryDelta> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return default;
        }

        var changed = false;
        var deferredForActiveLease = false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Kind == DiscoveryDeltaKind.Upsert)
                {
                    if (change.File is null)
                    {
                        throw new InvalidDataException("DISCOVERY_DELTA_UPSERT_MISSING_FILE");
                    }

                    var result = await ApplyDiscoveryHintCoreAsync(
                            connection,
                            transaction,
                            change.File,
                            cancellationToken)
                        .ConfigureAwait(false);
                    changed |= result.Changed;
                    deferredForActiveLease |= result.DeferredForActiveLease;
                }
                else
                {
                    var result = await ApplyDiscoveryDeletionHintCoreAsync(
                            connection,
                            transaction,
                            change.FullPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    changed |= result.Changed;
                    deferredForActiveLease |= result.DeferredForActiveLease;
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        if (changed)
        {
            MarkSearchContentChanged();
            MarkMetadataProgressBatchPublished();
        }

        return new DiscoveryHintBatchResult(changed, deferredForActiveLease);
    }

    /// <summary>
    /// Replays discovery evidence that was observed while a metadata worker
    /// owned the row. Selection and replay share the write gate, so an eligible
    /// row cannot be leased between its active-lease check and its mutation.
    /// Rows are removed only after the idempotent mutation has committed.
    /// </summary>
    internal async ValueTask<DiscoveryHintBatchResult> ApplyDeferredDiscoveryChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // Take the SQLite writer before reading eligibility. The process
            // gate is not cross-process; BEGIN IMMEDIATE prevents another
            // broker from claiming the old row between this check and replay.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var result = await ApplyDeferredDiscoveryChangesCoreAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Changed)
            {
                MarkSearchContentChanged();
                MarkMetadataProgressBatchPublished();
            }

            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<DiscoveryHintBatchResult> ApplyDeferredDiscoveryChangesCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var deferredForActiveLease = false;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var pending = new List<DeferredDiscoveryChange>();
        await using (var read = Command(connection, transaction, """
            SELECT d.file_id,d.change_kind,d.path,d.path_norm,d.root_path,
                   d.candidate_kind,d.size,d.modified_utc,d.file_identity
              FROM deferred_discovery_changes d
             WHERE NOT EXISTS(
                   SELECT 1 FROM index_queue active
                    WHERE active.file_id=d.file_id
                      AND active.lease_owner IS NOT NULL
                      AND active.lease_until_utc>@now)
             ORDER BY d.recorded_utc,d.file_id
             LIMIT 4096;
            """))
        {
            read.Parameters.AddWithValue("@now", now);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.Add(new DeferredDiscoveryChange(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        foreach (var entry in pending)
        {
            DiscoveryHintBatchResult result;
            if (entry.ChangeKind == DeferredDiscoveryUpsert)
            {
                if (entry.RootPath is null ||
                    entry.CandidateKind is null ||
                    entry.Size is null ||
                    entry.ModifiedUtc is null ||
                    !Enum.IsDefined((SolidWorksDocumentKind)entry.CandidateKind.Value))
                {
                    throw new InvalidDataException("DEFERRED_DISCOVERY_UPSERT_INVALID");
                }

                var candidate = new FileCandidate(
                    entry.FullPath,
                    (SolidWorksDocumentKind)entry.CandidateKind.Value,
                    entry.Size.Value,
                    new DateTimeOffset(entry.ModifiedUtc.Value, TimeSpan.Zero));
                result = await ApplyDiscoveryHintCoreAsync(
                        connection,
                        transaction,
                        new DiscoveredFile(
                            candidate,
                            entry.PathNorm,
                            entry.RootPath,
                            ParseDeferredIdentity(entry.FileIdentity)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (entry.ChangeKind == DeferredDiscoveryDelete)
            {
                result = await ApplyDiscoveryDeletionHintCoreAsync(
                        connection,
                        transaction,
                        entry.FullPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new InvalidDataException("DEFERRED_DISCOVERY_CHANGE_INVALID");
            }

            changed |= result.Changed;
            deferredForActiveLease |= result.DeferredForActiveLease;
            if (result.DeferredForActiveLease)
            {
                continue;
            }

            await using var remove = Command(
                connection,
                transaction,
                "DELETE FROM deferred_discovery_changes WHERE file_id=@file;");
            remove.Parameters.AddWithValue("@file", entry.FileId);
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DiscoveryHintBatchResult(changed, deferredForActiveLease);
    }

    private static FileSystemIdentity? ParseDeferredIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        var separator = identity.IndexOf(':');
        if (separator <= 0 || separator == identity.Length - 1 ||
            !uint.TryParse(
                identity.AsSpan(0, separator),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var volume) ||
            !ulong.TryParse(
                identity.AsSpan(separator + 1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var fileIndex))
        {
            throw new InvalidDataException("DEFERRED_DISCOVERY_IDENTITY_INVALID");
        }

        return new FileSystemIdentity(volume, fileIndex);
    }

    private readonly record struct DeferredDiscoveryChange(
        long FileId,
        int ChangeKind,
        string FullPath,
        string PathNorm,
        string? RootPath,
        int? CandidateKind,
        long? Size,
        long? ModifiedUtc,
        string? FileIdentity);

    private static async ValueTask<DiscoveryHintBatchResult> ApplyDiscoveryDeletionHintCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var pathNorm = IndexNormalization.PathKey(fullPath);
        var changed = false;
        var deferredForActiveLease = false;
            long? fileId = null;
            await using (var find = Command(connection, transaction, """
                SELECT f.id,
                       EXISTS(
                           SELECT 1 FROM index_queue active
                            WHERE active.file_id=f.id
                              AND active.lease_owner IS NOT NULL
                              AND active.lease_until_utc>@now)
                  FROM files f
                 WHERE f.path_norm=@path AND f.state=0;
                """))
            {
                find.Parameters.AddWithValue("@path", pathNorm);
                find.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
                await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    fileId = reader.GetInt64(0);
                    deferredForActiveLease = reader.GetInt32(1) != 0;
                }
            }

            if (deferredForActiveLease)
            {
                // A deleting event must not silently disappear while its
                // current owner still holds the CAS lease. Persist the
                // tombstone before returning so callers may safely advance a
                // durable USN cursor and a crash cannot resurrect the file.
                await PersistDeferredDiscoveryDeletionCoreAsync(
                        connection,
                        transaction,
                        fileId!.Value,
                        fullPath,
                        pathNorm,
                        DateTimeOffset.UtcNow.UtcTicks,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new DiscoveryHintBatchResult(false, true);
            }

            if (fileId.HasValue)
            {
                changed = true;
                await using var mutate = Command(connection, transaction, """
                    UPDATE files SET state=1,metadata_state=1 WHERE id=@file;
                    DELETE FROM index_queue WHERE file_id=@file;
                    """);
                mutate.Parameters.AddWithValue("@file", fileId.Value);
                await mutate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await RebuildSearchFtsCoreAsync(connection, transaction, fileId.Value, cancellationToken)
                    .ConfigureAwait(false);
                await using var clearDeferred = Command(
                    connection,
                    transaction,
                    "DELETE FROM deferred_discovery_changes WHERE file_id=@file;");
                clearDeferred.Parameters.AddWithValue("@file", fileId.Value);
                await clearDeferred.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

        return new DiscoveryHintBatchResult(changed, false);
    }
}
