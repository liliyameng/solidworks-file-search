using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc55QueueSchedulingTests
{
    private const string OldLeaseSql = """
        SELECT q.file_id
          FROM index_queue q JOIN files f ON f.id=q.file_id AND f.state=0
         WHERE q.due_utc<=@now AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now)
           AND NOT EXISTS(SELECT 1 FROM dead_letter d WHERE d.file_id=q.file_id
                          AND d.fingerprint=q.fingerprint AND d.extractor_version=q.extractor_version)
         ORDER BY q.priority,
                  CASE f.kind WHEN 1 THEN 0 WHEN 0 THEN 1 WHEN 2 THEN 2 ELSE 3 END,
                  CASE WHEN q.priority BETWEEN 2 AND 4 THEN f.modified_utc END DESC,
                  q.due_utc,q.attempt,q.file_id LIMIT 4;
        """;

    private const string NewLeaseSql = """
        SELECT q.file_id
          FROM index_queue q INDEXED BY ix_queue_schedule
          CROSS JOIN files f ON f.id=q.file_id AND f.state=0
         WHERE q.due_utc<=@now AND (q.lease_until_utc IS NULL OR q.lease_until_utc<=@now)
           AND NOT EXISTS(SELECT 1 FROM dead_letter d WHERE d.file_id=q.file_id
                          AND d.fingerprint=q.fingerprint AND d.extractor_version=q.extractor_version)
         ORDER BY q.priority,
                  q.schedule_kind_rank,
                  CASE WHEN q.priority BETWEEN 2 AND 4
                       THEN q.schedule_modified_utc ELSE 0 END DESC,
                  q.due_utc,q.attempt,q.file_id LIMIT 4;
        """;

    [Fact]
    public async Task V8Queue_MigratesIdempotentlyAndMaintainsPersistedSchedule()
    {
        await using var database = new TemporaryDatabase();
        var files = new[]
        {
            new FileCandidate(@"C:\RC55\old.SLDASM", SolidWorksDocumentKind.Assembly, 1,
                new DateTimeOffset(100, TimeSpan.Zero)),
            new FileCandidate(@"C:\RC55\new.SLDASM", SolidWorksDocumentKind.Assembly, 1,
                new DateTimeOffset(200, TimeSpan.Zero)),
            new FileCandidate(@"C:\RC55\part.SLDPRT", SolidWorksDocumentKind.Part, 1,
                new DateTimeOffset(300, TimeSpan.Zero)),
            new FileCandidate(@"C:\RC55\drawing.SLDDRW", SolidWorksDocumentKind.Drawing, 1,
                new DateTimeOffset(400, TimeSpan.Zero)),
        };

        await using (var create = new SqliteIndexStore(database.Path))
        {
            await create.InitializeAsync();
            await create.ApplyDiscoverySnapshotAsync(AsyncFiles.From(files), CancellationToken.None);
        }

        await using (var v8 = await OpenAsync(database.Path))
        {
            await ExecuteAsync(v8, """
                DROP TRIGGER tr_index_queue_schedule_insert;
                DROP TRIGGER tr_files_queue_schedule_update;
                DROP INDEX ix_queue_schedule;
                ALTER TABLE index_queue DROP COLUMN schedule_modified_utc;
                ALTER TABLE index_queue DROP COLUMN schedule_kind_rank;
                PRAGMA user_version=8;
                """);
        }

        await using (var migrated = new SqliteIndexStore(database.Path))
        {
            await migrated.InitializeAsync();
            var leases = await migrated.LeaseMetadataAsync(
                4,
                TimeSpan.FromSeconds(10),
                "rc55-migration-order");
            Assert.Equal(
                [files[1].FullPath, files[0].FullPath, files[2].FullPath, files[3].FullPath],
                leases.Select(static lease => lease.FullPath));
            await migrated.ReleaseMetadataLeasesAsync("rc55-migration-order");
        }

        // A second initialization must be a no-op, while the trigger-maintained
        // values continue to follow live file kind/modified changes.
        await using (var reopened = new SqliteIndexStore(database.Path))
        {
            await reopened.InitializeAsync();
        }
        await using var verify = await OpenAsync(database.Path);
        Assert.Equal(9L, await ScalarAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(2L, await ScalarAsync(
            verify,
            "SELECT count(*) FROM pragma_table_info('index_queue') WHERE name LIKE 'schedule_%';"));
        Assert.Equal(1L, await ScalarAsync(
            verify,
            "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='ix_queue_schedule';"));

        await ExecuteAsync(verify, """
            UPDATE files SET kind=2,modified_utc=999 WHERE path_norm='C:\RC55\PART.SLDPRT';
            """);
        await using (var maintained = verify.CreateCommand())
        {
            maintained.CommandText = """
                SELECT q.schedule_kind_rank,q.schedule_modified_utc
                  FROM index_queue q JOIN files f ON f.id=q.file_id
                 WHERE f.path_norm='C:\RC55\PART.SLDPRT';
                """;
            await using var reader = await maintained.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(999L, reader.GetInt64(1));
        }

        await ExecuteAsync(verify, """
            DELETE FROM index_queue
             WHERE file_id=(SELECT id FROM files WHERE path_norm='C:\RC55\PART.SLDPRT');
            INSERT INTO index_queue(
                file_id,reason,priority,due_utc,attempt,failure_count,fingerprint,extractor_version)
            SELECT id,'LegacyInsert',4,0,0,0,'legacy',0
              FROM files WHERE path_norm='C:\RC55\PART.SLDPRT';
            """);
        Assert.Equal(999L, await ScalarAsync(
            verify,
            """
            SELECT q.schedule_modified_utc
              FROM index_queue q JOIN files f ON f.id=q.file_id
             WHERE f.path_norm='C:\RC55\PART.SLDPRT';
            """));
    }

    [Fact]
    public async Task Queue100k_PersistedScheduleEliminatesCrossTableTempSort()
    {
        const int rowCount = 100_000;
        await using var database = new TemporaryDatabase();
        await using (var store = new SqliteIndexStore(database.Path))
        {
            await store.InitializeAsync();
        }

        await using var connection = await OpenAsync(database.Path);
        await ExecuteAsync(connection, """
            DROP TRIGGER tr_index_queue_schedule_insert;
            BEGIN IMMEDIATE;
            WITH digits(n) AS (
                VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)
            ), numbers(n) AS (
                SELECT 1+a.n+10*b.n+100*c.n+1000*d.n+10000*e.n
                  FROM digits a CROSS JOIN digits b CROSS JOIN digits c
                       CROSS JOIN digits d CROSS JOIN digits e
            )
            INSERT INTO files(
                id,path,path_norm,kind,size,modified_utc,discovery_generation,
                state,metadata_state,extractor_version)
            SELECT n,'C:\RC55\Queue\F'||printf('%06d',n)||'.SLDPRT',
                   'C:\RC55\QUEUE\F'||printf('%06d',n)||'.SLDPRT',
                   n%3,n,1000000+(n%10000),1,0,1,4
              FROM numbers;

            INSERT INTO index_queue(
                file_id,reason,priority,due_utc,attempt,failure_count,
                fingerprint,extractor_version,schedule_kind_rank,schedule_modified_utc)
            SELECT id,'Benchmark',id%6,id%1000,id%3,0,
                   printf('%d:%d',size,modified_utc),4,
                   CASE kind WHEN 1 THEN 0 WHEN 0 THEN 1 WHEN 2 THEN 2 ELSE 3 END,
                   modified_utc
              FROM files;

            INSERT INTO dead_letter(
                file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
            SELECT q.file_id,q.fingerprint,q.extractor_version,3,'BENCH',0
              FROM index_queue q WHERE q.file_id%997=0;
            COMMIT;
            """);
        Assert.Equal(rowCount, await ScalarAsync(connection, "SELECT count(*) FROM index_queue;"));

        var oldPlan = await ReadPlanAsync(connection, OldLeaseSql);
        var newPlan = await ReadPlanAsync(connection, NewLeaseSql);
        Assert.Contains(oldPlan, static detail =>
            detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(newPlan, static detail =>
            detail.Contains("ix_queue_schedule", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(newPlan, static detail =>
            detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));

        var oldIds = await ReadIdsAsync(connection, OldLeaseSql);
        var newIds = await ReadIdsAsync(connection, NewLeaseSql);
        Assert.Equal(oldIds, newIds);

        _ = await MeasureAsync(connection, OldLeaseSql, iterations: 2);
        _ = await MeasureAsync(connection, NewLeaseSql, iterations: 2);
        var oldMs = await MeasureAsync(connection, OldLeaseSql, iterations: 7);
        var newMs = await MeasureAsync(connection, NewLeaseSql, iterations: 7);
        Console.WriteLine(
            $"RC55_QUEUE_100K old_median_ms={oldMs:F3} new_median_ms={newMs:F3} " +
            $"speedup={oldMs / Math.Max(newMs, 0.001):F1}x");
        Console.WriteLine($"RC55_QUEUE_OLD_PLAN {string.Join(" | ", oldPlan)}");
        Console.WriteLine($"RC55_QUEUE_NEW_PLAN {string.Join(" | ", newPlan)}");
        Assert.True(newMs < oldMs, $"Persisted order {newMs:F3} ms was not faster than {oldMs:F3} ms.");
    }

    private static async Task<List<string>> ReadPlanAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@now", long.MaxValue);
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }
        return details;
    }

    private static async Task<long[]> ReadIdsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@now", long.MaxValue);
        var ids = new List<long>(4);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return [.. ids];
    }

    private static async Task<double> MeasureAsync(
        SqliteConnection connection,
        string sql,
        int iterations)
    {
        var samples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var clock = Stopwatch.StartNew();
            _ = await ReadIdsAsync(connection, sql);
            clock.Stop();
            samples[iteration] = clock.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[samples.Length / 2];
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
