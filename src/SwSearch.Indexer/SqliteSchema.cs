using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

internal static class SqliteSchema
{
    private const int CurrentVersion = ContractVersions.IndexSchema;

    private const string EnsureDiscoveryJournalCheckpoint = """
        CREATE TABLE IF NOT EXISTS discovery_journal_checkpoints(
            source TEXT NOT NULL,
            volume_root TEXT NOT NULL COLLATE NOCASE,
            volume_serial INTEGER NOT NULL,
            journal_id TEXT NOT NULL,
            next_usn INTEGER NOT NULL,
            captured_utc INTEGER NOT NULL,
            PRIMARY KEY(source, volume_root)
        ) WITHOUT ROWID;

        -- Discovery must never overwrite a valid metadata lease merely to
        -- publish fresher file evidence.  Retain that evidence durably until
        -- the lease reaches a terminal state; this table is deliberately
        -- ensured outside a versioned rebuild so every existing v9 cache gains
        -- the safety invariant on its next open.
        CREATE TABLE IF NOT EXISTS deferred_discovery_changes(
            file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            change_kind INTEGER NOT NULL CHECK(change_kind IN (0,1)),
            path TEXT NOT NULL,
            path_norm TEXT NOT NULL COLLATE NOCASE,
            root_path TEXT,
            candidate_kind INTEGER,
            size INTEGER,
            modified_utc INTEGER,
            file_identity TEXT,
            recorded_utc INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_deferred_discovery_changes_recorded
            ON deferred_discovery_changes(recorded_utc,file_id);

        CREATE TABLE IF NOT EXISTS index_maintenance_state(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_utc INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS interactive_metadata_attempts(
            path_norm TEXT PRIMARY KEY COLLATE NOCASE,
            attempt_id TEXT NOT NULL,
            pipeline_lease_id TEXT NOT NULL,
            baseline_file_id INTEGER,
            baseline_fingerprint TEXT,
            started_utc INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS drawing_projection_queue(
            drawing_file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            reason TEXT NOT NULL,
            queued_utc INTEGER NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 0,
            next_due_utc INTEGER NOT NULL DEFAULT 0,
            last_error TEXT
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_files_identity
            ON files(file_identity) WHERE file_identity IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_files_state_name
            ON files(state,file_name_norm,id);
        CREATE INDEX IF NOT EXISTS ix_discovery_stage_run_identity
            ON discovery_stage(run_id,file_identity) WHERE file_identity IS NOT NULL;

        CREATE TABLE IF NOT EXISTS search_projection_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
            current_sequence INTEGER NOT NULL DEFAULT 0,
            rebuild_required INTEGER NOT NULL DEFAULT 1,
            rebuild_cursor INTEGER NOT NULL DEFAULT 0,
            search_schema_version INTEGER NOT NULL DEFAULT 2,
            core_instance_id TEXT NOT NULL DEFAULT ''
        );
        INSERT OR IGNORE INTO search_projection_state(singleton_id)
        VALUES(1);
        CREATE TABLE IF NOT EXISTS search_projection_outbox(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            file_id INTEGER NOT NULL UNIQUE,
            reason TEXT NOT NULL,
            queued_utc INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_search_projection_outbox_file
            ON search_projection_outbox(file_id,sequence);
        """;

    private const string EnsureQueueScheduler = """
        CREATE INDEX IF NOT EXISTS ix_queue_schedule
            ON index_queue(
                priority,
                schedule_kind_rank,
                (CASE WHEN priority BETWEEN 2 AND 4 THEN schedule_modified_utc ELSE 0 END) DESC,
                due_utc,
                attempt,
                file_id,
                lease_until_utc);

        CREATE TRIGGER IF NOT EXISTS tr_index_queue_schedule_insert
        AFTER INSERT ON index_queue
        BEGIN
            UPDATE index_queue
               SET schedule_kind_rank=coalesce((
                       SELECT CASE f.kind
                           WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END
                         FROM files f WHERE f.id=NEW.file_id),3),
                   schedule_modified_utc=coalesce((
                       SELECT f.modified_utc FROM files f WHERE f.id=NEW.file_id),0)
             WHERE file_id=NEW.file_id;
        END;

        CREATE TRIGGER IF NOT EXISTS tr_files_queue_schedule_update
        AFTER UPDATE OF kind,modified_utc ON files
        WHEN OLD.kind<>NEW.kind OR OLD.modified_utc<>NEW.modified_utc
        BEGIN
            UPDATE index_queue
                       SET schedule_kind_rank=CASE NEW.kind
                       WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END,
                   schedule_modified_utc=NEW.modified_utc
             WHERE file_id=NEW.id;
        END;
        """;

