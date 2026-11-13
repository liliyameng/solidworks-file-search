using Microsoft.Data.Sqlite;
using SwSearch.Contracts;
using System.Text;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    private long _metadataBatchContextCount;

    internal long MetadataBatchContextCountForTesting =>
        Interlocked.Read(ref _metadataBatchContextCount);

    private static bool IsPseudoConfiguration(string name) =>
        name.StartsWith("Config-", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name.AsSpan("Config-".Length), out _);

    public ValueTask UpsertMetadataAsync(
        DocumentMetadata metadata,
        CancellationToken cancellationToken) =>
        UpsertMetadataBatchAsync([metadata], cancellationToken);

    public async ValueTask UpsertMetadataBatchAsync(
        IReadOnlyList<DocumentMetadata> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batch));
        }

        var eligibleBatch = batch
            .Where(static metadata => !DiscoveryPath.IsExcludedSystemPath(metadata.File.FullPath))
            .ToArray();
        if (eligibleBatch.Length == 0)
        {
            return;
        }

        await ThrowIfMetadataPayloadWritesPausedAsync(cancellationToken).ConfigureAwait(false);

        var searchContentChanged = false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var writes = await CreateMetadataBatchCommandsAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            foreach (var metadata in eligibleBatch)
            {
                searchContentChanged |= await UpsertMetadataCoreAsync(
                    metadata,
                    writes,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        if (searchContentChanged)
        {
            MarkSearchContentChanged();
        }
        else
        {
            // Equivalent metadata can still supersede a durable projection
            // token. Wake the sidecar owner independently of UI revision.
            StartSearchProjection();
        }

        ScheduleStorageMaintenanceAfterMetadataCommit();
    }

    internal async ValueTask<InteractiveMetadataCommitToken> BeginInteractiveMetadataAttemptAsync(
        string fullPath,
        Guid pipelineLeaseId,
        CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (pipelineLeaseId == Guid.Empty)
        {
            throw new ArgumentException("A pipeline lease id is required.", nameof(pipelineLeaseId));
        }
        if (DiscoveryPath.IsExcludedSystemPath(normalized))
        {
            throw new ArgumentException("The file is outside the supported index scope.", nameof(fullPath));
        }
        if (!DiscoveryPath.TryGetDocumentKind(normalized, out _))
        {
            throw new ArgumentException("Only .SLDPRT and .SLDASM files can be indexed.", nameof(fullPath));
        }

        var pathNorm = IndexNormalization.PathKey(normalized);
        var attemptId = Guid.NewGuid();
        long? fileId = null;
        string? persistedFingerprint = null;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var select = Command(connection, transaction, """
                SELECT id,size,modified_utc,coalesce(file_identity,'')
                  FROM files WHERE path_norm=@path LIMIT 1;
                """))
            {
                select.Parameters.AddWithValue("@path", pathNorm);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    fileId = reader.GetInt64(0);
                    persistedFingerprint = BuildFingerprint(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3));
                }
            }

            await using var begin = Command(connection, transaction, """
                INSERT INTO interactive_metadata_attempts(
                    path_norm,attempt_id,pipeline_lease_id,baseline_file_id,
                    baseline_fingerprint,started_utc)
                VALUES(@path,@attempt,@pipeline,@file,@fingerprint,@now)
                ON CONFLICT(path_norm) DO UPDATE SET
                    attempt_id=excluded.attempt_id,
                    pipeline_lease_id=excluded.pipeline_lease_id,
                    baseline_file_id=excluded.baseline_file_id,
                    baseline_fingerprint=excluded.baseline_fingerprint,
                    started_utc=excluded.started_utc;
                DELETE FROM dead_letter WHERE file_id=@file;
                UPDATE files
                   SET metadata_state=CASE WHEN metadata_state=3 THEN 1 ELSE metadata_state END,
                       error_code=CASE WHEN metadata_state=3 THEN NULL ELSE error_code END
                 WHERE id=@file;
                """);
            begin.Parameters.AddWithValue("@path", pathNorm);
            begin.Parameters.AddWithValue("@attempt", attemptId.ToString("N"));
            begin.Parameters.AddWithValue("@pipeline", pipelineLeaseId.ToString("N"));
            begin.Parameters.AddWithValue("@file", (object?)fileId ?? DBNull.Value);
            begin.Parameters.AddWithValue("@fingerprint", (object?)persistedFingerprint ?? DBNull.Value);
            begin.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
            await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        return new InteractiveMetadataCommitToken(
            attemptId,
            pipelineLeaseId,
            fileId,
            pathNorm,
            persistedFingerprint);
    }

    internal async ValueTask<bool> TryCommitInteractiveMetadataAsync(
        DocumentMetadata metadata,
        StableFileEvidence before,
        StableFileEvidence after,
        InteractiveMetadataCommitToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(token);
        if (!before.SameObjectAndVersion(after) ||
            !after.Matches(metadata.File) ||
            !string.Equals(
                IndexNormalization.PathKey(before.RequestedPath),
                token.PathNorm,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await ThrowIfMetadataPayloadWritesPausedAsync(cancellationToken).ConfigureAwait(false);
        var searchContentChanged = false;
        var committed = false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var attemptMatches = false;
            await using (var attempt = Command(connection, transaction, """
                SELECT baseline_file_id,baseline_fingerprint
                  FROM interactive_metadata_attempts
                 WHERE path_norm=@path
                   AND attempt_id=@attempt
                   AND pipeline_lease_id=@pipeline;
                """))
            {
                attempt.Parameters.AddWithValue("@path", token.PathNorm);
                attempt.Parameters.AddWithValue("@attempt", token.AttemptId.ToString("N"));
                attempt.Parameters.AddWithValue("@pipeline", token.PipelineLeaseId.ToString("N"));
                await using var reader = await attempt.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var storedFileId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                    var storedFingerprint = reader.IsDBNull(1) ? null : reader.GetString(1);
                    attemptMatches =
                        storedFileId == token.FileId &&
                        string.Equals(
                            storedFingerprint,
                            token.PersistedFingerprint,
                            StringComparison.Ordinal);
                }
            }

            if (attemptMatches &&
                token.FileId is { } existingFileId &&
                await PersistedEvidenceMatchesAsync(
                    connection,
                    transaction,
                    existingFileId,
                    token.PathNorm,
                    token.PersistedFingerprint!,
                    leaseOwner: null,
                    cancellationToken).ConfigureAwait(false))
            {
                await UpdateFileEvidenceCoreAsync(
                    connection,
                    transaction,
                    existingFileId,
                    after,
                    cancellationToken).ConfigureAwait(false);
                searchContentChanged = await UpsertMetadataCoreAsync(
                    metadata,
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                committed = true;
            }
            else if (attemptMatches && token.FileId is null && token.PersistedFingerprint is null)
            {
                await using var exists = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM files WHERE path_norm=@path AND state=0);
                    """);
                exists.Parameters.AddWithValue("@path", token.PathNorm);
                if (Convert.ToInt32(
                        await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                {
                    await using var insert = Command(connection, transaction, """
                        INSERT INTO files(
                            path,path_norm,kind,size,modified_utc,file_identity,
                            discovery_generation,state,metadata_state,extractor_version)
                        SELECT @path,@norm,@kind,@size,@modified,@identity,
                               discovery_generation,0,1,0
                          FROM index_state WHERE singleton_id=1;
                        SELECT last_insert_rowid();
                        """);
                    insert.Parameters.AddWithValue("@path", after.RequestedPath);
                    insert.Parameters.AddWithValue("@norm", token.PathNorm);
                    insert.Parameters.AddWithValue("@kind", (int)after.Kind);
                    insert.Parameters.AddWithValue("@size", after.Size);
                    insert.Parameters.AddWithValue("@modified", UtcTicks(after.LastWriteTimeUtc));
                    insert.Parameters.AddWithValue("@identity", after.FileIdentity);
                    var fileId = Convert.ToInt64(
                        await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    await RebuildSearchFtsCoreAsync(
                        connection,
                        transaction,
                        fileId,
                        cancellationToken).ConfigureAwait(false);
                    searchContentChanged = true;
                    searchContentChanged |= await UpsertMetadataCoreAsync(
                        metadata,
                        connection,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    committed = true;
                }
            }

            await DeleteInteractiveAttemptCoreAsync(
                connection,
                transaction,
                token,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        if (searchContentChanged)
        {
            MarkSearchContentChanged();
        }
        else if (committed)
        {
            StartSearchProjection();
        }

        if (committed)
        {
            ScheduleStorageMaintenanceAfterMetadataCommit();
        }
        return committed;
    }

    internal async ValueTask EndInteractiveMetadataAttemptAsync(
        InteractiveMetadataCommitToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await DeleteInteractiveAttemptCoreAsync(
                connection,
                transaction,
                token,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async ValueTask DeleteInteractiveAttemptCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InteractiveMetadataCommitToken token,
        CancellationToken cancellationToken)
    {
        await using var delete = Command(connection, transaction, """
            DELETE FROM interactive_metadata_attempts
             WHERE path_norm=@path
               AND attempt_id=@attempt
               AND pipeline_lease_id=@pipeline;
            """);
        delete.Parameters.AddWithValue("@path", token.PathNorm);
        delete.Parameters.AddWithValue("@attempt", token.AttemptId.ToString("N"));
        delete.Parameters.AddWithValue("@pipeline", token.PipelineLeaseId.ToString("N"));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlySet<long>> TryCommitMetadataLeaseBatchAsync(
        IReadOnlyList<MetadataCommitItem> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batch));
        }

        await ThrowIfMetadataPayloadWritesPausedAsync(cancellationToken).ConfigureAwait(false);
        var committed = new HashSet<long>();
        var searchContentChanged = false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var writes = await CreateMetadataBatchCommandsAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            foreach (var item in batch)
            {
                if (!item.Evidence.Matches(item.Metadata.File) ||
                    !string.Equals(
                        Path.GetFullPath(item.Lease.FullPath),
                        item.Evidence.RequestedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!await writes.PersistedEvidenceMatchesAsync(
                        item.Lease.FileId,
                        IndexNormalization.PathKey(item.Lease.FullPath),
                        item.Lease.Fingerprint,
                        item.Lease.LeaseOwner,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await writes.UpdateFileEvidenceAsync(
                    item.Lease.FileId,
                    item.Evidence,
                    cancellationToken).ConfigureAwait(false);
                searchContentChanged |= await UpsertMetadataCoreAsync(
                    item.Metadata,
                    writes,
                    cancellationToken).ConfigureAwait(false);
                committed.Add(item.Lease.FileId);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        if (searchContentChanged)
        {
            MarkSearchContentChanged();
        }
        else if (committed.Count > 0)
        {
            StartSearchProjection();
        }

        if (committed.Count > 0)
        {
            ScheduleStorageMaintenanceAfterMetadataCommit();
        }
        return committed;
    }

    private async ValueTask ThrowIfMetadataPayloadWritesPausedAsync(
        CancellationToken cancellationToken)
    {
        var storage = await GetStorageStatusAsync(cancellationToken).ConfigureAwait(false);
        if (storage.MetadataWritesPaused)
        {
            throw new InvalidOperationException($"METADATA_WRITES_PAUSED:{storage.PauseReason}");
        }
    }

    private static async ValueTask<bool> PersistedEvidenceMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        string pathNorm,
        string expectedFingerprint,
        string? leaseOwner,
        CancellationToken cancellationToken)
    {
        var sql = leaseOwner is null
            ? """
                SELECT size,modified_utc,coalesce(file_identity,'')
                  FROM files
                 WHERE id=@file AND path_norm=@path;
                """
            : """
                SELECT f.size,f.modified_utc,coalesce(f.file_identity,'')
                  FROM files f JOIN index_queue q ON q.file_id=f.id
                 WHERE f.id=@file AND f.path_norm=@path AND f.state=0
                   AND q.lease_owner=@owner AND q.fingerprint=@fingerprint
                   AND q.lease_until_utc>@now;
                """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("@file", fileId);
        command.Parameters.AddWithValue("@path", pathNorm);
        if (leaseOwner is not null)
        {
            command.Parameters.AddWithValue("@owner", leaseOwner);
            command.Parameters.AddWithValue("@fingerprint", expectedFingerprint);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
               string.Equals(
                   BuildFingerprint(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)),
                   expectedFingerprint,
                   StringComparison.Ordinal);
    }

    private static async ValueTask UpdateFileEvidenceCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        StableFileEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var update = Command(connection, transaction, """
            UPDATE files
               SET path=@path,path_norm=@norm,kind=@kind,size=@size,modified_utc=@modified,
                   file_identity=@identity,state=0,error_code=NULL
             WHERE id=@file;
            """);
        update.Parameters.AddWithValue("@file", fileId);
        update.Parameters.AddWithValue("@path", evidence.RequestedPath);
        update.Parameters.AddWithValue("@norm", IndexNormalization.PathKey(evidence.RequestedPath));
        update.Parameters.AddWithValue("@kind", (int)evidence.Kind);
        update.Parameters.AddWithValue("@size", evidence.Size);
        update.Parameters.AddWithValue("@modified", UtcTicks(evidence.LastWriteTimeUtc));
        update.Parameters.AddWithValue("@identity", evidence.FileIdentity);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildFingerprint(long size, long modifiedUtcTicks, string identity) =>
        $"{size}:{modifiedUtcTicks}:{identity}:{ExtractorVersions.Current}";

    private sealed class MetadataBatchCommands : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly SqliteCommand _findTarget;
        private readonly SqliteCommand _insertFile;
        private readonly SqliteCommand _clearMetadata;
        private readonly SqliteCommand _insertConfiguration;
        private readonly SqliteCommand _insertProperty;
        private readonly SqliteCommand _makeCurrent;
        private readonly SqliteCommand _complete;
        private readonly SqliteCommand _signatureFile;
        private readonly SqliteCommand _signatureProperties;
        private readonly SqliteCommand _signatureConfigurations;
        private readonly SqliteCommand _verifyLeaseEvidence;
        private readonly SqliteCommand _updateFileEvidence;

        private MetadataBatchCommands(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
            _findTarget = Prepared("""
                SELECT id,
                       extracted_utc IS NOT NULL
                       OR EXISTS(SELECT 1 FROM properties p WHERE p.file_id=files.id)
                  FROM files
                 WHERE path_norm=@path AND state=0;
                """, ("@path", string.Empty));
            _insertFile = Prepared("""
                INSERT INTO files(
                    path, path_norm, kind, size, modified_utc,
                    discovery_generation, state, metadata_state)
                SELECT @path, @norm, @kind, @size, @modified,
                       discovery_generation, 0, 1
                  FROM index_state WHERE singleton_id=1;
                SELECT last_insert_rowid();
                """,
                ("@path", string.Empty), ("@norm", string.Empty), ("@kind", 0),
                ("@size", 0L), ("@modified", 0L));
            _clearMetadata = Prepared("""
                DELETE FROM properties WHERE file_id=@file;
                DELETE FROM configurations WHERE file_id=@file;
                """, ("@file", 0L));
            _insertConfiguration = Prepared("""
                INSERT INTO configurations(file_id, configuration_name, ordinal)
                VALUES(@file, @name, @ordinal);
                """, ("@file", 0L), ("@name", string.Empty), ("@ordinal", 0));
            _insertProperty = Prepared("""
                INSERT INTO properties(
                    file_id, scope, configuration_name, name, name_norm,
                    raw_value, resolved_value, display_value, cached_value, is_linked,
                    value_norm, source)
                VALUES(@file, @scope, @configuration, @name, @nameNorm,
                       @raw, @resolved, @display, @cached, @linked, @valueNorm, @source);
                """,
                ("@file", 0L), ("@scope", 0), ("@configuration", DBNull.Value),
                ("@name", string.Empty), ("@nameNorm", string.Empty), ("@raw", string.Empty),
                ("@resolved", string.Empty), ("@display", DBNull.Value), ("@cached", DBNull.Value),
                ("@linked", 0), ("@valueNorm", string.Empty), ("@source", 0));
            _makeCurrent = Prepared("""
                UPDATE files
                   SET metadata_state=0, extractor_version=@extractor,
                       extracted_utc=@extracted, error_code=@warning
                 WHERE id=@file;
                """,
                ("@extractor", ExtractorVersions.Current), ("@extracted", 0L),
                ("@warning", DBNull.Value), ("@file", 0L));
            _complete = Prepared("""
                UPDATE files
                   SET metadata_state=0, extractor_version=@extractor,
                       extracted_utc=@extracted, error_code=@warning
                 WHERE id=@file;
                DELETE FROM index_queue WHERE file_id=@file;
                DELETE FROM dead_letter WHERE file_id=@file;
                """,
                ("@extractor", ExtractorVersions.Current), ("@extracted", 0L),
                ("@warning", DBNull.Value), ("@file", 0L));
            _signatureFile = Prepared(
                "SELECT path,kind FROM files WHERE id=@file;", ("@file", 0L));
            _signatureProperties = Prepared("""
                SELECT scope,configuration_name,name,raw_value,resolved_value,
                       display_value,cached_value,is_linked,source
                  FROM properties WHERE file_id=@file
                 ORDER BY scope,coalesce(configuration_name,''),name,raw_value,resolved_value,
                          coalesce(display_value,''),coalesce(cached_value,''),is_linked,source;
                """, ("@file", 0L));
            _signatureConfigurations = Prepared("""
                SELECT configuration_name FROM configurations WHERE file_id=@file ORDER BY ordinal;
                """, ("@file", 0L));
            _verifyLeaseEvidence = Prepared("""
                SELECT f.size,f.modified_utc,coalesce(f.file_identity,'')
                  FROM files f JOIN index_queue q ON q.file_id=f.id
                 WHERE f.id=@file AND f.path_norm=@path AND f.state=0
                   AND q.lease_owner=@owner AND q.fingerprint=@fingerprint
                   AND q.lease_until_utc>@now;
                """,
                ("@file", 0L), ("@path", string.Empty), ("@owner", string.Empty),
                ("@fingerprint", string.Empty), ("@now", 0L));
            _updateFileEvidence = Prepared("""
                UPDATE files
                   SET path=@path,path_norm=@norm,kind=@kind,size=@size,modified_utc=@modified,
                       file_identity=@identity,state=0,error_code=NULL
                 WHERE id=@file;
                """,
                ("@path", string.Empty), ("@norm", string.Empty), ("@kind", 0),
                ("@size", 0L), ("@modified", 0L), ("@identity", string.Empty), ("@file", 0L));
        }

        internal SqliteConnection Connection => _connection;

        internal SqliteTransaction Transaction => _transaction;

        internal static async ValueTask<MetadataBatchCommands> CreateAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            return new MetadataBatchCommands(connection, transaction);
        }

        private SqliteCommand Prepared(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            var command = Command(_connection, _transaction, sql);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
            command.Prepare();
            return command;
        }

        private static void Set(SqliteCommand command, string name, object? value) =>
            command.Parameters[name].Value = value ?? DBNull.Value;

        internal async ValueTask<(long? FileId, bool HasCachedMetadata)> FindTargetAsync(
            string pathNorm,
            CancellationToken cancellationToken)
        {
            Set(_findTarget, "@path", pathNorm);
            await using var reader = await _findTarget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? (reader.GetInt64(0), reader.GetInt32(1) != 0)
                : (null, false);
        }

        internal async ValueTask<long> InsertFileAsync(
            DocumentMetadata metadata,
            string pathNorm,
            CancellationToken cancellationToken)
        {
            Set(_insertFile, "@path", metadata.File.FullPath);
            Set(_insertFile, "@norm", pathNorm);
            Set(_insertFile, "@kind", (int)metadata.File.Kind);
            Set(_insertFile, "@size", metadata.File.Size);
            Set(_insertFile, "@modified", UtcTicks(metadata.File.LastWriteTimeUtc));
            return Convert.ToInt64(
                await _insertFile.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        internal async ValueTask ClearMetadataAsync(long fileId, CancellationToken cancellationToken)
        {
            Set(_clearMetadata, "@file", fileId);
            await _clearMetadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask InsertConfigurationAsync(
            long fileId,
            string name,
            int ordinal,
            CancellationToken cancellationToken)
        {
            Set(_insertConfiguration, "@file", fileId);
            Set(_insertConfiguration, "@name", name);
            Set(_insertConfiguration, "@ordinal", ordinal);
            await _insertConfiguration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask InsertPropertyAsync(
            long fileId,
            DocumentProperty property,
            CancellationToken cancellationToken)
        {
            Set(_insertProperty, "@file", fileId);
            Set(_insertProperty, "@scope", (int)property.Scope);
            Set(_insertProperty, "@configuration", property.ConfigurationName);
            Set(_insertProperty, "@name", property.Name);
            Set(_insertProperty, "@nameNorm", IndexNormalization.Text(property.Name));
            Set(_insertProperty, "@raw", property.RawValue);
            Set(_insertProperty, "@resolved", property.ResolvedValue);
            Set(_insertProperty, "@display", property.DisplayValue);
            Set(_insertProperty, "@cached", property.CachedValue);
            Set(_insertProperty, "@linked", property.IsLinked ? 1 : 0);
            var searchableValue = SolidWorksPropertyValue.TryGetResolvedDisplayValue(property, out var safeValue)
                ? safeValue
                : string.Empty;
            Set(_insertProperty, "@valueNorm", IndexNormalization.Text(searchableValue));
            Set(_insertProperty, "@source", (int)property.Source);
            await _insertProperty.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask MakeCurrentAsync(
            long fileId,
            DocumentMetadata metadata,
            CancellationToken cancellationToken)
        {
            Set(_makeCurrent, "@extractor", ExtractorVersions.Current);
            Set(_makeCurrent, "@extracted", UtcTicks(metadata.ExtractedAtUtc));
            Set(_makeCurrent, "@warning", metadata.WarningCode);
            Set(_makeCurrent, "@file", fileId);
            await _makeCurrent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask CompleteAsync(
            long fileId,
            DocumentMetadata metadata,
            CancellationToken cancellationToken)
        {
            Set(_complete, "@extractor", ExtractorVersions.Current);
            Set(_complete, "@extracted", UtcTicks(metadata.ExtractedAtUtc));
            Set(_complete, "@warning", metadata.WarningCode);
            Set(_complete, "@file", fileId);
            await _complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask<string> ReadPersistedSignatureAsync(
            long fileId,
            CancellationToken cancellationToken)
        {
            var signature = new StringBuilder();
            Set(_signatureFile, "@file", fileId);
            await using (var reader = await _signatureFile.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return string.Empty;
                }
                AppendSignatureValue(signature, reader.GetString(0));
                AppendSignatureValue(
                    signature,
                    reader.GetInt32(1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            Set(_signatureProperties, "@file", fileId);
            await using (var reader = await _signatureProperties.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    AppendSignatureValue(signature, "P");
                    for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    {
                        AppendSignatureValue(
                            signature,
                            reader.IsDBNull(ordinal)
                                ? null
                                : Convert.ToString(
                                    reader.GetValue(ordinal),
                                    System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            Set(_signatureConfigurations, "@file", fileId);
            await using (var reader = await _signatureConfigurations.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    AppendSignatureValue(signature, "C");
                    AppendSignatureValue(signature, reader.GetString(0));
                }
            }

            return signature.ToString();
        }

        internal async ValueTask<bool> PersistedEvidenceMatchesAsync(
            long fileId,
            string pathNorm,
            string expectedFingerprint,
            string leaseOwner,
            CancellationToken cancellationToken)
        {
            Set(_verifyLeaseEvidence, "@file", fileId);
            Set(_verifyLeaseEvidence, "@path", pathNorm);
            Set(_verifyLeaseEvidence, "@owner", leaseOwner);
            Set(_verifyLeaseEvidence, "@fingerprint", expectedFingerprint);
            Set(_verifyLeaseEvidence, "@now", DateTimeOffset.UtcNow.UtcTicks);
            await using var reader = await _verifyLeaseEvidence.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                   string.Equals(
                       BuildFingerprint(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)),
                       expectedFingerprint,
                       StringComparison.Ordinal);
        }

        internal async ValueTask UpdateFileEvidenceAsync(
            long fileId,
            StableFileEvidence evidence,
            CancellationToken cancellationToken)
        {
            Set(_updateFileEvidence, "@path", evidence.RequestedPath);
            Set(_updateFileEvidence, "@norm", IndexNormalization.PathKey(evidence.RequestedPath));
            Set(_updateFileEvidence, "@kind", (int)evidence.Kind);
            Set(_updateFileEvidence, "@size", evidence.Size);
            Set(_updateFileEvidence, "@modified", UtcTicks(evidence.LastWriteTimeUtc));
            Set(_updateFileEvidence, "@identity", evidence.FileIdentity);
            Set(_updateFileEvidence, "@file", fileId);
            await _updateFileEvidence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var command in new[]
                     {
                          _findTarget, _insertFile, _clearMetadata,
                          _insertConfiguration, _insertProperty, _makeCurrent, _complete, _signatureFile,
                          _signatureProperties, _signatureConfigurations,
                         _verifyLeaseEvidence, _updateFileEvidence,
                     })
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<bool> UpsertMetadataCoreAsync(
        DocumentMetadata metadata,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var writes = await CreateMetadataBatchCommandsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        return await UpsertMetadataCoreAsync(metadata, writes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MetadataBatchCommands> CreateMetadataBatchCommandsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var commands = await MetadataBatchCommands.CreateAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _metadataBatchContextCount);
        return commands;
    }

    private async ValueTask<bool> UpsertMetadataCoreAsync(
        DocumentMetadata metadata,
        MetadataBatchCommands writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        IndexNormalization.ValidateCandidate(metadata.File);
        var pathNorm = IndexNormalization.PathKey(metadata.File.FullPath);
        var target = await writes.FindTargetAsync(pathNorm, cancellationToken).ConfigureAwait(false);
        var fileId = target.FileId;
        var searchContentChanged = !fileId.HasValue || !target.HasCachedMetadata;
        if (fileId is { } cachedFileId && target.HasCachedMetadata)
        {
            var searchContentUnchanged = string.Equals(
                await writes.ReadPersistedSignatureAsync(fileId.Value, cancellationToken).ConfigureAwait(false),
                BuildIncomingSearchMetadataSignature(metadata),
                StringComparison.Ordinal);
            if (searchContentUnchanged)
            {
                // A duplicate queue delivery can otherwise delete and rewrite
                // every property, configuration and derived search row.
                // Completing the lease still refreshes extraction state and
                // clears retry/dead-letter state, while preserving the already
                // valid durable metadata and projection.
                await writes.CompleteAsync(cachedFileId, metadata, cancellationToken).ConfigureAwait(false);
                return false;
            }

            searchContentChanged = true;
        }
        if (!fileId.HasValue)
        {
            fileId = await writes.InsertFileAsync(metadata, pathNorm, cancellationToken).ConfigureAwait(false);
        }

        await writes.ClearMetadataAsync(fileId.Value, cancellationToken).ConfigureAwait(false);

        var configurationNames = (metadata.ConfigurationNames ?? Array.Empty<string>())
            .Concat(metadata.Properties
                .Where(static property => property.Scope == PropertyScope.Configuration)
                .Select(static property => property.ConfigurationName)
                .OfType<string>())
            .Where(static name => !string.IsNullOrWhiteSpace(name) && !IsPseudoConfiguration(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var ordinal = 0; ordinal < configurationNames.Length; ordinal++)
        {
            await writes.InsertConfigurationAsync(
                fileId.Value,
                configurationNames[ordinal],
                ordinal,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var property in metadata.Properties)
        {
            if (property.Scope is not (PropertyScope.Document or PropertyScope.Configuration) ||
                property.Source == MetadataSource.DrawingCache)
            {
                continue;
            }
            if (property.Scope == PropertyScope.Configuration &&
                property.ConfigurationName is { } configurationName &&
                IsPseudoConfiguration(configurationName))
            {
                continue;
            }
            await writes.InsertPropertyAsync(fileId.Value, property, cancellationToken).ConfigureAwait(false);
        }

        await writes.MakeCurrentAsync(fileId.Value, metadata, cancellationToken).ConfigureAwait(false);

        await RebuildSearchFtsCoreAsync(
            writes.Connection,
            writes.Transaction,
            fileId.Value,
            cancellationToken).ConfigureAwait(false);
        await writes.CompleteAsync(fileId.Value, metadata, cancellationToken).ConfigureAwait(false);
        return searchContentChanged;
    }

    private static async ValueTask<string> ReadPersistedSearchMetadataSignatureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        CancellationToken cancellationToken)
    {
        var signature = new StringBuilder();
        await using (var file = Command(connection, transaction, "SELECT path,kind FROM files WHERE id=@file;"))
        {
            file.Parameters.AddWithValue("@file", fileId);
            await using var reader = await file.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return string.Empty;
            }

            AppendSignatureValue(signature, reader.GetString(0));
            AppendSignatureValue(signature, reader.GetInt32(1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await using (var properties = Command(connection, transaction, """
            SELECT scope,configuration_name,name,raw_value,resolved_value,
                   display_value,cached_value,is_linked,source
              FROM properties WHERE file_id=@file
             ORDER BY scope,coalesce(configuration_name,''),name,raw_value,resolved_value,
                      coalesce(display_value,''),coalesce(cached_value,''),is_linked,source;
            """))
        {
            properties.Parameters.AddWithValue("@file", fileId);
            await using var reader = await properties.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AppendSignatureValue(signature, "P");
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    AppendSignatureValue(signature, reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        await using (var configurations = Command(connection, transaction, """
            SELECT configuration_name FROM configurations WHERE file_id=@file ORDER BY ordinal;
            """))
        {
            configurations.Parameters.AddWithValue("@file", fileId);
            await using var reader = await configurations.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AppendSignatureValue(signature, "C");
                AppendSignatureValue(signature, reader.GetString(0));
            }
        }

        return signature.ToString();
    }

    private static string BuildIncomingSearchMetadataSignature(DocumentMetadata metadata)
    {
        var signature = new StringBuilder();
        AppendSignatureValue(signature, metadata.File.FullPath);
        AppendSignatureValue(signature, ((int)metadata.File.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var property in metadata.Properties
                     .Where(static property =>
                         property.Scope is PropertyScope.Document or PropertyScope.Configuration &&
                         property.Source != MetadataSource.DrawingCache &&
                         (property.Scope != PropertyScope.Configuration ||
                          property.ConfigurationName is null ||
                          !IsPseudoConfiguration(property.ConfigurationName)))
                     .OrderBy(static property => property.Scope)
                     .ThenBy(static property => property.ConfigurationName ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(static property => property.Name, StringComparer.Ordinal)
                     .ThenBy(static property => property.RawValue, StringComparer.Ordinal)
                     .ThenBy(static property => property.ResolvedValue, StringComparer.Ordinal)
                     .ThenBy(static property => property.DisplayValue ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(static property => property.CachedValue ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(static property => property.IsLinked)
                     .ThenBy(static property => property.Source))
        {
            AppendSignatureValue(signature, "P");
            AppendSignatureValue(signature, ((int)property.Scope).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendSignatureValue(signature, property.ConfigurationName);
            AppendSignatureValue(signature, property.Name);
            AppendSignatureValue(signature, property.RawValue);
            AppendSignatureValue(signature, property.ResolvedValue);
            AppendSignatureValue(signature, property.DisplayValue);
            AppendSignatureValue(signature, property.CachedValue);
            AppendSignatureValue(signature, property.IsLinked ? "1" : "0");
            AppendSignatureValue(signature, ((int)property.Source).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var configurationNames = (metadata.ConfigurationNames ?? Array.Empty<string>())
            .Concat(metadata.Properties
                .Where(static property => property.Scope == PropertyScope.Configuration)
                .Select(static property => property.ConfigurationName)
                .OfType<string>())
            .Where(static name => !string.IsNullOrWhiteSpace(name) && !IsPseudoConfiguration(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in configurationNames)
        {
            AppendSignatureValue(signature, "C");
            AppendSignatureValue(signature, name);
        }

        return signature.ToString();
    }

    private static void AppendSignatureValue(StringBuilder signature, string? value)
    {
        if (value is null)
        {
            signature.Append("-1:");
            return;
        }

        signature.Append(value.Length)
            .Append(':')
            .Append(value);
    }

    public async ValueTask<DocumentMetadata?> TryGetMetadataAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var pathNorm = IndexNormalization.PathKey(fullPath);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        long fileId;
        FileCandidate file;
        DateTimeOffset extracted;
        bool hasMetadata;
        string? warning;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, path, kind, size, modified_utc, extracted_utc, error_code
                  FROM files WHERE path_norm=@path AND state=0 AND kind IN (0,1);
                """;
            command.Parameters.AddWithValue("@path", pathNorm);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            fileId = reader.GetInt64(0);
            file = new FileCandidate(
                reader.GetString(1),
                (SolidWorksDocumentKind)reader.GetInt32(2),
                reader.GetInt64(3),
                FromUtcTicks(reader.GetInt64(4)));
            hasMetadata = !reader.IsDBNull(5);
            extracted = hasMetadata ? FromUtcTicks(reader.GetInt64(5)) : DateTimeOffset.MinValue;
            warning = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        if (!hasMetadata)
        {
            return null;
        }

        var properties = new List<DocumentProperty>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name, raw_value, resolved_value, scope, configuration_name, source,
                       display_value, cached_value, is_linked
                  FROM properties WHERE file_id=@file ORDER BY scope, configuration_name, name;
                """;
            command.Parameters.AddWithValue("@file", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                properties.Add(new DocumentProperty(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (PropertyScope)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    (MetadataSource)reader.GetInt32(5),
                    DisplayValue: reader.IsDBNull(6) ? null : reader.GetString(6),
                    CachedValue: reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsLinked: reader.GetInt32(8) != 0));
            }
        }

        var configurationNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT configuration_name FROM configurations WHERE file_id=@file ORDER BY ordinal;";
            command.Parameters.AddWithValue("@file", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) configurationNames.Add(reader.GetString(0));
        }

        return new DocumentMetadata(
            file,
            properties,
            Array.Empty<DocumentReference>(),
            MetadataSource.PersistentCache,
            extracted,
            warning,
            configurationNames);
    }

    private static async ValueTask RebuildSearchFtsCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        CancellationToken cancellationToken)
    {
        await QueueSearchProjectionCoreAsync(
            connection,
            transaction,
            fileId,
            "CoreChanged",
            cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveReferencePath(string ownerPath, string referencedPath)
    {
        if (Path.IsPathFullyQualified(referencedPath))
        {
            return Path.GetFullPath(referencedPath);
        }

        var directory = Path.GetDirectoryName(ownerPath)
            ?? throw new ArgumentException("所有者路径没有目录。", nameof(ownerPath));
        return Path.GetFullPath(Path.Combine(directory, referencedPath));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
