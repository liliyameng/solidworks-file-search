using System.Text;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    private const int SearchSidecarSchemaVersion = 2;
    private const int SearchRebuildSeedBatchSize = 2_000;
    // The sidecar is derived data, but it is also the interactive search path.
    // Keep its commits materially larger than the parser's one-file units so a
    // large initial metadata pass does not leave every keypress on the core
    // fallback.  This changes SQLite write amortisation only; it does not add
    // metadata parsing concurrency.
    private const int SearchProjectionWriteBatchSize = 192;
    // These values remain observable test hooks for the former admission
    // policy.  Projection itself is no longer starved behind metadata: a
    // stale-but-valid sidecar plus a bounded core dirty merge is safer and much
    // faster than an all-core LIKE query while properties are being extracted.
    private const int SearchProjectionFairnessWriteBatchSize = 8;
    private const int SearchProjectionMetadataBacklogHighWater = 128;
    private const int SearchProjectionMetadataBacklogLowWater = 32;
    private static readonly TimeSpan SearchProjectionMetadataBacklogPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SearchProjectionMetadataFairnessInterval = TimeSpan.FromSeconds(30);
    private long _searchProjectionBatchCommitCount;
    private long _searchProjectionBatchFileCount;
    private int _searchProjectionMetadataBacklogDeferred;
    private long _nextSearchProjectionFairnessUtcTicks;
    private long _searchProjectionMetadataBacklogDeferralCount;

    internal long SearchProjectionBatchCommitCountForTesting =>
        Interlocked.Read(ref _searchProjectionBatchCommitCount);

    internal long SearchProjectionBatchFileCountForTesting =>
        Interlocked.Read(ref _searchProjectionBatchFileCount);

    internal int SearchProjectionMetadataBacklogHighWaterForTesting { get; set; } =
        SearchProjectionMetadataBacklogHighWater;

    internal int SearchProjectionMetadataBacklogLowWaterForTesting { get; set; } =
        SearchProjectionMetadataBacklogLowWater;

    internal TimeSpan SearchProjectionMetadataFairnessIntervalForTesting { get; set; } =
        SearchProjectionMetadataFairnessInterval;

    internal long SearchProjectionMetadataBacklogDeferralCountForTesting =>
        Interlocked.Read(ref _searchProjectionMetadataBacklogDeferralCount);

    internal int SearchProjectionFairnessWriteBatchSizeForTesting =>
        SearchProjectionFairnessWriteBatchSize;

    private const string SearchSidecarSchema = """
        CREATE TABLE IF NOT EXISTS search_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
            schema_version INTEGER NOT NULL,
            core_path TEXT NOT NULL,
            core_instance_id TEXT NOT NULL,
            applied_sequence INTEGER NOT NULL DEFAULT 0,
            ready INTEGER NOT NULL DEFAULT 0,
            generation TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS search_files(
            file_id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            path_norm TEXT NOT NULL COLLATE NOCASE,
            kind INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_search_files_kind_path
            ON search_files(kind,path_norm,file_id);
        CREATE TABLE IF NOT EXISTS search_documents(
            document_id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES search_files(file_id) ON DELETE CASCADE,
            source_kind INTEGER NOT NULL,
            configuration_name TEXT,
            file_name TEXT NOT NULL,
            path_norm TEXT NOT NULL,
            names_text TEXT NOT NULL,
            values_text TEXT NOT NULL,
            origin_path TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_search_documents_key
            ON search_documents(
                file_id,source_kind,coalesce(configuration_name,''),coalesce(origin_path,''));
        CREATE INDEX IF NOT EXISTS ix_search_documents_file
            ON search_documents(file_id);
        CREATE INDEX IF NOT EXISTS ix_search_documents_filename
            ON search_documents(source_kind,file_name,file_id);
        CREATE TABLE IF NOT EXISTS search_property_values(
            property_id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES search_files(file_id) ON DELETE CASCADE,
            source_kind INTEGER NOT NULL,
            configuration_name TEXT,
            origin_path TEXT,
            property_name TEXT NOT NULL,
            name_norm TEXT NOT NULL,
            display_value TEXT,
            value_norm TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_search_property_values_file
            ON search_property_values(
                file_id,source_kind,coalesce(configuration_name,''),coalesce(origin_path,''));
        CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
            document_id UNINDEXED,
            file_id UNINDEXED,
            source_kind UNINDEXED,
            configuration_name UNINDEXED,
            file_name,
            path_norm,
            names_text,
            values_text,
            origin_path UNINDEXED,
            tokenize='trigram'
        );
        PRAGMA user_version=2;
        """;

    private sealed record SearchProjectionEvent(long Sequence, long FileId, string Reason);

    private sealed record SearchProjectionItem(
        SearchProjectionEvent Event,
        SearchFileSnapshot? Snapshot);

    private sealed record SearchProjectedDocument(
        int SourceKind,
        string? ConfigurationName,
        string FileName,
        string PathNorm,
        string NamesText,
        string ValuesText,
        string? OriginPath);

    private sealed record SearchProjectedValue(
        int SourceKind,
        string? ConfigurationName,
        string? OriginPath,
        string PropertyName,
        string NameNorm,
        string? DisplayValue,
        string ValueNorm);

    private sealed record SearchFileSnapshot(
        long FileId,
        string Path,
        string PathNorm,
        int Kind,
        IReadOnlyList<SearchProjectedDocument> Documents,
        IReadOnlyList<SearchProjectedValue> Values);

    private sealed class SearchReadLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sequence,
        bool isCurrent) : IAsyncDisposable
    {
        internal SqliteConnection Connection { get; } = connection;
        internal SqliteTransaction Transaction { get; } = transaction;
        internal long Sequence { get; } = sequence;
        internal bool IsCurrent { get; } = isCurrent;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask QueueSearchProjectionCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var enqueue = Command(connection, transaction, """
            DELETE FROM search_projection_outbox WHERE file_id=@file;
            INSERT INTO search_projection_outbox(file_id,reason,queued_utc)
            VALUES(@file,@reason,@now);
            UPDATE search_projection_state
               SET current_sequence=last_insert_rowid()
             WHERE singleton_id=1;
            """);
        enqueue.Parameters.AddWithValue("@file", fileId);
        enqueue.Parameters.AddWithValue("@reason", reason);
        enqueue.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        await enqueue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask InitializeSearchSidecarBestEffortAsync(
        CancellationToken cancellationToken)
    {
        if (_readOnlyExisting || _disposed)
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Never open, clear, or rebuild a potentially huge derived database on
        // the Broker initialization path. The background owner performs the
        // bounded probe and file-level reset; core fallback is available now.
        Volatile.Write(ref _searchProjectionHealth, "rebuilding");
        return ValueTask.CompletedTask;
    }

    private async ValueTask EnsureSearchSidecarAsync(CancellationToken cancellationToken)
    {
        var coreIdentity = await ReadCoreProjectionIdentityAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_searchConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureSearchConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = SearchSidecarSchema;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var reset = false;
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT schema_version,core_path,core_instance_id
                  FROM search_state WHERE singleton_id=1;
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reset =
                    reader.GetInt32(0) != SearchSidecarSchemaVersion ||
                    !string.Equals(
                        Path.GetFullPath(reader.GetString(1)),
                        DatabasePath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(reader.GetString(2), coreIdentity, StringComparison.Ordinal);
            }
        }

        if (reset)
        {
            throw new InvalidOperationException("SEARCH_SIDECAR_RESET_REQUIRED");
        }

        var initialized = 0;
        await using (var initialize = connection.CreateCommand())
        {
            initialize.CommandText = """
                INSERT INTO search_state(
                    singleton_id,schema_version,core_path,core_instance_id,
                    applied_sequence,ready,generation)
                VALUES(1,@schema,@core,@identity,0,0,@generation)
                ON CONFLICT(singleton_id) DO NOTHING;
                """;
            initialize.Parameters.AddWithValue("@schema", SearchSidecarSchemaVersion);
            initialize.Parameters.AddWithValue("@core", DatabasePath);
            initialize.Parameters.AddWithValue("@identity", coreIdentity);
            initialize.Parameters.AddWithValue("@generation", Guid.NewGuid().ToString("N"));
            initialized = await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (initialized > 0)
        {
            await MarkSearchRebuildRequiredAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask MarkSearchRebuildRequiredAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE search_projection_state
                   SET rebuild_required=1,rebuild_cursor=0
                 WHERE singleton_id=1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void StartSearchProjection()
    {
        if (_readOnlyExisting || _disposed)
        {
            return;
        }

        lock (_searchProjectionSync)
        {
            if (_disposed || _searchProjectionCancellation.IsCancellationRequested)
            {
                return;
            }
            if (_searchProjectionTask is { IsCompleted: false })
            {
                _searchProjectionKickPending = true;
                return;
            }
            StartSearchProjectionWorkerUnderLock();
        }
    }

    private void StartSearchProjectionWorkerUnderLock()
    {
        _searchProjectionKickPending = false;
        _searchProjectionTask = Task.Run(RunSearchProjectionWorkerAsync);
    }

    private async Task RunSearchProjectionWorkerAsync()
    {
        try
        {
            await RunSearchProjectionLoopAsync(_searchProjectionCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_searchProjectionSync)
            {
                if (_searchProjectionKickPending &&
                    !_disposed &&
                    !_searchProjectionCancellation.IsCancellationRequested)
                {
                    StartSearchProjectionWorkerUnderLock();
                }
                else
                {
                    _searchProjectionKickPending = false;
                    _searchProjectionTask = null;
                }
            }
        }
    }

    private async Task RunSearchProjectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureSearchSidecarAsync(cancellationToken).ConfigureAwait(false);
                // Do not defer the searchable projection behind the metadata
                // queue.  The projection only consumes already-read rows and
                // runs as one bounded writer; starving it previously forced
                // every interactive property search back through expensive
                // core aggregation for minutes or hours.
                _ = await GetSearchProjectionAdmissionAsync(cancellationToken)
                    .ConfigureAwait(false);
                var projectionBatchLimit = SearchProjectionWriteBatchSize;
                var projections = await ReadSearchProjectionBatchAsync(
                        projectionBatchLimit,
                        cancellationToken)
                    .ConfigureAwait(false);
                var seeded = false;
                if (projections.Count == 0)
                {
                    // Drain the current bounded obligation before seeding the
                    // next 2k window. Full rebuild can never materialize the
                    // entire library in the durable core outbox.
                    seeded = await SeedSearchRebuildBatchAsync(cancellationToken).ConfigureAwait(false);
                    projections = await ReadSearchProjectionBatchAsync(
                            projectionBatchLimit,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (projections.Count > 0)
                {
                    await CommitSearchProjectionBatchAsync(
                        projections,
                        cancellationToken).ConfigureAwait(false);
                    if (await AcknowledgeSearchProjectionBatchAsync(
                            projections.Select(static item => item.Event).ToArray(),
                            cancellationToken).ConfigureAwait(false) > 0)
                    {
                        MarkSearchContentChanged();
                    }
                    await Task.Yield();
                    continue;
                }

                if (seeded || !await TryMarkSearchProjectionReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                Volatile.Write(ref _searchProjectionHealth, "ready");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException exception) when (
                string.Equals(
                    exception.Message,
                    "SEARCH_SIDECAR_SEQUENCE_MISMATCH",
                    StringComparison.Ordinal))
            {
                // A structurally valid sidecar with a stale watermark can be
                // reset transactionally in place. Avoid file deletion here:
                // concurrent stale-while-revalidate readers may legitimately
                // hold the old WAL generation open.
                Volatile.Write(ref _searchProjectionHealth, "rebuilding");
                try
                {
                    await ResetSearchSidecarContentsAsync(cancellationToken).ConfigureAwait(false);
                    await MarkSearchRebuildRequiredAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    return;
                }
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _searchProjectionHealth, "recovery-failed");
                // This worker is best effort.  A lock/IO race while probing a
                // usable sidecar must never delete or reset its contents: that
                // used to turn a complete multi-GB FTS database back into an
                // indefinitely rebuilding one after an ordinary restart.
                // The known, state-proven sequence-mismatch path above still
                // performs its in-place recovery; all other faults preserve
                // the derived database and leave authoritative core fallback.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }
        }
    }

    private enum SearchProjectionAdmission
    {
        Normal,
        DeferredForMetadataBacklog,
        FairnessSlice,
    }

    /// <summary>
    /// Keeps the independently rebuildable FTS sidecar off the disk/CPU path
    /// while durable metadata extraction is backlogged.  This has its own
    /// hysteresis/fairness state rather than sharing the maintenance worker's
    /// reservation, so a sidecar slice can never consume the orphaned
    /// worker's one permitted fairness transaction.
    /// </summary>
    private async ValueTask<SearchProjectionAdmission> GetSearchProjectionAdmissionAsync(
        CancellationToken cancellationToken)
    {
        var highWater = Math.Max(1, SearchProjectionMetadataBacklogHighWaterForTesting);
        var lowWater = Math.Clamp(
            SearchProjectionMetadataBacklogLowWaterForTesting,
            0,
            Math.Max(0, highWater - 1));
        var wasDeferred = Volatile.Read(ref _searchProjectionMetadataBacklogDeferred) != 0;
        var requiredRows = wasDeferred ? lowWater + 1 : highWater;

        if (!await HasAtLeastMetadataQueueRowsAsync(requiredRows, cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _searchProjectionMetadataBacklogDeferred, 0);
            Interlocked.Exchange(ref _nextSearchProjectionFairnessUtcTicks, 0);
            return SearchProjectionAdmission.Normal;
        }

        if (!wasDeferred)
        {
            Volatile.Write(ref _searchProjectionMetadataBacklogDeferred, 1);
            Interlocked.Exchange(
                ref _nextSearchProjectionFairnessUtcTicks,
                DateTimeOffset.UtcNow.Add(GetSearchProjectionFairnessInterval()).UtcTicks);
            Interlocked.Increment(ref _searchProjectionMetadataBacklogDeferralCount);
            return SearchProjectionAdmission.DeferredForMetadataBacklog;
        }

        if (TryReserveSearchProjectionFairnessWindow())
        {
            return SearchProjectionAdmission.FairnessSlice;
        }

        Interlocked.Increment(ref _searchProjectionMetadataBacklogDeferralCount);
        return SearchProjectionAdmission.DeferredForMetadataBacklog;
    }

    private TimeSpan GetSearchProjectionFairnessInterval()
    {
        var interval = SearchProjectionMetadataFairnessIntervalForTesting;
        return interval > TimeSpan.Zero
            ? interval
            : SearchProjectionMetadataFairnessInterval;
    }

    private bool TryReserveSearchProjectionFairnessWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var next = now.Add(GetSearchProjectionFairnessInterval()).UtcTicks;
        while (true)
        {
            var observed = Volatile.Read(ref _nextSearchProjectionFairnessUtcTicks);
            if (observed > now.UtcTicks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _nextSearchProjectionFairnessUtcTicks,
                    next,
                    observed) == observed)
            {
                return true;
            }
        }
    }

    internal async ValueTask<string> GetSearchProjectionAdmissionForTestingAsync(
        CancellationToken cancellationToken = default) =>
        (await GetSearchProjectionAdmissionAsync(cancellationToken).ConfigureAwait(false)).ToString();

    private async ValueTask ResetSearchSidecarContentsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_searchConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureSearchConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var reset = connection.CreateCommand();
        reset.Transaction = transaction;
        reset.CommandText = """
            DELETE FROM search_fts;
            DELETE FROM search_property_values;
            DELETE FROM search_documents;
            DELETE FROM search_files;
            UPDATE search_state
               SET applied_sequence=0,ready=0,generation=@generation
             WHERE singleton_id=1;
            """;
        reset.Parameters.AddWithValue("@generation", Guid.NewGuid().ToString("N"));
        await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _searchProjectionAppliedSequence, 0);
    }

    private async ValueTask<bool> SeedSearchRebuildBatchAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            long cursor;
            int required;
            await using (var state = Command(connection, transaction, """
                SELECT rebuild_required,rebuild_cursor
                  FROM search_projection_state WHERE singleton_id=1;
                """))
            await using (var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                required = reader.GetInt32(0);
                cursor = reader.GetInt64(1);
            }
            if (required == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            long nextCursor;
            await using (var files = Command(connection, transaction, """
                SELECT coalesce(max(id),0)
                  FROM (
                    SELECT id FROM files
                     WHERE id>@cursor
                     ORDER BY id LIMIT @limit);
                """))
            {
                files.Parameters.AddWithValue("@cursor", cursor);
                files.Parameters.AddWithValue("@limit", SearchRebuildSeedBatchSize);
                nextCursor = Convert.ToInt64(
                    await files.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (nextCursor == 0)
            {
                await using var complete = Command(connection, transaction, """
                    UPDATE search_projection_state
                       SET rebuild_required=0,rebuild_cursor=0
                     WHERE singleton_id=1;
                    """);
                await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            // Rebuild windows contain many unrelated files.  Coalesce only
            // when an existing, newer file obligation is already present;
            // do not issue a DELETE + INSERT command pair for every row.  The
            // one set statement keeps the durable dirty delta narrow and
            // removes thousands of SQLite round-trips per 2k seed window.
            await using var insert = Command(connection, transaction, """
                INSERT OR IGNORE INTO search_projection_outbox(file_id,reason,queued_utc)
                SELECT id,'FullRebuild',@now
                  FROM files
                 WHERE id>@cursor AND id<=@nextCursor
                 ORDER BY id;
                """);
            insert.Parameters.AddWithValue("@cursor", cursor);
            insert.Parameters.AddWithValue("@nextCursor", nextCursor);
            insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using (var advance = Command(connection, transaction, """
                UPDATE search_projection_state
                   SET rebuild_cursor=@cursor,
                       current_sequence=CASE
                           WHEN changes()>0 THEN last_insert_rowid()
                           ELSE current_sequence END
                 WHERE singleton_id=1;
                """))
            {
                advance.Parameters.AddWithValue("@cursor", nextCursor);
                await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _searchProjectionHealth, "rebuilding");
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<SearchProjectionItem>> ReadSearchProjectionBatchAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        if (maximumItems is < 1 or > SearchProjectionWriteBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<SearchProjectionEvent>(maximumItems);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT o.sequence,o.file_id,o.reason
                  FROM search_projection_outbox o
                  LEFT JOIN files f ON f.id=o.file_id
                  ORDER BY CASE f.kind
                               WHEN 1 THEN 0
                               WHEN 0 THEN 1
                               ELSE 2 END,
                          o.sequence
                 LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@limit", maximumItems);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new SearchProjectionEvent(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2)));
            }
        }

        var batch = new List<SearchProjectionItem>(events.Count);
        foreach (var projection in events)
        {
            batch.Add(new SearchProjectionItem(
                projection,
                await ReadSearchFileSnapshotAsync(
                    connection,
                    projection.FileId,
                    cancellationToken).ConfigureAwait(false)));
        }
        return batch;
    }

    private async ValueTask<SearchFileSnapshot?> ReadSearchFileSnapshotAsync(
        SqliteConnection connection,
        long fileId,
        CancellationToken cancellationToken)
    {
        string path;
        string pathNorm;
        int kind;
        await using (var file = connection.CreateCommand())
        {
            file.CommandText = """
                SELECT path,path_norm,kind
                  FROM files
                 WHERE id=@file AND state=0 AND kind IN (0,1);
                """;
            file.Parameters.AddWithValue("@file", fileId);
            await using var reader = await file.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            path = reader.GetString(0);
            pathNorm = reader.GetString(1);
            kind = reader.GetInt32(2);
        }

        var documents = new List<SearchProjectedDocument>
        {
            new(
                0,
                null,
                IndexNormalization.Text(Path.GetFileName(path)),
                pathNorm,
                string.Empty,
                string.Empty,
                null),
        };
        var values = new List<SearchProjectedValue>();
        var directGroups = new Dictionary<string, List<SearchProjectedValue>>(StringComparer.Ordinal);
        await using (var direct = connection.CreateCommand())
        {
            direct.CommandText = """
                SELECT configuration_name,name,name_norm,raw_value,resolved_value,
                       display_value,cached_value,is_linked,value_norm
                  FROM properties
                 WHERE file_id=@file AND scope IN (@document,@configuration)
                 ORDER BY coalesce(configuration_name,''),id;
                """;
            direct.Parameters.AddWithValue("@file", fileId);
            direct.Parameters.AddWithValue("@document", (int)PropertyScope.Document);
            direct.Parameters.AddWithValue("@configuration", (int)PropertyScope.Configuration);
            await using var reader = await direct.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var configuration = reader.IsDBNull(0) ? null : reader.GetString(0);
                var value = new SearchProjectedValue(
                    1,
                    configuration,
                    null,
                    reader.GetString(1),
                    reader.GetString(2),
                    SolidWorksPropertyValue.GetResolvedDisplayValue(
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetInt32(7) != 0),
                    reader.GetString(8));
                values.Add(value);
                var key = configuration ?? string.Empty;
                if (!directGroups.TryGetValue(key, out var group))
                {
                    group = [];
                    directGroups.Add(key, group);
                }
                group.Add(value);
            }
        }
        foreach (var group in directGroups)
        {
            documents.Add(new SearchProjectedDocument(
                1,
                group.Key.Length == 0 ? null : group.Key,
                string.Empty,
                string.Empty,
                JoinSearchTerms(group.Value.Select(static item => item.NameNorm)),
                JoinSearchTerms(group.Value.Select(static item => item.ValueNorm)),
                null));
        }

        return new SearchFileSnapshot(fileId, path, pathNorm, kind, documents, values);
    }

    private static string JoinSearchTerms(IEnumerable<string> terms) =>
        string.Join('\u001f', terms);

    private async ValueTask CommitSearchProjectionBatchAsync(
        IReadOnlyList<SearchProjectionItem> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count is < 1 or > SearchProjectionWriteBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batch));
        }

        await using var connection = new SqliteConnection(_searchConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureSearchConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (batch.Any(IsRetiredDrawingTombstone))
        {
            // The v10 contract never exposes drawings. Remove any legacy
            // drawing corpus once as a set operation, then acknowledge its
            // tombstones without issuing thousands of individual FTS deletes.
            // This work remains entirely on the background sidecar owner.
            await using var prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM search_fts
                 WHERE document_id IN (
                    SELECT d.document_id
                      FROM search_documents d
                      JOIN search_files f ON f.file_id=d.file_id
                     WHERE f.kind NOT IN (0,1));
                DELETE FROM search_property_values
                 WHERE file_id IN (
                    SELECT file_id FROM search_files WHERE kind NOT IN (0,1));
                DELETE FROM search_documents
                 WHERE file_id IN (
                    SELECT file_id FROM search_files WHERE kind NOT IN (0,1));
                DELETE FROM search_files WHERE kind NOT IN (0,1);
                """;
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var clear = PreparedSearchCommand(connection, transaction, """
            DELETE FROM search_fts
             WHERE document_id IN (
                SELECT document_id FROM search_documents WHERE file_id=@file);
            DELETE FROM search_property_values WHERE file_id=@file;
            DELETE FROM search_documents WHERE file_id=@file;
            DELETE FROM search_files WHERE file_id=@file;
            """, ("@file", 0L));
        await using var insertFile = PreparedSearchCommand(connection, transaction, """
            INSERT INTO search_files(file_id,path,path_norm,kind)
            VALUES(@file,@path,@norm,@kind);
            """,
            ("@file", 0L), ("@path", string.Empty), ("@norm", string.Empty), ("@kind", 0));
        await using var insertDocument = PreparedSearchCommand(connection, transaction, """
            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,
                names_text,values_text,origin_path)
            VALUES(@file,@source,@configuration,@name,@norm,@names,@values,@origin);
            SELECT last_insert_rowid();
            """,
            ("@file", 0L), ("@source", 0), ("@configuration", DBNull.Value),
            ("@name", string.Empty), ("@norm", string.Empty), ("@names", string.Empty),
            ("@values", string.Empty), ("@origin", DBNull.Value));
        await using var insertFts = PreparedSearchCommand(connection, transaction, """
            INSERT INTO search_fts(
                document_id,file_id,source_kind,configuration_name,file_name,
                path_norm,names_text,values_text,origin_path)
            VALUES(@document,@file,@source,@configuration,@name,@norm,@names,@values,@origin);
            """,
            ("@document", 0L), ("@file", 0L), ("@source", 0),
            ("@configuration", DBNull.Value), ("@name", string.Empty), ("@norm", string.Empty),
            ("@names", string.Empty), ("@values", string.Empty), ("@origin", DBNull.Value));
        await using var insertValue = PreparedSearchCommand(connection, transaction, """
            INSERT INTO search_property_values(
                file_id,source_kind,configuration_name,origin_path,property_name,
                name_norm,display_value,value_norm)
            VALUES(@file,@source,@configuration,@origin,@name,@nameNorm,@value,@valueNorm);
            """,
            ("@file", 0L), ("@source", 0), ("@configuration", DBNull.Value),
            ("@origin", DBNull.Value), ("@name", string.Empty), ("@nameNorm", string.Empty),
            ("@value", DBNull.Value), ("@valueNorm", string.Empty));
        await using var state = PreparedSearchCommand(connection, transaction, """
            UPDATE search_state
               -- `ready` means this sidecar has completed at least one
               -- identity-verified baseline and is safe to read.  Do not
               -- clear it for ordinary dirty rows: readers exclude that small
               -- core outbox delta and merge fresh rows while the writer
               -- catches up.
               -- Projection work is intentionally priority-ordered (assembly,
               -- part, assembly), rather than sequence-ordered.  Never let a
               -- later low-priority batch move the durable watermark backwards:
               -- that would make a fully drained sidecar look permanently
               -- behind the core sequence.
               SET applied_sequence=CASE
                   WHEN applied_sequence > @sequence THEN applied_sequence
                   ELSE @sequence
               END
             WHERE singleton_id=1;
            """, ("@sequence", 0L));

        foreach (var item in batch)
        {
            if (IsRetiredDrawingTombstone(item))
            {
                continue;
            }

            SetSearchParameter(clear, "@file", item.Event.FileId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = item.Snapshot;
            if (snapshot is null)
            {
                continue;
            }

            SetSearchParameter(insertFile, "@file", snapshot.FileId);
            SetSearchParameter(insertFile, "@path", snapshot.Path);
            SetSearchParameter(insertFile, "@norm", snapshot.PathNorm);
            SetSearchParameter(insertFile, "@kind", snapshot.Kind);
            await insertFile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var document in snapshot.Documents)
            {
                SetSearchDocumentParameters(insertDocument, snapshot.FileId, document);
                var documentId = Convert.ToInt64(
                    await insertDocument.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                SetSearchParameter(insertFts, "@document", documentId);
                SetSearchDocumentParameters(insertFts, snapshot.FileId, document);
                await insertFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var value in snapshot.Values)
            {
                SetSearchParameter(insertValue, "@file", snapshot.FileId);
                SetSearchParameter(insertValue, "@source", value.SourceKind);
                SetSearchParameter(insertValue, "@configuration", value.ConfigurationName);
                SetSearchParameter(insertValue, "@origin", value.OriginPath);
                SetSearchParameter(insertValue, "@name", value.PropertyName);
                SetSearchParameter(insertValue, "@nameNorm", value.NameNorm);
                SetSearchParameter(insertValue, "@value", value.DisplayValue);
                SetSearchParameter(insertValue, "@valueNorm", value.ValueNorm);
                await insertValue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // The batch ordering is a UX priority order, not a commit order.  Its
        // final item is therefore not necessarily the newest projected event.
        var appliedSequence = batch.Max(static item => item.Event.Sequence);
        SetSearchParameter(state, "@sequence", appliedSequence);
        await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var observedSequence = Interlocked.Read(ref _searchProjectionAppliedSequence);
        Interlocked.Exchange(
            ref _searchProjectionAppliedSequence,
            Math.Max(observedSequence, appliedSequence));
        Interlocked.Increment(ref _searchProjectionBatchCommitCount);
        Interlocked.Add(ref _searchProjectionBatchFileCount, batch.Count);
    }

    private static bool IsRetiredDrawingTombstone(SearchProjectionItem item) =>
        item.Snapshot is null &&
        string.Equals(
            item.Event.Reason,
            "DrawingSupportRemoved",
            StringComparison.Ordinal);

    private static SqliteCommand PreparedSearchCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        command.Prepare();
        return command;
    }

    private static void SetSearchParameter(
        SqliteCommand command,
        string name,
        object? value) =>
        command.Parameters[name].Value = value ?? DBNull.Value;

    private static void SetSearchDocumentParameters(
        SqliteCommand command,
        long fileId,
        SearchProjectedDocument document)
    {
        SetSearchParameter(command, "@file", fileId);
        SetSearchParameter(command, "@source", document.SourceKind);
        SetSearchParameter(command, "@configuration", document.ConfigurationName);
        SetSearchParameter(command, "@name", document.FileName);
        SetSearchParameter(command, "@norm", document.PathNorm);
        SetSearchParameter(command, "@names", document.NamesText);
        SetSearchParameter(command, "@values", document.ValuesText);
        SetSearchParameter(command, "@origin", document.OriginPath);
    }

    private async ValueTask<int> AcknowledgeSearchProjectionBatchAsync(
        IReadOnlyList<SearchProjectionEvent> projections,
        CancellationToken cancellationToken)
    {
        if (projections.Count is < 1 or > SearchProjectionWriteBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(projections));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, transaction, """
                DELETE FROM search_projection_outbox
                 WHERE sequence=@sequence AND file_id=@file;
                """);
            command.Parameters.AddWithValue("@sequence", 0L);
            command.Parameters.AddWithValue("@file", 0L);
            command.Prepare();
            var acknowledged = 0;
            foreach (var projection in projections)
            {
                SetSearchParameter(command, "@sequence", projection.Sequence);
                SetSearchParameter(command, "@file", projection.FileId);
                acknowledged += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return acknowledged;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<bool> TryMarkSearchProjectionReadyAsync(
        CancellationToken cancellationToken)
    {
        long current;
        string coreIdentity;
        await using (var core = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var state = core.CreateCommand())
        {
            state.CommandText = """
                SELECT current_sequence,core_instance_id
                  FROM search_projection_state
                 WHERE singleton_id=1 AND rebuild_required=0
                    AND NOT EXISTS(SELECT 1 FROM search_projection_outbox);
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            current = reader.GetInt64(0);
            coreIdentity = reader.GetString(1);
        }

        await using var search = new SqliteConnection(_searchConnectionString);
        await search.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureSearchConnectionAsync(search, cancellationToken).ConfigureAwait(false);
        long applied;
        string projectedIdentity;
        await using (var read = search.CreateCommand())
        {
            read.CommandText = """
                SELECT applied_sequence,core_instance_id
                  FROM search_state WHERE singleton_id=1;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("SEARCH_SIDECAR_STATE_MISSING");
            }
            applied = reader.GetInt64(0);
            projectedIdentity = reader.GetString(1);
        }
        if (applied != current ||
            !string.Equals(projectedIdentity, coreIdentity, StringComparison.Ordinal))
        {
            // Never certify content by advancing only its watermark. A lost
            // sidecar commit or restored/replaced core requires a full rebuild.
            throw new InvalidOperationException("SEARCH_SIDECAR_SEQUENCE_MISMATCH");
        }
        await using var mark = search.CreateCommand();
        mark.CommandText = """
            UPDATE search_state SET ready=1
             WHERE singleton_id=1 AND applied_sequence=@sequence
               AND core_instance_id=@identity;
            """;
        mark.Parameters.AddWithValue("@sequence", current);
        mark.Parameters.AddWithValue("@identity", coreIdentity);
        if (await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("SEARCH_SIDECAR_READY_RACE");
        }
        Interlocked.Exchange(ref _searchProjectionAppliedSequence, current);
        return true;
    }

    private async ValueTask<IReadOnlyList<long>> ReadSearchProjectionDirtyFileIdsAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        if (maximumItems is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        await using var core = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = core.CreateCommand();
        command.CommandText = """
            WITH dirty(file_id,sequence) AS (
                SELECT file_id,sequence FROM search_projection_outbox
            )
            SELECT file_id
              FROM dirty
             GROUP BY file_id
             ORDER BY min(sequence)
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", maximumItems);
        var result = new List<long>(maximumItems);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }
        return result;
    }

    private async ValueTask<SearchReadLease?> TryOpenReadySearchConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(SearchDatabasePath))
        {
            StartSearchProjection();
            return null;
        }

        SqliteConnection? search = null;
        try
        {
            search = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = SearchDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                // WAL snapshot isolation is database-wide. A shared page cache
                // adds table-lock semantics that can turn a valid pinned read
                // into SQLITE_LOCKED while the private-cache projector commits;
                // the search fallback would then observe newer core state.
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            await search.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Memory-mapped reads keep search pages file-backed instead of
            // private heap copies for these per-query lease connections.
            await SqliteSchema.ExecuteAsync(search, "PRAGMA mmap_size=134217728;", cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = search.BeginTransaction(deferred: true);
            await using var state = search.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = """
                SELECT applied_sequence,core_instance_id
                  FROM search_state
                 WHERE singleton_id=1 AND ready=1 AND schema_version=@schema
                   AND core_path=@core;
                """;
            state.Parameters.AddWithValue("@schema", SearchSidecarSchemaVersion);
            state.Parameters.AddWithValue("@core", DatabasePath);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                await search.DisposeAsync().ConfigureAwait(false);
                StartSearchProjection();
                return null;
            }
            var sequence = reader.GetInt64(0);
            var projectedIdentity = reader.GetString(1);
            var readiness = await GetCoreProjectionReadinessAsync(
                    sequence,
                    projectedIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!readiness.IdentityMatches ||
                (!readiness.IsCurrent && !readiness.CanServeDirtyDelta))
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                await search.DisposeAsync().ConfigureAwait(false);
                StartSearchProjection();
                return null;
            }
            // A sidecar that has completed a verified baseline stays readable
            // while a bounded dirty outbox is projected.  SearchAsync merges
            // that delta from core; refusing the whole sidecar here previously
            // sent every keypress through a full core aggregation.
            if (readiness.IsCurrent)
            {
                Volatile.Write(ref _searchProjectionHealth, "ready");
            }
            return new SearchReadLease(search, transaction, sequence, readiness.IsCurrent);
        }
        catch
        {
            if (search is not null)
            {
                await search.DisposeAsync().ConfigureAwait(false);
            }
            Volatile.Write(ref _searchProjectionHealth, "unavailable");
            StartSearchProjection();
            return null;
        }
    }

    private async ValueTask<(bool IdentityMatches, bool IsCurrent, bool CanServeDirtyDelta)>
        GetCoreProjectionReadinessAsync(
        long sequence,
        string coreIdentity,
        CancellationToken cancellationToken)
    {
        await using var core = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var state = core.CreateCommand();
        state.CommandText = """
            SELECT core_instance_id,current_sequence,rebuild_required,
                   (SELECT count(*) FROM search_projection_outbox),
                   EXISTS(
                       SELECT 1
                         FROM search_projection_outbox o
                         LEFT JOIN files f ON f.id=o.file_id
                        WHERE o.reason<>'DrawingSupportRemoved' OR f.id IS NOT NULL)
              FROM search_projection_state
             WHERE singleton_id=1;
            """;
        await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, false, false);
        }
        var identityMatches = string.Equals(
            reader.GetString(0),
            coreIdentity,
            StringComparison.Ordinal);
        var currentSequence = reader.GetInt64(1);
        var rebuildRequired = reader.GetInt32(2) != 0;
        var dirtyOutboxCount = reader.GetInt64(3);
        var hasOutbox = dirtyOutboxCount != 0;
        var hasQueryRelevantOutbox = reader.GetInt64(4) != 0;
        // Drawing support was retired in schema v10. A persisted sidecar
        // containing every supported row remains exact while only deleted
        // drawing tombstones await background cleanup. Never apply this
        // exception to a live core row or ordinary outbox event.
        var hasOnlyRetiredDrawingTombstones = hasOutbox && !hasQueryRelevantOutbox;
        var isCurrent = identityMatches &&
                        ((currentSequence == sequence &&
                          !rebuildRequired &&
                          !hasOutbox) ||
                         (hasOnlyRetiredDrawingTombstones &&
                          sequence <= currentSequence));

        // The sidecar can serve a stale snapshot only when every omitted row is
        // represented by the ordinary file outbox.
        var canServeDirtyDelta = identityMatches &&
                                  currentSequence >= sequence &&
                                  !rebuildRequired &&
                                  hasOutbox &&
                                  dirtyOutboxCount <= SearchDirtyDeltaMaximumFiles;
        return (identityMatches, isCurrent, canServeDirtyDelta);
    }

    private async ValueTask<bool> ResetSearchSidecarBestEffortAsync()
    {
        if (string.Equals(DatabasePath, SearchDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        SqliteConnection.ClearAllPools();
        var removed = true;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                var path = SearchDatabasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A reader may still own the old generation. Core remains safe;
                // a later kick retries.
                removed = false;
            }
            catch (UnauthorizedAccessException)
            {
                // Search failure never escalates into a metadata pause.
                removed = false;
            }
        }
        await ValueTask.CompletedTask;
        return removed && !File.Exists(SearchDatabasePath);
    }

    private static async ValueTask ConfigureSearchConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken)
            .ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA busy_timeout=1000;", cancellationToken)
            .ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken)
            .ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken)
            .ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=4096;", cancellationToken)
            .ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<(long Current, long Applied, string Health)> GetSearchProjectionStatusAsync(
        CancellationToken cancellationToken)
    {
        long current = 0;
        try
        {
            await using var core = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = core.CreateCommand();
            command.CommandText = """
                SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;
                """;
            current = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            // Core storage status reports its own fault separately.
        }
        return (
            current,
            Interlocked.Read(ref _searchProjectionAppliedSequence),
            Volatile.Read(ref _searchProjectionHealth));
    }

    private async ValueTask<string> ReadCoreProjectionIdentityAsync(
        CancellationToken cancellationToken)
    {
        await using var core = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = core.CreateCommand();
        command.CommandText = """
            SELECT core_instance_id
              FROM search_projection_state WHERE singleton_id=1;
            """;
        return Convert.ToString(
                   await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? throw new InvalidOperationException("CORE_PROJECTION_IDENTITY_MISSING");
    }
}
