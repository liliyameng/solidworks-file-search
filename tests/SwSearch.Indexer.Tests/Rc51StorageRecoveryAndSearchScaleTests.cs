using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc51StorageRecoveryAndSearchScaleTests
{
    [Fact]
    public async Task Rc51_V6LargeGramCache_RetiresAtomicallyAndUsesFreelistForLogicalAdmission()
    {
        await using var database = new TemporaryDatabase();
        await using (var create = new SqliteIndexStore(database.Path))
        {
            await create.InitializeAsync();
        }

        var fixture = await CreateV6GramFixtureAsync(database.Path, documentCount: 20_000, gramsPerDocument: 128);
        Assert.True(fixture.GramRows >= 2_500_000);
        Assert.True(fixture.PhysicalBytes > fixture.CoreLiveBytes);
        var logicalMaximum = fixture.CoreLiveBytes +
            Math.Max(1, (fixture.PhysicalBytes - fixture.CoreLiveBytes) / 2);
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: Math.Max(1, logicalMaximum / 2),
            MaximumDatabaseBytes: logicalMaximum,
            PassiveCheckpointWalBytes: 256L * 1024 * 1024,
            PauseWalBytes: 512L * 1024 * 1024);

        var migrateClock = Stopwatch.StartNew();
        IndexStorageStatus recovered;
        await using (var migrated = new SqliteIndexStore(database.Path, policy))
        {
            await migrated.InitializeAsync();
            migrateClock.Stop();
            recovered = await migrated.GetStorageStatusAsync();
            Assert.False(recovered.MetadataWritesPaused,
                $"reason={recovered.PauseReason} physical={recovered.DatabaseBytes} logical={recovered.LogicalDatabaseBytes}");
            Assert.True(recovered.DatabaseBytes >= logicalMaximum);
            Assert.True(recovered.LogicalDatabaseBytes < logicalMaximum);
            Assert.True(recovered.ReusableDatabaseBytes > 0);
            Assert.Equal(0, await ReadScalarAsync(database.Path, "SELECT count(*) FROM search_short_grams;"));
            Assert.Equal(7, await ReadScalarAsync(database.Path, "PRAGMA user_version;"));

            // The physical file is still above the injected logical ceiling,
            // but retired pages are reusable, so a real metadata payload must
            // be admitted and consume that headroom.
            var owner = new FileCandidate(
                @"C:\RC51\GramOwner.SLDPRT",
                SolidWorksDocumentKind.Part,
                1,
                new DateTimeOffset(1, TimeSpan.Zero));
            await migrated.UpsertMetadataAsync(new DocumentMetadata(
                owner,
                [new DocumentProperty(
                    "RC51",
                    "FREELIST-ADMITTED",
                    "FREELIST-ADMITTED",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow),
                CancellationToken.None);
            Assert.Equal(1, await ReadScalarAsync(
                database.Path,
                "SELECT count(*) FROM properties WHERE raw_value='FREELIST-ADMITTED';"));
            recovered = await migrated.GetStorageStatusAsync();
            Assert.False(recovered.MetadataWritesPaused);
        }

        var firstFreePages = recovered.FreePageCount;
        await using (var reopened = new SqliteIndexStore(database.Path, policy))
        {
            await reopened.InitializeAsync();
            var second = await reopened.GetStorageStatusAsync();
            Assert.Equal(0, await ReadScalarAsync(database.Path, "SELECT count(*) FROM search_short_grams;"));
            Assert.Equal(firstFreePages, second.FreePageCount);
        }

        var physicalSafetyPolicy = policy with
        {
            PhysicalSafetyDatabaseBytes = Math.Max(
                policy.MaximumDatabaseBytes + 1,
                recovered.DatabaseBytes - 1),
        };
        await using (var physicallyBlocked = new SqliteIndexStore(database.Path, physicalSafetyPolicy))
        {
            await physicallyBlocked.InitializeAsync();
            var status = await physicallyBlocked.GetStorageStatusAsync();
            Assert.True(status.MetadataWritesPaused);
            Assert.Equal("DATABASE_PHYSICAL_SAFETY_LIMIT", status.PauseReason);
        }

        Console.WriteLine(
            $"RC51_GRAM_RETIRE rows={fixture.GramRows} ms={migrateClock.Elapsed.TotalMilliseconds:F2} " +
            $"physical={recovered.DatabaseBytes} logical={recovered.LogicalDatabaseBytes} " +
            $"reusable={recovered.ReusableDatabaseBytes} wal={recovered.WalBytes}");
        Assert.True(migrateClock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.True(recovered.WalBytes < 256L * 1024 * 1024);
    }

    [Fact]
    public async Task Rc51_V6IncomingForeignKey_FailsClosedWithoutChangingVersionOrData()
    {
        await using var database = new TemporaryDatabase();
        await using (var create = new SqliteIndexStore(database.Path))
        {
            await create.InitializeAsync();
        }
        await ExecuteAsync(database.Path, """
            PRAGMA foreign_keys=OFF;
            INSERT INTO files(path,path_norm,kind,size,modified_utc,file_identity,
                              discovery_generation,state,metadata_state,extractor_version)
            VALUES('C:\RC51\Incoming.SLDPRT','C:\RC51\INCOMING.SLDPRT',0,1,1,NULL,0,0,0,3);
            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            VALUES(last_insert_rowid(),0,NULL,'INCOMING.SLDPRT','C:\RC51\INCOMING.SLDPRT','','',NULL);
            INSERT INTO search_short_grams(gram_len,gram,document_id,field_mask)
            SELECT 2,'IN',id,1 FROM search_documents WHERE file_name='INCOMING.SLDPRT';
            CREATE TABLE evil_gram_dependency(
                id INTEGER PRIMARY KEY,
                gram_len INTEGER NOT NULL,
                gram TEXT NOT NULL,
                document_id INTEGER NOT NULL,
                field_mask INTEGER NOT NULL,
                FOREIGN KEY(gram_len,gram,document_id,field_mask)
                    REFERENCES search_short_grams(gram_len,gram,document_id,field_mask));
            INSERT INTO evil_gram_dependency(gram_len,gram,document_id,field_mask)
            SELECT gram_len,gram,document_id,field_mask FROM search_short_grams;
            PRAGMA user_version=6;
            """);

        await using var migration = new SqliteIndexStore(database.Path);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migration.InitializeAsync().AsTask());
        Assert.Contains("evil_gram_dependency", failure.Message, StringComparison.Ordinal);
        Assert.Equal(6, await ReadScalarAsync(database.Path, "PRAGMA user_version;"));
        Assert.Equal(1, await ReadScalarAsync(database.Path, "SELECT count(*) FROM search_short_grams;"));
        Assert.Equal(1, await ReadScalarAsync(database.Path, "SELECT count(*) FROM evil_gram_dependency;"));
    }

    [Fact]
    public async Task Rc51_V6RetirementCancellation_RollsBackAndRetryCompletes()
    {
        await using var database = new TemporaryDatabase();
        await using (var create = new SqliteIndexStore(database.Path))
        {
            await create.InitializeAsync();
        }
        var fixture = await CreateV6GramFixtureAsync(database.Path, documentCount: 5_000, gramsPerDocument: 128);
        Assert.True(fixture.GramRows >= 640_000);

        await using (var cancelled = new SqliteIndexStore(database.Path))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelled.InitializeAsync(cancellation.Token).AsTask());
        }
        Assert.Equal(6, await ReadScalarAsync(database.Path, "PRAGMA user_version;"));
        Assert.Equal(fixture.GramRows, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM search_short_grams;"));

        await using var retry = new SqliteIndexStore(database.Path);
        await retry.InitializeAsync();
        Assert.Equal(7, await ReadScalarAsync(database.Path, "PRAGMA user_version;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path, "SELECT count(*) FROM search_short_grams;"));
    }

    [Fact]
    public async Task Rc51_V6PostDdlForeignKeyCheckFailure_RollsBackRetirementAndVersion()
    {
        await using var database = new TemporaryDatabase();
        await using (var create = new SqliteIndexStore(database.Path))
        {
            await create.InitializeAsync();
        }
        var fixture = await CreateV6GramFixtureAsync(
            database.Path,
            documentCount: 100,
            gramsPerDocument: 20);
        await ExecuteAsync(database.Path, """
            PRAGMA foreign_keys=OFF;
            INSERT INTO properties(
                file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                display_value,cached_value,is_linked,value_norm,source)
            VALUES(999999,0,NULL,'Broken','BROKEN','BROKEN','BROKEN',
                   'BROKEN','BROKEN',0,'BROKEN',4);
            """);

        await using var migration = new SqliteIndexStore(database.Path);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migration.InitializeAsync().AsTask());
        Assert.Contains("foreign key", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, await ReadScalarAsync(database.Path, "PRAGMA user_version;"));
        Assert.Equal(fixture.GramRows, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM search_short_grams;"));
        Assert.Equal(1, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM properties WHERE file_id=999999;"));
    }

    [Fact]
    public async Task Rc51_ShortLikeFallback_138kFiles236kDocuments_IsBoundedExactAndCancelable()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await CreateShortSearchFixtureAsync(database.Path, fileCount: 138_000, propertyDocumentCount: 98_000);

        var fileClock = Stopwatch.StartNew();
        var fileHits = await store.SearchAsync(
            new SearchQuery("0", SearchMode.FileName, Limit: 120),
            CancellationToken.None);
        fileClock.Stop();
        Assert.Equal(120, fileHits.Count);

        var countClock = Stopwatch.StartNew();
        var propertyCount = await store.CountSearchAsync(
            new SearchQuery("A", SearchMode.Property),
            CancellationToken.None);
        countClock.Stop();
        Assert.Equal(98_000, propertyCount);

        var propertyClock = Stopwatch.StartNew();
        var propertyHits = await store.SearchAsync(
            new SearchQuery("A", SearchMode.Property, Limit: 120),
            CancellationToken.None);
        propertyClock.Stop();
        Assert.Equal(120, propertyHits.Count);
        Assert.All(propertyHits, static hit =>
        {
            Assert.Equal("Code", hit.MatchedProperty);
            Assert.Equal("A", hit.MatchedValue);
        });

        await AssertCancellationIsPromptAsync(token => store.SearchAsync(
            new SearchQuery("A", SearchMode.Property, Limit: 120), token).AsTask());
        await AssertCancellationIsPromptAsync(token => store.CountSearchAsync(
            new SearchQuery("A", SearchMode.Property), token).AsTask());

        Console.WriteLine(
            $"RC51_SHORT_LIKE files=138000 docs=236000 file_ms={fileClock.Elapsed.TotalMilliseconds:F2} " +
            $"property_ms={propertyClock.Elapsed.TotalMilliseconds:F2} count_ms={countClock.Elapsed.TotalMilliseconds:F2}");
        Assert.True(fileClock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(propertyClock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(countClock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Rc51_TwoCharacterSearch_SearchAndCountShareExactPagingScopeLibraryAndKindSemantics()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await CreateShortSemanticFixtureAsync(database.Path);

        Assert.Equal(2, await store.CountSearchAsync(
            new SearchQuery("AB", SearchMode.FileName),
            CancellationToken.None));
        var fileMatches = await store.SearchAsync(
            new SearchQuery("AB", SearchMode.FileName, Limit: 20),
            CancellationToken.None);
        Assert.Equal(2, fileMatches.Count);
        Assert.Contains(fileMatches, hit => hit.FullPath == @"C:\LibA\Models\AB-Part.SLDPRT");
        Assert.Contains(fileMatches, hit => hit.FullPath == @"C:\LibA\FolderAB\Plain.SLDASM");
        Assert.All(fileMatches, hit =>
        {
            Assert.Null(hit.MatchedProperty);
            Assert.Null(hit.MatchedValue);
        });

        Assert.Equal(3, await store.CountSearchAsync(
            new SearchQuery("AB", SearchMode.Property),
            CancellationToken.None));
        var propertyMatches = await store.SearchAsync(
            new SearchQuery("AB", SearchMode.Property, Limit: 20),
            CancellationToken.None);
        Assert.Equal(3, propertyMatches.Count);
        var effective = Assert.Single(
            propertyMatches,
            hit => hit.FullPath == @"C:\LibA\Drawings\Sheet.SLDDRW");
        Assert.Equal("Code", effective.MatchedProperty);
        Assert.Equal("ABMODEL", effective.MatchedValue);
        Assert.Equal(@"C:\LibA\Models\AB-Part.SLDPRT", effective.OriginModelPath);

        var allCount = await store.CountSearchAsync(
            new SearchQuery("AB", SearchMode.All),
            CancellationToken.None);
        Assert.Equal(4, allCount);
        var pagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < allCount; offset += 2)
        {
            var page = await store.SearchAsync(
                new SearchQuery("AB", SearchMode.All, Offset: offset, Limit: 2),
                CancellationToken.None);
            Assert.Equal(Math.Min(2, (int)allCount - offset), page.Count);
            Assert.All(page, hit => Assert.True(pagedPaths.Add(hit.FullPath), hit.FullPath));
        }
        Assert.Equal(allCount, pagedPaths.Count);

        var libraryQuery = new SearchQuery(
            "AB",
            SearchMode.Library,
            Limit: 20,
            LibraryRoots: [@"C:\LibA"]);
        Assert.Equal(3, await store.CountSearchAsync(libraryQuery, CancellationToken.None));
        Assert.Equal(3, (await store.SearchAsync(libraryQuery, CancellationToken.None)).Count);

        var folderQuery = new SearchQuery(
            "AB",
            SearchMode.All,
            Limit: 20,
            Scope: SearchScope.Folder(@"C:\LibA\Drawings"));
        Assert.Equal(1, await store.CountSearchAsync(folderQuery, CancellationToken.None));
        Assert.Equal(
            @"C:\LibA\Drawings\Sheet.SLDDRW",
            Assert.Single(await store.SearchAsync(folderQuery, CancellationToken.None)).FullPath);

        var partQuery = new SearchQuery(
            "AB",
            SearchMode.All,
            Limit: 20,
            Kinds: [SolidWorksDocumentKind.Part]);
        Assert.Equal(2, await store.CountSearchAsync(partQuery, CancellationToken.None));
        Assert.All(
            await store.SearchAsync(partQuery, CancellationToken.None),
            hit => Assert.Equal(SolidWorksDocumentKind.Part, hit.Kind));
    }

    [Fact]
    public async Task Rc51_LogicalHardLimit_BlocksBatchLeaseAndInteractivePayloadsButAllowsControlWrites()
    {
        await using var database = new TemporaryDatabase();
        var modified = new DateTimeOffset(2026, 7, 26, 1, 2, 3, TimeSpan.Zero);
        var candidate = new FileCandidate(
            @"C:\RC51\Admission.SLDPRT",
            SolidWorksDocumentKind.Part,
            64,
            modified);
        MetadataWorkLease lease;
        IndexStorageStatus baseline;
        await using (var normal = new SqliteIndexStore(database.Path))
        {
            await normal.InitializeAsync();
            await normal.ApplyDiscoverySnapshotAsync(
                AsyncFiles.From([candidate]),
                CancellationToken.None);
            lease = Assert.Single(await normal.LeaseMetadataAsync(
                1,
                TimeSpan.FromMinutes(5),
                "rc51-admission"));
            baseline = await normal.GetStorageStatusAsync();
        }

        var hardMaximum = Math.Max(2, baseline.LogicalDatabaseBytes - 1);
        var blockedPolicy = new IndexStoragePolicy(
            TargetDatabaseBytes: Math.Max(1, hardMaximum / 2),
            MaximumDatabaseBytes: hardMaximum,
            PassiveCheckpointWalBytes: 256L * 1024 * 1024,
            PauseWalBytes: 512L * 1024 * 1024);
        await using var blocked = new SqliteIndexStore(database.Path, blockedPolicy);
        await blocked.InitializeAsync();
        var blockedStatus = await blocked.GetStorageStatusAsync();
        Assert.True(blockedStatus.MetadataWritesPaused);
        Assert.Equal("DATABASE_HARD_LIMIT", blockedStatus.PauseReason);
        Assert.True(blockedStatus.LogicalDatabaseBytes >= hardMaximum);
        Assert.True(blockedStatus.DatabaseBytes < blockedPolicy.PhysicalSafetyDatabaseBytes);

        var metadata = new DocumentMetadata(
            candidate,
            [new DocumentProperty(
                "Code",
                "BLOCKED",
                "BLOCKED",
                PropertyScope.Document,
                null,
                MetadataSource.NoApi)],
            [],
            MetadataSource.NoApi,
            modified.AddMinutes(1));
        var evidence = new StableFileEvidence(
            candidate.FullPath,
            candidate.FullPath,
            candidate.Kind,
            candidate.Size,
            candidate.LastWriteTimeUtc,
            FileIdentity: string.Empty);

        var batchFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blocked.UpsertMetadataAsync(metadata, CancellationToken.None).AsTask());
        Assert.Equal(
            "METADATA_WRITES_PAUSED:DATABASE_HARD_LIMIT",
            batchFailure.Message);

        var leaseFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blocked.TryCommitMetadataLeaseBatchAsync(
                [new MetadataCommitItem(lease, metadata, evidence)],
                CancellationToken.None).AsTask());
        Assert.Equal(
            "METADATA_WRITES_PAUSED:DATABASE_HARD_LIMIT",
            leaseFailure.Message);

        // Attempt lifecycle rows are small control-plane writes and remain
        // available so callers can establish/clean up ownership while payloads
        // are paused.
        var attempt = await blocked.BeginInteractiveMetadataAttemptAsync(
            candidate.FullPath,
            Guid.NewGuid());
        var interactiveFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blocked.TryCommitInteractiveMetadataAsync(
                metadata,
                evidence,
                evidence,
                attempt,
                CancellationToken.None).AsTask());
        Assert.Equal(
            "METADATA_WRITES_PAUSED:DATABASE_HARD_LIMIT",
            interactiveFailure.Message);
        Assert.Equal(1, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM interactive_metadata_attempts;"));
        Assert.Equal(0, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM properties;"));
        await blocked.EndInteractiveMetadataAttemptAsync(attempt);
        Assert.Equal(0, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM interactive_metadata_attempts;"));
    }

    [Fact]
    public async Task Rc51_IdentityHint_RenamesOneFileAmong100kIndexedIdentitiesWithoutScanning()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await CreateIdentityFixtureAsync(database.Path, 100_000, 0xAABBCCDD);

        const ulong targetIndex = 50_000;
        var renamed = new FileCandidate(
            @"C:\Identity\Renamed-050000.SLDPRT",
            SolidWorksDocumentKind.Part,
            51_030,
            new DateTimeOffset(2, TimeSpan.Zero));
        var clock = Stopwatch.StartNew();
        await store.ApplyDiscoveryHintAsync(
            new DiscoveredFile(
                renamed,
                DiscoveryPath.NormalizeKey(renamed.FullPath),
                @"C:\Identity",
                new FileSystemIdentity(0xAABBCCDD, targetIndex)),
            CancellationToken.None);
        clock.Stop();

        Assert.Equal(100_000, await ReadScalarAsync(database.Path, "SELECT count(*) FROM files;"));
        Assert.Equal(50_000, await ReadScalarAsync(
            database.Path,
            "SELECT id FROM files WHERE path_norm='C:\\IDENTITY\\RENAMED-050000.SLDPRT';"));
        Assert.Equal(0, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM files WHERE path_norm='C:\\IDENTITY\\PART-049999.SLDPRT';"));
        Assert.Equal(1, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM index_queue WHERE file_id=50000 AND reason='Modified';"));

        await using var connection = await OpenAsync(database.Path);
        await using var plan = connection.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT id FROM files
             WHERE file_identity='AABBCCDD:000000000000C350';
            """;
        var details = new List<string>();
        await using (var reader = await plan.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }
        }
        Assert.Contains(
            details,
            detail => detail.Contains("ix_files_identity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            details,
            detail => detail.Contains("SCAN files", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(
            $"RC51_IDENTITY_100K rename_ms={clock.Elapsed.TotalMilliseconds:F2} plan={string.Join(" | ", details)}");
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(2),
            $"Identity rename took {clock.Elapsed.TotalMilliseconds:F2} ms.");
    }

    [Fact]
    public async Task Rc51_HighFanoutProjection_ReleasesWriteGateForMetadataLease()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await CreateHighFanoutFixtureAsync(database.Path, drawingCount: 500);

        var model = new FileCandidate(
            @"C:\RC51\Fanout\Model.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            new DateTimeOffset(1, TimeSpan.Zero));
        var fanoutClock = Stopwatch.StartNew();
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                model,
                [new DocumentProperty(
                    "Code",
                    "NEW-FANOUT",
                    "NEW-FANOUT",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        fanoutClock.Stop();

        var queuedBeforeLease = await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM drawing_projection_queue;");
        Assert.True(queuedBeforeLease > 0);
        var leaseClock = Stopwatch.StartNew();
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMinutes(1),
            "rc51-fanout-gate"));
        leaseClock.Stop();

        Assert.Equal(@"C:\RC51\Fanout\LeaseTarget.SLDPRT", lease.FullPath);
        Assert.True(
            leaseClock.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Metadata lease waited {leaseClock.Elapsed.TotalMilliseconds:F2} ms.");
        Assert.True(
            fanoutClock.Elapsed < TimeSpan.FromSeconds(2),
            $"Set-based high-fanout commit took {fanoutClock.Elapsed.TotalMilliseconds:F2} ms.");
        Console.WriteLine(
            $"RC51_FANOUT drawings=500 commit_ms={fanoutClock.Elapsed.TotalMilliseconds:F2} " +
            $"lease_ms={leaseClock.Elapsed.TotalMilliseconds:F2} queued={queuedBeforeLease}");
    }

    [Fact]
    public async Task Rc51_MaintenanceKickStorm_CoalescesOneRestartAndDisposeFreezesWorkerOwnership()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        var storeType = typeof(SqliteIndexStore);
        var start = Assert.IsAssignableFrom<MethodInfo>(storeType.GetMethod(
            "StartStorageMaintenance",
            BindingFlags.Instance | BindingFlags.NonPublic));
        var taskField = Assert.IsAssignableFrom<FieldInfo>(storeType.GetField(
            "_storageMaintenanceTask",
            BindingFlags.Instance | BindingFlags.NonPublic));
        var pendingField = Assert.IsAssignableFrom<FieldInfo>(storeType.GetField(
            "_storageMaintenanceKickPending",
            BindingFlags.Instance | BindingFlags.NonPublic));
        var gateField = Assert.IsAssignableFrom<FieldInfo>(storeType.GetField(
            "_writeGate",
            BindingFlags.Instance | BindingFlags.NonPublic));
        var gate = Assert.IsType<SemaphoreSlim>(gateField.GetValue(store));
        await WaitUntilAsync(
            () => taskField.GetValue(store) is null,
            TimeSpan.FromSeconds(2));

        await ExecuteAsync(database.Path, """
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,file_identity,
                discovery_generation,state,metadata_state,extractor_version,extracted_utc)
            VALUES(1,'C:\RC51\Kick\D.SLDDRW','C:\RC51\KICK\D.SLDDRW',
                   2,1,1,NULL,0,0,0,3,1);
            INSERT INTO drawing_projection_queue(
                drawing_file_id,reason,queued_utc,attempt,next_due_utc,last_error)
            VALUES(1,'KickStorm',0,0,0,NULL);
            """);

        await gate.WaitAsync();
        Task firstGeneration;
        try
        {
            start.Invoke(store, null);
            firstGeneration = Assert.IsAssignableFrom<Task>(taskField.GetValue(store));
            Parallel.For(0, 10_000, _ => start.Invoke(store, null));
            Assert.Same(firstGeneration, taskField.GetValue(store));
            Assert.True(Assert.IsType<bool>(pendingField.GetValue(store)));
        }
        finally
        {
            gate.Release();
        }

        await WaitUntilAsync(
            () => taskField.GetValue(store) is null &&
                  !Assert.IsType<bool>(pendingField.GetValue(store)),
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, await ReadScalarAsync(
            database.Path,
            "SELECT count(*) FROM drawing_projection_queue;"));

        // Race one pending generation against owner shutdown. Dispose must
        // cancel/await the last observed worker and prevent a successor from
        // touching the disposed CTS or gate.
        await ExecuteAsync(database.Path, """
            INSERT INTO drawing_projection_queue(
                drawing_file_id,reason,queued_utc,attempt,next_due_utc,last_error)
            VALUES(1,'DisposeRace',0,0,0,NULL);
            """);
        await gate.WaitAsync();
        start.Invoke(store, null);
        start.Invoke(store, null);
        var dispose = store.DisposeAsync().AsTask();
        gate.Release();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(taskField.GetValue(store));
        Assert.False(Assert.IsType<bool>(pendingField.GetValue(store)));
    }

    private static async Task AssertCancellationIsPromptAsync(Func<CancellationToken, Task> operation)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        var clock = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(() => operation(cancellation.Token));
        clock.Stop();
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Cancellation took {clock.Elapsed.TotalMilliseconds:F2} ms.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(clock.Elapsed < timeout, $"Condition did not complete within {timeout}.");
            await Task.Delay(10);
        }
    }

    private static async Task<(long CoreLiveBytes, long PhysicalBytes, long GramRows)> CreateV6GramFixtureAsync(
        string path,
        int documentCount,
        int gramsPerDocument)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, """
            PRAGMA foreign_keys=OFF;
            PRAGMA synchronous=OFF;
            INSERT INTO files(path,path_norm,kind,size,modified_utc,file_identity,
                              discovery_generation,state,metadata_state,extractor_version)
            VALUES('C:\RC51\GramOwner.SLDPRT','C:\RC51\GRAMOWNER.SLDPRT',0,1,1,NULL,0,0,0,3);
            """);
        await using (var documents = connection.CreateCommand())
        {
            documents.CommandText = """
                WITH RECURSIVE n(value) AS (
                    SELECT 0 UNION ALL SELECT value+1 FROM n WHERE value+1<@documents)
                INSERT INTO search_documents(
                    file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
                SELECT (SELECT id FROM files WHERE path_norm='C:\RC51\GRAMOWNER.SLDPRT'),
                       1,printf('Config-%06d',value),'','','CODE','VALUE',NULL
                  FROM n;
                """;
            documents.Parameters.AddWithValue("@documents", documentCount);
            await documents.ExecuteNonQueryAsync();
        }
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        var coreLiveBytes = await ReadLiveBytesAsync(connection);

        await using (var grams = connection.CreateCommand())
        {
            grams.CommandText = """
                WITH RECURSIVE g(value) AS (
                    SELECT 0 UNION ALL SELECT value+1 FROM g WHERE value+1<@grams)
                INSERT INTO search_short_grams(gram_len,gram,document_id,field_mask)
                SELECT 2,printf('%02X',g.value),d.id,2
                  FROM search_documents d CROSS JOIN g
                 WHERE d.source_kind=1;
                """;
            grams.Parameters.AddWithValue("@grams", gramsPerDocument);
            await grams.ExecuteNonQueryAsync();
        }
        await ExecuteAsync(connection, "PRAGMA user_version=6; PRAGMA wal_checkpoint(TRUNCATE);");
        var gramRows = await ReadScalarAsync(connection, "SELECT count(*) FROM search_short_grams;");
        var physicalBytes = new FileInfo(path).Length;
        return (coreLiveBytes, physicalBytes, gramRows);
    }

    private static async Task CreateShortSearchFixtureAsync(
        string path,
        int fileCount,
        int propertyDocumentCount)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, "PRAGMA synchronous=OFF;");
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var files = connection.CreateCommand())
        {
            files.Transaction = (SqliteTransaction)transaction;
            files.CommandText = """
                WITH RECURSIVE n(value) AS (
                    SELECT 0 UNION ALL SELECT value+1 FROM n WHERE value+1<@files)
                INSERT INTO files(path,path_norm,kind,size,modified_utc,file_identity,
                                  discovery_generation,state,metadata_state,extractor_version,extracted_utc)
                SELECT printf('C:\RC51\Part-%06d.SLDPRT',value),
                       upper(printf('C:\RC51\Part-%06d.SLDPRT',value)),
                       0,1024+value,1,NULL,0,0,0,3,1
                  FROM n;
                """;
            files.Parameters.AddWithValue("@files", fileCount);
            await files.ExecuteNonQueryAsync();
        }
        await using (var documents = connection.CreateCommand())
        {
            documents.Transaction = (SqliteTransaction)transaction;
            documents.CommandText = """
                INSERT INTO search_documents(
                    file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
                SELECT id,0,NULL,upper(substr(path,length(rtrim(path,replace(path,'\','')))+1)),
                       path_norm,'','',NULL
                  FROM files;
                INSERT INTO properties(
                    file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                    display_value,cached_value,is_linked,value_norm,source)
                SELECT id,0,NULL,'Code','CODE','A','A','A','A',0,'A',4
                  FROM files WHERE id<=@propertyDocuments;
                INSERT INTO search_documents(
                    file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
                SELECT id,1,NULL,'','','CODE','A',NULL
                  FROM files WHERE id<=@propertyDocuments;
                INSERT INTO search_fts(
                    document_id,file_id,source_kind,configuration_name,file_name,path_norm,
                    names_text,values_text,origin_path)
                SELECT id,file_id,source_kind,configuration_name,file_name,path_norm,
                       names_text,values_text,origin_path
                  FROM search_documents;
                """;
            documents.Parameters.AddWithValue("@propertyDocuments", propertyDocumentCount);
            await documents.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task CreateShortSemanticFixtureAsync(string path)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, """
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,file_identity,
                discovery_generation,state,metadata_state,extractor_version,extracted_utc)
            VALUES
                (1,'C:\LibA\Models\AB-Part.SLDPRT','C:\LIBA\MODELS\AB-PART.SLDPRT',0,1,1,NULL,0,0,0,3,1),
                (2,'C:\LibA\FolderAB\Plain.SLDASM','C:\LIBA\FOLDERAB\PLAIN.SLDASM',1,1,1,NULL,0,0,0,3,1),
                (3,'C:\LibA\Drawings\Sheet.SLDDRW','C:\LIBA\DRAWINGS\SHEET.SLDDRW',2,1,1,NULL,0,0,0,3,1),
                (4,'C:\LibB\Parts\Other.SLDPRT','C:\LIBB\PARTS\OTHER.SLDPRT',0,1,1,NULL,0,0,0,3,1),
                (5,'C:\Outside\Nope.SLDPRT','C:\OUTSIDE\NOPE.SLDPRT',0,1,1,NULL,0,0,0,3,1),
                (6,'C:\LibA\DeletedAB.SLDPRT','C:\LIBA\DELETEDAB.SLDPRT',0,1,1,NULL,0,1,0,3,1);

            INSERT INTO properties(
                id,file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                display_value,cached_value,is_linked,value_norm,source)
            VALUES
                (101,1,0,NULL,'Code','CODE','ABMODEL','ABMODEL','ABMODEL','ABMODEL',0,'ABMODEL',4),
                (104,4,0,NULL,'LocalCode','LOCALCODE','AB-LOCAL','AB-LOCAL','AB-LOCAL','AB-LOCAL',0,'AB-LOCAL',4);

            INSERT INTO document_references(
                drawing_file_id,target_path,target_path_norm,target_file_name_norm,target_file_id,
                referenced_configuration,resolution_state)
            VALUES(
                3,'C:\LibA\Models\AB-Part.SLDPRT','C:\LIBA\MODELS\AB-PART.SLDPRT',
                'AB-PART.SLDPRT',1,NULL,0);
            INSERT INTO effective_properties(
                drawing_file_id,origin_file_id,property_id,name_norm,value_norm,rule_version)
            VALUES(3,1,101,'CODE','ABMODEL',1);

            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            VALUES
                (1,0,NULL,'AB-PART.SLDPRT','C:\LIBA\MODELS\AB-PART.SLDPRT','','',NULL),
                (2,0,NULL,'PLAIN.SLDASM','C:\LIBA\FOLDERAB\PLAIN.SLDASM','','',NULL),
                (3,0,NULL,'SHEET.SLDDRW','C:\LIBA\DRAWINGS\SHEET.SLDDRW','','',NULL),
                (4,0,NULL,'OTHER.SLDPRT','C:\LIBB\PARTS\OTHER.SLDPRT','','',NULL),
                (5,0,NULL,'NOPE.SLDPRT','C:\OUTSIDE\NOPE.SLDPRT','','',NULL),
                (6,0,NULL,'DELETEDAB.SLDPRT','C:\LIBA\DELETEDAB.SLDPRT','','',NULL),
                (1,1,NULL,'','','CODE','ABMODEL',NULL),
                (4,1,NULL,'','','LOCALCODE','AB-LOCAL',NULL),
                (3,2,NULL,'','','CODE','ABMODEL','C:\LibA\Models\AB-Part.SLDPRT');
            INSERT INTO search_fts(
                document_id,file_id,source_kind,configuration_name,file_name,path_norm,
                names_text,values_text,origin_path)
            SELECT id,file_id,source_kind,configuration_name,file_name,path_norm,
                   names_text,values_text,origin_path
              FROM search_documents;
            """);
    }

    private static async Task CreateIdentityFixtureAsync(
        string path,
        int fileCount,
        uint volumeSerialNumber)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, "PRAGMA synchronous=OFF;");
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            WITH RECURSIVE n(value) AS (
                SELECT 0 UNION ALL SELECT value+1 FROM n WHERE value+1<@files)
            INSERT INTO files(
                path,path_norm,kind,size,modified_utc,file_identity,
                discovery_generation,state,metadata_state,extractor_version,extracted_utc)
            SELECT printf('C:\Identity\Part-%06d.SLDPRT',value),
                   upper(printf('C:\Identity\Part-%06d.SLDPRT',value)),
                   0,1024+value,1,printf('%08X:%016X',@volume,value+1),
                   0,0,0,3,1
              FROM n;
            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT id,0,NULL,upper(substr(path,length(rtrim(path,replace(path,'\','')))+1)),
                   path_norm,'','',NULL
              FROM files;
            """;
        insert.Parameters.AddWithValue("@files", fileCount);
        insert.Parameters.AddWithValue("@volume", (long)volumeSerialNumber);
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task CreateHighFanoutFixtureAsync(string path, int drawingCount)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, "PRAGMA synchronous=OFF;");
        await using var transaction = await connection.BeginTransactionAsync();
        await using var seed = connection.CreateCommand();
        seed.Transaction = (SqliteTransaction)transaction;
        seed.CommandText = """
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,file_identity,
                discovery_generation,state,metadata_state,extractor_version,extracted_utc)
            VALUES(
                1,'C:\RC51\Fanout\Model.SLDPRT','C:\RC51\FANOUT\MODEL.SLDPRT',
                0,1,1,NULL,0,0,0,3,1);
            WITH RECURSIVE n(value) AS (
                SELECT 0 UNION ALL SELECT value+1 FROM n WHERE value+1<@drawings)
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,file_identity,
                discovery_generation,state,metadata_state,extractor_version,extracted_utc)
            SELECT value+2,
                   printf('C:\RC51\Fanout\Drawing-%04d.SLDDRW',value),
                   upper(printf('C:\RC51\Fanout\Drawing-%04d.SLDDRW',value)),
                   2,1,1,NULL,0,0,0,3,1
              FROM n;
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,file_identity,
                discovery_generation,state,metadata_state,extractor_version)
            VALUES(
                10000,'C:\RC51\Fanout\LeaseTarget.SLDPRT',
                'C:\RC51\FANOUT\LEASETARGET.SLDPRT',0,64,1,NULL,0,0,1,0);

            INSERT INTO properties(
                id,file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                display_value,cached_value,is_linked,value_norm,source)
            VALUES(1,1,0,NULL,'Code','CODE','OLD-FANOUT','OLD-FANOUT',
                   'OLD-FANOUT','OLD-FANOUT',0,'OLD-FANOUT',4);
            INSERT INTO document_references(
                drawing_file_id,target_path,target_path_norm,target_file_name_norm,target_file_id,
                referenced_configuration,resolution_state)
            SELECT id,'C:\RC51\Fanout\Model.SLDPRT','C:\RC51\FANOUT\MODEL.SLDPRT',
                   'MODEL.SLDPRT',1,NULL,0
              FROM files WHERE kind=2;

            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT id,0,NULL,upper(substr(path,length(rtrim(path,replace(path,'\','')))+1)),
                   path_norm,'','',NULL
              FROM files;
            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            VALUES(1,1,NULL,'','','CODE','OLD-FANOUT',NULL);
            INSERT INTO search_fts(
                document_id,file_id,source_kind,configuration_name,file_name,path_norm,
                names_text,values_text,origin_path)
            SELECT id,file_id,source_kind,configuration_name,file_name,path_norm,
                   names_text,values_text,origin_path
              FROM search_documents;

            INSERT INTO index_queue(
                file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
            VALUES(10000,'New',3,0,0,0,'64:1::3',3);
            """;
        seed.Parameters.AddWithValue("@drawings", drawingCount);
        await seed.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<long> ReadLiveBytesAsync(SqliteConnection connection)
    {
        var pageSize = await ReadScalarAsync(connection, "PRAGMA page_size;");
        var pageCount = await ReadScalarAsync(connection, "PRAGMA page_count;");
        var freePageCount = await ReadScalarAsync(connection, "PRAGMA freelist_count;");
        return checked(pageSize * (pageCount - freePageCount));
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        return await ReadScalarAsync(connection, sql);
    }

    private static async Task<long> ReadScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
