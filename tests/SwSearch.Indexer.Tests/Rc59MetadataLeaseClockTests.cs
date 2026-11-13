using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc59MetadataLeaseClockTests
{
    [Fact]
    public async Task LeaseMetadata_UsesStatementClockAfterWriteGateDelay()
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, "gate-delay.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        var reachedWriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeMetadataLeaseWriteGateForTestingAsync = _ =>
        {
            reachedWriteGate.TrySetResult();
            return ValueTask.CompletedTask;
        };

        var gate = await store.AcquireWriteGateForTestingAsync();
        try
        {
            var claimTask = store.LeaseMetadataAsync(
                1,
                TimeSpan.FromMilliseconds(100),
                "rc59-statement-clock-owner").AsTask();
            await reachedWriteGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(250);
            await gate.DisposeAsync();

            var lease = Assert.Single(await claimTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(
                lease.LeaseUntilUtc > DateTimeOffset.UtcNow.AddMilliseconds(50),
                $"Lease was already expired at admission: {lease.LeaseUntilUtc:O}");
            Assert.Empty(await store.LeaseMetadataAsync(
                1,
                TimeSpan.FromMilliseconds(100),
                "rc59-statement-clock-contender"));

            await store.ReleaseMetadataLeasesAsync(lease.LeaseOwner);
        }
        finally
        {
            await gate.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenewMetadata_RejectsLeaseThatExpiresDuringConnectionDelay()
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, "renew-delay.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        var original = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-expired-owner"));
        var delayedOpen = 0;
        store.BeforePostInitializationOpenForTestingAsync = async cancellationToken =>
        {
            if (Interlocked.Exchange(ref delayedOpen, 1) == 0)
            {
                await Task.Delay(250, cancellationToken);
                store.BeforePostInitializationOpenForTestingAsync = null;
            }
        };

        var renewed = await store.RenewMetadataLeasesAsync(
            [original],
            DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Empty(renewed);
        var replacement = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(1),
            "rc59-replacement-owner"));
        try
        {
            Assert.NotEqual(original.LeaseOwner, replacement.LeaseOwner);
            Assert.Empty(await store.RenewMetadataLeasesAsync(
                [original],
                DateTimeOffset.UtcNow.AddSeconds(1)));
        }
        finally
        {
            await store.ReleaseMetadataLeasesAsync(replacement.LeaseOwner);
        }
    }

    [Fact]
    public async Task RenewMetadata_RenewsCurrentExactLeaseAtStatementTime()
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, "renew-live.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        var original = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(2),
            "rc59-live-owner"));
        var requestedUntil = DateTimeOffset.UtcNow.AddSeconds(3);

        var renewed = await store.RenewMetadataLeasesAsync([original], requestedUntil);

        Assert.Contains(original.FileId, renewed);
        Assert.Empty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-live-contender"));
        await store.ReleaseMetadataLeasesAsync(original.LeaseOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataLease_UsesStatementClockAfterRealSqliteWriterLock(bool singlePath)
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, singlePath ? "path-writer-lock.SLDPRT" : "batch-writer-lock.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        const string oldOwner = "rc59-writer-lock-old-owner";
        var original = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(350),
            oldOwner));
        var untilExpired = original.LeaseUntilUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
        if (untilExpired > TimeSpan.Zero)
        {
            await Task.Delay(untilExpired);
        }

        await using var writerLock = await OpenWriterLockAsync(sandbox.DatabasePath);
        // BEGIN IMMEDIATE deliberately blocks before SELECT.  Run the claimant
        // on an owned test worker because Microsoft.Data.Sqlite executes the
        // contended native begin synchronously before returning its Task.
        Task<MetadataWorkLease?> replacementTask = Task.Run(async () =>
            singlePath
                ? await store.TryLeaseMetadataPathAsync(
                    file.FullPath,
                    TimeSpan.FromSeconds(1),
                    "rc59-writer-lock-new-owner")
                : await LeaseOneAsync(
                    store,
                    TimeSpan.FromSeconds(1),
                    "rc59-writer-lock-new-owner"));
        // This is intentionally longer than the requested new lease.  The
        // claim must remain blocked at BEGIN IMMEDIATE; it must not select an
        // old candidate snapshot while the independent writer owns the lock.
        await Task.Delay(TimeSpan.FromMilliseconds(1_500));
        Assert.False(replacementTask.IsCompleted);
        await ExecuteAsync(writerLock, "COMMIT;");

        var replacement = Assert.IsType<MetadataWorkLease>(
            await replacementTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(
            replacement.LeaseUntilUtc > DateTimeOffset.UtcNow.AddMilliseconds(200),
            $"{(singlePath ? "path" : "batch")} lease was stale after SQLite writer wait: " +
            replacement.LeaseUntilUtc.ToString("O"));

        // The replacement must be the only live lease.  A delayed old worker
        // may neither renew nor complete (delete/fail) the replacement.
        Assert.Empty(await store.RenewMetadataLeasesAsync(
            [original],
            DateTimeOffset.UtcNow.AddSeconds(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteMetadataLeaseAsync(
                original.FileId,
                original.LeaseOwner,
                succeeded: true).AsTask());
        Assert.Empty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(1),
            "rc59-writer-lock-contender"));
        await store.ReleaseMetadataLeasesAsync(replacement.LeaseOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataLease_DoesNotUseStaleSnapshotAfterExternalWriterCommits(bool singlePath)
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, singlePath ? "path-external-commit.SLDPRT" : "batch-external-commit.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        var pathNorm = IndexNormalization.PathKey(file.FullPath);
        var reachedWriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeMetadataLeaseWriteGateForTestingAsync = _ =>
        {
            reachedWriteGate.TrySetResult();
            return ValueTask.CompletedTask;
        };

        await using var writerLock = await OpenWriterLockAsync(sandbox.DatabasePath);
        // Start the claimant only after the independent writer has acquired
        // BEGIN IMMEDIATE.  The claimant must wait before SELECT; once the
        // writer gives the row a live lease and commits, fresh selection must
        // return no candidate rather than throw SQLITE_BUSY_SNAPSHOT or steal
        // the external owner's lease.
        Task<MetadataWorkLease?> claimTask = Task.Run(async () =>
            singlePath
                ? await store.TryLeaseMetadataPathAsync(
                    file.FullPath,
                    TimeSpan.FromSeconds(2),
                    "rc59-external-commit-claimant")
                : await LeaseOneAsync(
                    store,
                    TimeSpan.FromSeconds(2),
                    "rc59-external-commit-claimant"));

        await reachedWriteGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(claimTask.IsCompleted);

        await ExecuteAsync(writerLock, $"""
            UPDATE index_queue
               SET lease_owner='rc59-external-writer',
                   lease_until_utc={DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks}
             WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);
            """, ("@path", pathNorm));
        await ExecuteAsync(writerLock, "COMMIT;");

        Assert.Null(await claimTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var verify = await OpenReadConnectionAsync(sandbox.DatabasePath);
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT lease_owner FROM index_queue WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);";
        command.Parameters.AddWithValue("@path", pathNorm);
        Assert.Equal("rc59-external-writer", await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataLease_RejectsCandidateWhoseFingerprintChangesBeforeClaim(bool singlePath)
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, singlePath ? "path-replaced.SLDPRT" : "batch-replaced.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        var pathNorm = IndexNormalization.PathKey(file.FullPath);
        store.BeforeMetadataLeaseUpdateForTestingAsync = (connection, transaction, cancellationToken) =>
            ReplaceCandidateAsync(
                connection,
                transaction,
                pathNorm,
                "RC59_REPLACED_FINGERPRINT",
                leaseOwner: null,
                leaseUntilUtc: null,
                cancellationToken: cancellationToken);

        if (singlePath)
        {
            Assert.Null(await store.TryLeaseMetadataPathAsync(
                file.FullPath,
                TimeSpan.FromSeconds(1),
                "rc59-path-stale-candidate"));
        }
        else
        {
            Assert.Empty(await store.LeaseMetadataAsync(
                1,
                TimeSpan.FromSeconds(1),
                "rc59-batch-stale-candidate"));
        }
    }

    [Fact]
    public async Task PathLease_RejectsCandidateThatWasReplacedByAnExpiredLease()
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, "path-expired-replacement.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);
        var pathNorm = IndexNormalization.PathKey(file.FullPath);
        store.BeforeMetadataLeaseUpdateForTestingAsync = (connection, transaction, cancellationToken) =>
            ReplaceCandidateAsync(
                connection,
                transaction,
                pathNorm,
                "RC59_EXPIRED_REPLACEMENT",
                "rc59-expired-other-owner",
                DateTimeOffset.UtcNow.AddSeconds(-5),
                cancellationToken);

        // The replacement is itself expired, so a file_id-only claim would
        // incorrectly take it and return the old selected fingerprint/attempt.
        // The full CAS must reject the altered candidate instead.
        Assert.Null(await store.TryLeaseMetadataPathAsync(
            file.FullPath,
            TimeSpan.FromSeconds(1),
            "rc59-path-expired-stale-candidate"));
    }

    [Fact]
    public async Task PathLease_ReturnsActualClaimedLeaseOnNormalPath()
    {
        using var sandbox = new Sandbox();
        var file = CreateFile(sandbox, "path-normal.SLDPRT");
        await using var store = await CreateStoreAsync(sandbox, file);

        var lease = Assert.IsType<MetadataWorkLease>(await store.TryLeaseMetadataPathAsync(
            file.FullPath,
            TimeSpan.FromSeconds(2),
            "rc59-path-normal-owner"));

        Assert.Equal(file.FullPath, lease.FullPath);
        Assert.Equal("rc59-path-normal-owner", lease.LeaseOwner);
        Assert.Equal(1, lease.Attempt);
        Assert.True(lease.LeaseUntilUtc > DateTimeOffset.UtcNow.AddSeconds(1));
        await store.ReleaseMetadataLeasesAsync(lease.LeaseOwner);
    }

    private static async Task<MetadataWorkLease?> LeaseOneAsync(
        SqliteIndexStore store,
        TimeSpan leaseDuration,
        string leaseOwner)
    {
        var leases = await store.LeaseMetadataAsync(1, leaseDuration, leaseOwner);
        return leases.SingleOrDefault();
    }

    private static async ValueTask ReplaceCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string pathNorm,
        string fingerprint,
        string? leaseOwner,
        DateTimeOffset? leaseUntilUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE index_queue
               SET fingerprint=@fingerprint,
                   lease_owner=@owner,
                   lease_until_utc=@until
             WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);
            """;
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.Parameters.AddWithValue("@owner", leaseOwner ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@until",
            leaseUntilUtc is { } until ? until.UtcTicks : DBNull.Value);
        command.Parameters.AddWithValue("@path", pathNorm);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<SqliteConnection> OpenWriterLockAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000; BEGIN IMMEDIATE;");
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenReadConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<SqliteIndexStore> CreateStoreAsync(Sandbox sandbox, FileCandidate file)
    {
        var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync([file]), CancellationToken.None);
        return store;
    }

    private static FileCandidate CreateFile(Sandbox sandbox, string name)
    {
        var path = Path.Combine(sandbox.Path, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return StableFileEvidenceReader.Capture(path).Candidate;
    }

    private static async IAsyncEnumerable<FileCandidate> AsAsync(
        IEnumerable<FileCandidate> files,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        await Task.CompletedTask;
    }

    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"swsearch-rc59-lease-clock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string DatabasePath => System.IO.Path.Combine(Path, "index.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