    private const string SchemaV3 = """
        CREATE TABLE schema_info(
            version INTEGER PRIMARY KEY,
            created_utc INTEGER NOT NULL,
            app_version TEXT NOT NULL
        );

        CREATE TABLE index_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
            discovery_generation INTEGER NOT NULL DEFAULT 0,
            last_success_utc INTEGER,
            source_state TEXT NOT NULL DEFAULT 'unavailable',
            last_error_code TEXT,
            extractor_version INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO index_state(singleton_id) VALUES(1);

        CREATE TABLE files(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            path_norm TEXT NOT NULL UNIQUE COLLATE NOCASE,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            modified_utc INTEGER NOT NULL,
            file_identity TEXT,
            discovery_generation INTEGER NOT NULL,
            state INTEGER NOT NULL DEFAULT 0,
            metadata_state INTEGER NOT NULL DEFAULT 0,
            extractor_version INTEGER NOT NULL DEFAULT 0,
            extracted_utc INTEGER,
            error_code TEXT
        );
        CREATE INDEX ix_files_state_kind_name ON files(state, kind, path);
        CREATE INDEX ix_files_modified ON files(state, modified_utc DESC);
        CREATE INDEX ix_files_extractor ON files(state, extractor_version, metadata_state);
        CREATE INDEX ix_files_identity ON files(file_identity) WHERE file_identity IS NOT NULL;

        CREATE TABLE properties(
            id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            scope INTEGER NOT NULL,
            configuration_name TEXT,
            name TEXT NOT NULL,
            name_norm TEXT NOT NULL,
            raw_value TEXT NOT NULL,
            resolved_value TEXT NOT NULL,
            display_value TEXT,
            cached_value TEXT,
            is_linked INTEGER NOT NULL DEFAULT 0,
            value_norm TEXT NOT NULL,
            source INTEGER NOT NULL
        );
        CREATE INDEX ix_properties_file ON properties(file_id);
        CREATE INDEX ix_properties_name_value ON properties(name_norm, value_norm);

        CREATE TABLE configurations(
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            configuration_name TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            PRIMARY KEY(file_id, configuration_name)
        );
        CREATE INDEX ix_configurations_file_ordinal ON configurations(file_id, ordinal);

        CREATE TABLE document_references(
            id INTEGER PRIMARY KEY,
            drawing_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            target_path TEXT NOT NULL,
            target_path_norm TEXT NOT NULL COLLATE NOCASE,
            target_file_name_norm TEXT NOT NULL,
            target_file_id INTEGER REFERENCES files(id) ON DELETE SET NULL,
            referenced_configuration TEXT,
            resolution_state INTEGER NOT NULL
        );
        CREATE INDEX ix_references_drawing ON document_references(drawing_file_id);
        CREATE INDEX ix_references_target ON document_references(target_file_id);
        CREATE INDEX ix_references_target_path ON document_references(target_path_norm);
        CREATE INDEX ix_references_pending_name ON document_references(target_file_id, target_file_name_norm);

        CREATE TABLE effective_properties(
            drawing_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            origin_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            property_id INTEGER NOT NULL REFERENCES properties(id) ON DELETE CASCADE,
            name_norm TEXT NOT NULL,
            value_norm TEXT NOT NULL,
            rule_version INTEGER NOT NULL,
            PRIMARY KEY(drawing_file_id, origin_file_id, property_id)
        );
        CREATE INDEX ix_effective_origin ON effective_properties(origin_file_id);

        CREATE TABLE index_queue(
            file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            reason TEXT NOT NULL,
            priority INTEGER NOT NULL DEFAULT 4 CHECK(priority BETWEEN 0 AND 5),
            due_utc INTEGER NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 0,
            failure_count INTEGER NOT NULL DEFAULT 0,
            fingerprint TEXT NOT NULL,
            extractor_version INTEGER NOT NULL,
            lease_until_utc INTEGER,
            lease_owner TEXT,
            last_error TEXT
        );
        CREATE INDEX ix_queue_due ON index_queue(priority, due_utc, lease_until_utc);

        CREATE TABLE dead_letter(
            file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            fingerprint TEXT NOT NULL,
            extractor_version INTEGER NOT NULL,
            failure_count INTEGER NOT NULL,
            error_code TEXT NOT NULL,
            failed_utc INTEGER NOT NULL
        );

        CREATE TABLE discovery_runs(
            run_id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL,
            started_utc INTEGER NOT NULL,
            completed INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE discovery_stage(
            run_id TEXT NOT NULL REFERENCES discovery_runs(run_id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            path_norm TEXT NOT NULL COLLATE NOCASE,
            root_norm TEXT NOT NULL COLLATE NOCASE,
            file_name_norm TEXT NOT NULL,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            modified_utc INTEGER NOT NULL,
            file_identity TEXT,
            PRIMARY KEY(run_id, path_norm)
        ) WITHOUT ROWID;
        CREATE INDEX ix_discovery_stage_run_root ON discovery_stage(run_id, root_norm);
        CREATE INDEX ix_discovery_stage_run_name ON discovery_stage(run_id, file_name_norm);
        CREATE INDEX ix_discovery_stage_run_identity
            ON discovery_stage(run_id, file_identity) WHERE file_identity IS NOT NULL;
        CREATE TABLE indexed_roots(
            root_norm TEXT PRIMARY KEY COLLATE NOCASE,
            last_success_utc INTEGER NOT NULL,
            discovery_generation INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE drawing_projection_queue(
            drawing_file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            reason TEXT NOT NULL,
            queued_utc INTEGER NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 0,
            next_due_utc INTEGER NOT NULL DEFAULT 0,
            last_error TEXT
        ) WITHOUT ROWID;

        PRAGMA user_version=8;
        """;

