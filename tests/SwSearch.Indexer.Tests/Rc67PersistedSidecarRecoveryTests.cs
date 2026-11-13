using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc67PersistedSidecarRecoveryTests
{
    [Fact]
    public async Task DrawingOnlyV10Upgrade_UsesPersistedSidecarAndAvoidsFullRebuild()
    {
        await using var sandbox = new Sandbox();
        var part = new FileCandidate(
            @"C:\RC67\Persisted\Valve.SLDPRT",
            SolidWorksDocumentKind.Part,
            42,
            new DateTimeOffset(2026, 8, 4, 1, 0, 0, TimeSpan.Zero));

        await using (var baseline = new SqliteIndexStore(sandbox.CorePath))
        {
            await baseline.InitializeAsync();
            await baseline.ApplyDiscoverySnapshotAsync(AsyncFiles.From([part]), CancellationToken.None);
            await baseline.UpsertMetadataAsync(
                new DocumentMetadata(
                    part,
                    [new DocumentProperty(
                        "Code",
                        "RC67-PERSISTED-PROPERTY",
                        "RC67-PERSISTED-PROPERTY",
                        PropertyScope.Document,
                        null,
                        MetadataSource.NoApi)],
                    [],
                    MetadataSource.NoApi,
                    part.LastWriteTimeUtc),
                CancellationToken.None);
            await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));
        }

        // Create a pre-v10 drawing row while retaining the already-certified
        // Part/Assembly sidecar. The direct schema invocation avoids starting
        // a projector before the read-side admission check is exercised.
        await ExecuteAsync(sandbox.CorePath, """
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,discovery_generation,state,metadata_state)
            VALUES(
                670001,'C:\RC67\Persisted\Legacy.SLDDRW','C:\RC67\PERSISTED\LEGACY.SLDDRW',
                2,1,1,1,0,0);
            PRAGMA user_version=9;
            """);
        await InitializeSchemaAsync(sandbox.CorePath);

        Assert.Equal(10, await ReadScalarAsync(sandbox.CorePath, "PRAGMA user_version;"));
        Assert.Equal(0, await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT rebuild_required FROM search_projection_state WHERE singleton_id=1;"));
        Assert.Equal(1, await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT count(*) FROM search_projection_outbox WHERE reason='DrawingSupportRemoved';"));
        Assert.Equal(0, await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT count(*) FROM files WHERE kind NOT IN (0,1);"));

        // Model the RC66 database left on disk: a stale drawing remains in the
        // sidecar and its core marker still asks for a full rebuild. Neither is
        // visible to the v10 public query contract.
        await ExecuteAsync(sandbox.SearchPath, """
            INSERT INTO search_files(file_id,path,path_norm,kind)
            VALUES(
                670001,'C:\RC67\Persisted\Legacy.SLDDRW','C:\RC67\PERSISTED\LEGACY.SLDDRW',2);
            INSERT INTO search_documents(
                file_id,source_kind,configuration_name,file_name,path_norm,
                names_text,values_text,origin_path)
            VALUES(
                670001,0,NULL,'LEGACY.SLDDRW','C:\RC67\PERSISTED\LEGACY.SLDDRW','', '',NULL);
            """);
        await ExecuteAsync(sandbox.CorePath, """
            UPDATE search_projection_state
               SET rebuild_required=1,rebuild_cursor=0
             WHERE singleton_id=1;
            """);

        // A writable startup repairs the historical marker only when every
        // outstanding row is a deleted drawing tombstone.
        await InitializeSchemaAsync(sandbox.CorePath);
        Assert.Equal(0, await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT rebuild_required FROM search_projection_state WHERE singleton_id=1;"));

        // The local desktop reader can open before that writable repair runs.
        // Non-empty search must still use the persisted sidecar; the empty
        // initial listing deliberately retains its authoritative core path.
        await ExecuteAsync(sandbox.CorePath, """
            UPDATE search_projection_state
               SET rebuild_required=1,rebuild_cursor=0
             WHERE singleton_id=1;
            """);
        await using (var reader = SqliteIndexStore.OpenReadOnlyExisting(sandbox.CorePath))
        {
            var sidecarAcceptances = 0;
            reader.AfterSearchReadinessAcceptedForTestingAsync = _ =>
            {
                Interlocked.Increment(ref sidecarAcceptances);
                return ValueTask.CompletedTask;
            };

            var initial = await reader.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All, Limit: 20),
                CancellationToken.None);
            Assert.Equal(part.FullPath, Assert.Single(initial).FullPath);
            Assert.Equal(1, await reader.CountSearchAsync(
                new SearchQuery(string.Empty, SearchMode.All),
                CancellationToken.None));

            var property = new SearchQuery("RC67-PERSISTED-PROPERTY", SearchMode.Property, Limit: 20);
            Assert.Equal(part.FullPath, Assert.Single(
                await reader.SearchAsync(property, CancellationToken.None)).FullPath);
            Assert.Equal(1, await reader.CountSearchAsync(property, CancellationToken.None));
            Assert.Equal(2, Volatile.Read(ref sidecarAcceptances));
        }

        // On the next writable broker start the obsolete drawing corpus is
        // pruned in one sidecar transaction and the single tombstone is
        // acknowledged; no 138k-row baseline rebuild is seeded.
        await using var repaired = new SqliteIndexStore(sandbox.CorePath);
        await repaired.InitializeAsync();
        await WaitForSearchReadyAsync(sandbox, TimeSpan.FromSeconds(10));
        Assert.Equal(0, await ReadScalarAsync(
            sandbox.SearchPath,
            "SELECT count(*) FROM search_files WHERE kind NOT IN (0,1);"));
        Assert.Equal(1, repaired.SearchProjectionBatchFileCountForTesting);
    }

    private static async Task InitializeSchemaAsync(string path)
    {
        await using var connection = await OpenAsync(path, readOnly: false);
        await SqliteSchema.InitializeAsync(connection, CancellationToken.None);
    }

    private static async Task WaitForSearchReadyAsync(Sandbox sandbox, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var current = await ReadScalarAsync(
                    sandbox.CorePath,
                    "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;");
                if (await ReadScalarAsync(
                        sandbox.CorePath,
                        "SELECT rebuild_required FROM search_projection_state WHERE singleton_id=1;") == 0 &&
                    await ReadScalarAsync(
                        sandbox.CorePath,
                        "SELECT count(*) FROM search_projection_outbox;") == 0 &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT ready FROM search_state WHERE singleton_id=1;") == 1 &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT applied_sequence FROM search_state WHERE singleton_id=1;") == current)
                {
                    return;
                }
            }
            catch (SqliteException)
            {
                // The sidecar worker owns this sandbox-local derived file.
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("RC67 persisted sidecar did not become ready.");
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path, readOnly: false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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

    private sealed class Sandbox : IAsyncDisposable
    {
        internal Sandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"swsearch-rc67-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            CorePath = Path.Combine(RootPath, "core.db");
            SearchPath = Path.Combine(RootPath, "core.search.db");
        }

        internal string RootPath { get; }

        internal string CorePath { get; }

        internal string SearchPath { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
