using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc88DiscoveryRetryTests
{
    private static readonly DateTimeOffset Modified = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnchangedCloudPlaceholders_AreNotRequeuedAcrossFourteenSnapshotsInsideCooldown()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var files = Enumerable.Range(0, 196)
            .Select(index => Candidate($@"C:\RC88\Cloud\same-{index:D3}.SLDPRT"))
            .ToArray();

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        await FinalizeCloudPlaceholdersAsync(store, files.Length);
        var initialDue = await ReadQueueDueAsync(database.Path, files[0].FullPath);

        for (var snapshot = 0; snapshot < 14; snapshot++)
        {
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
            Assert.Empty(await store.LeaseMetadataAsync(
                100,
                TimeSpan.FromSeconds(5),
                $"rc88-unchanged-{snapshot}"));
        }

        Assert.Equal(initialDue, await ReadQueueDueAsync(database.Path, files[0].FullPath));
        Assert.Equal(files.Length, (await store.GetStatusAsync()).PendingMetadataCount);
        // Cooled-down cloud placeholders are settled work for progress: they
        // can never be extracted while they stay cloud-only, so counting them
        // as pending froze both user-visible percentages below 100% and kept
        // the broker on its responsive base cadence forever. The queue rows
        // (PendingMetadataCount above) still drive the hourly retry.
        var progress = await store.GetMetadataProgressAsync();
        Assert.Equal(0, progress.PendingCount);
        Assert.Equal(files.Length, progress.SkippedCount);
        Assert.Equal(files.Length, progress.ProcessedCount);
    }

    [Fact]
    public async Task UnchangedCloudPlaceholder_IsAutomaticallyRetriedAfterPersistedCooldown()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC88\Cloud\eventual-everything-hydration.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await FinalizeCloudPlaceholdersAsync(store, 1);

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc88-before-cooldown"));

        // Simulate a later, restart-safe scheduler tick without a discovery pass.
        await AgeCloudTerminalAsync(database.Path, DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(-1));

        var reopened = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(5),
            "rc88-after-cooldown"));
        Assert.Equal(file.FullPath, reopened.FullPath);
        Assert.Equal("CloudPlaceholderCooldown", reopened.Reason);
        await store.FinalizeDeferredMetadataLeaseAsync(
            reopened.FileId,
            reopened.LeaseOwner,
            IndexingRuntime.CloudPlaceholderRetryExhaustedErrorCode);
        Assert.True(await ReadQueueDueAsync(database.Path, file.FullPath) > DateTimeOffset.UtcNow.UtcTicks);
    }

    [Fact]
    public async Task Rc87TerminalCloudRowsWithoutQueues_AreSeededAfterCooldownAcrossRestart()
    {
        await using var database = new TemporaryDatabase();
        var files = Enumerable.Range(0, 196)
            .Select(index => Candidate($@"C:\RC88\Legacy\placeholder-{index:D3}.SLDPRT"))
            .ToArray();
        await using (var original = new SqliteIndexStore(database.Path))
        {
            await original.InitializeAsync();
            await original.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
            await FinalizeCloudPlaceholdersAsync(original, files.Length);
        }

        await RemoveCloudCooldownQueuesAndAgeDeadLettersAsync(
            database.Path,
            DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(-1));

        await using var restarted = new SqliteIndexStore(database.Path);
        await restarted.InitializeAsync();
        var leases = await restarted.LeaseMetadataAsync(100, TimeSpan.FromSeconds(5), "rc88-legacy-restart");

        Assert.Equal(100, leases.Count);
        Assert.All(leases, lease => Assert.Equal("CloudPlaceholderCooldown", lease.Reason));
        Assert.Equal(100, (await restarted.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task LegacyCloudCooldownRecovery_UsesCompleteExtractorIndexPrefixAgainstLargeCache()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await SeedLargeCachedLegacyCloudSetAsync(database.Path);

        var plan = await ReadLegacyCooldownRecoveryPlanAsync(database.Path);
        Assert.Contains(
            "SEARCH f USING INDEX ix_files_extractor (state=? AND extractor_version=? AND metadata_state=?)",
            plan);

        var leases = await store.LeaseMetadataAsync(100, TimeSpan.FromSeconds(5), "rc88-large-cache");
        Assert.Equal(2, leases.Count);
        Assert.All(leases, lease => Assert.Equal("CloudPlaceholderCooldown", lease.Reason));
    }

    [Fact]
    public async Task CloudPlaceholder_ReopensImmediatelyForChangedEvidenceOrHydrationIdentity()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var changed = Candidate(@"C:\RC88\Cloud\changed.SLDPRT");
        var hydrated = Candidate(@"C:\RC88\Cloud\hydrated-same-size-mtime.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([changed, hydrated]), CancellationToken.None);
        await FinalizeCloudPlaceholdersAsync(store, 2);

        var changedEvidence = changed with { Size = changed.Size + 1 };
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([changedEvidence, hydrated]), CancellationToken.None);
        var changedLease = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc88-changed"));
        Assert.Equal(changed.FullPath, changedLease.FullPath);
        Assert.Equal("Modified", changedLease.Reason);
        await store.FinalizeDeferredMetadataLeaseAsync(
            changedLease.FileId,
            changedLease.LeaseOwner,
            IndexingRuntime.CloudPlaceholderRetryExhaustedErrorCode);

        var hydratedEntry = new DiscoveredFile(
            hydrated,
            hydrated.FullPath.ToUpperInvariant(),
            @"C:\",
            new FileSystemIdentity(0x12345678, 0x1234));
        await store.ApplyDiscoverySnapshotAsync(
            new SnapshotProvider([hydratedEntry]),
            "rc88-hydration",
            CancellationToken.None);
        var hydratedLease = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc88-hydrated"));
        Assert.Equal(hydrated.FullPath, hydratedLease.FullPath);
        Assert.Equal("DiscoveryRetry", hydratedLease.Reason);
    }

    [Fact]
    public async Task FileNotFoundReappearanceAndInteractiveRead_StillBypassCloudCooldown()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var missing = Candidate(@"C:\RC88\missing-then-returned.SLDASM", SolidWorksDocumentKind.Assembly);
        var cloud = Candidate(@"C:\RC88\Cloud\user-opened.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([missing, cloud]), CancellationToken.None);

        await FinalizeFileNotFoundAsync(store, database.Path, missing.FullPath);
        await FinalizeCloudPlaceholdersAsync(store, 1);

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([missing, cloud]), CancellationToken.None);
        var reappeared = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc88-reappeared"));
        Assert.Equal(missing.FullPath, reappeared.FullPath);
        await store.CompleteMetadataLeaseAsync(reappeared.FileId, reappeared.LeaseOwner, succeeded: true);

        var token = await store.BeginInteractiveMetadataAttemptAsync(
            cloud.FullPath,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.NotNull(token.FileId);
        Assert.Equal((0, 1), await ReadCloudTerminalStateAsync(database.Path, cloud.FullPath));
        await store.EndInteractiveMetadataAttemptAsync(token, CancellationToken.None);
    }

    [Fact]
    public async Task SuccessfulMetadataCommit_ClearsCloudCooldownQueueAndDeadLetter()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC88\Cloud\recovered-and-committed.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await FinalizeCloudPlaceholdersAsync(store, 1);

        await store.UpsertMetadataAsync(
            new DocumentMetadata(file, [], [], MetadataSource.NoApi, Modified),
            CancellationToken.None);

        Assert.Equal((0, 0), await ReadCloudTerminalStateAsync(database.Path, file.FullPath));
        Assert.Equal(0, (await store.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task FutureCloudCooldown_DoesNotBlockFirstSettlementOrLeaseBeforeDue()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC88\Cloud\future-startup.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await FinalizeCloudPlaceholdersAsync(store, 1);
        var dueBeforeStart = await ReadQueueDueAsync(database.Path, file.FullPath);
        await SetSourceStateAsync(database.Path, "stale");

        var discovery = new FirstPassObservingDiscovery();
        var metadata = new CountingMetadata();
        await using var runtime = new IndexingRuntime(
            store,
            discovery,
            metadata,
            IndexingRuntimeOptions.Default with
            {
                DiscoveryRefreshInterval = TimeSpan.FromHours(1),
                IdleDelay = TimeSpan.FromMilliseconds(50),
                MaximumConcurrentExtractions = 1,
                MaximumFilesPerSecond = 1_000,
                MaximumReadBytesPerSecond = 100_000_000,
                MaximumWriteBytesPerSecond = 100_000_000,
            },
            new RuntimeFileLog(Path.Combine(Path.GetTempPath(), $"swsearch-rc88-{Guid.NewGuid():N}")));
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(75);
        Assert.Equal(0, metadata.ExtractionCalls);
        Assert.Equal(dueBeforeStart, await ReadQueueDueAsync(database.Path, file.FullPath));
        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "rc88-future-contender"));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static FileCandidate Candidate(
        string fullPath,
        SolidWorksDocumentKind kind = SolidWorksDocumentKind.Part) =>
        new(fullPath, kind, 123, Modified);

    private static async Task FinalizeCloudPlaceholdersAsync(SqliteIndexStore store, int expectedCount)
    {
        var finalized = 0;
        while (finalized < expectedCount)
        {
            var leases = await store.LeaseMetadataAsync(
                Math.Min(100, expectedCount - finalized),
                TimeSpan.FromSeconds(5),
                $"rc88-cloud-terminal-{Guid.NewGuid():N}");
            Assert.NotEmpty(leases);
            foreach (var lease in leases)
            {
                await store.FinalizeDeferredMetadataLeaseAsync(
                    lease.FileId,
                    lease.LeaseOwner,
                    IndexingRuntime.CloudPlaceholderRetryExhaustedErrorCode);
            }

            finalized += leases.Count;
        }
    }

    private static async Task FinalizeFileNotFoundAsync(
        SqliteIndexStore store,
        string databasePath,
        string fullPath)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lease = Assert.IsType<MetadataWorkLease>(await store.TryLeaseMetadataPathAsync(
                fullPath,
                TimeSpan.FromSeconds(5),
                $"rc88-missing-{attempt}"));
            await store.CompleteMetadataLeaseAsync(
                lease.FileId,
                lease.LeaseOwner,
                succeeded: false,
                errorCode: "FILE_NOT_FOUND",
                retryDelay: TimeSpan.Zero);
            if (attempt == 0)
            {
                await MakeQueueDueAsync(databasePath, fullPath);
            }
        }
    }

    private static async Task MakeQueueDueAsync(string databasePath, string fullPath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE index_queue SET due_utc=0
             WHERE lease_owner IS NULL
               AND file_id=(SELECT id FROM files WHERE path_norm=@path);
            """;
        command.Parameters.AddWithValue("@path", fullPath.ToUpperInvariant());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AgeCloudTerminalAsync(string databasePath, DateTimeOffset failedAtUtc)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dead_letter SET failed_utc=@failed
             WHERE error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED';
            UPDATE index_queue SET due_utc=0
             WHERE reason='CloudPlaceholderCooldown';
            """;
        command.Parameters.AddWithValue("@failed", failedAtUtc.UtcTicks);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private static async Task RemoveCloudCooldownQueuesAndAgeDeadLettersAsync(
        string databasePath,
        DateTimeOffset failedAtUtc)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM index_queue WHERE reason='CloudPlaceholderCooldown';
            UPDATE dead_letter SET failed_utc=@failed
             WHERE error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED';
            """;
        command.Parameters.AddWithValue("@failed", failedAtUtc.UtcTicks);
        Assert.Equal(392, await command.ExecuteNonQueryAsync());
    }

    private static async Task<long> ReadQueueDueAsync(string databasePath, string fullPath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT due_utc FROM index_queue
             WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);
            """;
        command.Parameters.AddWithValue("@path", fullPath.ToUpperInvariant());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SeedLargeCachedLegacyCloudSetAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE n(value) AS (
                VALUES(1)
                UNION ALL SELECT value+1 FROM n WHERE value<82000)
            INSERT INTO files(
                path,path_norm,kind,size,modified_utc,discovery_generation,state,metadata_state,extractor_version,error_code)
            SELECT printf('C:\RC88\Cache\cached-%05d.SLDPRT',value),
                   printf('C:\RC88\CACHE\CACHED-%05d.SLDPRT',value),
                   0,123,0,1,0,2,@extractor,NULL FROM n;
            INSERT INTO files(
                path,path_norm,kind,size,modified_utc,discovery_generation,state,metadata_state,extractor_version,error_code)
            VALUES
                ('C:\RC88\Legacy\one.SLDPRT','C:\RC88\LEGACY\ONE.SLDPRT',0,123,0,1,0,3,@extractor,'CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'),
                ('C:\RC88\Legacy\two.SLDPRT','C:\RC88\LEGACY\TWO.SLDPRT',0,123,0,1,0,3,@extractor,'CLOUD_PLACEHOLDER_RETRY_EXHAUSTED');
            INSERT INTO dead_letter(file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
            SELECT id,'legacy',@extractor,0,'CLOUD_PLACEHOLDER_RETRY_EXHAUSTED',@failed
              FROM files WHERE error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED';
            """;
        command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        command.Parameters.AddWithValue("@failed", DateTimeOffset.UtcNow.AddHours(-2).UtcTicks);
        Assert.Equal(82004, await command.ExecuteNonQueryAsync());
    }

    private static async Task<string> ReadLegacyCooldownRecoveryPlanAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT f.id
              FROM files f INDEXED BY ix_files_extractor
              JOIN dead_letter d ON d.file_id=f.id
             WHERE f.state=0 AND f.extractor_version=@extractor AND f.kind IN (0,1)
               AND f.metadata_state=3
               AND f.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
               AND d.error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
               AND d.extractor_version=@extractor
               AND NOT EXISTS(SELECT 1 FROM index_queue q WHERE q.file_id=f.id)
             LIMIT 100;
            """;
        command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, details);
    }

    private static async Task SetSourceStateAsync(string databasePath, string state)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE index_state SET source_state=@state WHERE singleton_id=1;";
        command.Parameters.AddWithValue("@state", state);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(int DeadLetters, int MetadataState)> ReadCloudTerminalStateAsync(
        string databasePath,
        string fullPath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM dead_letter d WHERE d.file_id=f.id),f.metadata_state
              FROM files f WHERE f.path_norm=@path;
            """;
        command.Parameters.AddWithValue("@path", fullPath.ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class SnapshotProvider(IReadOnlyList<DiscoveredFile> entries) : IIncrementalFileDiscoveryProvider
    {
        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(1);

        public DiscoveryScanReport LastReport { get; } = new(
            Modified,
            Modified,
            IsCompleteForDeletion: true,
            [new DiscoveryRootReport(
                @"C:\",
                DiscoveryRootKind.ExplicitLocal,
                WasOnline: true,
                FullyEnumerated: true,
                VisitedEntries: entries.Count,
                SolidWorksFiles: entries.Count,
                ReparsePointsSkipped: 0,
                OfflineFilesSkipped: 0,
                new Dictionary<string, int>())]);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "RC88", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry.Candidate;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DiscoveredFile?>(entries.FirstOrDefault(entry =>
                string.Equals(entry.Candidate.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed class FirstPassObservingDiscovery : IFileDiscoveryProvider
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "RC88_DISCOVERY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class CountingMetadata : IMetadataExtractionClient
    {
        internal int ExtractionCalls => Volatile.Read(ref _extractionCalls);
        private int _extractionCalls;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "RC88_METADATA", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _extractionCalls);
            throw new InvalidOperationException("A future cooldown task must not be extracted.");
        }
    }
}
