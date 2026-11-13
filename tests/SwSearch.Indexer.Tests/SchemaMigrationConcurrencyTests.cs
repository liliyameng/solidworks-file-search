using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class SchemaMigrationConcurrencyTests
{
    private const int ConcurrentInitializers = 12;
    private const int ConcurrencyRounds = 5;

    [Fact]
    public async Task ContractSchemaVersion_MatchesInitializedDatabaseUserVersion()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        Assert.Equal(10, ContractVersions.IndexSchema);
        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
    }

    [Fact]
    public async Task SchemaV2ToV3_PreservesFilesMetadataReferencesAndInheritedSearch()
    {
        await using var database = new TemporaryDatabase();
        var fixture = await CreateV2FixtureAsync(database.Path);

        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
        Assert.Equal(2, (await store.GetStatusAsync()).PresentFileCount);
        var model = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(fixture.Model.FullPath, CancellationToken.None));
        var property = Assert.Single(model.Properties);
        Assert.Equal("MIGRATE-42", property.ResolvedValue);
        var drawing = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(fixture.Drawing.FullPath, CancellationToken.None));
        Assert.Equal(fixture.Model.FullPath, Assert.Single(drawing.References).ReferencedPath);

        var hits = await store.SearchAsync(
            new SearchQuery("MIGRATE-42", SearchMode.Property),
            CancellationToken.None);
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, hit => hit.FullPath == fixture.Model.FullPath && hit.OriginModelPath is null);
        Assert.Contains(hits, hit =>
            hit.FullPath == fixture.Drawing.FullPath && hit.OriginModelPath == fixture.Model.FullPath);
    }

    [Fact]
    public async Task SchemaV2ToV3_FiveRoundsOfTwelveConcurrentInitializers_HaveZeroFailures()
    {
        var failures = new List<Exception>();
        for (var round = 0; round < ConcurrencyRounds; round++)
        {
            await using var database = new TemporaryDatabase();
            await CreateV2FixtureAsync(database.Path);
            var stores = Enumerable.Range(0, ConcurrentInitializers)
                .Select(_ => new SqliteIndexStore(database.Path))
                .ToArray();
            try
            {
                var outcomes = await Task.WhenAll(stores.Select(async store =>
                {
                    try
                    {
                        await store.InitializeAsync();
                        return null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                }));
                failures.AddRange(outcomes.OfType<Exception>());
                Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
            }
            finally
            {
                foreach (var store in stores)
                {
                    await store.DisposeAsync();
                }
            }
        }

        Console.WriteLine(
            $"MIGRATION_CONCURRENCY rounds={ConcurrencyRounds} initializers={ConcurrentInitializers} " +
            $"attempts={ConcurrencyRounds * ConcurrentInitializers} failures={failures.Count}");
        Assert.Empty(failures);
    }

    [Fact]
    public async Task PartiallyAppliedSchemaV2Migration_IsDetectedAndResumesIdempotently()
    {
        await using var database = new TemporaryDatabase();
        var fixture = await CreateV2FixtureAsync(database.Path);
        await ExecuteAsync(database.Path, """
            ALTER TABLE document_references
                ADD COLUMN target_file_name_norm TEXT NOT NULL DEFAULT '';
            CREATE INDEX ix_references_pending_name
                ON document_references(target_file_id, target_file_name_norm);
            PRAGMA user_version=2;
            """);

        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
        var drawing = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(fixture.Drawing.FullPath, CancellationToken.None));
        Assert.Equal(fixture.Model.FullPath, Assert.Single(drawing.References).ReferencedPath);
        Assert.Equal(2, (await store.SearchAsync(
            new SearchQuery("MIGRATE-42", SearchMode.Property),
            CancellationToken.None)).Count);
    }

    [Fact]
    public async Task MigrationLockWait_CanBeCancelledAndRetriedWithoutSchemaDamage()
    {
        await using var database = new TemporaryDatabase();
        await CreateV2FixtureAsync(database.Path);
        await using var blocker = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database.Path,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
        await blocker.OpenAsync();
        await using var writerLock = blocker.BeginTransaction(deferred: false);

        await using var store = new SqliteIndexStore(database.Path);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.InitializeAsync(cancellation.Token));
        }

        Assert.Equal(2, await ReadUserVersionAsync(database.Path));
        await writerLock.RollbackAsync();
        await store.InitializeAsync();
        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
    }

    [Fact]
    public async Task SchemaV5ToV6_RemovesRawDrawingProjectionAndStaleOrigins()
    {
        await using var database = new TemporaryDatabase();
        var now = DateTimeOffset.UtcNow;
        var model = new FileCandidate(@"C:\Migration\Model.SLDPRT", SolidWorksDocumentKind.Part, 10, now);
        var deleted = new FileCandidate(@"C:\Migration\Deleted.SLDPRT", SolidWorksDocumentKind.Part, 11, now);
        var drawing = new FileCandidate(@"C:\Migration\Drawing.SLDDRW", SolidWorksDocumentKind.Drawing, 12, now);
        await using (var store = new SqliteIndexStore(database.Path))
        {
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([model, deleted, drawing]), CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(model,
                [new DocumentProperty("物料编码", "0404-249", "0404-249", PropertyScope.Configuration,
                    "Default", MetadataSource.NoApi)], [], MetadataSource.NoApi, now), CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(deleted,
                [new DocumentProperty("物料编码", "STALE-777", "STALE-777", PropertyScope.Document,
                    null, MetadataSource.NoApi)], [], MetadataSource.NoApi, now), CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(drawing, [],
                [new DocumentReference(model.FullPath, "Default", true),
                 new DocumentReference(deleted.FullPath, null, true)], MetadataSource.NoApi, now),
                CancellationToken.None);
        }

        await ExecuteAsync(database.Path, """
            UPDATE files SET state=1 WHERE path='C:\Migration\Deleted.SLDPRT';
            INSERT INTO document_references(
                drawing_file_id,target_path,target_path_norm,target_file_name_norm,target_file_id,
                referenced_configuration,resolution_state)
            SELECT d.id,'C:\Migration\Model.SLDPRT',upper('C:\Migration\Model.SLDPRT'),'MODEL.SLDPRT',m.id,NULL,0
              FROM files d,files m
             WHERE d.path='C:\Migration\Drawing.SLDDRW' AND m.path='C:\Migration\Model.SLDPRT';
            INSERT INTO properties(
                file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                display_value,cached_value,is_linked,value_norm,source)
            SELECT id,2,NULL,'DrawingCacheEnvelope','DRAWINGCACHEENVELOPE',
                   '$-物料编码-$<cache_string>0404-249</cache_string>',
                   '$-物料编码-$<cache_string>0404-249</cache_string>',
                   '$-物料编码-$<cache_string>0404-249</cache_string>',
                   '$-物料编码-$<cache_string>0404-249</cache_string>',0,
                   '$-物料编码-$<CACHE_STRING>0404-249</CACHE_STRING>',4
              FROM files WHERE path='C:\Migration\Drawing.SLDDRW';
            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT id,1,NULL,'','', 'DRAWINGCACHEENVELOPE',
                   '$-物料编码-$<CACHE_STRING>0404-249</CACHE_STRING>',NULL
              FROM files WHERE path='C:\Migration\Drawing.SLDDRW';
            INSERT INTO search_fts(document_id,file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path)
            SELECT id,file_id,source_kind,configuration_name,file_name,path_norm,names_text,values_text,origin_path
              FROM search_documents WHERE names_text='DRAWINGCACHEENVELOPE';
            INSERT OR IGNORE INTO search_short_grams(gram_len,gram,document_id,field_mask)
            SELECT 2,'$-',id,2 FROM search_documents WHERE names_text='DRAWINGCACHEENVELOPE';
            INSERT OR IGNORE INTO effective_properties(
                drawing_file_id,origin_file_id,property_id,name_norm,value_norm,rule_version)
            SELECT d.id,o.id,p.id,p.name_norm,p.value_norm,1
              FROM files d,files o,properties p
             WHERE d.path='C:\Migration\Drawing.SLDDRW'
               AND o.path='C:\Migration\Deleted.SLDPRT' AND p.file_id=o.id;
            PRAGMA user_version=5;
            """);

        await using (var migrated = new SqliteIndexStore(database.Path))
        {
            await migrated.InitializeAsync();
            Assert.DoesNotContain(await migrated.SearchAsync(
                new SearchQuery("CACHE_STRING", SearchMode.Property), CancellationToken.None),
                hit => hit.FullPath == drawing.FullPath);
            Assert.DoesNotContain(await migrated.SearchAsync(
                new SearchQuery("$-", SearchMode.Property), CancellationToken.None),
                hit => hit.FullPath == drawing.FullPath);
            Assert.Equal(0, await migrated.CountSearchAsync(
                new SearchQuery("$-", SearchMode.Property), CancellationToken.None));
            Assert.DoesNotContain(await migrated.SearchAsync(
                new SearchQuery("STALE-777", SearchMode.Property), CancellationToken.None),
                hit => hit.FullPath == drawing.FullPath);
            Assert.Contains(await migrated.SearchAsync(
                new SearchQuery("0404-249", SearchMode.Property), CancellationToken.None),
                hit => hit.FullPath == drawing.FullPath && hit.OriginModelPath == model.FullPath);
        }

        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
        await using var check = await OpenUnpooledAsync(database.Path);
        await using var command = check.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM document_references r
                WHERE r.referenced_configuration IS NULL AND EXISTS(
                    SELECT 1 FROM document_references q
                     WHERE q.drawing_file_id=r.drawing_file_id AND q.target_path_norm=r.target_path_norm
                       AND q.referenced_configuration IS NOT NULL)),
              (SELECT COUNT(*) FROM effective_properties ep JOIN files f ON f.id=ep.origin_file_id WHERE f.state<>0),
              (SELECT COUNT(*) FROM search_short_grams g
                 JOIN search_documents d ON d.id=g.document_id
                 JOIN files f ON f.id=d.file_id
                WHERE f.path='C:\Migration\Drawing.SLDDRW' AND d.source_kind=1
                  AND (d.names_text LIKE '%DRAWINGCACHE%' OR d.values_text LIKE '%$-%'));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task SchemaV5ToV6_LargeUnrelatedPropertySet_IsBoundedAndPreservesGrams()
    {
        await using var database = new TemporaryDatabase();
        await using (var store = new SqliteIndexStore(database.Path))
        {
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([
                new FileCandidate(@"C:\Scale\Part.SLDPRT", SolidWorksDocumentKind.Part, 1,
                    DateTimeOffset.UtcNow)
            ]), CancellationToken.None);
        }
        await ExecuteAsync(database.Path, """
            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)),
            numbers(n) AS (
                SELECT a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+100000*f.n
                  FROM digits a,digits b,digits c,digits d,digits e,digits f
                 LIMIT 864232)
            INSERT INTO properties(
                file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                display_value,cached_value,is_linked,value_norm,source)
            SELECT (SELECT id FROM files LIMIT 1),0,NULL,'P','P','V','V','V','V',0,'V',4
              FROM numbers;
            PRAGMA user_version=5;
            PRAGMA wal_checkpoint(TRUNCATE);
            """);
        var gramsBefore = await ReadScalarAsync(database.Path, "SELECT COUNT(*) FROM search_short_grams;");
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var peakBefore = process.PeakWorkingSet64;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using (var migrated = new SqliteIndexStore(database.Path))
            await migrated.InitializeAsync();
        stopwatch.Stop();
        process.Refresh();
        var peakDelta = Math.Max(0, process.PeakWorkingSet64 - peakBefore);
        var walBytes = File.Exists(database.Path + "-wal") ? new FileInfo(database.Path + "-wal").Length : 0;
        var gramsAfter = await ReadScalarAsync(database.Path, "SELECT COUNT(*) FROM search_short_grams;");

        Console.WriteLine(
            $"V6_BOUNDED properties=864232 ms={stopwatch.Elapsed.TotalMilliseconds:F2} " +
            $"peakDelta={peakDelta} wal={walBytes} grams={gramsAfter}");
        Assert.Equal(gramsBefore, gramsAfter);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.True(peakDelta < 256L * 1024 * 1024);
        Assert.True(walBytes < 32L * 1024 * 1024);

        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.Indexer", "SqliteSchema.cs"));
        var start = source.IndexOf("MigrateSafeDrawingProjectionToV6Async", StringComparison.Ordinal);
        var end = source.IndexOf("ConfigureBeforeMigrationAsync", start, StringComparison.Ordinal);
        var migration = source[start..end];
        Assert.DoesNotContain("new List<(long Id, DocumentProperty Property)>", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM search_short_grams;", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM search_documents;", migration, StringComparison.Ordinal);
    }

    private static async Task<(FileCandidate Model, FileCandidate Drawing)> CreateV2FixtureAsync(string path)
    {
        var extracted = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var model = new FileCandidate(
            @"C:\Migration\Model.SLDASM",
            SolidWorksDocumentKind.Assembly,
            10_000,
            extracted);
        var drawing = new FileCandidate(
            @"C:\Migration\Drawing.SLDDRW",
            SolidWorksDocumentKind.Drawing,
            5_000,
            extracted);

        await using (var store = new SqliteIndexStore(path))
        {
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(
                AsyncFiles.From([model, drawing]),
                CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(
                model,
                [new DocumentProperty(
                    "PartNumber",
                    "MIGRATE-42",
                    "MIGRATE-42",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                extracted), CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(
                drawing,
                [],
                [new DocumentReference(model.FullPath, null, true)],
                MetadataSource.NoApi,
                extracted), CancellationToken.None);
        }

        await ExecuteAsync(path, """
            DROP INDEX IF EXISTS ix_references_pending_name;
            DROP INDEX IF EXISTS ix_discovery_stage_run_name;
            DROP INDEX IF EXISTS ix_search_documents_filename;
            ALTER TABLE document_references DROP COLUMN target_file_name_norm;
            DELETE FROM schema_info WHERE version=3;
            INSERT OR REPLACE INTO schema_info(version,created_utc,app_version) VALUES(2,0,'2.0-test');
            PRAGMA user_version=2;
            """);
        return (model, drawing);
    }

    private static async Task<int> ReadUserVersionAsync(string path)
    {
        await using var connection = await OpenUnpooledAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = await OpenUnpooledAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenUnpooledAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenUnpooledAsync(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
