using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

/// <summary>
/// RC51 dual-database safety gates.
///
/// Every test owns one unpredictable directory below the process temp root.
/// Nothing here opens the product cache, starts a product process, or scans the
/// machine.  Core is authoritative; the search database is disposable.
/// </summary>
public sealed class Rc51DualDatabaseIsolationTests
{
    [Fact]
    public async Task FreshInitialization_CreatesV9CoreWithoutDerivedSearchTables_AndSeparateSidecar()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);

        await store.InitializeAsync();
        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));

        Assert.Equal(9, await ReadScalarAsync(sandbox.CorePath, "PRAGMA user_version;"));
        Assert.Equal(
            0,
            await ReadScalarAsync(
                sandbox.CorePath,
                """
                SELECT count(*) FROM sqlite_schema
                 WHERE lower(name) IN ('search_documents','search_fts','search_short_grams');
                """));
        Assert.Equal(
            2,
            await ReadScalarAsync(
                sandbox.CorePath,
                """
                SELECT count(*) FROM sqlite_schema
                 WHERE type='table'
                   AND lower(name) IN ('search_projection_state','search_projection_outbox');
                """));

        Assert.Equal(sandbox.SearchPath, store.SearchDatabasePath);
        Assert.NotEqual(
            Path.GetFullPath(store.DatabasePath),
            Path.GetFullPath(store.SearchDatabasePath));
        Assert.True(File.Exists(store.SearchDatabasePath), store.SearchDatabasePath);
        Assert.Equal(2, await ReadScalarAsync(store.SearchDatabasePath, "PRAGMA user_version;"));
        Assert.Equal(
            4,
            await ReadScalarAsync(
                store.SearchDatabasePath,
                """
                SELECT count(*) FROM sqlite_schema
                 WHERE type='table'
                   AND lower(name) IN (
                       'search_state','search_files','search_documents','search_property_values');
                """));
    }

    [Fact]
    public void ProductCoreAndLegacyAliases_AlwaysMapToTheFixedSearchDatabase()
    {
        var resolver = Assert.IsAssignableFrom<MethodInfo>(typeof(SqliteIndexStore).GetMethod(
            "ResolveSearchDatabasePath",
            BindingFlags.Static | BindingFlags.NonPublic));

        Assert.Equal(
            Path.GetFullPath(SwSearchPaths.SearchDatabasePath),
            Assert.IsType<string>(resolver.Invoke(null, [SwSearchPaths.CoreDatabasePath])));
        Assert.Equal(
            Path.GetFullPath(SwSearchPaths.SearchDatabasePath),
            Assert.IsType<string>(resolver.Invoke(null, [SwSearchPaths.LegacyDatabasePath])));
    }

    [Fact]
    public async Task FileNameMode_NeverMatchesParentOnlyText_InCoreOrReadySidecarSearchAndCount()
    {
        await using var sandbox = new DualDatabaseSandbox();
        const string parentOnly = "PARENT-ONLY-RC51";
        var file = new FileCandidate(
            $@"C:\{parentOnly}\Neutral.SLDPRT",
            SolidWorksDocumentKind.Part,
            17,
            new DateTimeOffset(2026, 7, 26, 6, 0, 0, TimeSpan.Zero));

        var sidecarAcceptances = 0;
        await using (var store = new SqliteIndexStore(sandbox.CorePath))
        {
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
            await WaitForSearchReadyAsync(
                sandbox,
                TimeSpan.FromSeconds(10),
                minimumSearchDocumentCount: 1);
            store.AfterSearchReadinessAcceptedForTestingAsync = _ =>
            {
                Interlocked.Increment(ref sidecarAcceptances);
                return ValueTask.CompletedTask;
            };

            var fileName = new SearchQuery(parentOnly, SearchMode.FileName, Limit: 10);
            Assert.Empty(await store.SearchAsync(fileName, CancellationToken.None));
            Assert.Equal(0, await store.CountSearchAsync(fileName, CancellationToken.None));

            var all = new SearchQuery(parentOnly, SearchMode.All, Limit: 10);
            Assert.Equal(file.FullPath, Assert.Single(
                await store.SearchAsync(all, CancellationToken.None)).FullPath);
            Assert.Equal(1, await store.CountSearchAsync(all, CancellationToken.None));
            Assert.Equal(4, Volatile.Read(ref sidecarAcceptances));
        }

        // Make the disposable sidecar explicitly ineligible, then use a
        // read-only store so no projector can race the authoritative core gate.
        await ExecuteAsync(
            sandbox.SearchPath,
            "UPDATE search_state SET ready=0 WHERE singleton_id=1;");
        await using var coreOnly = SqliteIndexStore.OpenReadOnlyExisting(sandbox.CorePath);
        var unexpectedSidecarAcceptance = 0;
        coreOnly.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref unexpectedSidecarAcceptance);
            return ValueTask.CompletedTask;
        };

        var coreFileName = new SearchQuery(parentOnly, SearchMode.FileName, Limit: 10);
        Assert.Empty(await coreOnly.SearchAsync(coreFileName, CancellationToken.None));
        Assert.Equal(0, await coreOnly.CountSearchAsync(coreFileName, CancellationToken.None));

        var coreAll = new SearchQuery(parentOnly, SearchMode.All, Limit: 10);
        Assert.Equal(file.FullPath, Assert.Single(
            await coreOnly.SearchAsync(coreAll, CancellationToken.None)).FullPath);
        Assert.Equal(1, await coreOnly.CountSearchAsync(coreAll, CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref unexpectedSidecarAcceptance));
    }

    [Fact]
    public async Task PendingDrawingProjection_PreventsReadyCertificationAndForcesCoreFallback()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await CreateDirectAndEffectiveFixtureAsync(sandbox);
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));

        await ExecuteAsync(
            sandbox.CorePath,
            """
            INSERT OR REPLACE INTO drawing_projection_queue(
                drawing_file_id,reason,queued_utc,attempt,next_due_utc,last_error)
            SELECT id,'RC51Gate',1,0,9223372036854775807,NULL
              FROM files WHERE path_norm='C:\RC51\DUAL\DOCUMENTDRAWING.SLDDRW';
            """);
        await ExecuteAsync(
            sandbox.SearchPath,
            "UPDATE search_state SET ready=0 WHERE singleton_id=1;");

        var sidecarAcceptances = 0;
        store.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref sidecarAcceptances);
            return ValueTask.CompletedTask;
        };
        var inherited = new SearchQuery("RC51DUAL-A", SearchMode.Property, Limit: 10);
        Assert.Equal(2, (await store.SearchAsync(inherited, CancellationToken.None)).Count);
        Assert.Equal(2, await store.CountSearchAsync(inherited, CancellationToken.None));
        await Task.Delay(100);
        Assert.Equal(
            0,
            await ReadScalarAsync(
                sandbox.SearchPath,
                "SELECT ready FROM search_state WHERE singleton_id=1;"));
        Assert.Equal(0, Volatile.Read(ref sidecarAcceptances));

        await ExecuteAsync(sandbox.CorePath, "DELETE FROM drawing_projection_queue;");
        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await store.SearchAsync(inherited, CancellationToken.None)).Count);
        Assert.Equal(2, await store.CountSearchAsync(inherited, CancellationToken.None));
        Assert.Equal(2, Volatile.Read(ref sidecarAcceptances));
    }

    [Fact]
    public async Task ReadySidecar_BoundedOrdinaryOutbox_MergesFreshCoreWithoutResultLoss()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await CreateDirectAndEffectiveFixtureAsync(sandbox);

        // The completed sidecar deliberately remains readable while one normal
        // file projection is pending.  Use a read-only store after preparing
        // the deterministic dirty row so the background projector cannot race
        // this stale-while-revalidate gate.
        await ExecuteAsync(
            sandbox.CorePath,
            """
            INSERT INTO search_projection_outbox(file_id,reason,queued_utc)
            SELECT id,'RC63_BoundedDirty',1
              FROM files
             WHERE path_norm='C:\RC51\DUAL\DOCUMENTMODEL.SLDPRT';
            UPDATE search_projection_state
               SET current_sequence=(SELECT max(sequence) FROM search_projection_outbox)
             WHERE singleton_id=1;
            """);

        await using var reader = SqliteIndexStore.OpenReadOnlyExisting(sandbox.CorePath);
        var sidecarAcceptances = 0;
        reader.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref sidecarAcceptances);
            return ValueTask.CompletedTask;
        };

        await AssertCompleteCoreFallbackAsync(reader);
        // Count remains authoritative while stale; each of the five result
        // pages in the shared assertion is served by sidecar + one-file core
        // overlay without changing the result set.
        Assert.Equal(5, Volatile.Read(ref sidecarAcceptances));
    }

    [Fact]
    public async Task ReadySidecar_OverBudgetOrdinaryOutbox_UsesCompleteCoreFallback()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await CreateDirectAndEffectiveFixtureAsync(sandbox);

        await ExecuteAsync(
            sandbox.CorePath,
            """
            WITH RECURSIVE ids(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM ids WHERE value < 193
            )
            INSERT INTO search_projection_outbox(file_id,reason,queued_utc)
            SELECT 900000 + value,'RC63_OverBudgetDirty',value FROM ids;
            UPDATE search_projection_state
               SET current_sequence=(SELECT max(sequence) FROM search_projection_outbox)
             WHERE singleton_id=1;
            """);

        await using var reader = SqliteIndexStore.OpenReadOnlyExisting(sandbox.CorePath);
        var sidecarAcceptances = 0;
        reader.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref sidecarAcceptances);
            return ValueTask.CompletedTask;
        };

        await AssertCompleteCoreFallbackAsync(reader);
        Assert.Equal(0, Volatile.Read(ref sidecarAcceptances));
    }

    [Fact]
    public async Task MetadataLeaseFailure_InvalidatesInheritedSidecarUntilSkippedRecoveryProjects()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await CreateDirectAndEffectiveFixtureAsync(sandbox);

        IndexStorageStatus baseline;
        await using (var baselineStore = new SqliteIndexStore(sandbox.CorePath))
        {
            await baselineStore.InitializeAsync();
            await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));
            baseline = await baselineStore.GetStorageStatusAsync();
        }

        // Keep metadata admission open while reserving the last eighth of the
        // logical core for metadata. This deterministically leaves the drawing
        // obligation pending until the recovery store below uses normal policy.
        var logicalHeadroom = Math.Max(4_096, baseline.LogicalDatabaseBytes / 10);
        var logicalMaximum = checked(baseline.LogicalDatabaseBytes + logicalHeadroom);
        var constrainedPolicy = new IndexStoragePolicy(
            TargetDatabaseBytes: Math.Max(1, logicalMaximum / 2),
            MaximumDatabaseBytes: logicalMaximum,
            PassiveCheckpointWalBytes: 256L * 1024 * 1024,
            PauseWalBytes: 512L * 1024 * 1024,
            PhysicalSafetyDatabaseBytes: Math.Max(
                logicalMaximum + 1,
                baseline.DatabaseBytes + baseline.WalBytes + 1024L * 1024 * 1024));
        var optionalCeiling = logicalMaximum -
            Math.Min(
                SqliteIndexStore.CoreMetadataReserveBytes,
                Math.Max(1, logicalMaximum / 8));
        Assert.True(baseline.LogicalDatabaseBytes >= optionalCeiling);

        const string modelPath = @"C:\RC51\Dual\DocumentModel.SLDPRT";
        var drawingOnly = new SearchQuery(
            "RC51DUAL-A",
            SearchMode.Property,
            Limit: 10,
            Kinds: [SolidWorksDocumentKind.Drawing]);
        await using (var constrained = new SqliteIndexStore(sandbox.CorePath, constrainedPolicy))
        {
            await constrained.InitializeAsync();
            await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));
            var sidecarAcceptances = 0;
            constrained.AfterSearchReadinessAcceptedForTestingAsync = _ =>
            {
                Interlocked.Increment(ref sidecarAcceptances);
                return ValueTask.CompletedTask;
            };
            Assert.Single(await constrained.SearchAsync(drawingOnly, CancellationToken.None));
            Assert.Equal(1, await constrained.CountSearchAsync(drawingOnly, CancellationToken.None));
            Assert.Equal(2, Volatile.Read(ref sidecarAcceptances));

            await constrained.EnqueueMetadataAsync(
                modelPath,
                "RC51LeaseFailure",
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            var failedLease = Assert.IsType<MetadataWorkLease>(
                await constrained.TryLeaseMetadataPathAsync(
                    modelPath,
                    TimeSpan.FromSeconds(30),
                    "rc51-failure"));
            await constrained.CompleteMetadataLeaseAsync(
                failedLease.FileId,
                failedLease.LeaseOwner,
                succeeded: false,
                errorCode: "RC51_FAILURE",
                retryDelay: TimeSpan.Zero);

            Assert.Equal(
                2,
                await ReadScalarAsync(
                    sandbox.CorePath,
                    "SELECT metadata_state FROM files WHERE id=" + failedLease.FileId + ";"));
            Assert.Equal(
                1,
                await ReadScalarAsync(
                    sandbox.CorePath,
                    "SELECT count(*) FROM drawing_projection_queue;"));
            Assert.True(
                await ReadScalarAsync(
                    sandbox.SearchPath,
                    """
                    SELECT count(*) FROM search_property_values
                     WHERE source_kind=2 AND value_norm='RC51DUAL-A';
                    """) > 0);

            Interlocked.Exchange(ref sidecarAcceptances, 0);
            Assert.Empty(await constrained.SearchAsync(drawingOnly, CancellationToken.None));
            Assert.Equal(0, await constrained.CountSearchAsync(drawingOnly, CancellationToken.None));
            Assert.Equal(0, Volatile.Read(ref sidecarAcceptances));

            var recoveryLease = Assert.IsType<MetadataWorkLease>(
                await constrained.TryLeaseMetadataPathAsync(
                    modelPath,
                    TimeSpan.FromSeconds(30),
                    "rc51-recovery"));
            await constrained.SkipMetadataLeaseAsync(
                recoveryLease.FileId,
                recoveryLease.LeaseOwner,
                "RC51_RECOVERED");
            Assert.Equal(
                0,
                await ReadScalarAsync(
                    sandbox.CorePath,
                    "SELECT metadata_state FROM files WHERE id=" + recoveryLease.FileId + ";"));
            Assert.Equal(
                1,
                await ReadScalarAsync(
                    sandbox.CorePath,
                    "SELECT count(*) FROM drawing_projection_queue;"));
        }

        await using var recovered = new SqliteIndexStore(sandbox.CorePath);
        await recovered.InitializeAsync();
        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));
        var recoveredSidecarAcceptances = 0;
        recovered.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref recoveredSidecarAcceptances);
            return ValueTask.CompletedTask;
        };
        Assert.Single(await recovered.SearchAsync(drawingOnly, CancellationToken.None));
        Assert.Equal(1, await recovered.CountSearchAsync(drawingOnly, CancellationToken.None));
        Assert.Equal(2, Volatile.Read(ref recoveredSidecarAcceptances));
        Assert.Equal(
            0,
            await ReadScalarAsync(
                sandbox.CorePath,
                "SELECT count(*) FROM drawing_projection_queue;"));
    }

    [Theory]
    [InlineData(SearchSidecarFault.Missing)]
    [InlineData(SearchSidecarFault.Corrupt)]
    [InlineData(SearchSidecarFault.Rebuilding)]
    [InlineData(SearchSidecarFault.AppliedSequenceBehind)]
    public async Task MissingCorruptRebuildingOrLaggingSidecar_SearchAndCountUseCompleteCoreFallback(
        SearchSidecarFault fault)
    {
        await using var sandbox = new DualDatabaseSandbox();
        await CreateDirectAndEffectiveFixtureAsync(sandbox);
        await SabotageSearchSidecarAsync(sandbox, fault);

        if (fault == SearchSidecarFault.AppliedSequenceBehind)
        {
            await using (var staleReader = SqliteIndexStore.OpenReadOnlyExisting(sandbox.CorePath))
            {
                await AssertCompleteCoreFallbackAsync(staleReader);
            }

            // A writable reopen must not "repair" an N-1 sidecar by merely
            // stamping applied_sequence=N on its empty contents.  It must seed
            // and project the authoritative core before it can become ready.
            await using var writable = new SqliteIndexStore(sandbox.CorePath);
            await writable.InitializeAsync();
            await WaitForSearchReadyAsync(
                sandbox,
                TimeSpan.FromSeconds(10),
                minimumSearchDocumentCount: 4);
            await AssertCompleteCoreFallbackAsync(writable);
            return;
        }

        await using var reader = SqliteIndexStore.OpenReadOnlyExisting(sandbox.CorePath);
        await AssertCompleteCoreFallbackAsync(reader);
    }

    [Fact]
    public async Task PermanentlyUnavailableSidecar_DoesNotBlockMetadata_AndOutboxStaysOneLatestRowPerFile()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);

        // A directory at the exact sidecar path is a persistent, sandbox-local
        // open failure.  It exercises the real best-effort recovery path
        // without changing ACLs or touching any external file.
        Directory.CreateDirectory(store.SearchDatabasePath);
        await store.InitializeAsync();

        var modified = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);
        var files = Enumerable.Range(0, 3)
            .Select(index => new FileCandidate(
                $@"C:\RC51\Dual\Unavailable-{index}.SLDPRT",
                SolidWorksDocumentKind.Part,
                100 + index,
                modified))
            .ToArray();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);

        for (var revision = 0; revision < 20; revision++)
        {
            foreach (var file in files)
            {
                await store.UpsertMetadataAsync(
                    new DocumentMetadata(
                        file,
                        [
                            new DocumentProperty(
                                "Code",
                                $"LATEST-{revision:00}",
                                $"LATEST-{revision:00}",
                                PropertyScope.Document,
                                null,
                                MetadataSource.NoApi),
                        ],
                        [],
                        MetadataSource.NoApi,
                        modified.AddMinutes(revision)),
                    CancellationToken.None);
            }
        }

        Assert.Equal(3, await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT count(*) FROM search_projection_outbox;"));
        Assert.Equal(3, await ReadScalarAsync(
            sandbox.CorePath,
            """
            SELECT count(*) FROM (
                SELECT file_id,count(*) AS copies
                  FROM search_projection_outbox
                 GROUP BY file_id HAVING copies=1);
            """));
        Assert.Equal(3, await ReadScalarAsync(
            sandbox.CorePath,
            """
            SELECT count(*)
              FROM search_projection_outbox o
              JOIN search_projection_state s ON s.singleton_id=1
             WHERE o.sequence<=s.current_sequence;
            """));

        var before = await ReadOutboxWatermarkAsync(sandbox.CorePath);
        await Task.Delay(500);
        var after = await ReadOutboxWatermarkAsync(sandbox.CorePath);
        Assert.Equal(before, after);

        var metadata = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(files[0].FullPath, CancellationToken.None));
        Assert.Contains(
            metadata.Properties,
            property => property.Name == "Code" && property.ResolvedValue == "LATEST-19");
        var latestQuery = new SearchQuery("LATEST-19", SearchMode.Property, Limit: 10);
        Assert.Equal(3, await store.CountSearchAsync(latestQuery, CancellationToken.None));
        Assert.Equal(3, (await store.SearchAsync(latestQuery, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ReadinessAndQueryShareOneSidecarSnapshot_AcrossConcurrentProjectorCommit()
    {
        await using var sandbox = new DualDatabaseSandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\RC51\Dual\Snapshot.SLDPRT",
            SolidWorksDocumentKind.Part,
            501,
            new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero));
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await StoreSinglePropertyAsync(store, file, "SNAPSHOT-OLD", file.LastWriteTimeUtc);
        await WaitForSearchReadyAsync(
            sandbox,
            TimeSpan.FromSeconds(10),
            minimumSearchDocumentCount: 2);
        await WaitForSearchValueAsync(sandbox.SearchPath, "SNAPSHOT-OLD", TimeSpan.FromSeconds(5));

        var searchEntered = NewSignal();
        var releaseSearch = NewSignal();
        store.AfterSearchReadinessAcceptedForTestingAsync = async cancellationToken =>
        {
            searchEntered.TrySetResult();
            await releaseSearch.Task.WaitAsync(cancellationToken);
        };
        var oldSearch = store.SearchAsync(
            new SearchQuery("SNAPSHOT-OLD", SearchMode.Property, Limit: 10),
            CancellationToken.None).AsTask();
        await searchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await StoreSinglePropertyAsync(
            store,
            file,
            "SNAPSHOT-NEW",
            file.LastWriteTimeUtc.AddMinutes(1));
        await WaitForSearchValueAsync(sandbox.SearchPath, "SNAPSHOT-NEW", TimeSpan.FromSeconds(10));
        await ExecuteAsync(
            sandbox.SearchPath,
            "UPDATE search_state SET generation='RC51-RACE-GENERATION-2' WHERE singleton_id=1;");
        releaseSearch.TrySetResult();

        var oldSnapshot = Assert.Single(await oldSearch);
        Assert.Equal("SNAPSHOT-OLD", oldSnapshot.MatchedValue);

        store.AfterSearchReadinessAcceptedForTestingAsync = null;
        Assert.Empty(await store.SearchAsync(
            new SearchQuery("SNAPSHOT-OLD", SearchMode.Property, Limit: 10),
            CancellationToken.None));
        Assert.Equal(
            "SNAPSHOT-NEW",
            Assert.Single(await store.SearchAsync(
                new SearchQuery("SNAPSHOT-NEW", SearchMode.Property, Limit: 10),
                CancellationToken.None)).MatchedValue);

        var countEntered = NewSignal();
        var releaseCount = NewSignal();
        store.AfterSearchReadinessAcceptedForTestingAsync = async cancellationToken =>
        {
            countEntered.TrySetResult();
            await releaseCount.Task.WaitAsync(cancellationToken);
        };
        var oldCount = store.CountSearchAsync(
            new SearchQuery("SNAPSHOT-NEW", SearchMode.Property),
            CancellationToken.None).AsTask();
        await countEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await StoreSinglePropertyAsync(
            store,
            file,
            "SNAPSHOT-LATEST",
            file.LastWriteTimeUtc.AddMinutes(2));
        await WaitForSearchValueAsync(sandbox.SearchPath, "SNAPSHOT-LATEST", TimeSpan.FromSeconds(10));
        await ExecuteAsync(
            sandbox.SearchPath,
            "UPDATE search_state SET generation='RC51-RACE-GENERATION-3' WHERE singleton_id=1;");
        releaseCount.TrySetResult();

        Assert.Equal(1, await oldCount);
        store.AfterSearchReadinessAcceptedForTestingAsync = null;
        Assert.Equal(0, await store.CountSearchAsync(
            new SearchQuery("SNAPSHOT-NEW", SearchMode.Property),
            CancellationToken.None));
        Assert.Equal(1, await store.CountSearchAsync(
            new SearchQuery("SNAPSHOT-LATEST", SearchMode.Property),
            CancellationToken.None));
    }

    private static async Task CreateDirectAndEffectiveFixtureAsync(DualDatabaseSandbox sandbox)
    {
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        var now = new DateTimeOffset(2026, 7, 26, 7, 0, 0, TimeSpan.Zero);
        var documentModel = new FileCandidate(
            @"C:\RC51\Dual\DocumentModel.SLDPRT",
            SolidWorksDocumentKind.Part,
            101,
            now);
        var configurationModel = new FileCandidate(
            @"C:\RC51\Dual\ConfigurationModel.SLDASM",
            SolidWorksDocumentKind.Assembly,
            102,
            now);
        var documentDrawing = new FileCandidate(
            @"C:\RC51\Dual\DocumentDrawing.SLDDRW",
            SolidWorksDocumentKind.Drawing,
            103,
            now);
        var configurationDrawing = new FileCandidate(
            @"C:\RC51\Dual\ConfigurationDrawing.SLDDRW",
            SolidWorksDocumentKind.Drawing,
            104,
            now);
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From(
                [documentModel, configurationModel, documentDrawing, configurationDrawing]),
            CancellationToken.None);

        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                documentModel,
                [
                    new DocumentProperty(
                        "Code",
                        "RC51DUAL-A",
                        "RC51DUAL-A",
                        PropertyScope.Document,
                        null,
                        MetadataSource.NoApi),
                ],
                [],
                MetadataSource.NoApi,
                now),
            CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                configurationModel,
                [
                    new DocumentProperty(
                        "Code",
                        "RC51DUAL-B",
                        "RC51DUAL-B",
                        PropertyScope.Configuration,
                        "Default",
                        MetadataSource.NoApi),
                ],
                [],
                MetadataSource.NoApi,
                now,
                ConfigurationNames: ["Default"]),
            CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                documentDrawing,
                [],
                [new DocumentReference(documentModel.FullPath, null, true)],
                MetadataSource.NoApi,
                now),
            CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                configurationDrawing,
                [],
                [new DocumentReference(configurationModel.FullPath, "Default", true)],
                MetadataSource.NoApi,
                now),
            CancellationToken.None);

        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));
    }

    private static ValueTask StoreSinglePropertyAsync(
        SqliteIndexStore store,
        FileCandidate file,
        string value,
        DateTimeOffset extractedAtUtc) =>
        store.UpsertMetadataAsync(
            new DocumentMetadata(
                file,
                [
                    new DocumentProperty(
                        "Code",
                        value,
                        value,
                        PropertyScope.Document,
                        null,
                        MetadataSource.NoApi),
                ],
                [],
                MetadataSource.NoApi,
                extractedAtUtc),
            CancellationToken.None);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertCompleteCoreFallbackAsync(SqliteIndexStore store)
    {
        var all = new SearchQuery("RC51DUAL", SearchMode.Property, Limit: 20);
        Assert.Equal(4, await store.CountSearchAsync(all, CancellationToken.None));
        var hits = await store.SearchAsync(all, CancellationToken.None);
        Assert.Equal(4, hits.Count);
        Assert.Equal(4, hits.Select(hit => hit.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            2,
            hits.Count(hit => hit.OriginModelPath is not null));
        Assert.Contains(
            hits,
            hit => hit.FullPath.EndsWith(
                       "DocumentDrawing.SLDDRW",
                       StringComparison.OrdinalIgnoreCase) &&
                   hit.MatchedValue == "RC51DUAL-A" &&
                   hit.OriginModelPath?.EndsWith(
                       "DocumentModel.SLDPRT",
                       StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            hits,
            hit => hit.FullPath.EndsWith(
                       "ConfigurationDrawing.SLDDRW",
                       StringComparison.OrdinalIgnoreCase) &&
                   hit.MatchedValue == "RC51DUAL-B" &&
                   hit.OriginConfiguration == "Default");

        var firstPage = await store.SearchAsync(
            all with { Offset = 0, Limit = 2 },
            CancellationToken.None);
        var secondPage = await store.SearchAsync(
            all with { Offset = 2, Limit = 2 },
            CancellationToken.None);
        Assert.Equal(2, firstPage.Count);
        Assert.Equal(2, secondPage.Count);
        Assert.Equal(
            4,
            firstPage.Concat(secondPage)
                .Select(hit => hit.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        var documentOnly = new SearchQuery("RC51DUAL-A", SearchMode.Property, Limit: 20);
        Assert.Equal(2, await store.CountSearchAsync(documentOnly, CancellationToken.None));
        Assert.Equal(
            2,
            (await store.SearchAsync(documentOnly, CancellationToken.None)).Count);
        var configurationOnly = new SearchQuery("RC51DUAL-B", SearchMode.Property, Limit: 20);
        Assert.Equal(2, await store.CountSearchAsync(configurationOnly, CancellationToken.None));
        Assert.Equal(
            2,
            (await store.SearchAsync(configurationOnly, CancellationToken.None)).Count);
    }

    private static async Task SabotageSearchSidecarAsync(
        DualDatabaseSandbox sandbox,
        SearchSidecarFault fault)
    {
        SqliteConnection.ClearAllPools();
        switch (fault)
        {
            case SearchSidecarFault.Missing:
                DeleteSqliteFiles(sandbox.SearchPath);
                return;

            case SearchSidecarFault.Corrupt:
                DeleteSqliteFiles(sandbox.SearchPath);
                await File.WriteAllBytesAsync(
                    sandbox.SearchPath,
                    "THIS IS NOT SQLITE"u8.ToArray());
                return;

            case SearchSidecarFault.Rebuilding:
                await ExecuteAsync(
                    sandbox.SearchPath,
                    """
                    DELETE FROM search_fts;
                    DELETE FROM search_property_values;
                    DELETE FROM search_documents;
                    DELETE FROM search_files;
                    UPDATE search_state SET ready=1;
                    """);
                await ExecuteAsync(
                    sandbox.CorePath,
                    """
                    UPDATE search_projection_state
                       SET rebuild_required=1;
                    """);
                return;

            case SearchSidecarFault.AppliedSequenceBehind:
                var current = await ReadScalarAsync(
                    sandbox.CorePath,
                    """
                    SELECT current_sequence
                      FROM search_projection_state WHERE singleton_id=1;
                    """);
                Assert.True(current > 0);
                await ExecuteAsync(
                    sandbox.SearchPath,
                    $"""
                    DELETE FROM search_fts;
                    DELETE FROM search_property_values;
                    DELETE FROM search_documents;
                    DELETE FROM search_files;
                    UPDATE search_state
                       SET ready=1,applied_sequence={current - 1};
                    """);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
    }

    private static async Task WaitForSearchReadyAsync(
        DualDatabaseSandbox sandbox,
        TimeSpan timeout,
        long minimumSearchDocumentCount = 0)
    {
        var deadline = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (deadline.Elapsed < timeout)
        {
            try
            {
                var core = await ReadProjectionStateAsync(sandbox.CorePath);
                var search = await ReadSearchStateAsync(sandbox.SearchPath);
                if (core.RebuildRequired == 0 &&
                    core.OutboxCount == 0 &&
                    search.HasValue &&
                    search.Value.Ready == 1 &&
                    search.Value.AppliedSequence == core.CurrentSequence &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT count(*) FROM search_documents;") >= minimumSearchDocumentCount)
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is SqliteException or IOException)
            {
                lastFailure = exception;
            }
            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Search sidecar was not ready within {timeout}. Last failure: {lastFailure}");
    }

    private static async Task WaitForSearchValueAsync(
        string searchPath,
        string value,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (deadline.Elapsed < timeout)
        {
            try
            {
                await using var connection = await OpenAsync(searchPath, readOnly: true);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS(
                        SELECT 1 FROM search_property_values
                         WHERE value_norm=@value);
                    """;
                command.Parameters.AddWithValue("@value", IndexNormalization.Text(value));
                if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0)
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is SqliteException or IOException)
            {
                lastFailure = exception;
            }
            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Search value {value} was not projected within {timeout}. Last failure: {lastFailure}");
    }

    private static async Task<(long CurrentSequence, long RebuildRequired, long OutboxCount)>
        ReadProjectionStateAsync(string corePath)
    {
        await using var connection = await OpenAsync(corePath, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.current_sequence,s.rebuild_required,
                   (SELECT count(*) FROM search_projection_outbox)
              FROM search_projection_state s WHERE s.singleton_id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<(long AppliedSequence, long Ready)?> ReadSearchStateAsync(
        string searchPath)
    {
        await using var connection = await OpenAsync(searchPath, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT applied_sequence,ready
              FROM search_state WHERE singleton_id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<(long Count, long MaximumSequence)> ReadOutboxWatermarkAsync(
        string corePath)
    {
        await using var connection = await OpenAsync(corePath, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*),coalesce(max(sequence),0)
              FROM search_projection_outbox;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path, readOnly: false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static void DeleteSqliteFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    public enum SearchSidecarFault
    {
        Missing,
        Corrupt,
        Rebuilding,
        AppliedSequenceBehind,
    }

    private sealed class DualDatabaseSandbox : IAsyncDisposable
    {
        private const string Prefix = "swsearch-rc51-dualdb-";

        internal DualDatabaseSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CorePath = Path.Combine(Root, "core.db");
            SearchPath = Path.Combine(Root, "core.search.db");
        }

        internal string Root { get; }

        internal string CorePath { get; }

        internal string SearchPath { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            var fullRoot = Path.GetFullPath(Root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to clean a non-test directory: {fullRoot}");
            }
            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
