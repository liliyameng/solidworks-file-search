using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    private sealed record StagedDiscoveryItem(
        FileCandidate Candidate,
        string RootPath,
        string? FileIdentity);

    internal readonly record struct DiscoveryHintBatchResult(
        bool Changed,
        bool DeferredForActiveLease);

    private const int DeferredDiscoveryUpsert = 0;
    private const int DeferredDiscoveryDelete = 1;
    // A terminal cloud placeholder remains searchable, but has already
    // consumed the bounded extraction attempt.  Discovery must not reset it
    // on every unchanged snapshot; nevertheless Everything cannot always
    // report hydration when size and mtime stay stable.  Persisted
    // dead_letter.failed_utc gives every provider a durable, bounded probe.
    private static readonly TimeSpan CloudPlaceholderRediscoveryCooldown = TimeSpan.FromHours(1);

    private static async ValueTask PersistDeferredDiscoveryUpsertCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        DiscoveredFile entry,
        string pathNorm,
        string? identity,
        long recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO deferred_discovery_changes(
                file_id,change_kind,path,path_norm,root_path,candidate_kind,
                size,modified_utc,file_identity,recorded_utc)
            VALUES(@file,@change,@path,@norm,@root,@kind,@size,@modified,@identity,@recorded)
            ON CONFLICT(file_id) DO UPDATE SET
                change_kind=excluded.change_kind,path=excluded.path,path_norm=excluded.path_norm,
                root_path=excluded.root_path,candidate_kind=excluded.candidate_kind,
                size=excluded.size,modified_utc=excluded.modified_utc,
                file_identity=excluded.file_identity,recorded_utc=excluded.recorded_utc;
            """);
        command.Parameters.AddWithValue("@file", fileId);
        command.Parameters.AddWithValue("@change", DeferredDiscoveryUpsert);
        command.Parameters.AddWithValue("@path", entry.Candidate.FullPath);
        command.Parameters.AddWithValue("@norm", pathNorm);
        command.Parameters.AddWithValue("@root", NormalizeDiscoveryRoot(entry.RootPath));
        command.Parameters.AddWithValue("@kind", (int)entry.Candidate.Kind);
        command.Parameters.AddWithValue("@size", entry.Candidate.Size);
        command.Parameters.AddWithValue("@modified", UtcTicks(entry.Candidate.LastWriteTimeUtc));
        command.Parameters.AddWithValue("@identity", (object?)identity ?? DBNull.Value);
        command.Parameters.AddWithValue("@recorded", recordedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PersistDeferredDiscoveryDeletionCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        string fullPath,
        string pathNorm,
        long recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO deferred_discovery_changes(
                file_id,change_kind,path,path_norm,root_path,candidate_kind,
                size,modified_utc,file_identity,recorded_utc)
            VALUES(@file,@change,@path,@norm,NULL,NULL,NULL,NULL,NULL,@recorded)
            ON CONFLICT(file_id) DO UPDATE SET
                change_kind=excluded.change_kind,path=excluded.path,path_norm=excluded.path_norm,
                root_path=NULL,candidate_kind=NULL,size=NULL,modified_utc=NULL,
                file_identity=NULL,recorded_utc=excluded.recorded_utc;
            """);
        command.Parameters.AddWithValue("@file", fileId);
        command.Parameters.AddWithValue("@change", DeferredDiscoveryDelete);
        command.Parameters.AddWithValue("@path", fullPath);
        command.Parameters.AddWithValue("@norm", pathNorm);
        command.Parameters.AddWithValue("@recorded", recordedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ApplyDiscoverySnapshotAsync(
        IAsyncEnumerable<FileCandidate> files,
        CancellationToken cancellationToken) =>
        ApplyDiscoverySnapshotAsync(files, DiscoverySnapshotOptions.Complete(), cancellationToken);

    public async ValueTask ApplyDiscoverySnapshotAsync(
        IAsyncEnumerable<FileCandidate> files,
        DiscoverySnapshotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceId);
        if (options.CommitBatchSize is < 100 or > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.CommitBatchSize));
        }

        var runId = Guid.NewGuid().ToString("N");
        var observedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CreateDiscoveryRunAsync(runId, options.SourceId, cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new Dictionary<string, StagedDiscoveryItem>(StringComparer.OrdinalIgnoreCase);
            await foreach (var file in files.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (DiscoveryPath.IsExcludedSystemPath(file.FullPath))
                {
                    continue;
                }
                IndexNormalization.ValidateCandidate(file);
                var pathNorm = IndexNormalization.PathKey(file.FullPath);
                // The legacy surface has no discovery-root field. Preserve its
                // historical whole-volume semantics by inferring only here.
                var root = InferCompatibilityRoot(file.FullPath);
                batch[pathNorm] = new StagedDiscoveryItem(file, root, null);
                observedRoots.Add(root);
                if (batch.Count >= options.CommitBatchSize)
                {
                    await StageDiscoveryBatchAsync(runId, batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await StageDiscoveryBatchAsync(runId, batch, cancellationToken).ConfigureAwait(false);
            }

            var onlineRoots = (options.OnlineRoots ?? observedRoots)
                .Select(NormalizeDiscoveryRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await FinalizeDiscoveryRunAsync(
                runId,
                options.IsComplete,
                options.IsComplete,
                onlineRoots,
                Array.Empty<string>(),
                Array.Empty<string>(),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteDiscoveryRunBestEffortAsync(runId).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask ApplyDiscoverySnapshotAsync(
        IIncrementalFileDiscoveryProvider provider,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var runId = Guid.NewGuid().ToString("N");
        var discoveryStartedAtUtc = DateTimeOffset.UtcNow;
        var batch = new Dictionary<string, StagedDiscoveryItem>(StringComparer.OrdinalIgnoreCase);
        await CreateDiscoveryRunAsync(runId, sourceId, cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep provider enumeration faults distinct from storage faults.
            // A provider may have streamed a large, internally coherent prefix
            // before its own budget or an I/O source fails.  That prefix can be
            // published safely as a merge-only partial pass when the provider
            // has supplied a current scan report.  A staging/finalization fault
            // must still roll the run back rather than masquerading as provider
            // evidence.
            Exception? enumerationFailure = null;
            var enumerator = provider.DiscoverEntriesAsync(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    DiscoveredFile entry;
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        entry = moved ? enumerator.Current : null!;
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A provider-owned timeout/budget is a partial scan;
                        // caller cancellation remains an atomic rollback.
                        enumerationFailure = exception;
                        break;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        enumerationFailure = exception;
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    if (DiscoveryPath.IsExcludedSystemPath(entry.Candidate.FullPath))
                    {
                        continue;
                    }
                    IndexNormalization.ValidateCandidate(entry.Candidate);
                    var pathNorm = IndexNormalization.PathKey(entry.Candidate.FullPath);
                    var identity = entry.Identity is null
                        ? null
                        : $"{entry.Identity.VolumeSerialNumber:X8}:{entry.Identity.FileIndex:X16}";
                    var rootNorm = NormalizeDiscoveryRoot(entry.RootPath);
                    EnsurePathIsWithinRoot(pathNorm, rootNorm, nameof(entry));
                    if (identity is null && batch.TryGetValue(pathNorm, out var previouslyStaged))
                    {
                        identity = previouslyStaged.FileIdentity;
                    }
                    batch[pathNorm] = new StagedDiscoveryItem(
                        entry.Candidate,
                        rootNorm,
                        identity);
                    if (batch.Count >= 5_000)
                    {
                        await StageDiscoveryBatchAsync(runId, batch, cancellationToken).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    enumerationFailure ??= exception;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    enumerationFailure ??= exception;
                }
            }

            if (enumerationFailure is not null)
            {
                if (await TryFinalizeObservedPartialDiscoveryRunAsync(
                        runId,
                        batch,
                        provider,
                        discoveryStartedAtUtc,
                        enumerationFailure,
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                ExceptionDispatchInfo.Capture(enumerationFailure).Throw();
            }

            if (batch.Count > 0)
            {
                await StageDiscoveryBatchAsync(runId, batch, cancellationToken).ConfigureAwait(false);
            }

            var report = provider.LastReport;
            var (onlineRoots, incompletePrefixes, terminalSkippedPrefixes) = BuildReconciliationScope(report);
            var completeForDeletion = report.IsCompleteForDeletion &&
                report.Roots.All(static root =>
                    root.WasOnline && root.FullyEnumerated && root.IncompletePrefixes.Count == 0);
            await FinalizeDiscoveryRunAsync(
                runId,
                completeForDeletion,
                // A provider can return useful changed entries even when a scan is
                // incomplete, but that result is not an authoritative view of any
                // previously indexed root.  Treat it as a merge-only pass.  In
                // particular, do not shrink the cached total or remove queued
                // metadata work merely because a degraded accelerator/fallback
                // reported a subset of files.  A later trusted complete scan owns
                // reconciliation and deletion.
                reconcile: completeForDeletion,
                onlineRoots,
                incompletePrefixes,
                terminalSkippedPrefixes,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteDiscoveryRunBestEffortAsync(runId).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryFinalizeObservedPartialDiscoveryRunAsync(
        string runId,
        Dictionary<string, StagedDiscoveryItem> batch,
        IIncrementalFileDiscoveryProvider provider,
        DateTimeOffset discoveryStartedAtUtc,
        Exception enumerationFailure,
        CancellationToken cancellationToken)
    {
        var report = provider.LastReport;
        if (!CanCommitObservedPartialSnapshot(
                enumerationFailure,
                report,
                discoveryStartedAtUtc))
        {
            return false;
        }

        // A current report lets us retain every staged observation, but not
        // infer deletion from any root that was not authoritatively completed.
        // The finalizer records source_state=partial, preserves last success
        // and root trust, and deliberately receives reconcile:false.
        if (batch.Count > 0)
        {
            await StageDiscoveryBatchAsync(runId, batch, cancellationToken).ConfigureAwait(false);
            batch.Clear();
        }

        if (!await HasStagedDiscoveryEntriesAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var (onlineRoots, incompletePrefixes, terminalSkippedPrefixes) =
            BuildReconciliationScope(report);
        await FinalizeDiscoveryRunAsync(
            runId,
            complete: false,
            reconcile: false,
            onlineRoots,
            incompletePrefixes,
            terminalSkippedPrefixes,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> HasStagedDiscoveryEntriesAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM discovery_stage WHERE run_id=@run);";
            command.Parameters.AddWithValue("@run", runId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool CanCommitObservedPartialSnapshot(
        Exception exception,
        DiscoveryScanReport report,
        DateTimeOffset discoveryStartedAtUtc)
    {
        // This helper is reached only for MoveNext/Dispose faults from the
        // provider, never for validation or SQLite staging/finalization faults.
        // A typed incomplete exception must still name this exact report. For
        // timeout and generic provider faults, a report generated during this
        // invocation is enough to publish the observations as *partial*;
        // reconciliation remains disabled regardless of the report's original
        // completeness flag.
        if (exception is OutOfMemoryException or StackOverflowException ||
            (exception is DiscoverySnapshotIncompleteException partial &&
             !ReferenceEquals(partial.Report, report)) ||
            report.Roots.Count == 0 ||
            report.StartedAtUtc < discoveryStartedAtUtc ||
            report.CompletedAtUtc < report.StartedAtUtc)
        {
            return false;
        }

        // At least one online root is required. A non-empty stage plus this
        // report is evidence of a usable current prefix; offline-only/stale
        // reports still take the normal rollback path.
        return report.Roots.Any(static root => root.WasOnline);
    }

    private async ValueTask CreateDiscoveryRunAsync(
        string runId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM discovery_runs WHERE completed=0;
                INSERT INTO discovery_runs(run_id,source_id,started_utc) VALUES(@run,@source,@now);
                """;
            command.Parameters.AddWithValue("@run", runId);
            command.Parameters.AddWithValue("@source", sourceId);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask StageDiscoveryBatchAsync(
        string runId,
        IReadOnlyDictionary<string, StagedDiscoveryItem> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Issue up to 100 rows per INSERT to stay safely under the 999-param
            // SQLite limit (one shared run parameter plus 8 per row). This drops the round-trip
            // count from one per row to ~50 per 5,000-row batch.
            const int maxRowsPerInsert = 100;
            var pairs = batch.ToArray();

            for (var chunkStart = 0; chunkStart < pairs.Length; chunkStart += maxRowsPerInsert)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkSize = Math.Min(maxRowsPerInsert, pairs.Length - chunkStart);
                var sql = BuildMultiRowStageInsertSql(chunkSize);
                await using var stage = Command(connection, transaction, sql);
                stage.Parameters.AddWithValue("@run", runId);
                for (var i = 0; i < chunkSize; i++)
                {
                    var pair = pairs[chunkStart + i];
                    stage.Parameters.AddWithValue($"@path{i}", pair.Value.Candidate.FullPath);
                    stage.Parameters.AddWithValue($"@norm{i}", pair.Key);
                    stage.Parameters.AddWithValue($"@root{i}", pair.Value.RootPath);
                    stage.Parameters.AddWithValue(
                        $"@name{i}",
                        IndexNormalization.Text(Path.GetFileName(pair.Value.Candidate.FullPath)));
                    stage.Parameters.AddWithValue($"@kind{i}", (int)pair.Value.Candidate.Kind);
                    stage.Parameters.AddWithValue($"@size{i}", pair.Value.Candidate.Size);
                    stage.Parameters.AddWithValue(
                        $"@modified{i}",
                        UtcTicks(pair.Value.Candidate.LastWriteTimeUtc));
                    stage.Parameters.AddWithValue(
                        $"@identity{i}",
                        (object?)pair.Value.FileIdentity ?? DBNull.Value);
                }

                await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        // Checkpoint between bounded staging transactions. PASSIVE never waits
        // for readers and keeps a large initial discovery from growing WAL
        // without bound before the final reconciliation.
        await MaintainStorageAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildMultiRowStageInsertSql(int rowCount)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO discovery_stage(");
        sb.Append("run_id,path,path_norm,root_norm,file_name_norm,");
        sb.Append("kind,size,modified_utc,file_identity) VALUES");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('(');
            sb.Append($"@run,@path{i},@norm{i},@root{i},@name{i},");
            sb.Append($"@kind{i},@size{i},@modified{i},@identity{i}");
            sb.Append(')');
        }
        sb.Append(" ON CONFLICT(run_id,path_norm) DO UPDATE SET ");
        sb.Append("path=excluded.path,root_norm=excluded.root_norm,");
        sb.Append("file_name_norm=excluded.file_name_norm,");
        sb.Append("kind=excluded.kind,size=excluded.size,");
        sb.Append("modified_utc=excluded.modified_utc,");
        sb.Append("file_identity=coalesce(excluded.file_identity,discovery_stage.file_identity);");
        return sb.ToString();
    }

    private async ValueTask FinalizeDiscoveryRunAsync(
        string runId,
        bool complete,
        bool reconcile,
        IReadOnlyCollection<string> onlineRoots,
        IReadOnlyCollection<string> incompletePrefixes,
        IReadOnlyCollection<string> terminalSkippedPrefixes,
        CancellationToken cancellationToken)
    {
        var searchContentChanged = false;
        // Phase timings feed LastDiscoveryFinalizeTiming so a long merge can be
        // attributed without any path- or term-bearing diagnostics.
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        var gateWatch = System.Diagnostics.Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gateWatch.Stop();
        // A failed finalize must leave no stale timing behind; the runtime
        // archives this value only after the snapshot boundary settles.
        Volatile.Write(ref _lastDiscoveryFinalizeTiming, null);
        var tempPrepElapsed = 0L;
        var stagedRows = 0L;
        var activeRows = 0L;
        var detectElapsed = 0L;
        var mergeElapsed = 0L;
        var commitElapsed = 0L;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                DROP TABLE IF EXISTS temp.online_roots;
                DROP TABLE IF EXISTS temp.incomplete_prefixes;
                DROP TABLE IF EXISTS temp.terminal_skipped_prefixes;
                DROP TABLE IF EXISTS temp.changed_search_documents;
                DROP TABLE IF EXISTS temp.new_search_files;
                DROP TABLE IF EXISTS temp.search_result_changes;
                DROP TABLE IF EXISTS temp.neutral_identity_backfills;
                DROP TABLE IF EXISTS temp.cloud_placeholder_retry_candidates;
                CREATE TEMP TABLE online_roots(root_norm TEXT PRIMARY KEY COLLATE NOCASE) WITHOUT ROWID;
                CREATE TEMP TABLE incomplete_prefixes(path_norm TEXT PRIMARY KEY COLLATE NOCASE) WITHOUT ROWID;
                CREATE TEMP TABLE terminal_skipped_prefixes(path_norm TEXT PRIMARY KEY COLLATE NOCASE) WITHOUT ROWID;
                CREATE TEMP TABLE changed_search_documents(document_id INTEGER PRIMARY KEY) WITHOUT ROWID;
                CREATE TEMP TABLE new_search_files(file_id INTEGER PRIMARY KEY) WITHOUT ROWID;
                CREATE TEMP TABLE search_result_changes(marker INTEGER NOT NULL);
                CREATE TEMP TABLE neutral_identity_backfills(
                    file_id INTEGER PRIMARY KEY,
                    file_identity TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    modified_utc INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TEMP TABLE cloud_placeholder_retry_candidates(
                    file_id INTEGER PRIMARY KEY) WITHOUT ROWID;
                """, cancellationToken).ConfigureAwait(false);
            await using (var insertRoot = Command(
                connection,
                transaction,
                "INSERT OR IGNORE INTO online_roots(root_norm) VALUES(@root);"))
            {
                var rootParameter = insertRoot.Parameters.Add("@root", SqliteType.Text);
                foreach (var root in onlineRoots)
                {
                    rootParameter.Value = root;
                    await insertRoot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var insertPrefix = Command(
                connection,
                transaction,
                "INSERT OR IGNORE INTO incomplete_prefixes(path_norm) VALUES(@prefix);"))
            {
                var prefixParameter = insertPrefix.Parameters.Add("@prefix", SqliteType.Text);
                foreach (var prefix in incompletePrefixes)
                {
                    prefixParameter.Value = prefix;
                    await insertPrefix.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var insertTerminalPrefix = Command(
                connection,
                transaction,
                "INSERT OR IGNORE INTO terminal_skipped_prefixes(path_norm) VALUES(@prefix);"))
            {
                var prefixParameter = insertTerminalPrefix.Parameters.Add("@prefix", SqliteType.Text);
                foreach (var prefix in terminalSkippedPrefixes)
                {
                    prefixParameter.Value = prefix;
                    await insertTerminalPrefix.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // Identity is tri-state discovery evidence: NULL means the provider
            // could not observe it. Only two known, different identities prove
            // replacement; a newly known value is a metadata-neutral backfill.
            tempPrepElapsed = totalWatch.ElapsedMilliseconds;
            await using (var stagedCount = Command(
                connection,
                transaction,
                """
                SELECT
                  (SELECT COUNT(*) FROM discovery_stage WHERE run_id=@run),
                  (SELECT COUNT(*) FROM files WHERE state=0);
                """))
            {
                stagedCount.Parameters.AddWithValue("@run", runId);
                await using var reader = await stagedCount.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    stagedRows = reader.GetInt64(0);
                    activeRows = reader.GetInt64(1);
                }
            }

            await using (var detectSearchChanges = Command(connection, transaction, """
                INSERT INTO search_result_changes(marker)
                SELECT 1 WHERE EXISTS(
                    SELECT 1
                     FROM discovery_stage s
                      LEFT JOIN files f ON f.path_norm=s.path_norm
                     WHERE s.run_id=@run
                       AND (f.id IS NULL OR f.state<>0 OR f.path<>s.path OR f.kind<>s.kind
                            OR f.size<>s.size OR f.modified_utc<>s.modified_utc
                            OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                                AND f.file_identity<>s.file_identity)));
                INSERT INTO search_result_changes(marker)
                SELECT 1 WHERE @reconcile=1 AND EXISTS(
                    SELECT 1 FROM files
                     WHERE state=0
                       AND EXISTS(
                           SELECT 1 FROM online_roots r
                            WHERE files.path_norm=r.root_norm
                               OR (substr(files.path_norm,1,length(r.root_norm))=r.root_norm
                                   AND (substr(r.root_norm,-1,1)=@separator
                                        OR substr(files.path_norm,length(r.root_norm)+1,1)=@separator)))
                       AND NOT EXISTS(
                           SELECT 1 FROM incomplete_prefixes x
                            WHERE files.path_norm=x.path_norm
                               OR (substr(files.path_norm,1,length(x.path_norm))=x.path_norm
                                   AND (substr(x.path_norm,-1,1)=@separator
                                        OR substr(files.path_norm,length(x.path_norm)+1,1)=@separator)))
                       AND NOT EXISTS(
                           SELECT 1 FROM discovery_stage s
                            WHERE s.run_id=@run AND s.path_norm=files.path_norm));
                """))
            {
                detectSearchChanges.Parameters.AddWithValue("@run", runId);
                detectSearchChanges.Parameters.AddWithValue("@reconcile", reconcile ? 1 : 0);
                detectSearchChanges.Parameters.AddWithValue("@separator", Path.DirectorySeparatorChar.ToString());
                await detectSearchChanges.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            detectElapsed = totalWatch.ElapsedMilliseconds - tempPrepElapsed;

            long generation;
            await using (var generationCommand = Command(
                connection,
                transaction,
                "SELECT discovery_generation+1 FROM index_state WHERE singleton_id=1;"))
            {
                generation = Convert.ToInt64(
                    await generationCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }

            var now = DateTimeOffset.UtcNow.UtcTicks;
            var cloudPlaceholderRetryCutoff = now - CloudPlaceholderRediscoveryCooldown.Ticks;
            await using (var merge = Command(connection, transaction, """
                INSERT OR IGNORE INTO neutral_identity_backfills(
                    file_id,file_identity,size,modified_utc)
                SELECT f.id,s.file_identity,s.size,s.modified_utc
                  FROM files f JOIN discovery_stage s
                    ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.state=0
                   AND f.path=s.path AND f.kind=s.kind
                   AND f.size=s.size AND f.modified_utc=s.modified_utc
                   AND f.file_identity IS NULL AND s.file_identity IS NOT NULL
                   AND NOT EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=f.id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now);

                UPDATE index_queue AS q SET
                    fingerprint=printf('%lld:%lld:%s:%d',
                                       backfill.size,backfill.modified_utc,
                                       backfill.file_identity,q.extractor_version)
                  FROM neutral_identity_backfills AS backfill
                 WHERE q.file_id=backfill.file_id;

                UPDATE dead_letter AS d SET
                    fingerprint=printf('%lld:%lld:%s:%d',
                                       backfill.size,backfill.modified_utc,
                                       backfill.file_identity,d.extractor_version)
                  FROM neutral_identity_backfills AS backfill
                 WHERE d.file_id=backfill.file_id;

                INSERT OR IGNORE INTO changed_search_documents(document_id)
                SELECT DISTINCT f.id
                  FROM files f
                  JOIN discovery_stage s
                    ON s.run_id=@run
                   AND (s.path_norm=f.path_norm
                        OR (f.file_identity IS NOT NULL AND s.file_identity=f.file_identity))
                 WHERE f.state<>0 OR f.path<>s.path OR f.kind<>s.kind
                    OR f.size<>s.size OR f.modified_utc<>s.modified_utc
                    OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                        AND f.file_identity<>s.file_identity);

                -- Do not revoke a valid parser lease simply because this
                -- snapshot observed newer evidence. Persist the last observed
                -- replacement atomically with the snapshot instead; the
                -- deferred row is replayed after the owner reaches a terminal
                -- CAS state, even across a broker restart.
                INSERT INTO deferred_discovery_changes(
                    file_id,change_kind,path,path_norm,root_path,candidate_kind,
                    size,modified_utc,file_identity,recorded_utc)
                SELECT f.id,0,s.path,s.path_norm,s.root_norm,s.kind,
                       s.size,s.modified_utc,s.file_identity,@now
                  FROM files f JOIN discovery_stage s
                    ON s.run_id=@run AND s.file_identity=f.file_identity
                 WHERE f.file_identity IS NOT NULL AND f.path_norm<>s.path_norm
                   AND (SELECT COUNT(*) FROM files x WHERE x.file_identity=f.file_identity)=1
                   AND (SELECT COUNT(*) FROM discovery_stage x WHERE x.run_id=@run AND x.file_identity=f.file_identity)=1
                   AND NOT EXISTS(SELECT 1 FROM files x WHERE x.path_norm=s.path_norm)
                   AND EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=f.id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now)
                ON CONFLICT(file_id) DO UPDATE SET
                    change_kind=excluded.change_kind,path=excluded.path,path_norm=excluded.path_norm,
                    root_path=excluded.root_path,candidate_kind=excluded.candidate_kind,
                    size=excluded.size,modified_utc=excluded.modified_utc,
                    file_identity=excluded.file_identity,recorded_utc=excluded.recorded_utc;

                INSERT INTO deferred_discovery_changes(
                    file_id,change_kind,path,path_norm,root_path,candidate_kind,
                    size,modified_utc,file_identity,recorded_utc)
                SELECT f.id,0,s.path,s.path_norm,s.root_norm,s.kind,
                       s.size,s.modified_utc,s.file_identity,@now
                  FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE (f.state<>0 OR f.path<>s.path OR f.kind<>s.kind
                       OR f.size<>s.size OR f.modified_utc<>s.modified_utc
                       OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                           AND f.file_identity<>s.file_identity))
                   AND EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=f.id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now)
                ON CONFLICT(file_id) DO UPDATE SET
                    change_kind=excluded.change_kind,path=excluded.path,path_norm=excluded.path_norm,
                    root_path=excluded.root_path,candidate_kind=excluded.candidate_kind,
                    size=excluded.size,modified_utc=excluded.modified_utc,
                    file_identity=excluded.file_identity,recorded_utc=excluded.recorded_utc;

                -- Identity-only evidence is neutral for metadata freshness,
                -- but it is still lease/CAS evidence and cannot be discarded
                -- while a worker owns the old fingerprint.
                INSERT INTO deferred_discovery_changes(
                    file_id,change_kind,path,path_norm,root_path,candidate_kind,
                    size,modified_utc,file_identity,recorded_utc)
                SELECT f.id,0,s.path,s.path_norm,s.root_norm,s.kind,
                       s.size,s.modified_utc,s.file_identity,@now
                  FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.state=0 AND f.path=s.path AND f.kind=s.kind
                   AND f.size=s.size AND f.modified_utc=s.modified_utc
                   AND f.file_identity IS NULL AND s.file_identity IS NOT NULL
                   AND EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=f.id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now)
                ON CONFLICT(file_id) DO UPDATE SET
                    change_kind=excluded.change_kind,path=excluded.path,path_norm=excluded.path_norm,
                    root_path=excluded.root_path,candidate_kind=excluded.candidate_kind,
                    size=excluded.size,modified_utc=excluded.modified_utc,
                    file_identity=excluded.file_identity,recorded_utc=excluded.recorded_utc;

                INSERT INTO deferred_discovery_changes(
                    file_id,change_kind,path,path_norm,root_path,candidate_kind,
                    size,modified_utc,file_identity,recorded_utc)
                SELECT f.id,1,f.path,f.path_norm,NULL,NULL,NULL,NULL,NULL,@now
                  FROM files f
                 WHERE @reconcile=1 AND f.state=0
                   AND EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=f.id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now)
                   AND EXISTS(
                       SELECT 1 FROM online_roots r
                        WHERE f.path_norm=r.root_norm
                           OR (substr(f.path_norm,1,length(r.root_norm))=r.root_norm
                               AND (substr(r.root_norm,-1,1)=@separator
                                    OR substr(f.path_norm,length(r.root_norm)+1,1)=@separator)))
                   AND NOT EXISTS(
                       SELECT 1 FROM incomplete_prefixes x
                        WHERE f.path_norm=x.path_norm
                           OR (substr(f.path_norm,1,length(x.path_norm))=x.path_norm
                               AND (substr(x.path_norm,-1,1)=@separator
                                    OR substr(f.path_norm,length(x.path_norm)+1,1)=@separator)))
                   AND NOT EXISTS(
                       SELECT 1 FROM discovery_stage s
                        WHERE s.run_id=@run
                          AND (s.path_norm=f.path_norm
                               OR (f.file_identity IS NOT NULL AND s.file_identity=f.file_identity)))
                ON CONFLICT(file_id) DO UPDATE SET
                    change_kind=excluded.change_kind,path=excluded.path,path_norm=excluded.path_norm,
                    root_path=NULL,candidate_kind=NULL,size=NULL,modified_utc=NULL,
                    file_identity=NULL,recorded_utc=excluded.recorded_utc;

                INSERT INTO index_queue(
                    file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'Renamed',2,@now,0,0,
                       printf('%lld:%lld:%s:%d',s.size,s.modified_utc,coalesce(s.file_identity,''),@extractor),@extractor
                  FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.file_identity=f.file_identity
                 WHERE f.file_identity IS NOT NULL AND f.path_norm<>s.path_norm
                   AND (SELECT COUNT(*) FROM files x WHERE x.file_identity=f.file_identity)=1
                   AND (SELECT COUNT(*) FROM discovery_stage x WHERE x.run_id=@run AND x.file_identity=f.file_identity)=1
                   AND NOT EXISTS(SELECT 1 FROM files x WHERE x.path_norm=s.path_norm)
                 ON CONFLICT(file_id) DO UPDATE SET reason='Renamed',priority=min(index_queue.priority,2),
                     due_utc=@now,attempt=0,failure_count=0,fingerprint=excluded.fingerprint,
                     extractor_version=@extractor,lease_until_utc=NULL,lease_owner=NULL,last_error=NULL
                   WHERE index_queue.lease_owner IS NULL OR index_queue.lease_until_utc IS NULL
                      OR index_queue.lease_until_utc<=@now;

                UPDATE files AS f SET
                    path=(SELECT s.path FROM discovery_stage s WHERE s.run_id=@run AND s.file_identity=f.file_identity),
                    path_norm=(SELECT s.path_norm FROM discovery_stage s WHERE s.run_id=@run AND s.file_identity=f.file_identity)
                 WHERE f.file_identity IS NOT NULL
                   AND (SELECT COUNT(*) FROM files x WHERE x.file_identity=f.file_identity)=1
                   AND (SELECT COUNT(*) FROM discovery_stage x WHERE x.run_id=@run AND x.file_identity=f.file_identity)=1
                    AND EXISTS(SELECT 1 FROM discovery_stage s WHERE s.run_id=@run AND s.file_identity=f.file_identity
                               AND s.path_norm<>f.path_norm)
                    AND NOT EXISTS(SELECT 1 FROM files x JOIN discovery_stage s ON s.run_id=@run
                                   WHERE s.file_identity=f.file_identity AND x.path_norm=s.path_norm AND x.id<>f.id)
                    AND NOT EXISTS(
                        SELECT 1 FROM index_queue active
                         WHERE active.file_id=f.id
                           AND active.lease_owner IS NOT NULL
                           AND active.lease_until_utc>@now);

                INSERT INTO index_queue(
                    file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'Modified',2,@now,0,0,
                       printf('%lld:%lld:%s:%d',s.size,s.modified_utc,
                              coalesce(s.file_identity,f.file_identity,''),@extractor),@extractor
                  FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.state<>0 OR f.size<>s.size OR f.modified_utc<>s.modified_utc
                       OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                           AND f.file_identity<>s.file_identity)
                 ON CONFLICT(file_id) DO UPDATE SET
                     reason='Modified',priority=min(index_queue.priority,2),due_utc=@now,
                     attempt=0,failure_count=0,fingerprint=excluded.fingerprint,
                     extractor_version=@extractor,lease_until_utc=NULL,lease_owner=NULL,last_error=NULL
                   WHERE index_queue.lease_owner IS NULL OR index_queue.lease_until_utc IS NULL
                      OR index_queue.lease_until_utc<=@now;

                DELETE FROM dead_letter
                 WHERE file_id IN (
                    SELECT f.id FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.path_norm=f.path_norm
                     WHERE f.size<>s.size OR f.modified_utc<>s.modified_utc
                        OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                            AND f.file_identity<>s.file_identity));

                INSERT INTO files(path,path_norm,kind,size,modified_utc,file_identity,
                                  discovery_generation,state,metadata_state,extractor_version)
                SELECT s.path,s.path_norm,s.kind,s.size,s.modified_utc,s.file_identity,
                       @generation,0,1,0
                  FROM discovery_stage s LEFT JOIN files f ON f.path_norm=s.path_norm
                 WHERE s.run_id=@run AND f.id IS NULL;

                INSERT OR IGNORE INTO new_search_files(file_id)
                SELECT f.id
                  FROM files f JOIN discovery_stage s
                    ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.discovery_generation=@generation
                   AND NOT EXISTS(
                       SELECT 1 FROM changed_search_documents c
                        WHERE c.document_id=f.id);

                INSERT INTO index_queue(
                    file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'New',3,@now,0,0,
                       printf('%lld:%lld:%s:%d',f.size,f.modified_utc,coalesce(f.file_identity,''),@extractor),@extractor
                  FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.discovery_generation=@generation AND f.metadata_state=1
                ON CONFLICT(file_id) DO NOTHING;

                UPDATE files AS f SET
                    metadata_state=CASE WHEN f.state<>0 OR f.size<>s.size
                         OR f.modified_utc<>s.modified_utc
                         OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                             AND f.file_identity<>s.file_identity)
                         THEN 1 ELSE metadata_state END,
                    path=s.path,
                    kind=s.kind,
                    size=s.size,
                    modified_utc=s.modified_utc,
                    file_identity=coalesce(s.file_identity,f.file_identity),
                    discovery_generation=@generation,
                    state=0,
                    error_code=CASE WHEN f.state<>0 OR f.size<>s.size
                         OR f.modified_utc<>s.modified_utc
                         OR (f.file_identity IS NOT NULL AND s.file_identity IS NOT NULL
                             AND f.file_identity<>s.file_identity)
                         THEN NULL ELSE f.error_code END
                  FROM discovery_stage AS s
                  WHERE s.run_id=@run AND s.path_norm=f.path_norm
                    AND f.discovery_generation<>@generation
                    AND NOT EXISTS(
                        SELECT 1 FROM index_queue active
                         WHERE active.file_id=f.id
                           AND active.lease_owner IS NOT NULL
                           AND active.lease_until_utc>@now)
                  AND (f.state<>0
                         OR f.path<>s.path OR f.kind<>s.kind OR f.size<>s.size
                         OR f.modified_utc<>s.modified_utc
                         OR (s.file_identity IS NOT NULL AND f.file_identity IS NOT NULL
                             AND f.file_identity<>s.file_identity)
                          OR EXISTS(
                              SELECT 1 FROM neutral_identity_backfills backfill
                               WHERE backfill.file_id=f.id));

                -- A newer unleased full snapshot supersedes any older
                -- protected observation for the same file. Keep the deferred
                -- row only while a worker still owns its original evidence.
                DELETE FROM deferred_discovery_changes AS d
                 WHERE NOT EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=d.file_id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now)
                   AND EXISTS(
                       SELECT 1 FROM files f JOIN discovery_stage s ON s.run_id=@run
                        WHERE f.id=d.file_id
                          AND (s.path_norm=f.path_norm
                               OR (f.file_identity IS NOT NULL AND s.file_identity=f.file_identity)));

                -- A normal root/subtree observation and a reappeared missing
                -- file are immediate recovery evidence.  A cloud placeholder
                -- is different: repeated Everything snapshots can see the
                -- same unavailable placeholder forever.  Reopen it only on a
                -- neutral identity backfill (NTFS observed hydration), after
                -- the durable cooldown, or when a legacy terminal row lost its
                -- dead letter.  The latter keeps historical states recoverable.
                -- RC87 terminal rows predate the retained cooldown queue.
                -- A trusted complete snapshot converts them once, retaining
                -- failed_utc+cooldown rather than pushing that due time forward
                -- on every later snapshot.
                INSERT INTO index_queue(
                    file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'CloudPlaceholderCooldown',5,
                       max(d.failed_utc+@cloudCooldown,@now),0,0,
                       d.fingerprint,d.extractor_version
                  FROM files f JOIN discovery_stage s
                    ON s.run_id=@run AND s.path_norm=f.path_norm
                  JOIN dead_letter d ON d.file_id=f.id
                 WHERE @complete=1 AND f.state=0 AND f.metadata_state=3
                   AND f.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                   AND d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                   AND d.extractor_version=@extractor
                   AND NOT EXISTS(SELECT 1 FROM index_queue q WHERE q.file_id=f.id)
                ON CONFLICT(file_id) DO NOTHING;
                INSERT OR IGNORE INTO cloud_placeholder_retry_candidates(file_id)
                SELECT f.id
                  FROM files f JOIN discovery_stage s
                    ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.state=0 AND f.metadata_state=3
                   AND f.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                   AND (
                       EXISTS(
                           SELECT 1 FROM neutral_identity_backfills backfill
                            WHERE backfill.file_id=f.id)
                       OR NOT EXISTS(
                           SELECT 1 FROM dead_letter d
                            WHERE d.file_id=f.id
                              AND d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED')
                       OR EXISTS(
                           SELECT 1 FROM dead_letter d
                            WHERE d.file_id=f.id
                              AND d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                              AND d.failed_utc<=@cloudRetryCutoff));
                DELETE FROM dead_letter
                 WHERE file_id IN (
                    SELECT f.id
                     FROM files f JOIN discovery_stage s
                        ON s.run_id=@run AND s.path_norm=f.path_norm
                     WHERE f.state=0 AND f.metadata_state=3
                       AND f.error_code IN (
                           'DISCOVERY_SUBTREE_SKIPPED',
                           'FILE_NOT_FOUND'))
                    OR file_id IN (SELECT file_id FROM cloud_placeholder_retry_candidates);
                -- Historical FILE_NOT_FOUND rows could have a stale queue
                -- state from an older broker.  A freshly staged path is proof
                -- that this exact file is available again even when its size
                -- and timestamp happen to be unchanged.
                DELETE FROM dead_letter
                 WHERE error_code='FILE_NOT_FOUND'
                   AND file_id IN (
                       SELECT f.id
                         FROM files f JOIN discovery_stage s
                           ON s.run_id=@run AND s.path_norm=f.path_norm
                        WHERE f.state=0);
                UPDATE files AS f
                   SET metadata_state=1,extractor_version=@extractor,error_code=NULL
                  FROM discovery_stage s
                 WHERE s.run_id=@run AND s.path_norm=f.path_norm
                   AND f.state=0 AND f.metadata_state=3
                   AND f.error_code IN (
                       'DISCOVERY_SUBTREE_SKIPPED',
                       'FILE_NOT_FOUND');
                UPDATE files AS f
                   SET metadata_state=1,extractor_version=@extractor,error_code=NULL
                 WHERE f.id IN (SELECT file_id FROM cloud_placeholder_retry_candidates);
                UPDATE files AS f
                   SET metadata_state=1,extractor_version=@extractor,error_code=NULL
                  FROM discovery_stage s
                 WHERE s.run_id=@run AND s.path_norm=f.path_norm
                   AND f.state=0 AND f.error_code='FILE_NOT_FOUND';
                INSERT OR IGNORE INTO index_queue(
                    file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                SELECT f.id,'DiscoveryRetry',3,@now,0,0,
                       printf('%lld:%lld:%s:%d',f.size,f.modified_utc,
                              coalesce(f.file_identity,''),@extractor),@extractor
                  FROM files f JOIN discovery_stage s ON s.run_id=@run AND s.path_norm=f.path_norm
                 WHERE f.state=0 AND f.metadata_state=1 AND f.error_code IS NULL
                ON CONFLICT(file_id) DO UPDATE SET
                    reason='DiscoveryRetry',priority=min(index_queue.priority,3),due_utc=@now,
                    attempt=0,failure_count=0,fingerprint=excluded.fingerprint,
                    extractor_version=excluded.extractor_version,lease_until_utc=NULL,lease_owner=NULL,last_error=NULL
                 WHERE index_queue.reason='CloudPlaceholderCooldown'
                   AND (index_queue.lease_owner IS NULL OR index_queue.lease_until_utc IS NULL
                        OR index_queue.lease_until_utc<=@now);

                -- Access-denied, disappeared and known transient subtrees keep
                -- their searchable file rows. They must not, however, leave
                -- metadata leases pending forever and pin the global progress.
                -- Never take an active lease; its owner finishes normally and a
                -- later reconciliation can close it if the boundary persists.
                INSERT INTO dead_letter(file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
                SELECT q.file_id,q.fingerprint,@extractor,0,'DISCOVERY_SUBTREE_SKIPPED',@now
                  FROM index_queue q JOIN files f ON f.id=q.file_id AND f.state=0
                 WHERE (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now)
                   AND EXISTS(
                       SELECT 1 FROM terminal_skipped_prefixes x
                        WHERE f.path_norm=x.path_norm
                           OR (substr(f.path_norm,1,length(x.path_norm))=x.path_norm
                               AND (substr(x.path_norm,-1,1)=@separator
                                    OR substr(f.path_norm,length(x.path_norm)+1,1)=@separator)))
                ON CONFLICT(file_id) DO UPDATE SET
                    fingerprint=excluded.fingerprint,extractor_version=excluded.extractor_version,
                    failure_count=excluded.failure_count,error_code=excluded.error_code,
                    failed_utc=excluded.failed_utc;
                UPDATE files
                   SET metadata_state=3,extractor_version=@extractor,
                       error_code='DISCOVERY_SUBTREE_SKIPPED'
                 WHERE state=0 AND id IN (
                    SELECT q.file_id
                      FROM index_queue q
                     WHERE (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now)
                       AND EXISTS(
                           SELECT 1 FROM terminal_skipped_prefixes x
                            WHERE files.path_norm=x.path_norm
                               OR (substr(files.path_norm,1,length(x.path_norm))=x.path_norm
                                   AND (substr(x.path_norm,-1,1)=@separator
                                        OR substr(files.path_norm,length(x.path_norm)+1,1)=@separator))));
                DELETE FROM index_queue
                 WHERE (lease_until_utc IS NULL OR lease_until_utc<=@now)
                   AND EXISTS(
                       SELECT 1 FROM files f JOIN terminal_skipped_prefixes x
                         ON f.id=index_queue.file_id
                        WHERE f.path_norm=x.path_norm
                           OR (substr(f.path_norm,1,length(x.path_norm))=x.path_norm
                               AND (substr(x.path_norm,-1,1)=@separator
                                    OR substr(f.path_norm,length(x.path_norm)+1,1)=@separator)));

                INSERT OR IGNORE INTO changed_search_documents(document_id)
                SELECT files.id
                  FROM files
                 WHERE @reconcile=1 AND files.state=0
                   AND EXISTS(
                       SELECT 1 FROM online_roots r
                        WHERE files.path_norm=r.root_norm
                           OR (substr(files.path_norm,1,length(r.root_norm))=r.root_norm
                               AND (substr(r.root_norm,-1,1)=@separator
                                    OR substr(files.path_norm,length(r.root_norm)+1,1)=@separator)))
                   AND NOT EXISTS(
                       SELECT 1 FROM incomplete_prefixes x
                        WHERE files.path_norm=x.path_norm
                           OR (substr(files.path_norm,1,length(x.path_norm))=x.path_norm
                               AND (substr(x.path_norm,-1,1)=@separator
                                    OR substr(files.path_norm,length(x.path_norm)+1,1)=@separator)))
                   AND NOT EXISTS(
                       SELECT 1 FROM discovery_stage s
                        WHERE s.run_id=@run AND s.path_norm=files.path_norm);

                UPDATE files SET state=1
                 WHERE @reconcile=1 AND state=0
                   AND NOT EXISTS(
                       SELECT 1 FROM index_queue active
                        WHERE active.file_id=files.id
                          AND active.lease_owner IS NOT NULL
                          AND active.lease_until_utc>@now)
                    AND EXISTS(
                       SELECT 1 FROM online_roots r
                        WHERE files.path_norm=r.root_norm
                           OR (substr(files.path_norm,1,length(r.root_norm))=r.root_norm
                               AND (substr(r.root_norm,-1,1)=@separator
                                    OR substr(files.path_norm,length(r.root_norm)+1,1)=@separator)))
                   AND NOT EXISTS(
                       SELECT 1 FROM incomplete_prefixes x
                        WHERE files.path_norm=x.path_norm
                           OR (substr(files.path_norm,1,length(x.path_norm))=x.path_norm
                               AND (substr(x.path_norm,-1,1)=@separator
                                   OR substr(files.path_norm,length(x.path_norm)+1,1)=@separator)))
                   AND NOT EXISTS(SELECT 1 FROM discovery_stage s WHERE s.run_id=@run AND s.path_norm=files.path_norm);

                DELETE FROM index_queue WHERE file_id IN (SELECT id FROM files WHERE state<>0);
                DELETE FROM deferred_discovery_changes
                 WHERE file_id IN (SELECT id FROM files WHERE state<>0);
                DELETE FROM search_projection_outbox
                 WHERE file_id IN (
                       SELECT document_id FROM changed_search_documents
                       UNION SELECT file_id FROM new_search_files);
                INSERT INTO search_projection_outbox(file_id,reason,queued_utc)
                SELECT file_id,'DiscoveryChanged',@now
                  FROM (
                        SELECT document_id AS file_id FROM changed_search_documents
                        UNION SELECT file_id FROM new_search_files
                       );
                UPDATE search_projection_state
                   SET current_sequence=last_insert_rowid()
                 WHERE singleton_id=1
                   AND EXISTS(SELECT 1 FROM changed_search_documents
                              UNION SELECT 1 FROM new_search_files);
                UPDATE index_state SET discovery_generation=@generation,
                       last_success_utc=CASE WHEN @complete=1 THEN @now ELSE last_success_utc END,
                       source_state=CASE WHEN @complete=1 THEN 'ready' ELSE 'partial' END,last_error_code=NULL
                 WHERE singleton_id=1;
                INSERT INTO indexed_roots(root_norm,last_success_utc,discovery_generation)
                SELECT root_norm,@now,@generation FROM online_roots WHERE @complete=1
                ON CONFLICT(root_norm) DO UPDATE SET last_success_utc=@now,
                    discovery_generation=@generation;
                UPDATE discovery_runs SET completed=1 WHERE run_id=@run;
                """))
            {
                merge.Parameters.AddWithValue("@run", runId);
                merge.Parameters.AddWithValue("@generation", generation);
                merge.Parameters.AddWithValue("@now", now);
                merge.Parameters.AddWithValue("@cloudRetryCutoff", cloudPlaceholderRetryCutoff);
                merge.Parameters.AddWithValue("@cloudCooldown", CloudPlaceholderRediscoveryCooldown.Ticks);
                merge.Parameters.AddWithValue("@complete", complete ? 1 : 0);
                merge.Parameters.AddWithValue("@reconcile", reconcile ? 1 : 0);
                merge.Parameters.AddWithValue("@separator", Path.DirectorySeparatorChar.ToString());
                merge.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
                await merge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            mergeElapsed = totalWatch.ElapsedMilliseconds - tempPrepElapsed - detectElapsed;

            await using (var cleanup = Command(connection, transaction, "DELETE FROM discovery_runs WHERE run_id=@run;"))
            {
                cleanup.Parameters.AddWithValue("@run", runId);
                await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var changed = Command(
                connection,
                transaction,
                "SELECT EXISTS(SELECT 1 FROM search_result_changes);"))
            {
                searchContentChanged = Convert.ToInt32(
                    await changed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
            }

            var commitWatch = System.Diagnostics.Stopwatch.StartNew();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            commitWatch.Stop();
            commitElapsed = commitWatch.ElapsedMilliseconds;
        }
        finally
        {
            _writeGate.Release();
        }

        // Counters and durations only; never paths or search terms. The
        // runtime archives this after the snapshot boundary settles.
        Volatile.Write(
            ref _lastDiscoveryFinalizeTiming,
            $"complete={(complete ? 1 : 0)};reconcile={(reconcile ? 1 : 0)};stagedRows={stagedRows};activeRows={activeRows}" +
            $" roots={onlineRoots.Count};incompletePrefixes={incompletePrefixes.Count}" +
            $" gateWaitMs={gateWatch.ElapsedMilliseconds};tempPrepMs={tempPrepElapsed};detectMs={detectElapsed}" +
            $" mergeMs={mergeElapsed};commitMs={commitElapsed};totalMs={totalWatch.ElapsedMilliseconds}");

        // A full or report-validated partial finalization is the publication
        // boundary for its staged comparison and queue work. Storage failures
        // never reach here, so they cannot advance the visible work epoch.
        MarkMetadataProgressBatchPublished();

        if (searchContentChanged)
        {
            MarkSearchContentChanged();
        }
        else
        {
            StartSearchProjection();
        }

        await MaintainStorageAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetDiscoveryCapabilityAsync(
        RuntimeCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE index_state SET source_state=@state,last_error_code=@code WHERE singleton_id=1;
                """;
            command.Parameters.AddWithValue("@state", capability.State == CapabilityState.Available ? "ready" : "stale");
            command.Parameters.AddWithValue("@code", capability.Code);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<string>> GetIndexedRootsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_norm FROM indexed_roots ORDER BY root_norm COLLATE NOCASE;";
        var roots = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roots.Add(reader.GetString(0));
        }

        return roots;
    }

    public async ValueTask ApplyDiscoveryHintAsync(
        DiscoveredFile entry,
        CancellationToken cancellationToken = default)
    {
        _ = await ApplyDiscoveryHintBatchAsync([entry], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a bounded set of live discovery upserts in one SQLite
    /// transaction. The boolean is deliberately about visible work-set changes,
    /// not merely identity backfills, so the runtime can publish exactly one
    /// progress epoch for a real batch.
    /// </summary>
    internal async ValueTask<DiscoveryHintBatchResult> ApplyDiscoveryHintBatchAsync(
        IReadOnlyList<DiscoveredFile> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return default;
        }

        var searchContentChanged = false;
        var deferredForActiveLease = false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var result = await ApplyDiscoveryHintCoreAsync(
                        connection,
                        transaction,
                        entry,
                        cancellationToken)
                    .ConfigureAwait(false);
                searchContentChanged |= result.Changed;
                deferredForActiveLease |= result.DeferredForActiveLease;
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
            MarkMetadataProgressBatchPublished();
        }
        else
        {
            StartSearchProjection();
        }

        return new DiscoveryHintBatchResult(searchContentChanged, deferredForActiveLease);
    }

    private async ValueTask<DiscoveryHintBatchResult> ApplyDiscoveryHintCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscoveredFile entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (DiscoveryPath.IsExcludedSystemPath(entry.Candidate.FullPath))
        {
            return default;
        }
        IndexNormalization.ValidateCandidate(entry.Candidate);
        var pathNorm = IndexNormalization.PathKey(entry.Candidate.FullPath);
        var rootNorm = NormalizeDiscoveryRoot(entry.RootPath);
        EnsurePathIsWithinRoot(pathNorm, rootNorm, nameof(entry));
        var identity = entry.Identity is null
            ? null
            : $"{entry.Identity.VolumeSerialNumber:X8}:{entry.Identity.FileIndex:X16}";
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var searchContentChanged = false;
        long? fileId = null;
            await using (var find = Command(connection, transaction, """
                SELECT id FROM files WHERE path_norm=@path
                UNION ALL
                SELECT id FROM files WHERE @identity IS NOT NULL AND file_identity=@identity
                  AND NOT EXISTS(SELECT 1 FROM files WHERE path_norm=@path)
                LIMIT 2;
                """))
            {
                find.Parameters.AddWithValue("@path", pathNorm);
                find.Parameters.AddWithValue("@identity", (object?)identity ?? DBNull.Value);
                var ids = new List<long>(2);
                await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(reader.GetInt64(0));
                }

                if (ids.Distinct().Count() == 1)
                {
                    fileId = ids[0];
                }
            }

            if (fileId.HasValue)
            {
                string? storedIdentity = null;
                await using (var current = Command(
                    connection,
                    transaction,
                    "SELECT path,kind,state,size,modified_utc,file_identity FROM files WHERE id=@file;"))
                {
                    current.Parameters.AddWithValue("@file", fileId.Value);
                    await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("The discovered file disappeared during its update.");
                    }

                    storedIdentity = reader.IsDBNull(5) ? null : reader.GetString(5);
                    var knownIdentityChanged =
                        storedIdentity is not null &&
                        identity is not null &&
                        !string.Equals(storedIdentity, identity, StringComparison.Ordinal);
                    searchContentChanged =
                        !string.Equals(reader.GetString(0), entry.Candidate.FullPath, StringComparison.Ordinal) ||
                        reader.GetInt32(1) != (int)entry.Candidate.Kind ||
                        reader.GetInt32(2) != 0 ||
                        reader.GetInt64(3) != entry.Candidate.Size ||
                        reader.GetInt64(4) != UtcTicks(entry.Candidate.LastWriteTimeUtc) ||
                        knownIdentityChanged;
                }

                if (searchContentChanged)
                {
                    await using (var activeLease = Command(connection, transaction, """
                        SELECT EXISTS(
                            SELECT 1 FROM index_queue
                             WHERE file_id=@file AND lease_owner IS NOT NULL
                               AND lease_until_utc>@now);
                        """))
                    {
                        activeLease.Parameters.AddWithValue("@file", fileId.Value);
                        activeLease.Parameters.AddWithValue("@now", now);
                        if (Convert.ToInt32(
                                await activeLease.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            // The worker's CAS commits against this exact queue
                            // fingerprint. Leave both its evidence and lease
                            // intact, but retain the newer observation inside
                            // this transaction so a process crash cannot lose
                            // it before the owner reaches a terminal state.
                            await PersistDeferredDiscoveryUpsertCoreAsync(
                                    connection,
                                    transaction,
                                    fileId.Value,
                                    entry,
                                    pathNorm,
                                    identity,
                                    now,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return new DiscoveryHintBatchResult(false, true);
                        }
                    }

                    await using var update = Command(connection, transaction, """
                        UPDATE files SET path=@path,path_norm=@norm,kind=@kind,size=@size,
                               modified_utc=@modified,file_identity=coalesce(@identity,file_identity),
                               state=0,metadata_state=1,error_code=NULL
                         WHERE id=@file;
                        DELETE FROM dead_letter WHERE file_id=@file;
                        INSERT INTO index_queue(
                            file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                        VALUES(@file,'Modified',2,@now,0,0,
                               printf('%lld:%lld:%s:%d',@size,@modified,
                                      coalesce((SELECT file_identity FROM files WHERE id=@file),''),
                                      @extractor),@extractor)
                        ON CONFLICT(file_id) DO UPDATE SET
                            reason='Modified',priority=min(index_queue.priority,2),
                            due_utc=@now,attempt=0,failure_count=0,fingerprint=excluded.fingerprint,
                            extractor_version=@extractor,lease_until_utc=NULL,lease_owner=NULL,last_error=NULL;
                        """);
                    AddHintParameters(update, entry, pathNorm, identity, fileId.Value, now);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (identity is not null && storedIdentity is null)
                {
                    await using var backfillIdentity = Command(connection, transaction, """
                        UPDATE index_queue AS q SET
                            fingerprint=printf('%lld:%lld:%s:%d',
                                               @size,@modified,@identity,q.extractor_version)
                         WHERE q.file_id=@file
                           AND (q.lease_owner IS NULL OR q.lease_until_utc IS NULL
                                OR q.lease_until_utc<=@now);
                        UPDATE dead_letter AS d SET
                            fingerprint=printf('%lld:%lld:%s:%d',
                                               @size,@modified,@identity,d.extractor_version)
                         WHERE d.file_id=@file
                           AND NOT EXISTS(
                               SELECT 1 FROM index_queue active
                                WHERE active.file_id=@file
                                  AND active.lease_owner IS NOT NULL
                                  AND active.lease_until_utc>@now);
                        UPDATE files SET file_identity=@identity
                         WHERE id=@file AND file_identity IS NULL
                           AND NOT EXISTS(
                               SELECT 1 FROM index_queue active
                                WHERE active.file_id=@file
                                  AND active.lease_owner IS NOT NULL
                                  AND active.lease_until_utc>@now);
                        """);
                    backfillIdentity.Parameters.AddWithValue("@identity", identity);
                    backfillIdentity.Parameters.AddWithValue("@file", fileId.Value);
                    backfillIdentity.Parameters.AddWithValue("@size", entry.Candidate.Size);
                    backfillIdentity.Parameters.AddWithValue(
                        "@modified",
                        UtcTicks(entry.Candidate.LastWriteTimeUtc));
                    backfillIdentity.Parameters.AddWithValue("@now", now);
                    await backfillIdentity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                searchContentChanged = true;
                await using var insert = Command(connection, transaction, """
                    INSERT INTO files(path,path_norm,kind,size,modified_utc,file_identity,
                                      discovery_generation,state,metadata_state,extractor_version)
                    SELECT @path,@norm,@kind,@size,@modified,@identity,discovery_generation,0,1,0
                      FROM index_state WHERE singleton_id=1;
                    SELECT last_insert_rowid();
                    """);
                AddHintParameters(insert, entry, pathNorm, identity, 0, now, includeFile: false);
                fileId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                await using var queue = Command(connection, transaction, """
                    INSERT INTO index_queue(file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
                    VALUES(@file,'New',3,@now,0,0,
                           printf('%lld:%lld:%s:%d',@size,@modified,coalesce(@identity,''),@extractor),@extractor);
                    """);
                AddHintParameters(queue, entry, pathNorm, identity, fileId.Value, now);
                await queue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (searchContentChanged)
            {
                await RebuildSearchFtsCoreAsync(connection, transaction, fileId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            await using (var root = Command(connection, transaction, """
                INSERT INTO indexed_roots(root_norm,last_success_utc,discovery_generation)
                SELECT @root,@now,discovery_generation FROM index_state WHERE singleton_id=1
                ON CONFLICT(root_norm) DO UPDATE SET last_success_utc=@now;
                """))
            {
                root.Parameters.AddWithValue("@root", rootNorm);
                root.Parameters.AddWithValue("@now", now);
                await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // A later unleased observation is authoritative over any older
            // protected evidence for this row. Without this cleanup, a delayed
            // drain could replay snapshot A after snapshot B already applied.
            if (fileId.HasValue)
            {
                await using var clearDeferred = Command(
                    connection,
                    transaction,
                    "DELETE FROM deferred_discovery_changes WHERE file_id=@file;");
                clearDeferred.Parameters.AddWithValue("@file", fileId.Value);
                await clearDeferred.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

        return new DiscoveryHintBatchResult(searchContentChanged, false);
    }

    private static void AddHintParameters(
        SqliteCommand command,
        DiscoveredFile entry,
        string pathNorm,
        string? identity,
        long fileId,
        long now,
        bool includeFile = true)
    {
        command.Parameters.AddWithValue("@path", entry.Candidate.FullPath);
        command.Parameters.AddWithValue("@norm", pathNorm);
        command.Parameters.AddWithValue("@kind", (int)entry.Candidate.Kind);
        command.Parameters.AddWithValue("@size", entry.Candidate.Size);
        command.Parameters.AddWithValue("@modified", UtcTicks(entry.Candidate.LastWriteTimeUtc));
        command.Parameters.AddWithValue("@identity", (object?)identity ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        if (includeFile)
        {
            command.Parameters.AddWithValue("@file", fileId);
        }
    }

    private async ValueTask DeleteDiscoveryRunBestEffortAsync(string runId)
    {
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM discovery_runs WHERE run_id=@run;";
                command.Parameters.AddWithValue("@run", runId);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            // Initialization removes incomplete runs after a crash.
        }
    }

    private static (
        IReadOnlyCollection<string> OnlineRoots,
        IReadOnlyCollection<string> IncompletePrefixes,
        IReadOnlyCollection<string> TerminalSkippedPrefixes) BuildReconciliationScope(
            DiscoveryScanReport report)
    {
        var onlineRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompletePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terminalSkippedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in report.Roots.Where(static root => root.WasOnline))
        {
            var rootNorm = NormalizeDiscoveryRoot(root.RootPath);
            onlineRoots.Add(rootNorm);
            foreach (var prefix in root.TerminalSkippedPrefixes)
            {
                var prefixNorm = NormalizeDiscoveryRoot(prefix);
                if (IsPathWithinNormalizedRoot(prefixNorm, rootNorm))
                {
                    terminalSkippedPrefixes.Add(prefixNorm);
                }
            }
            if (root.FullyEnumerated && root.IncompletePrefixes.Count == 0)
            {
                continue;
            }

            if (root.IncompletePrefixes.Count == 0)
            {
                // Older/custom providers cannot identify the failed subtree.
                // Excluding the entire root prevents a partial snapshot from
                // becoming an authoritative deletion pass.
                incompletePrefixes.Add(rootNorm);
                continue;
            }

            var invalidPrefix = false;
            foreach (var prefix in root.IncompletePrefixes)
            {
                var prefixNorm = NormalizeDiscoveryRoot(prefix);
                if (!IsPathWithinNormalizedRoot(prefixNorm, rootNorm))
                {
                    invalidPrefix = true;
                    break;
                }

                incompletePrefixes.Add(prefixNorm);
            }

            if (invalidPrefix)
            {
                incompletePrefixes.Add(rootNorm);
            }

        }

        return (onlineRoots.ToArray(), incompletePrefixes.ToArray(), terminalSkippedPrefixes.ToArray());
    }

    private static string InferCompatibilityRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("路径没有可对账的根目录。", nameof(path));
        }

        return NormalizeDiscoveryRoot(root);
    }

    private static string NormalizeDiscoveryRoot(string path) =>
        IndexNormalization.PathKey(path);

    private static void EnsurePathIsWithinRoot(string pathNorm, string rootNorm, string parameterName)
    {
        if (!IsPathWithinNormalizedRoot(pathNorm, rootNorm))
        {
            throw new ArgumentException("Discovered file is outside its reported discovery root.", parameterName);
        }
    }

    private static bool IsPathWithinNormalizedRoot(string pathNorm, string rootNorm)
    {
        if (pathNorm.Equals(rootNorm, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = rootNorm.EndsWith(Path.DirectorySeparatorChar)
            ? rootNorm
            : rootNorm + Path.DirectorySeparatorChar;
        return pathNorm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