    private const string MigrateV6ToV7 = """
        -- RC51 retires the unbounded all-property one/two-character postings.
        -- DROP is transactional: a failure leaves the complete v6 cache intact
        -- and user_version remains 6, so the next startup safely retries.
        DROP TABLE IF EXISTS search_short_grams;
        CREATE TABLE search_short_grams(
            gram_len INTEGER NOT NULL CHECK(gram_len IN (1,2)),
            gram TEXT NOT NULL,
            document_id INTEGER NOT NULL REFERENCES search_documents(id) ON DELETE CASCADE,
            field_mask INTEGER NOT NULL CHECK(field_mask IN (1,2)),
            PRIMARY KEY(gram_len,gram,document_id,field_mask)
        ) WITHOUT ROWID;
        CREATE INDEX ix_search_short_grams_document ON search_short_grams(document_id);
        DELETE FROM search_short_grams_state;
        INSERT INTO search_short_grams_state(singleton_id,last_document_id,ready)
        VALUES(1,0,1);

        CREATE INDEX IF NOT EXISTS ix_files_identity
            ON files(file_identity) WHERE file_identity IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_discovery_stage_run_identity
            ON discovery_stage(run_id,file_identity) WHERE file_identity IS NOT NULL;
        CREATE TABLE IF NOT EXISTS drawing_projection_queue(
            drawing_file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            reason TEXT NOT NULL,
            queued_utc INTEGER NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 0,
            next_due_utc INTEGER NOT NULL DEFAULT 0,
            last_error TEXT
        ) WITHOUT ROWID;

        PRAGMA user_version=7;
        """;

    private const string MigrateV7ToV8 = """
        -- Search is an independently rebuildable sidecar from v8 onward.
        -- The durable core retains only truth and a tombstone-safe outbox.
        DROP TABLE IF EXISTS search_short_grams;
        DROP TABLE IF EXISTS search_short_grams_state;
        DROP TABLE IF EXISTS search_fts;
        DROP TABLE IF EXISTS search_documents;

        ALTER TABLE files ADD COLUMN file_name_norm TEXT
            GENERATED ALWAYS AS (
                upper(substr(path,length(rtrim(path,replace(path,'\','')))+1))
            ) VIRTUAL;
        CREATE INDEX ix_files_state_name
            ON files(state,file_name_norm,id);

        CREATE TABLE search_projection_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
            current_sequence INTEGER NOT NULL DEFAULT 0,
            rebuild_required INTEGER NOT NULL DEFAULT 1,
            rebuild_cursor INTEGER NOT NULL DEFAULT 0,
            search_schema_version INTEGER NOT NULL DEFAULT 2,
            core_instance_id TEXT NOT NULL DEFAULT ''
        );
        INSERT INTO search_projection_state(
            singleton_id,current_sequence,rebuild_required,rebuild_cursor,search_schema_version)
        VALUES(1,0,1,0,2);
        CREATE TABLE search_projection_outbox(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            file_id INTEGER NOT NULL UNIQUE,
            reason TEXT NOT NULL,
            queued_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_search_projection_outbox_file
            ON search_projection_outbox(file_id,sequence);
        PRAGMA user_version=8;
        """;

    private const string MigrateV4ToV5 = """
        CREATE TABLE IF NOT EXISTS search_short_grams(
            gram_len INTEGER NOT NULL CHECK(gram_len IN (1,2)),
            gram TEXT NOT NULL,
            document_id INTEGER NOT NULL REFERENCES search_documents(id) ON DELETE CASCADE,
            field_mask INTEGER NOT NULL CHECK(field_mask IN (1,2)),
            PRIMARY KEY(gram_len,gram,document_id,field_mask)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_search_short_grams_document ON search_short_grams(document_id);
        CREATE TABLE IF NOT EXISTS search_short_grams_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
            last_document_id INTEGER NOT NULL DEFAULT 0,
            ready INTEGER NOT NULL DEFAULT 0
        );
        INSERT OR IGNORE INTO search_short_grams_state(singleton_id,last_document_id,ready) VALUES(1,0,0);
        PRAGMA user_version=5;
        """;

    private const string EnsureV3Indexes = """
        CREATE INDEX IF NOT EXISTS ix_references_pending_name
            ON document_references(target_file_id, target_file_name_norm);
        CREATE INDEX IF NOT EXISTS ix_discovery_stage_run_name
            ON discovery_stage(run_id, file_name_norm);
        CREATE INDEX IF NOT EXISTS ix_search_documents_filename
            ON search_documents(source_kind, file_name, file_id);
        """;

