using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc71ScopedFtsCandidateTests
{
    private const int OutsideCandidateCount = 8_050;
    private const string SearchTerm = "MATERIAL71";

    [Fact]
    public async Task SidecarFts_AppliesLibraryAndFolderEligibilityBeforeCandidateLimit()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await WaitForSearchSidecarAsync(database.Path, store.SearchDatabasePath);
        await CreateHighCardinalityPropertyFixtureAsync(store.SearchDatabasePath);

        var acceptedSidecarReads = 0;
        store.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref acceptedSidecarReads);
            return ValueTask.CompletedTask;
        };

        try
        {
            var libraryQuery = new SearchQuery(
                SearchTerm,
                SearchMode.Library,
                Limit: 200,
                Kinds: [SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly],
                LibraryRoots: [@"C:\DesignLibrary"]);

            var libraryHit = Assert.Single(
                await store.SearchAsync(libraryQuery, CancellationToken.None));
            Assert.Equal(1, await store.CountSearchAsync(libraryQuery, CancellationToken.None));
            Assert.Equal(@"C:\DesignLibrary\Target.SLDPRT", libraryHit.FullPath);
            Assert.Equal("MaterialCode", libraryHit.MatchedProperty);
            Assert.Equal(SearchTerm, libraryHit.MatchedValue);

            var folderQuery = new SearchQuery(
                SearchTerm,
                SearchMode.All,
                Limit: 200,
                Kinds: [SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly],
                Scope: SearchScope.Folder(@"C:\ScopedFolder"));

            var folderHit = Assert.Single(
                await store.SearchAsync(folderQuery, CancellationToken.None));
            Assert.Equal(1, await store.CountSearchAsync(folderQuery, CancellationToken.None));
            Assert.Equal(@"C:\ScopedFolder\Target.SLDASM", folderHit.FullPath);
            Assert.Equal("MaterialCode", folderHit.MatchedProperty);
            Assert.Equal(SearchTerm, folderHit.MatchedValue);

            Assert.True(
                Volatile.Read(ref acceptedSidecarReads) >= 4,
                "The regression must execute the production sidecar FTS search/count path.");
        }
        finally
        {
            store.AfterSearchReadinessAcceptedForTestingAsync = null;
        }
    }

    private static async Task CreateHighCardinalityPropertyFixtureAsync(string searchPath)
    {
        await using var connection = await OpenAsync(searchPath, readOnly: false);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            WITH RECURSIVE n(value) AS (
                SELECT 1
                UNION ALL
                SELECT value+1 FROM n WHERE value<@outsideCount)
            INSERT INTO search_files(file_id,path,path_norm,kind)
            SELECT value,
                   printf('C:\Outside\Part-%05d.SLDPRT',value),
                   upper(printf('C:\Outside\Part-%05d.SLDPRT',value)),
                   0
              FROM n;

            INSERT INTO search_files(file_id,path,path_norm,kind) VALUES
                (@libraryId,'C:\DesignLibrary\Target.SLDPRT','C:\DESIGNLIBRARY\TARGET.SLDPRT',0),
                (@folderId,'C:\ScopedFolder\Target.SLDASM','C:\SCOPEDFOLDER\TARGET.SLDASM',1);

            INSERT INTO search_documents(
                document_id,file_id,source_kind,configuration_name,file_name,path_norm,
                names_text,values_text,origin_path)
            SELECT file_id,file_id,1,NULL,'','',
                   'MATERIALCODE',@term,NULL
              FROM search_files;

            INSERT INTO search_property_values(
                property_id,file_id,source_kind,configuration_name,origin_path,
                property_name,name_norm,display_value,value_norm)
            SELECT file_id,file_id,1,NULL,NULL,
                   'MaterialCode','MATERIALCODE',@term,@term
              FROM search_files;

            INSERT INTO search_fts(
                document_id,file_id,source_kind,configuration_name,file_name,path_norm,
                names_text,values_text,origin_path)
            SELECT document_id,file_id,source_kind,configuration_name,file_name,path_norm,
                   names_text,values_text,origin_path
              FROM search_documents
             ORDER BY document_id;
            """;
        command.Parameters.AddWithValue("@outsideCount", OutsideCandidateCount);
        command.Parameters.AddWithValue("@libraryId", OutsideCandidateCount + 1);
        command.Parameters.AddWithValue("@folderId", OutsideCandidateCount + 2);
        command.Parameters.AddWithValue("@term", SearchTerm);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task WaitForSearchSidecarAsync(string corePath, string searchPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var current = await ReadScalarAsync(
                    corePath,
                    "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;");
                if (await ReadScalarAsync(
                        searchPath,
                        "SELECT ready FROM search_state WHERE singleton_id=1;") == 1 &&
                    await ReadScalarAsync(
                        searchPath,
                        "SELECT applied_sequence FROM search_state WHERE singleton_id=1;") == current)
                {
                    return;
                }
            }
            catch (SqliteException)
            {
                // The isolated store may still be creating its derived sidecar.
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("RC71 search sidecar did not become ready.");
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
