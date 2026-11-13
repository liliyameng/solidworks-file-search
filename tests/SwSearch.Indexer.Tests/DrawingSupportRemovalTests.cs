using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class DrawingSupportRemovalTests
{
    [Theory]
    [InlineData(@"C:\Library\Part.SLDPRT", SolidWorksDocumentKind.Part)]
    [InlineData(@"C:\Library\Assembly.SLDASM", SolidWorksDocumentKind.Assembly)]
    public void DiscoveryPath_RecognizesOnlySupportedDocumentKinds(
        string path,
        SolidWorksDocumentKind expected)
    {
        Assert.True(DiscoveryPath.TryGetDocumentKind(path, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(DiscoveryPath.TryGetDocumentKind(@"C:\Library\Legacy.SLDDRW", out _));
        Assert.Throws<ArgumentException>(() => IndexNormalization.ValidateCandidate(new FileCandidate(
            @"C:\Library\Legacy.SLDDRW",
            SolidWorksDocumentKind.Drawing,
            1,
            DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task V9Migration_RemovesLegacyDrawingRowsReferencesAndProjectionWork()
    {
        await using var database = new TemporaryDatabase();
        await using (var seed = new SqliteIndexStore(database.Path))
        {
            await seed.InitializeAsync();
        }

        const string drawingPath = @"C:\Legacy\Sheet.SLDDRW";
        await ExecuteAsync(database.Path, """
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,discovery_generation,state,metadata_state)
            VALUES
                (101,'C:\Legacy\Model.SLDPRT','C:\LEGACY\MODEL.SLDPRT',0,1,1,1,0,0),
                (102,'C:\Legacy\Sheet.SLDDRW','C:\LEGACY\SHEET.SLDDRW',2,1,1,1,0,0);
            INSERT INTO properties(
                id,file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                value_norm,source)
            VALUES
                (201,101,0,NULL,'PartNumber','PARTNUMBER','MODEL-1','MODEL-1','MODEL-1',4),
                (202,102,0,NULL,'SheetNumber','SHEETNUMBER','SHEET-1','SHEET-1','SHEET-1',4);
            INSERT INTO configurations(file_id,configuration_name,ordinal)
            VALUES(102,'Sheet1',0);
            INSERT INTO document_references(
                drawing_file_id,target_path,target_path_norm,target_file_name_norm,target_file_id,
                referenced_configuration,resolution_state)
            VALUES(102,'C:\Legacy\Model.SLDPRT','C:\LEGACY\MODEL.SLDPRT','MODEL.SLDPRT',101,NULL,0);
            INSERT INTO effective_properties(
                drawing_file_id,origin_file_id,property_id,name_norm,value_norm,rule_version)
            VALUES(102,101,201,'PARTNUMBER','MODEL-1',1);
            INSERT INTO drawing_projection_queue(
                drawing_file_id,reason,queued_utc,attempt,next_due_utc,last_error)
            VALUES(102,'Legacy',1,0,0,NULL);
            INSERT INTO index_queue(
                file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
            VALUES(102,'Legacy',4,1,0,0,'legacy-drawing',1);
            INSERT INTO dead_letter(
                file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
            VALUES(102,'legacy-drawing',1,1,'LEGACY',1);
            INSERT INTO deferred_discovery_changes(
                file_id,change_kind,path,path_norm,root_path,candidate_kind,size,modified_utc,
                file_identity,recorded_utc)
            VALUES(
                102,0,'C:\Legacy\Sheet.SLDDRW','C:\LEGACY\SHEET.SLDDRW','C:\LEGACY',2,1,1,
                NULL,1);
            INSERT INTO interactive_metadata_attempts(
                path_norm,attempt_id,pipeline_lease_id,baseline_file_id,baseline_fingerprint,started_utc)
            VALUES('C:\LEGACY\SHEET.SLDDRW','legacy-attempt','legacy-lease',102,NULL,1);
            INSERT INTO discovery_runs(run_id,source_id,started_utc,completed)
            VALUES('legacy-drawing','test',1,0);
            INSERT INTO discovery_stage(
                run_id,path,path_norm,root_norm,file_name_norm,kind,size,modified_utc,file_identity)
            VALUES(
                'legacy-drawing','C:\Legacy\Sheet.SLDDRW','C:\LEGACY\SHEET.SLDDRW',
                'C:\LEGACY','SHEET.SLDDRW',2,1,1,NULL);
            PRAGMA user_version=9;
            """);

        await using var migrated = new SqliteIndexStore(database.Path);
        await migrated.InitializeAsync();

        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
        Assert.Equal(1, (await migrated.GetStatusAsync()).PresentFileCount);
        Assert.Null(await migrated.TryGetMetadataAsync(drawingPath, CancellationToken.None));
        Assert.Empty(await migrated.SearchAsync(
            new SearchQuery("SHEET", SearchMode.All),
            CancellationToken.None));
        var drawingOnly = new SearchQuery(
            string.Empty,
            SearchMode.All,
            Kinds: [SolidWorksDocumentKind.Drawing]);
        Assert.Empty(await migrated.SearchAsync(drawingOnly, CancellationToken.None));
        Assert.Equal(0, await migrated.CountSearchAsync(drawingOnly, CancellationToken.None));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM files WHERE kind NOT IN (0,1);"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM document_references;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM effective_properties;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM drawing_projection_queue;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM properties WHERE file_id=102;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM configurations WHERE file_id=102;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM index_queue WHERE file_id=102;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM dead_letter WHERE file_id=102;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM deferred_discovery_changes WHERE file_id=102;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM interactive_metadata_attempts WHERE baseline_file_id=102;"));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM discovery_stage WHERE kind NOT IN (0,1);"));
    }

    [Fact]
    public async Task V9Migration_EnsuresDeferredJournalBeforeCleaningLegacyDrawings()
    {
        await using var database = new TemporaryDatabase();
        await using (var seed = new SqliteIndexStore(database.Path))
        {
            await seed.InitializeAsync();
        }

        await ExecuteAsync(database.Path, """
            DROP TABLE deferred_discovery_changes;
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,discovery_generation,state,metadata_state)
            VALUES(103,'C:\Legacy\EarlyV9.SLDDRW','C:\LEGACY\EARLYV9.SLDDRW',2,1,1,1,0,0);
            PRAGMA user_version=9;
            """);

        await using var migrated = new SqliteIndexStore(database.Path);
        await migrated.InitializeAsync();

        Assert.Equal(ContractVersions.IndexSchema, await ReadUserVersionAsync(database.Path));
        Assert.Equal(1, await ReadScalarAsync(database.Path, """
            SELECT count(*) FROM sqlite_master
             WHERE type='table' AND name='deferred_discovery_changes';
            """));
        Assert.Equal(0, await ReadScalarAsync(database.Path,
            "SELECT count(*) FROM files WHERE kind NOT IN (0,1);"));
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadUserVersionAsync(string path)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