    private const string MigrateV1ToV2 = """
        ALTER TABLE index_state ADD COLUMN extractor_version INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE files ADD COLUMN file_identity TEXT;
        ALTER TABLE files ADD COLUMN extractor_version INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE properties ADD COLUMN display_value TEXT;
        ALTER TABLE properties ADD COLUMN cached_value TEXT;
        ALTER TABLE properties ADD COLUMN is_linked INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE index_queue ADD COLUMN priority INTEGER NOT NULL DEFAULT 4;
        ALTER TABLE index_queue ADD COLUMN failure_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE index_queue ADD COLUMN fingerprint TEXT NOT NULL DEFAULT '';
        ALTER TABLE index_queue ADD COLUMN extractor_version INTEGER NOT NULL DEFAULT 0;

        UPDATE index_queue
           SET priority=CASE reason
               WHEN 'PropertyViewer' THEN 0 WHEN 'ActiveSearch' THEN 1
               WHEN 'DiscoveryChanged' THEN 2 WHEN 'Discovered' THEN 3 ELSE 4 END,
               fingerprint=(SELECT printf('%lld:%lld::0', size, modified_utc)
                              FROM files WHERE files.id=index_queue.file_id);

        CREATE INDEX ix_files_extractor ON files(state, extractor_version, metadata_state);
        DROP INDEX IF EXISTS ix_queue_due;
        CREATE INDEX ix_queue_due ON index_queue(priority, due_utc, lease_until_utc);

        CREATE TABLE dead_letter(
            file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            fingerprint TEXT NOT NULL,
            extractor_version INTEGER NOT NULL,
            failure_count INTEGER NOT NULL,
            error_code TEXT NOT NULL,
            failed_utc INTEGER NOT NULL
        );
        CREATE TABLE discovery_runs(
            run_id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL,
            started_utc INTEGER NOT NULL,
            completed INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE discovery_stage(
            run_id TEXT NOT NULL REFERENCES discovery_runs(run_id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            path_norm TEXT NOT NULL COLLATE NOCASE,
            root_norm TEXT NOT NULL COLLATE NOCASE,
            file_name_norm TEXT NOT NULL,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            modified_utc INTEGER NOT NULL,
            file_identity TEXT,
            PRIMARY KEY(run_id, path_norm)
        ) WITHOUT ROWID;
        CREATE INDEX ix_discovery_stage_run_root ON discovery_stage(run_id, root_norm);
        CREATE TABLE indexed_roots(
            root_norm TEXT PRIMARY KEY COLLATE NOCASE,
            last_success_utc INTEGER NOT NULL,
            discovery_generation INTEGER NOT NULL
        ) WITHOUT ROWID;

        DROP TABLE search_fts;
        CREATE TABLE search_documents(
            id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            source_kind INTEGER NOT NULL,
            configuration_name TEXT,
            file_name TEXT NOT NULL,
            path_norm TEXT NOT NULL,
            names_text TEXT NOT NULL,
            values_text TEXT NOT NULL,
            origin_path TEXT
        );
        CREATE UNIQUE INDEX ux_search_documents_key ON search_documents(
            file_id, source_kind, COALESCE(configuration_name,''), COALESCE(origin_path,''));
        CREATE INDEX ix_search_documents_file ON search_documents(file_id);
        CREATE VIRTUAL TABLE search_fts USING fts5(
            document_id UNINDEXED, file_id UNINDEXED, source_kind UNINDEXED,
            configuration_name UNINDEXED, file_name, path_norm, names_text,
            values_text, origin_path UNINDEXED, tokenize='trigram');
        PRAGMA user_version=2;
        """;

