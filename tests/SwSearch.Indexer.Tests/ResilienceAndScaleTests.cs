using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class ResilienceAndScaleTests
{
    [Fact]
    public async Task WalPressure_WithLongReaderPausesExplicitly_ThenRecoversWithoutDataLoss()
    {
        await using var database = new TemporaryDatabase();
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: 64 * 1024 * 1024,
            MaximumDatabaseBytes: 128 * 1024 * 1024,
            PassiveCheckpointWalBytes: 4 * 1024,
            PauseWalBytes: 32 * 1024);
        await using var store = new SqliteIndexStore(database.Path, policy);
        await store.InitializeAsync();
        var seed = new FileCandidate(@"C:\Wal\Seed.SLDPRT", SolidWorksDocumentKind.Part, 1, DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([seed]), CancellationToken.None);

        await using var readerConnection = new SqliteConnection($"Data Source={database.Path};Cache=Private");
        await readerConnection.OpenAsync();
        await using var pin = readerConnection.CreateCommand();
        pin.CommandText = "SELECT id,path FROM files ORDER BY id;";
        await using var pinnedReader = await pin.ExecuteReaderAsync();
        Assert.True(await pinnedReader.ReadAsync());

        var files = Enumerable.Range(0, 500)
            .Select(index => new FileCandidate(
                $@"C:\Wal\Part-{index:D4}.SLDPRT",
                SolidWorksDocumentKind.Part,
                1_024 + index,
                DateTimeOffset.UtcNow))
            .Prepend(seed)
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        var pressured = await store.GetStorageStatusAsync();
        Assert.True(pressured.MetadataWritesPaused, $"wal={pressured.WalBytes}");
        Assert.Equal("WAL_PRESSURE", pressured.PauseReason);
        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(1), "blocked"));

        await pinnedReader.DisposeAsync();
        await readerConnection.DisposeAsync();
        var recovered = await store.MaintainStorageAsync();
        Console.WriteLine(
            $"WAL_RECOVERY pressured_bytes={pressured.WalBytes} paused={pressured.MetadataWritesPaused} " +
            $"recovered_bytes={recovered.WalBytes} recovered_paused={recovered.MetadataWritesPaused}");
        Assert.False(recovered.MetadataWritesPaused, $"wal={recovered.WalBytes}");
        Assert.NotEmpty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(1), "recovered"));
        Assert.Equal(files.Length, (await store.GetStatusAsync()).PresentFileCount);
    }

    [Fact]
    public async Task ConcurrentStores_NeverLeaseSameFile()
    {
        await using var database = new TemporaryDatabase();
        await using var first = new SqliteIndexStore(database.Path);
        await using var second = new SqliteIndexStore(database.Path);
        await first.InitializeAsync();
        await second.InitializeAsync();
        var files = Enumerable.Range(0, 8)
            .Select(index => new FileCandidate(
                $@"C:\Lease\Part-{index}.SLDPRT",
                SolidWorksDocumentKind.Part,
                index + 1,
                DateTimeOffset.UtcNow))
            .ToArray();
        await first.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        var leases = await Task.WhenAll(
            first.LeaseMetadataAsync(4, TimeSpan.FromSeconds(10), "process-a").AsTask(),
            second.LeaseMetadataAsync(4, TimeSpan.FromSeconds(10), "process-b").AsTask());
        var all = leases.SelectMany(static batch => batch).ToArray();

        Assert.Equal(all.Length, all.Select(static lease => lease.FileId).Distinct().Count());
        Assert.Equal(8, all.Length);
    }

    [Fact]
    public async Task BundledSqlite_IsPatchedAndDoesNotUseMachineInstallation()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={database.Path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = Version.Parse(Convert.ToString(await command.ExecuteScalarAsync())!);
        Assert.True(version >= new Version(3, 49, 1), $"bundled SQLite was {version}");
    }

    [Fact]
    public async Task SchemaV1_MigratesInPlaceAndPreservesSearchableMetadata()
    {
        await using var database = new TemporaryDatabase();
        await CreateV1DatabaseAsync(database.Path);

        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        var hit = Assert.Single(await store.SearchAsync(
            new SearchQuery("LEGACY-0404", SearchMode.Property),
            CancellationToken.None));
        Assert.Equal(@"C:\Legacy\Part.SLDPRT", hit.FullPath);
        await using var connection = new SqliteConnection($"Data Source={database.Path}");
        await connection.OpenAsync();
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(4L, Convert.ToInt64(await version.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Queue_OrdersP0ThroughP5_AndDeadLettersSameFingerprintAfterTwoFailures()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var files = Enumerable.Range(0, 6)
            .Select(index => new FileCandidate(
                $@"C:\Queue\P{index}.SLDPRT",
                SolidWorksDocumentKind.Part,
                100 + index,
                now))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        foreach (var lease in await store.LeaseMetadataAsync(6, TimeSpan.FromSeconds(1), "clear"))
        {
            await store.CompleteMetadataLeaseAsync(lease.FileId, lease.LeaseOwner, succeeded: true);
        }

        foreach (var priority in Enum.GetValues<MetadataQueuePriority>())
        {
            await store.EnqueueMetadataAsync(
                files[(int)priority].FullPath,
                priority.ToString(),
                priority);
        }

        var ordered = await store.LeaseMetadataAsync(6, TimeSpan.FromSeconds(1), "priority");
        Assert.Equal(Enum.GetValues<MetadataQueuePriority>(), ordered.Select(static item => item.Priority));

        var first = ordered[0];
        await store.CompleteMetadataLeaseAsync(first.FileId, first.LeaseOwner, false, "BROKEN", TimeSpan.Zero);
        var retry = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(1), "retry"));
        Assert.Equal(first.FileId, retry.FileId);
        await store.CompleteMetadataLeaseAsync(retry.FileId, retry.LeaseOwner, false, "BROKEN", TimeSpan.Zero);
        await store.EnqueueMetadataAsync(
            files[0].FullPath,
            "PropertyViewer",
            MetadataQueuePriority.PropertyViewer);

        await using var connection = new SqliteConnection($"Data Source={database.Path}");
        await connection.OpenAsync();
        await using var counts = connection.CreateCommand();
        counts.CommandText = "SELECT (SELECT COUNT(*) FROM dead_letter),(SELECT COUNT(*) FROM index_queue WHERE file_id=@id);";
        counts.Parameters.AddWithValue("@id", first.FileId);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
    }

    [Fact]
    public async Task IncompleteSnapshot_NeverDeletesAndSuccessfulRootsPersist()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var one = new FileCandidate(@"C:\Safe\One.SLDPRT", SolidWorksDocumentKind.Part, 1, now);
        var two = new FileCandidate(@"C:\Safe\Two.SLDPRT", SolidWorksDocumentKind.Part, 2, now);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([one, two]), CancellationToken.None);
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([two]),
            new DiscoverySnapshotOptions("partial", IsComplete: false, OnlineRoots: [@"C:\"]),
            CancellationToken.None);

        Assert.Equal(2, (await store.GetStatusAsync()).PresentFileCount);
        Assert.Contains(@"C:\", await store.GetIndexedRootsAsync(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Batch_PreservesNoApiDisplayCacheAndLinkState_WithCompactFtsRows()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\Properties\Fixture.SLDPRT",
            SolidWorksDocumentKind.Part,
            42,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        var properties = new List<DocumentProperty>();
        for (var configuration = 0; configuration < 10; configuration++)
        {
            for (var property = 0; property < 20; property++)
            {
                properties.Add(new DocumentProperty(
                    $"Field-{property}",
                    $"RAW-{configuration}-{property}",
                    $"VALUE-{configuration}-{property}",
                    PropertyScope.Configuration,
                    $"配置-{configuration}",
                    MetadataSource.NoApi,
                    DisplayValue: $"DISPLAY-{configuration}-{property}",
                    CachedValue: $"CACHE-{configuration}-{property}",
                    IsLinked: property % 2 == 0));
            }
        }

        await store.UpsertMetadataBatchAsync(
            [new DocumentMetadata(file, properties, [], MetadataSource.NoApi, DateTimeOffset.UtcNow,
                ConfigurationNames: Enumerable.Range(0, 10).Select(static value => $"配置-{value}").ToArray())],
            CancellationToken.None);
        var cached = await store.TryGetMetadataAsync(file.FullPath, CancellationToken.None);
        Assert.NotNull(cached);
        var linked = Assert.Single(cached.Properties, static property => property.RawValue == "RAW-4-8");
        Assert.Equal("DISPLAY-4-8", linked.DisplayValue);
        Assert.Equal("CACHE-4-8", linked.CachedValue);
        Assert.True(linked.IsLinked);

        await using var connection = new SqliteConnection($"Data Source={database.Path}");
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM search_documents WHERE file_id=(SELECT id FROM files WHERE path_norm=@path);";
        count.Parameters.AddWithValue("@path", IndexNormalizationForTest(file.FullPath));
        Assert.Equal(11L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        Assert.Single(await store.SearchAsync(
            new SearchQuery("VALUE-4-8", SearchMode.Property),
            CancellationToken.None));
    }

    [Fact]
    public async Task Synthetic150k_StreamsInBatches_SearchesQuickly_AndStaysBelowStorageLimits()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var clock = Stopwatch.StartNew();
        await store.ApplyDiscoverySnapshotAsync(
            SyntheticFiles(150_000),
            new DiscoverySnapshotOptions("scale", IsComplete: true, OnlineRoots: [@"C:\"], CommitBatchSize: 5_000),
            CancellationToken.None);
        clock.Stop();

        Assert.Equal(150_000, (await store.GetStatusAsync()).PresentFileCount);
        await using (var auditConnection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await auditConnection.OpenAsync();
            await using var audit = auditConnection.CreateCommand();
            audit.CommandText = """
                CREATE TABLE refresh_write_audit(target TEXT NOT NULL);
                CREATE TRIGGER audit_files_update AFTER UPDATE ON files BEGIN
                    INSERT INTO refresh_write_audit VALUES('files'); END;
                CREATE TRIGGER audit_files_insert AFTER INSERT ON files BEGIN
                    INSERT INTO refresh_write_audit VALUES('files'); END;
                CREATE TRIGGER audit_files_delete AFTER DELETE ON files BEGIN
                    INSERT INTO refresh_write_audit VALUES('files'); END;
                CREATE TRIGGER audit_documents_update AFTER UPDATE ON search_documents BEGIN
                    INSERT INTO refresh_write_audit VALUES('search_documents'); END;
                CREATE TRIGGER audit_documents_insert AFTER INSERT ON search_documents BEGIN
                    INSERT INTO refresh_write_audit VALUES('search_documents'); END;
                CREATE TRIGGER audit_documents_delete AFTER DELETE ON search_documents BEGIN
                    INSERT INTO refresh_write_audit VALUES('search_documents'); END;
                """;
            await audit.ExecuteNonQueryAsync();
        }

        var unchangedClock = Stopwatch.StartNew();
        await store.ApplyDiscoverySnapshotAsync(
            SyntheticFiles(150_000),
            new DiscoverySnapshotOptions("scale-repeat", IsComplete: true, OnlineRoots: [@"C:\"], CommitBatchSize: 5_000),
            CancellationToken.None);
        unchangedClock.Stop();
        var afterRefreshStorage = await store.GetStorageStatusAsync();
        await using (var auditConnection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await auditConnection.OpenAsync();
            await using var countWrites = auditConnection.CreateCommand();
            countWrites.CommandText = "SELECT COUNT(*) FROM refresh_write_audit;";
            Assert.Equal(0L, Convert.ToInt64(await countWrites.ExecuteScalarAsync()));
            await using var cleanup = auditConnection.CreateCommand();
            cleanup.CommandText = """
                DROP TRIGGER audit_files_update; DROP TRIGGER audit_files_insert; DROP TRIGGER audit_files_delete;
                DROP TRIGGER audit_documents_update; DROP TRIGGER audit_documents_insert; DROP TRIGGER audit_documents_delete;
                DROP TABLE refresh_write_audit;
                """;
            await cleanup.ExecuteNonQueryAsync();
        }

        var timings = new List<double>();
        for (var index = 0; index < 20; index++)
        {
            var searchClock = Stopwatch.StartNew();
            var hits = await store.SearchAsync(
                new SearchQuery($"Part-{149_000 + index:D6}", SearchMode.FileName),
                CancellationToken.None);
            searchClock.Stop();
            Assert.Single(hits);
            timings.Add(searchClock.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var storage = await store.MaintainStorageAsync();
        Console.WriteLine(
            $"SCALE150K discovery_ms={clock.Elapsed.TotalMilliseconds:F1} search_p50_ms={timings[9]:F1} " +
            $"search_p95_ms={timings[18]:F1} unchanged_refresh_ms={unchangedClock.Elapsed.TotalMilliseconds:F1} " +
            $"unchanged_wal_bytes={afterRefreshStorage.WalBytes} db_bytes={storage.DatabaseBytes} wal_bytes={storage.WalBytes}");
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), $"discovery took {clock.Elapsed}");
        Assert.True(timings[18] < 500, $"search p95 was {timings[18]:F1} ms");
        Assert.True(unchangedClock.Elapsed < TimeSpan.FromSeconds(30), $"unchanged refresh took {unchangedClock.Elapsed}");
        Assert.True(afterRefreshStorage.WalBytes < 10L * 1024 * 1024, $"unchanged WAL was {afterRefreshStorage.WalBytes}");
        Assert.True(storage.DatabaseBytes < SqliteIndexStore.TargetDatabaseBytes);
        Assert.True(storage.WalBytes < SqliteIndexStore.PauseWalBytes);
    }

    private static async IAsyncEnumerable<FileCandidate> SyntheticFiles(int count)
    {
        var modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < count; index++)
        {
            yield return new FileCandidate(
                $@"C:\Synthetic\Part-{index:D6}.SLDPRT",
                SolidWorksDocumentKind.Part,
                1_024 + index,
                modified);
            if ((index & 4095) == 0)
            {
                await Task.Yield();
            }
        }
    }

    private static string IndexNormalizationForTest(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private static async Task CreateV1DatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE schema_info(version INTEGER PRIMARY KEY,created_utc INTEGER NOT NULL,app_version TEXT NOT NULL);
            INSERT INTO schema_info VALUES(1,0,'1.0');
            CREATE TABLE index_state(singleton_id INTEGER PRIMARY KEY,discovery_generation INTEGER NOT NULL DEFAULT 0,
                last_success_utc INTEGER,source_state TEXT NOT NULL DEFAULT 'ready',last_error_code TEXT);
            INSERT INTO index_state VALUES(1,1,0,'ready',NULL);
            CREATE TABLE files(id INTEGER PRIMARY KEY,path TEXT NOT NULL,path_norm TEXT NOT NULL UNIQUE COLLATE NOCASE,
                kind INTEGER NOT NULL,size INTEGER NOT NULL,modified_utc INTEGER NOT NULL,discovery_generation INTEGER NOT NULL,
                state INTEGER NOT NULL DEFAULT 0,metadata_state INTEGER NOT NULL DEFAULT 0,extracted_utc INTEGER,error_code TEXT);
            CREATE TABLE properties(id INTEGER PRIMARY KEY,file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                scope INTEGER NOT NULL,configuration_name TEXT,name TEXT NOT NULL,name_norm TEXT NOT NULL,raw_value TEXT NOT NULL,
                resolved_value TEXT NOT NULL,value_norm TEXT NOT NULL,source INTEGER NOT NULL);
            CREATE TABLE document_references(id INTEGER PRIMARY KEY,drawing_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                target_path TEXT NOT NULL,target_path_norm TEXT NOT NULL,target_file_id INTEGER,referenced_configuration TEXT,resolution_state INTEGER NOT NULL);
            CREATE TABLE effective_properties(drawing_file_id INTEGER NOT NULL,origin_file_id INTEGER NOT NULL,property_id INTEGER NOT NULL,
                name_norm TEXT NOT NULL,value_norm TEXT NOT NULL,rule_version INTEGER NOT NULL,
                PRIMARY KEY(drawing_file_id,origin_file_id,property_id));
            CREATE TABLE index_queue(file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,reason TEXT NOT NULL,due_utc INTEGER NOT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,lease_until_utc INTEGER,lease_owner TEXT,last_error TEXT);
            CREATE VIRTUAL TABLE search_fts USING fts5(file_id UNINDEXED,source_kind UNINDEXED,property_id UNINDEXED,
                file_name,path_norm,name_norm,value_norm,origin_path UNINDEXED,tokenize='trigram');
            INSERT INTO files VALUES(1,'C:\Legacy\Part.SLDPRT','C:\LEGACY\PART.SLDPRT',0,1,0,1,0,0,0,NULL);
            INSERT INTO properties VALUES(1,1,0,NULL,'物料编码','物料编码','LEGACY-0404','LEGACY-0404','LEGACY-0404',0);
            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
