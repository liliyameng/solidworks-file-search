using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class SqliteReadConnectionUdfScopeTests
{
    [Fact]
    public async Task GenericReadConnection_KeepsBusyTimeoutAndDoesNotRegisterSearchUdf()
    {
        await using var database = new TemporaryDatabase();
        await CreateEmptyDatabaseAsync(database.Path);
        await using var store = SqliteIndexStore.OpenReadOnlyExisting(database.Path);

        await using var connection = await store.OpenReadConnectionAsync(CancellationToken.None);

        Assert.Equal(100L, await ReadBusyTimeoutAsync(connection));
        await Assert.ThrowsAsync<SqliteException>(async () => await ExecuteSwSafeValueAsync(connection));
    }

    [Fact]
    public async Task SearchReadConnection_KeepsBusyTimeoutAndRegistersSearchUdf()
    {
        await using var database = new TemporaryDatabase();
        await CreateEmptyDatabaseAsync(database.Path);
        await using var store = SqliteIndexStore.OpenReadOnlyExisting(database.Path);

        await using var connection = await store.OpenSearchReadConnectionAsync(CancellationToken.None);

        Assert.Equal(100L, await ReadBusyTimeoutAsync(connection));
        Assert.Equal("resolved", await ExecuteSwSafeValueAsync(connection));
    }

    [Fact]
    public async Task SearchReadConnection_UdfDelegatesToResolvedDisplayValueSemantics()
    {
        await using var database = new TemporaryDatabase();
        await CreateEmptyDatabaseAsync(database.Path);
        await using var store = SqliteIndexStore.OpenReadOnlyExisting(database.Path);

        await using var connection = await store.OpenSearchReadConnectionAsync(CancellationToken.None);

        Assert.Equal(
            "display",
            await ExecuteSwSafeValueAsync(
                connection,
                rawValue: "$PRP:\"Unresolved\"",
                resolvedValue: "$PRP:\"Unresolved\"",
                displayValue: "display",
                cachedValue: "cached",
                isLinked: true));
    }

    [Fact]
    public void CoreSearchCtes_DoesNotReferenceSwSafeValue()
    {
        var field = typeof(SqliteIndexStore).GetField(
            "CoreSearchCtes",
            BindingFlags.Static | BindingFlags.NonPublic);
        var ctes = Assert.IsType<string>(field!.GetRawConstantValue());

        Assert.DoesNotContain("sw_safe_value", ctes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountSearchCoreAsync_RunsOnGenericConnectionWithoutUdf()
    {
        await using var sandbox = new Sandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        var file = CreateFile(@"C:\UdfScope\Count-V2.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(
            CreateMetadata(
                file,
                new DocumentProperty(
                    "Code",
                    "V2",
                    "V2",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)),
            CancellationToken.None);

        var method = typeof(SqliteIndexStore).GetMethod(
            "CountSearchCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var result = method!.Invoke(
            store,
            [new SearchQuery("V2", SearchMode.Property), "V2", CancellationToken.None]);
        var count = await Assert.IsAssignableFrom<ValueTask<long>>(result).AsTask();

        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task EmptyTermSearchAndCount_DoNotOpenOrReadSidecar()
    {
        await using var sandbox = new Sandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        var file = CreateFile(@"C:\UdfScope\Empty-List.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(
            CreateMetadata(
                file,
                new DocumentProperty(
                    "Code",
                    "V2",
                    "V2",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)),
            CancellationToken.None);

        var sidecarReads = 0;
        store.AfterSearchReadinessAcceptedForTestingAsync = _ =>
        {
            Interlocked.Increment(ref sidecarReads);
            return ValueTask.CompletedTask;
        };
        try
        {
            await WaitUntilAsync(async () =>
            {
                var hits = await store.SearchAsync(
                    new SearchQuery("V2", SearchMode.Property),
                    CancellationToken.None);
                return hits.Count == 1 && Volatile.Read(ref sidecarReads) > 0;
            });

            var before = Volatile.Read(ref sidecarReads);
            var hits = await store.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All),
                CancellationToken.None);
            Assert.Equal(file.FullPath, Assert.Single(hits).FullPath, ignoreCase: true);
            Assert.Equal(
                1L,
                await store.CountSearchAsync(
                    new SearchQuery(string.Empty, SearchMode.All),
                    CancellationToken.None));
            Assert.Equal(before, Volatile.Read(ref sidecarReads));

            Assert.Equal(
                hits.Count,
                (int)await store.CountSearchAsync(
                    new SearchQuery(string.Empty, SearchMode.All),
                    CancellationToken.None));
        }
        finally
        {
            store.AfterSearchReadinessAcceptedForTestingAsync = null;
        }
    }

    [Fact]
    public async Task CoreSearch_PreservesLinkedAndUnresolvedDisplaySemantics()
    {
        await using var sandbox = new Sandbox();
        await using var store = new SqliteIndexStore(sandbox.CorePath);
        await store.InitializeAsync();
        var file = CreateFile(@"C:\UdfScope\Display-Semantics.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);
        await store.UpsertMetadataAsync(
            CreateMetadata(
                file,
                new DocumentProperty(
                    "LinkedCode",
                    "V2",
                    "$PRP:\"Unresolved\"",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi,
                    DisplayValue: "Linked Display",
                    CachedValue: "Cached",
                    IsLinked: true),
                new DocumentProperty(
                    "UnresolvedName",
                    "$PRP:\"Missing\"",
                    "$PRP:\"Missing\"",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)),
            CancellationToken.None);

        var method = typeof(SqliteIndexStore).GetMethod(
            "SearchCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var linkedResult = method!.Invoke(
            store,
            [new SearchQuery("DISPLAY", SearchMode.Property), "DISPLAY", CancellationToken.None]);
        var linkedHit = Assert.Single(
            await Assert.IsAssignableFrom<ValueTask<IReadOnlyList<SearchHit>>>(linkedResult).AsTask());
        Assert.Equal("LinkedCode", linkedHit.MatchedProperty);
        Assert.Equal("Linked Display", linkedHit.MatchedValue);

        var unresolvedResult = method.Invoke(
            store,
            [
                new SearchQuery("UNRESOLVEDNAME", SearchMode.Property),
                "UNRESOLVEDNAME",
                CancellationToken.None,
            ]);
        var unresolvedHit = Assert.Single(
            await Assert.IsAssignableFrom<ValueTask<IReadOnlyList<SearchHit>>>(unresolvedResult).AsTask());
        Assert.Equal("UnresolvedName", unresolvedHit.MatchedProperty);
        Assert.Null(unresolvedHit.MatchedValue);
    }

    private static async Task CreateEmptyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
    }

    private static async Task<long> ReadBusyTimeoutAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteSwSafeValueAsync(SqliteConnection connection)
        => await ExecuteSwSafeValueAsync(
            connection,
            rawValue: null,
            resolvedValue: "resolved",
            displayValue: "display",
            cachedValue: "cached",
            isLinked: false).ConfigureAwait(false);

    private static async Task<string?> ExecuteSwSafeValueAsync(
        SqliteConnection connection,
        string? rawValue,
        string? resolvedValue,
        string? displayValue,
        string? cachedValue,
        bool isLinked)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sw_safe_value(@raw,@resolved,@display,@cached,@linked);
            """;
        command.Parameters.AddWithValue("@raw", (object?)rawValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@resolved", (object?)resolvedValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@display", (object?)displayValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@cached", (object?)cachedValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@linked", isLinked ? 1 : 0);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null || result is DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Search sidecar did not become ready.");
    }

    private static FileCandidate CreateFile(string path) => new(
        path,
        SolidWorksDocumentKind.Part,
        100,
        new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));

    private static DocumentMetadata CreateMetadata(
        FileCandidate file,
        params DocumentProperty[] properties) => new(
        file,
        properties,
        [],
        MetadataSource.NoApi,
        file.LastWriteTimeUtc);

    private sealed class Sandbox : IAsyncDisposable
    {
        internal Sandbox()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"swsearch-udf-scope-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            CorePath = Path.Combine(RootPath, "core.db");
        }

        private string RootPath { get; }

        internal string CorePath { get; }

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
