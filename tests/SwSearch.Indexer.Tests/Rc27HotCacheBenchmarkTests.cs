using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc27HotCacheBenchmarkTests
{
    [Fact]
    public async Task Rc32Synthetic150k_SingleCharacterFirstWindowIsBoundedAndCancelable()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(
            SyntheticFiles(150_000),
            new DiscoverySnapshotOptions("rc37-benchmark", true, [@"C:\"], 5_000),
            CancellationToken.None);

        var clock = Stopwatch.StartNew();
        var hits = await store.SearchAsync(
            new SearchQuery("0", SearchMode.All, Limit: 120), CancellationToken.None);
        clock.Stop();
        Console.WriteLine($"RC32_SQLITE_150K first_page_ms={clock.Elapsed.TotalMilliseconds:F2} hits={hits.Count}");
        Assert.Equal(120, hits.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Single-character first window took {clock.Elapsed.TotalMilliseconds:F2} ms.");

        await using (var planConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        {
            await planConnection.OpenAsync();
            await using var plan = planConnection.CreateCommand();
            plan.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT f.path,f.kind
                  FROM search_short_grams g
                  JOIN search_documents d ON d.id=g.document_id
                  JOIN files f ON f.id=d.file_id AND f.state=0
                 WHERE g.gram_len=1 AND g.gram='0' AND g.field_mask=1
                   AND d.file_name LIKE '%0%'
                 GROUP BY f.id,f.path,f.path_norm,f.kind
                 ORDER BY f.path_norm,f.id LIMIT 120;
                """;
            var details = new List<string>();
            await using var reader = await plan.ExecuteReaderAsync();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            Console.WriteLine("RC32_SHORT_GRAM_PLAN " + string.Join(" | ", details));
            Assert.Contains(details, detail =>
                detail.Contains("SEARCH g USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(details, detail =>
                detail.Contains("SCAN search_documents", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("SCAN search_fts", StringComparison.OrdinalIgnoreCase));
        }

        using var cancellation = new CancellationTokenSource();
        var slow = Task.Run(async () => await store.CountSearchAsync(
            new SearchQuery("0", SearchMode.FileName, Limit: 120), cancellation.Token));
        await Task.Delay(1);
        cancellation.Cancel();
        var cancelClock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
        cancelClock.Stop();
        Assert.True(cancelClock.Elapsed < TimeSpan.FromMilliseconds(500),
            $"SQLite interrupt did not stop the obsolete query: {cancelClock.Elapsed.TotalMilliseconds:F2} ms.");
    }

    [Fact]
    public async Task Synthetic100k_TenHotFilenameQueries_HaveP95Under100Milliseconds()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(
            SyntheticFiles(100_000),
            new DiscoverySnapshotOptions("rc27-benchmark", true, [@"C:\"], 5_000),
            CancellationToken.None);

        _ = await store.SearchAsync(new SearchQuery("Part-099990", SearchMode.FileName, Limit: 120), CancellationToken.None);
        var timings = new List<double>(10);
        for (var index = 0; index < 10; index++)
        {
            var clock = Stopwatch.StartNew();
            var hits = await store.SearchAsync(
                new SearchQuery($"Part-{99_990 + index:D6}", SearchMode.FileName, Limit: 120),
                CancellationToken.None);
            clock.Stop();
            Assert.Single(hits);
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var p50 = timings[4];
        var p95 = timings[9];
        Console.WriteLine($"RC27_HOT_CACHE_100K p50_ms={p50:F2} p95_ms={p95:F2} first_page_limit=120");
        Assert.True(p95 <= 100, $"Hot-cache filename query p95 was {p95:F2} ms (target <= 100 ms).");
    }

    private static async IAsyncEnumerable<FileCandidate> SyntheticFiles(int count)
    {
        var modified = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < count; index++)
        {
            yield return new FileCandidate(
                $@"C:\Synthetic\Part-{index:D6}.SLDPRT",
                SolidWorksDocumentKind.Part,
                1_024 + index,
                modified);
            if ((index & 4095) == 0) await Task.Yield();
        }
    }
}
