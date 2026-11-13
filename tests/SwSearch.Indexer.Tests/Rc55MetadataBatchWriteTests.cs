using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc55MetadataBatchWriteTests
{
    [Fact]
    public async Task LaggingEmptySidecar_RebuildsAndBecomesReadyAfterBatchedProjection()
    {
        using var sandbox = new Sandbox();
        var files = CreateFiles(2, "LAG");
        await using (var initial = new SqliteIndexStore(sandbox.CorePath))
        {
            await initial.InitializeAsync();
            await initial.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
            await initial.UpsertMetadataBatchAsync(
                files.Select((file, index) => Metadata(file, $"LAG-{index}")).ToArray(),
                CancellationToken.None);
            await WaitForProjectionReadyAsync(sandbox, TimeSpan.FromSeconds(10));
        }

        var current = await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;");
        await ExecuteAsync(
            sandbox.SearchPath,
            $"""
            DELETE FROM search_fts;
            DELETE FROM search_property_values;
            DELETE FROM search_documents;
            DELETE FROM search_files;
            UPDATE search_state SET ready=1,applied_sequence={current - 1};
            """);

        await using var recovered = new SqliteIndexStore(sandbox.CorePath);
        await recovered.InitializeAsync();
        await WaitForProjectionReadyAsync(sandbox, TimeSpan.FromSeconds(10));
        Assert.True(await ReadScalarAsync(
            sandbox.SearchPath,
            "SELECT count(*) FROM search_documents;") >= 4);
    }

    [Fact]
    public async Task MetadataBatch_UsesOnePreparedContext_AndSidecarProjectsInBoundedBatches()
    {
        using var sandbox = new Sandbox();
        // Keep the normal sidecar path pinned to its full 96-file write
        // batch. Backpressure fairness is covered separately at eight files.
        var files = CreateFiles(96, "SEMANTIC");
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        await WaitForProjectionReadyAsync(sandbox, TimeSpan.FromSeconds(10));

        var metadataContextsBefore = store.MetadataBatchContextCountForTesting;
        var projectionCommitsBefore = store.SearchProjectionBatchCommitCountForTesting;
        var projectionFilesBefore = store.SearchProjectionBatchFileCountForTesting;

        await store.UpsertMetadataBatchAsync(
            files.Select((file, index) => Metadata(file, $"RC55-GROUP-D-{index:D2}")).ToArray(),
            CancellationToken.None);

        Assert.Equal(
            metadataContextsBefore + 1,
            store.MetadataBatchContextCountForTesting);
        await WaitForProjectionReadyAsync(sandbox, TimeSpan.FromSeconds(10));

        Assert.Equal(
            96,
            store.SearchProjectionBatchFileCountForTesting - projectionFilesBefore);
        var projectionCommits =
            store.SearchProjectionBatchCommitCountForTesting - projectionCommitsBefore;
        Assert.Equal(1, projectionCommits);
        Assert.Equal(
            0,
            await ReadScalarAsync(
                sandbox.CorePath,
                "SELECT count(*) FROM search_projection_outbox;"));

        foreach (var index in new[] { 0, 23, 24, 47 })
        {
            var query = new SearchQuery(
                $"RC55-GROUP-D-{index:D2}",
                SearchMode.Property,
                Limit: 10);
            Assert.Equal(files[index].FullPath, Assert.Single(
                await store.SearchAsync(query, CancellationToken.None)).FullPath);
            Assert.Equal(1, await store.CountSearchAsync(query, CancellationToken.None));
        }
    }

    [Fact]
    public async Task EquivalentMetadata_CompletesWithoutRewritingPropertiesOrQueueingSidecarWork()
    {
        using var sandbox = new Sandbox();
        var file = CreateFiles(1, "UNCHANGED").Single();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);

        await store.UpsertMetadataAsync(Metadata(file, "UNCHANGED-CONTENT"), CancellationToken.None);
        await WaitForProjectionReadyAsync(sandbox, TimeSpan.FromSeconds(10));
        var propertyIdBefore = await ReadScalarAsync(
            sandbox.CorePath,
            "SELECT min(id) FROM properties;");

        await store.UpsertMetadataAsync(Metadata(file, "UNCHANGED-CONTENT"), CancellationToken.None);

        Assert.Equal(
            propertyIdBefore,
            await ReadScalarAsync(sandbox.CorePath, "SELECT min(id) FROM properties;"));
        Assert.Equal(
            0,
            await ReadScalarAsync(
                sandbox.CorePath,
                "SELECT count(*) FROM search_projection_outbox;"));
        Assert.Equal(
            0,
            await ReadScalarAsync(
                sandbox.CorePath,
                "SELECT count(*) FROM index_queue;"));
    }

    [Fact]
    public async Task PendingFileBatch_SkipsOldSignatureReads_AndOfflineBenchmarkReportsThroughput()
    {
        const int fileCount = 32;
        using var batchSandbox = new Sandbox();
        using var sequentialSandbox = new Sandbox();
        var batchFiles = CreateFiles(fileCount, "BATCH");
        var sequentialFiles = CreateFiles(fileCount, "SEQUENTIAL");

        var batchResult = await RunWriteBenchmarkAsync(
            batchSandbox,
            batchFiles,
            writeAsBatch: true);
        var sequentialResult = await RunWriteBenchmarkAsync(
            sequentialSandbox,
            sequentialFiles,
            writeAsBatch: false);

        Assert.Equal(1, batchResult.Contexts);
        Assert.Equal(fileCount, sequentialResult.Contexts);
        Assert.Equal(fileCount, batchResult.Completed);
        Assert.Equal(fileCount, sequentialResult.Completed);
        Assert.True(batchResult.FilesPerSecond > 0);
        Assert.True(sequentialResult.FilesPerSecond > 0);
        Console.WriteLine(
            $"RC55_GROUP_D_METADATA_WRITE files={fileCount} " +
            $"batch_ms={batchResult.Elapsed.TotalMilliseconds:F2} " +
            $"batch_files_per_sec={batchResult.FilesPerSecond:F2} " +
            $"sequential_ms={sequentialResult.Elapsed.TotalMilliseconds:F2} " +
            $"sequential_files_per_sec={sequentialResult.FilesPerSecond:F2} " +
            $"batch_contexts={batchResult.Contexts} sequential_contexts={sequentialResult.Contexts}");
    }

    private static async Task<BenchmarkResult> RunWriteBenchmarkAsync(
        Sandbox sandbox,
        IReadOnlyList<FileCandidate> files,
        bool writeAsBatch)
    {
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        // Keep the benchmark entirely local and prevent disposable sidecar work
        // from adding scheduler noise to the authoritative core measurement.
        Directory.CreateDirectory(store.SearchDatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        var contextsBefore = store.MetadataBatchContextCountForTesting;
        var metadata = files.Select((file, index) =>
            Metadata(file, $"BENCH-{index:D2}")).ToArray();

        var clock = Stopwatch.StartNew();
        if (writeAsBatch)
        {
            await store.UpsertMetadataBatchAsync(metadata, CancellationToken.None);
        }
        else
        {
            foreach (var item in metadata)
            {
                await store.UpsertMetadataAsync(item, CancellationToken.None);
            }
        }
        clock.Stop();

        var completed = await ReadScalarAsync(
            sandbox.CorePath,
            $"SELECT count(*) FROM files WHERE metadata_state=0 AND extractor_version={ExtractorVersions.Current};");
        return new BenchmarkResult(
            clock.Elapsed,
            files.Count,
            store.MetadataBatchContextCountForTesting - contextsBefore,
            completed);
    }

    private static DocumentMetadata Metadata(FileCandidate file, string value) =>
        new(
            file,
            [
                new DocumentProperty(
                    "Code",
                    value,
                    value,
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi),
                new DocumentProperty(
                    "Description",
                    $"Description-{value}",
                    $"Description-{value}",
                    PropertyScope.Configuration,
                    "Default",
                    MetadataSource.NoApi),
            ],
            [],
            MetadataSource.NoApi,
            DateTimeOffset.UtcNow,
            ConfigurationNames: ["Default"]);

    private static FileCandidate[] CreateFiles(int count, string prefix)
    {
        var modified = new DateTimeOffset(2026, 7, 27, 6, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(index => new FileCandidate(
                $@"C:\RC55\GroupD\{prefix}-{index:D2}.SLDPRT",
                SolidWorksDocumentKind.Part,
                4_096 + index,
                modified.AddSeconds(index)))
            .ToArray();
    }

    private static async Task WaitForProjectionReadyAsync(
        Sandbox sandbox,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(sandbox.SearchPath) &&
                    await ReadScalarAsync(
                        sandbox.CorePath,
                        "SELECT count(*) FROM search_projection_outbox;") == 0 &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT ready FROM search_state WHERE singleton_id=1;") == 1 &&
                    await ReadScalarAsync(
                        sandbox.SearchPath,
                        "SELECT applied_sequence FROM search_state WHERE singleton_id=1;") ==
                    await ReadScalarAsync(
                        sandbox.CorePath,
                        "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;"))
                {
                    return;
                }
            }
            catch (SqliteException)
            {
                // The background owner may be creating or replacing the
                // disposable sidecar; retry against the same temp-only files.
            }
            await Task.Delay(25);
        }
        var coreState = await ReadTextAsync(
            sandbox.CorePath,
            """
            SELECT rebuild_required || ':' || rebuild_cursor || ':' || current_sequence || ':' ||
                   (SELECT count(*) FROM search_projection_outbox)
              FROM search_projection_state WHERE singleton_id=1;
            """);
        var sidecarState = File.Exists(sandbox.SearchPath)
            ? await ReadTextAsync(
                sandbox.SearchPath,
                """
                SELECT ready || ':' || applied_sequence || ':' ||
                       (SELECT count(*) FROM search_files)
                  FROM search_state WHERE singleton_id=1;
                """)
            : "missing";
        throw new TimeoutException(
            $"Search sidecar did not become ready. core={coreState};sidecar={sidecarState}");
    }

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadTextAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "(null)";
    }

    private sealed record BenchmarkResult(
        TimeSpan Elapsed,
        int FileCount,
        long Contexts,
        long Completed)
    {
        internal double FilesPerSecond =>
            FileCount / Math.Max(Elapsed.TotalSeconds, 0.000_001);
    }

    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"swsearch-rc55-group-d-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            CorePath = System.IO.Path.Combine(Path, "core.db");
            SearchPath = System.IO.Path.Combine(Path, "core.search.db");
        }

        internal string Path { get; }

        internal string CorePath { get; }

        internal string SearchPath { get; }

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