    internal static async ValueTask InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ConfigureBeforeMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // foreign_keys is connection-local. Restore it even when a
            // cancellation or DDL failure rolls the transaction back.
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask InitializeCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (version != CurrentVersion)
        {
            await using var transaction = await BeginImmediateTransactionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            versionCommand.Transaction = transaction;
            version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (version == 0)
            {
            await using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = SchemaV3;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 1)
        {
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = MigrateV1ToV2;
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RebuildCompactSearchAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateReferencesToV3Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateConfigurationsToV4Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateShortGramsToV5Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSafeDrawingProjectionToV6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateStorageRecoveryToV7Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 2)
        {
            await MigrateReferencesToV3Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateConfigurationsToV4Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateShortGramsToV5Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSafeDrawingProjectionToV6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateStorageRecoveryToV7Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 3)
        {
            await MigrateConfigurationsToV4Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateShortGramsToV5Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSafeDrawingProjectionToV6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateStorageRecoveryToV7Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 4)
        {
            await MigrateShortGramsToV5Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSafeDrawingProjectionToV6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateStorageRecoveryToV7Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 5)
        {
            await MigrateSafeDrawingProjectionToV6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateStorageRecoveryToV7Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 6)
        {
            await MigrateStorageRecoveryToV7Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 7)
        {
            await MigrateSearchSidecarToV8Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
        else if (version != 8 && version != 9 && version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"INDEX_MIGRATION_FAILED: 数据库 schema={version}，程序支持 {CurrentVersion}。");
        }

            if (version <= 8)
            {
                await MigrateQueueSchedulingToV9Async(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                version = 9;
                await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (version == 9)
            {
                // The deferred discovery journal was deliberately delivered as
                // an idempotent v9 invariant.  A cache created by an earlier
                // v9 build can therefore lack the table, while a later v9
                // cache can contain rows pointing at a drawing.  Ensure the
                // layout before the v10 cleanup so either cache migrates
                // without leaving an FK orphan behind.
                await using (var ensureV9AuxiliaryTables = connection.CreateCommand())
                {
                    ensureV9AuxiliaryTables.Transaction = transaction;
                    ensureV9AuxiliaryTables.CommandText = EnsureDiscoveryJournalCheckpoint;
                    await ensureV9AuxiliaryTables.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await MigrateDrawingSupportRemovalToV10Async(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                await InsertSchemaInfoAsync(connection, transaction, CurrentVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await ThrowIfForeignKeyViolationAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);

        await using (var ensureCheckpoint = connection.CreateCommand())
        {
            ensureCheckpoint.CommandText = EnsureDiscoveryJournalCheckpoint;
            await ensureCheckpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var clearRetiredInteractiveAttempts = connection.CreateCommand())
        {
            clearRetiredInteractiveAttempts.CommandText = """
                DELETE FROM interactive_metadata_attempts
                 WHERE baseline_file_id IS NOT NULL
                   AND NOT EXISTS(SELECT 1 FROM files f WHERE f.id=baseline_file_id);
                """;
            await clearRetiredInteractiveAttempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var ensureQueueScheduler = connection.CreateCommand())
        {
            ensureQueueScheduler.CommandText = EnsureQueueScheduler;
            await ensureQueueScheduler.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var ensureCoreIdentity = connection.CreateCommand())
        {
            ensureCoreIdentity.CommandText = """
                UPDATE search_projection_state
                   SET core_instance_id=@identity
                 WHERE singleton_id=1 AND core_instance_id='';
                """;
            ensureCoreIdentity.Parameters.AddWithValue("@identity", Guid.NewGuid().ToString("N"));
            await ensureCoreIdentity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RepairRetiredDrawingOnlyProjectionStateAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await BackfillLegacyCachedReadableEvidenceAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var recover = connection.CreateCommand();
        recover.CommandText = """
            UPDATE index_queue SET lease_until_utc=NULL, lease_owner=NULL
             WHERE lease_until_utc IS NOT NULL AND lease_until_utc <= @now;
            """;
        recover.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RepairRetiredDrawingOnlyProjectionStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // RC66's v10 migration queued one tombstone per retired drawing and
        // marked the complete search sidecar for a rebuild. The v10 public
        // query contract can only return Parts and Assemblies, so a ready
        // matching sidecar remains authoritative for every observable query;
        // obsolete drawing rows are safely pruned by the background owner.
        //
        // Do not relax a normal rebuild: no rebuild window may have been
        // seeded, and every queued row (if any) must be one of those retired
        // drawing tombstones with its core file already gone. The empty-outbox
        // case covers an interrupted RC66 process after it acknowledged the
        // final tombstone but before it seeded the needless full rebuild.
        // A missing or mismatched sidecar is still detected independently and
        // rebuilt asynchronously by the sidecar owner.
        await using var repair = connection.CreateCommand();
        repair.CommandText = """
            UPDATE search_projection_state
             SET rebuild_required=0,rebuild_cursor=0
             WHERE singleton_id=1 AND rebuild_required=1
               AND rebuild_cursor=0
               AND NOT EXISTS(
                   SELECT 1
                     FROM search_projection_outbox o
                     LEFT JOIN files f ON f.id=o.file_id
                    WHERE o.reason<>'DrawingSupportRemoved' OR f.id IS NOT NULL);
            """;
        await repair.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask BackfillLegacyCachedReadableEvidenceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string marker = "cached-readable-v1";
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM index_maintenance_state WHERE key=@key LIMIT 1;";
            check.Parameters.AddWithValue("@key", marker);
            if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
        }

        await using var transaction = await BeginImmediateTransactionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await using var backfill = connection.CreateCommand();
        backfill.Transaction = transaction;
        backfill.CommandText = """
            UPDATE files
               SET extracted_utc=modified_utc
             WHERE state=0
               AND extracted_utc IS NULL
               AND NOT EXISTS(
                    SELECT 1 FROM index_maintenance_state
                     WHERE key=@key)
                AND (
                    EXISTS(SELECT 1 FROM properties p WHERE p.file_id=files.id)
                    OR EXISTS(SELECT 1 FROM configurations c WHERE c.file_id=files.id)
                );
            INSERT OR IGNORE INTO index_maintenance_state(key,value,updated_utc)
            VALUES(@key,'complete',@now);
            """;
        backfill.Parameters.AddWithValue("@key", marker);
        backfill.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateShortGramsToV5Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = MigrateV4ToV5;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateSafeDrawingProjectionToV6Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // This migration runs against caches that can contain hundreds of
        // thousands of files and millions of grams. Keep the startup
        // transaction bounded: identify only affected drawings, neutralize the
        // unsafe synthetic values, and let the existing low-priority metadata
        // queue rebuild each drawing in short batches. Search additionally
        // verifies every legacy FTS/gram candidate against safe live rows.
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            DROP TABLE IF EXISTS temp.affected_drawing_projection;
            CREATE TEMP TABLE affected_drawing_projection(
                drawing_file_id INTEGER PRIMARY KEY) WITHOUT ROWID;

            INSERT OR IGNORE INTO affected_drawing_projection(drawing_file_id)
            SELECT DISTINCT p.file_id
              FROM properties p JOIN files f ON f.id=p.file_id AND f.kind=2
             WHERE p.scope=2
                OR p.raw_value LIKE '$-%' OR p.resolved_value LIKE '$-%'
                OR coalesce(p.display_value,'') LIKE '$-%'
                OR coalesce(p.cached_value,'') LIKE '$-%';
            INSERT OR IGNORE INTO affected_drawing_projection(drawing_file_id)
            SELECT DISTINCT drawing_file_id FROM document_references
             WHERE referenced_configuration IS NULL;
            INSERT OR IGNORE INTO affected_drawing_projection(drawing_file_id)
            SELECT DISTINCT ep.drawing_file_id
              FROM effective_properties ep
              LEFT JOIN files owner ON owner.id=ep.drawing_file_id
              LEFT JOIN files origin ON origin.id=ep.origin_file_id
             WHERE owner.id IS NULL OR owner.state<>0
                OR origin.id IS NULL OR origin.state<>0 OR origin.metadata_state<>0;

            UPDATE properties SET value_norm=''
             WHERE scope=2
                OR raw_value LIKE '$-%' OR resolved_value LIKE '$-%'
                OR coalesce(display_value,'') LIKE '$-%'
                OR coalesce(cached_value,'') LIKE '$-%';

            DELETE FROM document_references AS unqualified
             WHERE unqualified.referenced_configuration IS NULL
               AND EXISTS(
                   SELECT 1 FROM document_references qualified
                    WHERE qualified.drawing_file_id=unqualified.drawing_file_id
                      AND qualified.target_path_norm=unqualified.target_path_norm
                      AND qualified.referenced_configuration IS NOT NULL);

            DELETE FROM effective_properties AS ep
             WHERE NOT EXISTS(
                       SELECT 1 FROM files owner
                        WHERE owner.id=ep.drawing_file_id AND owner.state=0)
                OR NOT EXISTS(
                       SELECT 1 FROM files origin
                        WHERE origin.id=ep.origin_file_id AND origin.state=0 AND origin.metadata_state=0)
                OR (EXISTS(SELECT 1 FROM properties p WHERE p.id=ep.property_id AND p.scope=1)
                    AND NOT EXISTS(
                        SELECT 1 FROM properties p
                        JOIN document_references r
                          ON r.drawing_file_id=ep.drawing_file_id
                         AND r.target_file_id=ep.origin_file_id
                         AND r.referenced_configuration IS NOT NULL
                         AND r.referenced_configuration=p.configuration_name
                         AND r.resolution_state=0
                         WHERE p.id=ep.property_id));

            UPDATE files SET metadata_state=1
             WHERE id IN (SELECT drawing_file_id FROM affected_drawing_projection);
            INSERT INTO index_queue(
                file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
            SELECT f.id,'DrawingProjectionV6',4,@now,0,0,
                   printf('%lld:%lld:%s:%d',f.size,f.modified_utc,coalesce(f.file_identity,''),@extractor),@extractor
              FROM files f JOIN affected_drawing_projection a ON a.drawing_file_id=f.id
             WHERE f.state=0
            ON CONFLICT(file_id) DO UPDATE SET
                reason='DrawingProjectionV6',priority=min(index_queue.priority,4),
                due_utc=min(index_queue.due_utc,excluded.due_utc),
                fingerprint=excluded.fingerprint,extractor_version=@extractor,
                lease_until_utc=NULL,lease_owner=NULL;

            INSERT OR REPLACE INTO schema_info(version,created_utc,app_version)
            VALUES(6,@now,@appVersion);
            PRAGMA user_version=6;
            """;
        migrate.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        migrate.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        migrate.Parameters.AddWithValue(
            "@appVersion", typeof(SqliteSchema).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateStorageRecoveryToV7Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await EnsureNoIncomingForeignKeysAsync(
            connection,
            transaction,
            "search_short_grams",
            cancellationToken).ConfigureAwait(false);
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = MigrateV6ToV7;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // foreign_key_check still reports violations while enforcement is OFF.
        // Run it before the caller commits so any unexpected dependency/data
        // problem rolls the complete optional-cache retirement back.
        await ThrowIfForeignKeyViolationAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask MigrateSearchSidecarToV8Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await EnsureNoIncomingForeignKeysAsync(
            connection,
            transaction,
            "search_documents",
            cancellationToken,
            ignoredDependentTables: ["search_short_grams"]).ConfigureAwait(false);
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = MigrateV7ToV8;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ThrowIfForeignKeyViolationAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask MigrateQueueSchedulingToV9Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, (string Type, bool NotNull, string? Default)>(
            StringComparer.OrdinalIgnoreCase);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "PRAGMA table_info(index_queue);";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns[reader.GetString(1)] = (
                    reader.GetString(2),
                    reader.GetInt32(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        await AddQueueScheduleColumnIfMissingAsync(
            connection,
            transaction,
            columns,
            "schedule_kind_rank",
            "ALTER TABLE index_queue ADD COLUMN schedule_kind_rank INTEGER NOT NULL DEFAULT 3 CHECK(schedule_kind_rank BETWEEN 0 AND 3);",
            expectedDefault: "3",
            cancellationToken).ConfigureAwait(false);
        await AddQueueScheduleColumnIfMissingAsync(
            connection,
            transaction,
            columns,
            "schedule_modified_utc",
            "ALTER TABLE index_queue ADD COLUMN schedule_modified_utc INTEGER NOT NULL DEFAULT 0;",
            expectedDefault: "0",
            cancellationToken).ConfigureAwait(false);

        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = $"""
            UPDATE index_queue AS q
               SET schedule_kind_rank=CASE f.kind
                       WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END,
                   schedule_modified_utc=f.modified_utc
              FROM files AS f
             WHERE f.id=q.file_id
               AND (q.schedule_kind_rank<>CASE f.kind
                       WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END
                    OR q.schedule_modified_utc<>f.modified_utc);

            {EnsureQueueScheduler}
            PRAGMA user_version=9;
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AddQueueScheduleColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IDictionary<string, (string Type, bool NotNull, string? Default)> columns,
        string columnName,
        string addSql,
        string expectedDefault,
        CancellationToken cancellationToken)
    {
        if (columns.TryGetValue(columnName, out var existing))
        {
            if (!string.Equals(existing.Type, "INTEGER", StringComparison.OrdinalIgnoreCase)
                || !existing.NotNull
                || !string.Equals(
                    existing.Default?.Trim('(', ')', '\'', '"', ' '),
                    expectedDefault,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"INDEX_MIGRATION_FAILED: index_queue.{columnName} 定义不兼容。");
            }

            return;
        }

        await using var add = connection.CreateCommand();
        add.Transaction = transaction;
        add.CommandText = addSql;
        await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        columns[columnName] = ("INTEGER", true, expectedDefault);
    }

    private static async ValueTask MigrateDrawingSupportRemovalToV10Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Keep the old relation tables for cache-layout compatibility during
        // upgrades, but make their contents permanently inert. A sidecar
        // tombstone is written before deleting each retired file id so a stale
        // search database cannot continue to surface an engineering drawing.
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            INSERT INTO search_projection_outbox(file_id,reason,queued_utc)
            SELECT id,'DrawingSupportRemoved',@now
              FROM files
             WHERE kind NOT IN (0,1)
            ON CONFLICT(file_id) DO UPDATE SET
                reason=excluded.reason,
                queued_utc=min(search_projection_outbox.queued_utc,excluded.queued_utc);

            DELETE FROM drawing_projection_queue;
            DELETE FROM effective_properties;
            DELETE FROM document_references;
            DELETE FROM deferred_discovery_changes
             WHERE file_id IN (SELECT id FROM files WHERE kind NOT IN (0,1));
            DELETE FROM dead_letter
             WHERE file_id IN (SELECT id FROM files WHERE kind NOT IN (0,1));
            DELETE FROM index_queue
             WHERE file_id IN (SELECT id FROM files WHERE kind NOT IN (0,1));
            DELETE FROM configurations
             WHERE file_id IN (SELECT id FROM files WHERE kind NOT IN (0,1));
            DELETE FROM properties
             WHERE file_id IN (SELECT id FROM files WHERE kind NOT IN (0,1));
            DELETE FROM discovery_stage WHERE kind NOT IN (0,1);
            DELETE FROM files WHERE kind NOT IN (0,1);

            UPDATE search_projection_state
               SET current_sequence=max(
                       current_sequence,
                       coalesce((SELECT max(sequence) FROM search_projection_outbox),0))
             WHERE singleton_id=1;

            PRAGMA user_version=10;
            """;
        migrate.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureNoIncomingForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? ignoredDependentTables = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.name
             FROM sqlite_schema s
              JOIN pragma_foreign_key_list(s.name) fk
                ON lower(fk."table")=lower(@table)
             WHERE s.type='table' AND s.name<>@table;
            """;
        command.Parameters.AddWithValue("@table", tableName);
        var ignored = ignoredDependentTables is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(ignoredDependentTables, StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dependent = reader.GetString(0);
            if (ignored.Contains(dependent))
            {
                continue;
            }
            throw new InvalidOperationException(
                $"INDEX_MIGRATION_FAILED: {dependent} references optional cache table {tableName}.");
        }
    }

    private static async ValueTask ThrowIfForeignKeyViolationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"INDEX_MIGRATION_FAILED: foreign key violation in table {reader.GetString(0)}.");
        }
    }

    private static async ValueTask ConfigureBeforeMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Keep FK enforcement disabled only while schema DDL is transactional.
        // With foreign_keys=ON SQLite implements DROP TABLE(child) as an
        // implicit row-by-row DELETE before freeing the b-tree. That would turn
        // RC51 retirement of 27M optional gram rows into the very multi-hour,
        // WAL-amplifying transaction this migration must recover from.
        await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=250;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SqliteTransaction> BeginImmediateTransactionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var previousDefaultTimeout = connection.DefaultTimeout;
        connection.DefaultTimeout = 1;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // deferred:false maps to BEGIN IMMEDIATE. Limit each probe to
                    // the provider's minimum finite timeout so cancellation is
                    // not hidden by its 30-second default transaction timeout.
                    return connection.BeginTransaction(deferred: false);
                }
                catch (SqliteException exception) when (
                    exception.SqliteErrorCode is 5 or 6 && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            connection.DefaultTimeout = previousDefaultTimeout;
        }
    }

