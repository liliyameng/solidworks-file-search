using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore : IIndexStore, IAsyncDisposable
{
    public const long TargetDatabaseBytes = 1_500L * 1024 * 1024;
    public const long MaximumDatabaseBytes = 2L * 1024 * 1024 * 1024;
    public const long PassiveCheckpointWalBytes = 32L * 1024 * 1024;
    public const long PauseWalBytes = 64L * 1024 * 1024;
    public const long PhysicalSafetyDatabaseBytes = 4L * 1024 * 1024 * 1024;
    internal const long CoreMetadataReserveBytes = 128L * 1024 * 1024;

    private readonly string _initializationConnectionString;
    private string _operationalConnectionString;
    private readonly string _searchConnectionString;
    private readonly IndexStoragePolicy _storagePolicy;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _storageStatusSnapshotGate = new(1, 1);
    private readonly AsyncLocal<StorageStatusCycleState?> _storageStatusCycle = new();
    private readonly CancellationTokenSource _storageMaintenanceCancellation = new();
    private readonly object _storageMaintenanceSync = new();
    private Task? _storageMaintenanceTask;
    private bool _storageMaintenanceKickPending;
    private readonly CancellationTokenSource _searchProjectionCancellation = new();
    private readonly object _searchProjectionSync = new();
    private Task? _searchProjectionTask;
    private bool _searchProjectionKickPending;
    private string _searchProjectionHealth = "initializing";
    private long _searchProjectionAppliedSequence;
    private long _searchContentVersion;
    // This is deliberately process-local. The UI only needs to distinguish
    // committed work-set publications in the currently connected broker
    // lifetime; a broker restart already resets its UI/session epoch.
    private long _metadataProgressEpoch;
    private long _lastMaintenanceGateWaitMilliseconds;
    private long _lastMaintenanceGateHoldMilliseconds;
    private string _maintenancePhase = "idle";
    private int _walPressureLatched;
    private IndexStorageStatus? _storageStatusSnapshot;
    private long _storageStatusSnapshotExpiresUtcTicks;
    private long _storageStatusFullProbeCount;
    private long _storageStatusCacheHitCount;
    private int _initializationCompleted;
    private bool _disposed;
    private bool _readOnlyExisting;
    private string? _lastDiscoveryFinalizeTiming;

    public SqliteIndexStore(string? databasePath = null, IndexStoragePolicy? storagePolicy = null)
    {
        _storagePolicy = storagePolicy ?? IndexStoragePolicy.Default;
        ValidateStoragePolicy(_storagePolicy);
        DatabasePath = databasePath ?? SwSearchPaths.DatabasePath;
        var fullPath = Path.GetFullPath(DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        DatabasePath = fullPath;
        SearchDatabasePath = ResolveSearchDatabasePath(fullPath);
        _initializationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        _operationalConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        _searchConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = SearchDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    private SqliteIndexStore(string databasePath, bool readOnlyExisting)
    {
        _storagePolicy = IndexStoragePolicy.Default;
        DatabasePath = databasePath;
        SearchDatabasePath = ResolveSearchDatabasePath(databasePath);
        _initializationConnectionString = string.Empty;
        // Read-only fallback consumers open per query. WAL snapshot isolation
        // is database-wide, so a shared page cache only adds table-lock
        // semantics that can surface SQLITE_LOCKED while the Broker-side
        // projector commits (see TryOpenReadySearchConnectionAsync). Pooling
        // must also stay disabled: ResetSearchSidecarBestEffortAsync deletes
        // the sidecar files cross-process and tolerates only transiently open
        // reader handles, and read leases pin a WAL snapshot for exactly one
        // query lifetime.
        _operationalConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();
        _readOnlyExisting = readOnlyExisting;
        _searchConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = SearchDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();
        Volatile.Write(ref _initializationCompleted, 1);
    }

    public string DatabasePath { get; private set; }

    public string SearchDatabasePath { get; }

    private static string DeriveSearchDatabasePath(string corePath)
    {
        var directory = Path.GetDirectoryName(corePath)!;
        var stem = Path.GetFileNameWithoutExtension(corePath);
        return Path.Combine(directory, stem + ".search.db");
    }

    private static string ResolveSearchDatabasePath(string corePath)
    {
        var fullCorePath = Path.GetFullPath(corePath);
        if (string.Equals(
                fullCorePath,
                Path.GetFullPath(SwSearchPaths.CoreDatabasePath),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fullCorePath,
                Path.GetFullPath(SwSearchPaths.LegacyDatabasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(SwSearchPaths.SearchDatabasePath);
        }
        return DeriveSearchDatabasePath(fullCorePath);
    }

    public static SqliteIndexStore OpenReadOnlyExisting(string? databasePath = null)
    {
        var fullPath = Path.GetFullPath(databasePath ?? SwSearchPaths.DatabasePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("INDEX_DATABASE_MISSING", fullPath);
        return new SqliteIndexStore(fullPath, readOnlyExisting: true);
    }

    internal long SearchContentVersion => Interlocked.Read(ref _searchContentVersion);

    /// <summary>
    /// Phase timings of the most recent committed discovery finalize, for
    /// diagnosing long merge transactions. Contains counters and durations
    /// only — never paths or search terms.
    /// </summary>
    internal string? LastDiscoveryFinalizeTiming => Volatile.Read(ref _lastDiscoveryFinalizeTiming);

    internal long MetadataProgressEpoch => Interlocked.Read(ref _metadataProgressEpoch);

    internal string InitializationConnectionString => _initializationConnectionString;

    internal string OperationalConnectionString => _operationalConnectionString;

    internal Func<CancellationToken, ValueTask>? BeforePostInitializationOpenForTestingAsync { get; set; }

    internal Func<CancellationToken, ValueTask>? AfterSearchReadinessAcceptedForTestingAsync { get; set; }

    internal TimeSpan StorageStatusSnapshotLifetimeForTesting { get; set; } = TimeSpan.FromSeconds(1);

    internal long StorageStatusFullProbeCount =>
        Interlocked.Read(ref _storageStatusFullProbeCount);

    internal long StorageStatusCacheHitCount =>
        Interlocked.Read(ref _storageStatusCacheHitCount);

    internal IDisposable BeginStorageStatusSnapshotCycle()
    {
        var prior = _storageStatusCycle.Value;
        var current = new StorageStatusCycleState();
        _storageStatusCycle.Value = current;
        return new StorageStatusCycleScope(this, current, prior);
    }

    private void MarkSearchContentChanged()
    {
        Interlocked.Increment(ref _searchContentVersion);
        StartSearchProjection();
    }

    /// <summary>
    /// Publishes one intentional metadata-work-set boundary after its SQLite
    /// transaction committed. Callers must never invoke this for staged or
    /// failed discovery work: readers use it to reject mixed progress samples.
    /// </summary>
    private void MarkMetadataProgressBatchPublished() =>
        Interlocked.Increment(ref _metadataProgressEpoch);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gateWait = System.Diagnostics.Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gateWait.Stop();
        var gateHold = System.Diagnostics.Stopwatch.StartNew();
        Volatile.Write(ref _lastMaintenanceGateWaitMilliseconds, (long)gateWait.Elapsed.TotalMilliseconds);
        Volatile.Write(ref _maintenancePhase, "short-gram-retirement");
        try
        {
            await using var connection = await OpenInitializationConnectionAsync(cancellationToken).ConfigureAwait(false);
            await SqliteSchema.InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
            await FinalizeLegacyFileNotFoundQueueAsync(connection, cancellationToken).ConfigureAwait(false);
            if (await PurgeExcludedSystemPathsAsync(connection, cancellationToken).ConfigureAwait(false) > 0)
            {
                MarkSearchContentChanged();
            }
        }
        catch
        {
            Volatile.Write(ref _maintenancePhase, "recovery-failed");
            throw;
        }
        finally
        {
            gateHold.Stop();
            Volatile.Write(ref _lastMaintenanceGateHoldMilliseconds, (long)gateHold.Elapsed.TotalMilliseconds);
            _writeGate.Release();
        }

        // Initialization is complete only when the primary database is present.
        // Unlike the WAL, a missing primary database is never a checkpoint race.
        _ = ReadRequiredDatabaseLength(DatabasePath, static path => new FileInfo(path).Length);
        Volatile.Write(ref _initializationCompleted, 1);
        await MaintainStorageAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _maintenancePhase, "idle");
        StartStorageMaintenance();
        await InitializeSearchSidecarBestEffortAsync(CancellationToken.None).ConfigureAwait(false);
        StartSearchProjection();
    }

    public async ValueTask DisposeAsync()
    {
        Task? maintenanceTask;
        lock (_storageMaintenanceSync)
        {
            if (_disposed)
            {
                return;
            }

            // The worker makes its restart decision under the same lock.
            // Freezing the owner first guarantees Dispose observes and awaits
            // the final generation, including one started just before this lock.
            _disposed = true;
            _storageMaintenanceKickPending = false;
            maintenanceTask = _storageMaintenanceTask;
        }

        _storageMaintenanceCancellation.Cancel();
        Task? searchProjectionTask;
        lock (_searchProjectionSync)
        {
            _searchProjectionKickPending = false;
            searchProjectionTask = _searchProjectionTask;
        }
        _searchProjectionCancellation.Cancel();
        if (maintenanceTask is not null)
        {
            try
            {
                await maintenanceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal bounded shutdown of the low-priority migration.
            }
        }
        if (searchProjectionTask is not null)
        {
            try
            {
                await searchProjectionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal sidecar shutdown.
            }
        }
        _storageMaintenanceCancellation.Dispose();
        _searchProjectionCancellation.Dispose();
        SqliteConnection.ClearAllPools();
        await ValueTask.CompletedTask;
        _storageStatusSnapshotGate.Dispose();
        _writeGate.Dispose();
    }

    private static async ValueTask<long> PurgeExcludedSystemPathsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM files WHERE instr(upper(replace(path,'/','\\')),@segment)>0;";
        count.Parameters.AddWithValue("@segment", IndexNormalization.ExcludedRecycleBinSegment);
        var affected = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (affected == 0)
        {
            return 0;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var purge = connection.CreateCommand();
        purge.Transaction = transaction;
        purge.CommandText = """
            DELETE FROM search_projection_outbox
             WHERE file_id IN (
                SELECT id FROM files
                 WHERE instr(upper(replace(path,'/','\')),@segment)>0);
            INSERT INTO search_projection_outbox(file_id,reason,queued_utc)
            SELECT id,'ExcludedSystemPath',@now
              FROM files WHERE instr(upper(replace(path,'/','\')),@segment)>0;
            UPDATE search_projection_state
               SET current_sequence=last_insert_rowid()
             WHERE singleton_id=1 AND changes()>0;
            DELETE FROM files WHERE instr(upper(replace(path,'/','\')),@segment)>0;
            """;
        purge.Parameters.AddWithValue("@segment", IndexNormalization.ExcludedRecycleBinSegment);
        purge.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
        await purge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    internal async ValueTask<IAsyncDisposable> AcquireWriteGateForTestingAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new WriteGateLease(_writeGate);
    }

    private sealed class WriteGateLease(SemaphoreSlim writeGate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                writeGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    internal async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _initializationCompleted) == 0)
        {
            throw new InvalidOperationException("INDEX_STORAGE_NOT_INITIALIZED");
        }

        // Operational paths are physically constrained to ReadWrite. The strict
        // probe gives a stable storage fault before open; ReadWrite closes the
        // TOCTOU window by making SQLite itself refuse to create a replacement.
        _ = ReadRequiredDatabaseLength(DatabasePath, static path => new FileInfo(path).Length);
        var connection = new SqliteConnection(_operationalConnectionString);
        try
        {
            if (BeforePostInitializationOpenForTestingAsync is { } beforeOpen)
            {
                await beforeOpen(cancellationToken).ConfigureAwait(false);
            }

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (_readOnlyExisting)
            {
                await SqliteSchema.ExecuteAsync(connection, "PRAGMA query_only=ON;", cancellationToken).ConfigureAwait(false);
                await SqliteSchema.ExecuteAsync(connection, "PRAGMA busy_timeout=50;", cancellationToken).ConfigureAwait(false);
                // Memory-mapped reads keep query pages file-backed (shared,
                // reclaimable) instead of private heap copies, lowering the
                // resident footprint of per-query reader connections.
                await SqliteSchema.ExecuteAsync(connection, "PRAGMA mmap_size=134217728;", cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            return connection;
        }
        catch (SqliteException) when (Volatile.Read(ref _initializationCompleted) != 0)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            // If the primary DB disappeared after the first strict probe, turn
            // SQLite's ReadWrite open failure into the same explicit fault.
            _ = ReadRequiredDatabaseLength(DatabasePath, static path => new FileInfo(path).Length);
            throw;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<SqliteConnection> OpenInitializationConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_initializationConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=16384;", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SqliteSchema.ExecuteAsync(connection, "PRAGMA busy_timeout=100;", cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<SqliteConnection> OpenSearchReadConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection.CreateFunction<string?, string?, string?, string?, long?, string?>(
                "sw_safe_value",
                static (raw, resolved, display, cached, linked) =>
                    SolidWorksPropertyValue.GetResolvedDisplayValue(
                        raw,
                        resolved,
                        display,
                        cached,
                        linked.GetValueOrDefault() != 0));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static long UtcTicks(DateTimeOffset value) => value.ToUniversalTime().Ticks;

    internal static DateTimeOffset FromUtcTicks(long value) =>
        new(value, TimeSpan.Zero);

    public async ValueTask<IndexStorageStatus> GetStorageStatusAsync(
        CancellationToken cancellationToken = default)
        => await GetStorageStatusAsync(forceFresh: false, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<IndexStorageStatus> GetStorageStatusAsync(
        bool forceFresh,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cycle = _storageStatusCycle.Value;
        if (!forceFresh &&
            cycle is { Active: true, Snapshot: { } cycleSnapshot } &&
            CanReuseStorageStatusWithinCycle(cycleSnapshot))
        {
            Interlocked.Increment(ref _storageStatusCacheHitCount);
            return cycleSnapshot;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var cached = Volatile.Read(ref _storageStatusSnapshot);
        if (!forceFresh &&
            CanReuseCurrentStorageStatusSnapshot(
                cached,
                Interlocked.Read(ref _storageStatusSnapshotExpiresUtcTicks),
                nowTicks))
        {
            Interlocked.Increment(ref _storageStatusCacheHitCount);
            return CaptureStorageStatusForCycle(cached!);
        }

        await _storageStatusSnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            cached = Volatile.Read(ref _storageStatusSnapshot);
            if (!forceFresh &&
                CanReuseCurrentStorageStatusSnapshot(
                    cached,
                    Interlocked.Read(ref _storageStatusSnapshotExpiresUtcTicks),
                    nowTicks))
            {
                Interlocked.Increment(ref _storageStatusCacheHitCount);
                return CaptureStorageStatusForCycle(cached!);
            }

            var fresh = await ReadStorageStatusAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _storageStatusSnapshot, fresh);
            var lifetime = StorageStatusSnapshotLifetimeForTesting;
            Interlocked.Exchange(
                ref _storageStatusSnapshotExpiresUtcTicks,
                DateTimeOffset.UtcNow.Add(lifetime).UtcTicks);
            return CaptureStorageStatusForCycle(fresh);
        }
        finally
        {
            _storageStatusSnapshotGate.Release();
        }
    }

    private async ValueTask<IndexStorageStatus> ReadStorageStatusAsync(
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _storageStatusFullProbeCount);
        var databaseBytes = ReadRequiredDatabaseLength(
            DatabasePath,
            static path => new FileInfo(path).Length);
        var walPath = DatabasePath + "-wal";
        var walBytes = ReadVolatileWalLength(
            walPath,
            static path => File.Exists(path),
            static path => new FileInfo(path).Length);

        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var pageSize = await ReadPragmaInt64Async(connection, "page_size", cancellationToken).ConfigureAwait(false);
        var pageCount = await ReadPragmaInt64Async(connection, "page_count", cancellationToken).ConfigureAwait(false);
        var freePageCount = await ReadPragmaInt64Async(connection, "freelist_count", cancellationToken)
            .ConfigureAwait(false);
        var reusableBytes = SaturatingMultiply(pageSize, freePageCount);
        var logicalBytes = SaturatingMultiply(pageSize, Math.Max(0, pageCount - freePageCount));
        var physicalAllocatedBytes = SaturatingAdd(databaseBytes, walBytes);
        var reason = ResolveStoragePauseReason(
            physicalAllocatedBytes,
            logicalBytes,
            walBytes);
        var projection = await GetSearchProjectionStatusAsync(cancellationToken).ConfigureAwait(false);
        var corePathMode = string.Equals(
                DatabasePath,
                Path.GetFullPath(SwSearchPaths.LegacyDatabasePath),
                StringComparison.OrdinalIgnoreCase)
            ? "legacy-index-v2-alias"
            : string.Equals(
                DatabasePath,
                Path.GetFullPath(SwSearchPaths.CoreDatabasePath),
                StringComparison.OrdinalIgnoreCase)
                ? "core"
                : "custom-core";
        return new IndexStorageStatus(
            databaseBytes,
            walBytes,
            reason is not null,
            reason,
            logicalBytes,
            reusableBytes,
            pageSize,
            pageCount,
            freePageCount,
            Volatile.Read(ref _maintenancePhase),
            Volatile.Read(ref _lastMaintenanceGateWaitMilliseconds),
            Volatile.Read(ref _lastMaintenanceGateHoldMilliseconds),
            DatabasePath,
            SearchDatabasePath,
            corePathMode,
            projection.Health,
            projection.Current,
            projection.Applied);
    }

    internal static bool CanReuseStorageStatusSnapshot(
        IndexStorageStatus? snapshot,
        long expiresUtcTicks,
        long nowUtcTicks,
        IndexStoragePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return snapshot is not null &&
            expiresUtcTicks > nowUtcTicks &&
            !IsStorageStatusNearPressure(snapshot, policy);
    }

    private bool CanReuseCurrentStorageStatusSnapshot(
        IndexStorageStatus? snapshot,
        long expiresUtcTicks,
        long nowUtcTicks)
    {
        if (!CanReuseStorageStatusSnapshot(
                snapshot,
                expiresUtcTicks,
                nowUtcTicks,
                _storagePolicy))
        {
            return false;
        }

        // File length is a cheap guard compared with opening two SQLite
        // connections and running the PRAGMA/projection probes. A write burst
        // can cross the WAL low-water mark while a previously healthy snapshot
        // is still inside its one-second lifetime; detect that transition
        // immediately so the RC54 high/low latch is never hidden by the cache.
        return CurrentWalBytesBelowLowWater();
    }

    private bool CanReuseStorageStatusWithinCycle(IndexStorageStatus snapshot) =>
        !IsStorageStatusNearPressure(snapshot, _storagePolicy) &&
        CurrentWalBytesBelowLowWater();

    private bool CurrentWalBytesBelowLowWater()
    {
        var walPath = DatabasePath + "-wal";
        var currentWalBytes = ReadVolatileWalLength(
            walPath,
            static path => File.Exists(path),
            static path => new FileInfo(path).Length);
        return currentWalBytes < _storagePolicy.PassiveCheckpointWalBytes;
    }

    private IndexStorageStatus CaptureStorageStatusForCycle(IndexStorageStatus status)
    {
        var cycle = _storageStatusCycle.Value;
        if (cycle is { Active: true })
        {
            cycle.Snapshot = status;
        }

        return status;
    }

    internal static bool IsStorageStatusNearPressure(
        IndexStorageStatus snapshot,
        IndexStoragePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        if (snapshot.MetadataWritesPaused ||
            snapshot.WalBytes >= policy.PassiveCheckpointWalBytes)
        {
            return true;
        }

        var logicalMargin = Math.Min(
            CoreMetadataReserveBytes,
            Math.Max(1, policy.MaximumDatabaseBytes / 16));
        if (snapshot.LogicalDatabaseBytes >= policy.MaximumDatabaseBytes - logicalMargin)
        {
            return true;
        }

        var physicalMargin = Math.Min(
            CoreMetadataReserveBytes,
            Math.Max(1, policy.PhysicalSafetyDatabaseBytes / 16));
        var physicalAllocatedBytes = SaturatingAdd(snapshot.DatabaseBytes, snapshot.WalBytes);
        return physicalAllocatedBytes >= policy.PhysicalSafetyDatabaseBytes - physicalMargin;
    }

    internal string? ResolveStoragePauseReason(
        long physicalAllocatedBytes,
        long logicalBytes,
        long walBytes)
    {
        // Once WAL pressure reaches the high-water mark, remain paused until
        // checkpointing reaches the low-water mark. This avoids repeatedly
        // flashing a pressure state while the WAL oscillates between them.
        if (walBytes >= _storagePolicy.PauseWalBytes)
        {
            Interlocked.Exchange(ref _walPressureLatched, 1);
        }
        else if (walBytes <= _storagePolicy.PassiveCheckpointWalBytes)
        {
            Interlocked.Exchange(ref _walPressureLatched, 0);
        }

        if (physicalAllocatedBytes >= _storagePolicy.PhysicalSafetyDatabaseBytes)
        {
            return "DATABASE_PHYSICAL_SAFETY_LIMIT";
        }

        if (logicalBytes >= _storagePolicy.MaximumDatabaseBytes)
        {
            return "DATABASE_HARD_LIMIT";
        }

        return Volatile.Read(ref _walPressureLatched) != 0
            ? "WAL_PRESSURE"
            : null;
    }

    private static async ValueTask<long> ReadPragmaInt64Async(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma + ";";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long SaturatingMultiply(long left, long right)
    {
        if (left <= 0 || right <= 0)
        {
            return 0;
        }
        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    internal static long ReadRequiredDatabaseLength(
        string path,
        Func<string, long> readLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(readLength);
        try
        {
            return readLength(path);
        }
        catch (FileNotFoundException exception)
        {
            throw new IndexStorageFaultException("INDEX_DATABASE_MISSING", path, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new IndexStorageFaultException("INDEX_DATABASE_MISSING", path, exception);
        }
    }

    internal static long ReadVolatileWalLength(
        string path,
        Func<string, bool> exists,
        Func<string, long> readLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(readLength);
        if (!exists(path))
        {
            return 0;
        }

        try
        {
            return readLength(path);
        }
        catch (FileNotFoundException)
        {
            // SQLite can checkpoint and remove the WAL between Exists and Length.
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    public async ValueTask<IndexStorageStatus> MaintainStorageAsync(
        CancellationToken cancellationToken = default)
    {
        StartStorageMaintenance();
        return await CheckpointStorageIfNeededAsync(
            forceFresh: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the expensive WAL checkpoint only when storage state requires it.
    /// The normal metadata hot path schedules this work on the maintenance owner
    /// instead of awaiting it after every committed batch. Explicit callers keep
    /// the synchronous recovery behavior through <see cref="MaintainStorageAsync"/>.
    /// </summary>
    private async ValueTask<IndexStorageStatus> CheckpointStorageIfNeededAsync(
        bool forceFresh,
        CancellationToken cancellationToken)
    {
        var before = await GetStorageStatusAsync(forceFresh, cancellationToken).ConfigureAwait(false);
        if (before.WalBytes < _storagePolicy.PassiveCheckpointWalBytes)
        {
            return before;
        }

        var gateWait = System.Diagnostics.Stopwatch.StartNew();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gateWait.Stop();
        var gateHold = System.Diagnostics.Stopwatch.StartNew();
        Volatile.Write(ref _lastMaintenanceGateWaitMilliseconds, (long)gateWait.Elapsed.TotalMilliseconds);
        Volatile.Write(ref _maintenancePhase, "checkpoint");
        try
        {
            Interlocked.Increment(ref _storageCheckpointCount);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // PASSIVE never blocks readers or forces a foreground stall.  journal_size_limit
            // trims the WAL after a successful checkpoint; VACUUM is intentionally never automatic.
            var allFramesCheckpointed = false;
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var busy = reader.GetInt32(0);
                    var logFrames = reader.GetInt32(1);
                    var checkpointedFrames = reader.GetInt32(2);
                    allFramesCheckpointed = busy == 0 && logFrames == checkpointedFrames;
                }
            }

            if (allFramesCheckpointed)
            {
                // A zero-timeout truncate is attempted only after PASSIVE reports
                // every frame checkpointed. It either succeeds immediately or
                // yields to an active search; it never stalls foreground work.
                await SqliteSchema.ExecuteAsync(connection, "PRAGMA busy_timeout=0;", cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await SqliteSchema.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 5)
                {
                    // A reader appeared after PASSIVE completed. Yield and let a
                    // later background recovery retry; foreground reads win.
                }
                finally
                {
                    await SqliteSchema.ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Release SQLite page-cache heap retained across the historical peak
            // so resident size tracks live data; no-op when nothing to free.
            await SqliteSchema.ExecuteAsync(connection, "PRAGMA shrink_memory;", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gateHold.Stop();
            Volatile.Write(ref _lastMaintenanceGateHoldMilliseconds, (long)gateHold.Elapsed.TotalMilliseconds);
            Volatile.Write(ref _maintenancePhase, "idle");
            _writeGate.Release();
        }

        return await GetStorageStatusAsync(forceFresh: true, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateStoragePolicy(IndexStoragePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.TargetDatabaseBytes <= 0 ||
            policy.MaximumDatabaseBytes < policy.TargetDatabaseBytes ||
            policy.PassiveCheckpointWalBytes <= 0 ||
            policy.PauseWalBytes <= policy.PassiveCheckpointWalBytes ||
            policy.PhysicalSafetyDatabaseBytes <= policy.MaximumDatabaseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }

    private sealed class StorageStatusCycleState
    {
        internal bool Active { get; set; } = true;

        internal IndexStorageStatus? Snapshot { get; set; }
    }

    private sealed class StorageStatusCycleScope(
        SqliteIndexStore owner,
        StorageStatusCycleState current,
        StorageStatusCycleState? prior) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            current.Active = false;
            if (ReferenceEquals(owner._storageStatusCycle.Value, current))
            {
                owner._storageStatusCycle.Value = prior;
            }
        }
    }
}