    internal static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=16384;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_size_limit=67108864;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA auto_vacuum=NONE;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertSchemaInfoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = "INSERT OR REPLACE INTO schema_info(version, created_utc, app_version) VALUES(@version,@created,@app);";
        info.Parameters.AddWithValue("@version", version);
        info.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.UtcTicks);
        info.Parameters.AddWithValue("@app", typeof(SqliteSchema).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        await info.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RebuildCompactSearchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_documents(file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT id,0,NULL,upper(substr(path, length(rtrim(path, replace(path,'\\',''))) + 1)),path_norm,'','',NULL
              FROM files WHERE state=0;
            INSERT INTO search_documents(file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT p.file_id,1,p.configuration_name,'','',group_concat(p.name_norm,char(31)),group_concat(p.value_norm,char(31)),NULL
              FROM properties p JOIN files f ON f.id=p.file_id AND f.state=0
             WHERE p.scope IN (0,1)
             GROUP BY p.file_id,COALESCE(p.configuration_name,'');
            INSERT INTO search_documents(file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT ep.drawing_file_id,2,p.configuration_name,'','',group_concat(p.name_norm,char(31)),group_concat(p.value_norm,char(31)),origin.path
              FROM effective_properties ep
              JOIN properties p ON p.id=ep.property_id
              JOIN files owner ON owner.id=ep.drawing_file_id AND owner.state=0
              JOIN files origin ON origin.id=ep.origin_file_id AND origin.state=0 AND origin.metadata_state=0
             GROUP BY ep.drawing_file_id,ep.origin_file_id,COALESCE(p.configuration_name,'');
            INSERT INTO search_fts(document_id,file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT id,file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path FROM search_documents;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateReferencesToV3Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var column = await ReadReferenceNameColumnAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (column is null)
        {
            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = """
                ALTER TABLE document_references
                    ADD COLUMN target_file_name_norm TEXT NOT NULL DEFAULT '';
                """;
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateReferenceNameColumn(column.Value);
        }

        var references = new List<(long Id, string Path)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id,target_path FROM document_references WHERE target_file_name_norm='';";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                references.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE document_references SET target_file_name_norm=@name WHERE id=@id;";
        var id = update.Parameters.Add("@id", SqliteType.Integer);
        var name = update.Parameters.Add("@name", SqliteType.Text);
        update.Prepare();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = reference.Id;
            name.Value = IndexNormalization.Text(Path.GetFileName(reference.Path));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var finish = connection.CreateCommand();
        finish.Transaction = transaction;
        finish.CommandText = EnsureV3Indexes + "\nPRAGMA user_version=3;";
        await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateConfigurationsToV4Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS configurations(
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                configuration_name TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(file_id, configuration_name)
            );
            CREATE INDEX IF NOT EXISTS ix_configurations_file_ordinal ON configurations(file_id, ordinal);
            DELETE FROM properties
             WHERE configuration_name GLOB 'Config-[0-9]*';
            INSERT OR IGNORE INTO configurations(file_id, configuration_name, ordinal)
            SELECT file_id, configuration_name,
                   row_number() OVER (PARTITION BY file_id ORDER BY configuration_name) - 1
              FROM properties
             WHERE configuration_name IS NOT NULL AND trim(configuration_name) <> ''
             GROUP BY file_id, configuration_name;
            DELETE FROM search_fts;
            DELETE FROM search_documents;
            PRAGMA user_version=4;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await RebuildCompactSearchAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(string Type, bool NotNull)?> ReadReferenceNameColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(document_references);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), "target_file_name_norm", StringComparison.OrdinalIgnoreCase))
            {
                return (reader.GetString(2), reader.GetInt64(3) != 0);
            }
        }

        return null;
    }

    private static void ValidateReferenceNameColumn((string Type, bool NotNull) column)
    {
        if (!string.Equals(column.Type, "TEXT", StringComparison.OrdinalIgnoreCase) || !column.NotNull)
        {
            throw new InvalidOperationException(
                "INDEX_MIGRATION_FAILED: document_references.target_file_name_norm has an incompatible definition.");
        }
    }

    internal static async ValueTask ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
