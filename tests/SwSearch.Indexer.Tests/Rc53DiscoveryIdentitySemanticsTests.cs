using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc53DiscoveryIdentitySemanticsTests
{
    private const string Root = @"C:\RC53";
    private static readonly DateTimeOffset Modified =
        new(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly FileSystemIdentity IdentityA = new(0xA1B2C3D4, 0x0102030405060708);
    private static readonly FileSystemIdentity IdentityB = new(0xA1B2C3D4, 0x1112131415161718);

    [Fact]
    public async Task FullSnapshot_KnownThenUnknownIdentity_PreservesCompletedStateAndIdentity()
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Known-Then-Unknown.SLDPRT");

        await ApplyFullAsync(store, Entry(file, IdentityA));
        await CompleteWithPropertyAsync(store, file, "RC53-STABLE");

        await ApplyFullAsync(store, Entry(file, identity: null));

        var state = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityA), state.Identity);
        Assert.Equal(0, state.MetadataState);
        Assert.Equal(0, state.QueueCount);
        Assert.Equal(1, state.PropertyCount);
        Assert.Single(await store.SearchAsync(
            new SearchQuery("RC53-STABLE", SearchMode.Property),
            CancellationToken.None));
    }

    [Fact]
    public async Task FullSnapshot_UnknownThenKnownIdentity_BackfillsWithoutRequeue()
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(
            @"C:\RC53\Unknown-Then-Known.SLDASM",
            SolidWorksDocumentKind.Assembly);

        await ApplyFullAsync(store, Entry(file, identity: null));
        await CompleteWithPropertyAsync(store, file, "RC53-BACKFILL");

        await ApplyFullAsync(store, Entry(file, IdentityA));

        var state = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityA), state.Identity);
        Assert.Equal(0, state.MetadataState);
        Assert.Equal(0, state.QueueCount);
        Assert.Equal(1, state.PropertyCount);
    }

    [Fact]
    public async Task FullSnapshot_TwoDifferentKnownIdentities_RequeuesAndKeepsOldProperties()
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Identity-Replaced.SLDPRT");

        await ApplyFullAsync(store, Entry(file, IdentityA));
        await CompleteWithPropertyAsync(store, file, "RC53-OLD-CACHE");
        await InsertDeadLetterAsync(database.CorePath, file.FullPath);

        await ApplyFullAsync(store, Entry(file, IdentityB));

        var state = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityB), state.Identity);
        Assert.Equal(1, state.MetadataState);
        Assert.Equal(1, state.QueueCount);
        Assert.Equal("Modified", state.QueueReason);
        Assert.Equal(0, state.DeadLetterCount);
        Assert.Equal(1, state.PropertyCount);
    }

    [Fact]
    public async Task ThreeUnknownIdentityGenerations_DoNotCreateSearchOrDrawingWork()
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var model = Candidate(@"C:\RC53\Model.SLDPRT");
        var drawing = Candidate(@"C:\RC53\Model.SLDDRW", SolidWorksDocumentKind.Drawing);

        await ApplyFullAsync(store, Entry(model, IdentityA), Entry(drawing, IdentityB));
        await CompleteWithPropertyAsync(store, model, "RC53-INHERITED");
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                drawing,
                [],
                [new DocumentReference(model.FullPath, null, true)],
                MetadataSource.NoApi,
                Modified),
            CancellationToken.None);
        var projectionSequence = await WaitForProjectionIdleAsync(database.CorePath);
        Assert.Equal(1, await ReadScalarAsync(
            database.CorePath,
            "SELECT count(*) FROM effective_properties;"));

        for (var generation = 0; generation < 3; generation++)
        {
            await ApplyFullAsync(store, Entry(model, identity: null), Entry(drawing, identity: null));
        }

        Assert.Equal(projectionSequence, await ReadScalarAsync(
            database.CorePath,
            "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;"));
        Assert.Equal(0, await ReadScalarAsync(database.CorePath, "SELECT count(*) FROM index_queue;"));
        Assert.Equal(0, await ReadScalarAsync(
            database.CorePath,
            "SELECT count(*) FROM files WHERE metadata_state<>0;"));
        Assert.Equal(1, await ReadScalarAsync(
            database.CorePath,
            "SELECT count(*) FROM effective_properties;"));
        var inherited = await store.SearchAsync(
            new SearchQuery("RC53-INHERITED", SearchMode.Property),
            CancellationToken.None);
        Assert.Contains(inherited, hit => hit.FullPath == drawing.FullPath);
    }

    [Theory]
    [InlineData("A", null, "A", false)]
    [InlineData(null, "A", "A", false)]
    [InlineData("A", "B", "B", true)]
    public async Task Hint_UsesThreeValuedIdentitySemantics(
        string? storedIdentityToken,
        string? hintIdentityToken,
        string expectedIdentityToken,
        bool expectedRequeue)
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Hint.SLDPRT");
        var storedIdentity = ParseIdentity(storedIdentityToken);
        var hintIdentity = ParseIdentity(hintIdentityToken);

        await ApplyFullAsync(store, Entry(file, storedIdentity));
        await CompleteWithPropertyAsync(store, file, "RC53-HINT-CACHE");
        var projectionSequence = await WaitForProjectionIdleAsync(database.CorePath);
        var searchVersion = store.SearchContentVersion;

        await store.ApplyDiscoveryHintAsync(Entry(file, hintIdentity), CancellationToken.None);

        var state = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(ParseIdentity(expectedIdentityToken)!), state.Identity);
        Assert.Equal(expectedRequeue ? 1 : 0, state.MetadataState);
        Assert.Equal(expectedRequeue ? 1 : 0, state.QueueCount);
        Assert.Equal(1, state.PropertyCount);
        if (expectedRequeue)
        {
            Assert.Equal("Modified", state.QueueReason);
            Assert.True(store.SearchContentVersion > searchVersion);
            Assert.True(await ReadScalarAsync(
                database.CorePath,
                "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;") > projectionSequence);
        }
        else
        {
            Assert.Equal(searchVersion, store.SearchContentVersion);
            Assert.Equal(projectionSequence, await ReadScalarAsync(
                database.CorePath,
                "SELECT current_sequence FROM search_projection_state WHERE singleton_id=1;"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityBackfill_MigratesUnleasedPendingFingerprintAndLeaseCommits(bool useHint)
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Pending-Backfill.SLDPRT");
        await ApplyFullAsync(store, Entry(file, identity: null));

        await ApplyIdentityBackfillAsync(store, file, useHint);

        var migrated = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityA), migrated.Identity);
        Assert.Equal(Fingerprint(file, IdentityA), migrated.QueueFingerprint);
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            $"rc53-pending-{useHint}"));
        Assert.Equal(Fingerprint(file, IdentityA), lease.Fingerprint);

        await CommitLeaseAsync(store, lease, file, IdentityA, "RC53-PENDING-COMMITTED");

        var completed = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(0, completed.MetadataState);
        Assert.Equal(0, completed.QueueCount);
        Assert.Equal(1, completed.PropertyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityBackfill_DefersForActiveLeaseAndExistingLeaseCommits(bool useHint)
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Leased-Backfill.SLDPRT");
        await ApplyFullAsync(store, Entry(file, identity: null));
        var lease = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            $"rc53-leased-{useHint}"));
        Assert.Equal(Fingerprint(file, identity: null), lease.Fingerprint);

        await ApplyIdentityBackfillAsync(store, file, useHint);

        var deferred = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Null(deferred.Identity);
        Assert.Equal(lease.Fingerprint, deferred.QueueFingerprint);
        Assert.Equal(lease.LeaseOwner, deferred.LeaseOwner);

        await CommitLeaseAsync(store, lease, file, IdentityA, "RC53-LEASED-COMMITTED");

        var completed = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityA), completed.Identity);
        Assert.Equal(0, completed.MetadataState);
        Assert.Equal(0, completed.QueueCount);
        Assert.Equal(1, completed.PropertyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityBackfill_MigratesDeadLetterWithoutRevivingWork(bool useHint)
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Dead-Letter-Backfill.SLDPRT");
        await ApplyFullAsync(store, Entry(file, identity: null));
        await FailToDeadLetterAsync(store);

        await ApplyIdentityBackfillAsync(store, file, useHint);

        var migrated = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityA), migrated.Identity);
        Assert.Equal(3, migrated.MetadataState);
        Assert.Equal(0, migrated.QueueCount);
        Assert.Equal(1, migrated.DeadLetterCount);
        Assert.Equal(Fingerprint(file, IdentityA), migrated.DeadLetterFingerprint);
        await store.EnqueueMetadataAsync(
            file.FullPath,
            "Historical",
            dueUtc: null,
            cancellationToken: CancellationToken.None);
        Assert.Equal(0, (await ReadFileStateAsync(database.CorePath, file.FullPath)).QueueCount);
    }

    [Fact]
    public async Task FullSnapshot_KnownIdentityRename_RefreshesReferencingDrawingPath()
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var model = Candidate(@"C:\RC53\Before-Rename.SLDPRT");
        var renamed = model with { FullPath = @"C:\RC53\After-Rename.SLDPRT" };
        var drawing = Candidate(@"C:\RC53\Rename.SLDDRW", SolidWorksDocumentKind.Drawing);
        await ApplyFullAsync(store, Entry(model, IdentityA), Entry(drawing, IdentityB));
        await CompleteWithPropertyAsync(store, model, "RC53-RENAMED-MODEL");
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                drawing,
                [],
                [new DocumentReference(model.FullPath, null, true)],
                MetadataSource.NoApi,
                Modified),
            CancellationToken.None);

        await ApplyFullAsync(store, Entry(renamed, IdentityA), Entry(drawing, IdentityB));

        var renamedState = await ReadFileStateAsync(database.CorePath, renamed.FullPath);
        Assert.Equal(IdentityText(IdentityA), renamedState.Identity);
        Assert.Equal("Renamed", renamedState.QueueReason);
        Assert.Equal(0, renamedState.MetadataState);
        Assert.Equal(1, await ReadScalarAsync(
            database.CorePath,
            "SELECT count(*) FROM effective_properties;"));
        Assert.Equal(renamed.FullPath, await ReadTextAsync(
            database.CorePath,
            "SELECT target_path FROM document_references;"));
    }

    [Fact]
    public async Task DuplicateStage_KnownThenUnknownIdentity_KeepsKnownEvidence()
    {
        await using var database = new Rc53TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.CorePath);
        await store.InitializeAsync();
        var file = Candidate(@"C:\RC53\Duplicate-Stage.SLDPRT");

        await ApplyFullAsync(store, Entry(file, IdentityA), Entry(file, identity: null));

        var state = await ReadFileStateAsync(database.CorePath, file.FullPath);
        Assert.Equal(IdentityText(IdentityA), state.Identity);
        Assert.Equal(Fingerprint(file, IdentityA), state.QueueFingerprint);
        Assert.Equal(1, await ReadScalarAsync(database.CorePath, "SELECT count(*) FROM files;"));
    }

    private static FileCandidate Candidate(
        string path,
        SolidWorksDocumentKind kind = SolidWorksDocumentKind.Part) =>
        new(path, kind, 128, Modified);

    private static DiscoveredFile Entry(FileCandidate candidate, FileSystemIdentity? identity) =>
        new(candidate, DiscoveryPath.NormalizeKey(candidate.FullPath), Root, identity);

    private static FileSystemIdentity? ParseIdentity(string? token) => token switch
    {
        null => null,
        "A" => IdentityA,
        "B" => IdentityB,
        _ => throw new ArgumentOutOfRangeException(nameof(token)),
    };

    private static string IdentityText(FileSystemIdentity identity) =>
        $"{identity.VolumeSerialNumber:X8}:{identity.FileIndex:X16}";

    private static string Fingerprint(FileCandidate file, FileSystemIdentity? identity) =>
        $"{file.Size}:{file.LastWriteTimeUtc.UtcTicks}:{(identity is null ? string.Empty : IdentityText(identity))}:{ExtractorVersions.Current}";

    private static ValueTask ApplyIdentityBackfillAsync(
        SqliteIndexStore store,
        FileCandidate file,
        bool useHint) =>
        useHint
            ? store.ApplyDiscoveryHintAsync(Entry(file, IdentityA), CancellationToken.None)
            : store.ApplyDiscoverySnapshotAsync(
                new Rc53SnapshotProvider([Entry(file, IdentityA)]),
                "rc53-backfill",
                CancellationToken.None);

    private static async Task ApplyFullAsync(
        SqliteIndexStore store,
        params DiscoveredFile[] entries) =>
        await store.ApplyDiscoverySnapshotAsync(
            new Rc53SnapshotProvider(entries),
            "rc53-identity",
            CancellationToken.None);

    private static async Task CommitLeaseAsync(
        SqliteIndexStore store,
        MetadataWorkLease lease,
        FileCandidate file,
        FileSystemIdentity identity,
        string value)
    {
        var metadata = MetadataWithProperty(file, value);
        var evidence = new StableFileEvidence(
            file.FullPath,
            file.FullPath,
            file.Kind,
            file.Size,
            file.LastWriteTimeUtc,
            IdentityText(identity));
        var committed = await store.TryCommitMetadataLeaseBatchAsync(
            [new MetadataCommitItem(lease, metadata, evidence)],
            CancellationToken.None);
        Assert.Contains(lease.FileId, committed);
    }

    private static async Task FailToDeadLetterAsync(SqliteIndexStore store)
    {
        var first = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "rc53-dead-first"));
        await store.CompleteMetadataLeaseAsync(
            first.FileId,
            first.LeaseOwner,
            succeeded: false,
            "RC53_FAILURE",
            TimeSpan.Zero);
        var second = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(30),
            "rc53-dead-second"));
        await store.CompleteMetadataLeaseAsync(
            second.FileId,
            second.LeaseOwner,
            succeeded: false,
            "RC53_FAILURE",
            TimeSpan.Zero);
    }

    private static async Task CompleteWithPropertyAsync(
        SqliteIndexStore store,
        FileCandidate file,
        string value) =>
        await store.UpsertMetadataAsync(MetadataWithProperty(file, value), CancellationToken.None);

    private static DocumentMetadata MetadataWithProperty(FileCandidate file, string value) =>
        new(
            file,
            [
                new DocumentProperty(
                    "RC53",
                    value,
                    value,
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi),
            ],
            [],
            MetadataSource.NoApi,
            Modified);

    private static async Task InsertDeadLetterAsync(string databasePath, string fullPath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dead_letter(
                file_id,fingerprint,extractor_version,failure_count,error_code,failed_utc)
            SELECT id,'rc53-old',@extractor,2,'RC53_OLD',@now
              FROM files WHERE path_norm=@path;
            """;
        command.Parameters.AddWithValue("@extractor", ExtractorVersions.Current);
        command.Parameters.AddWithValue("@now", Modified.UtcTicks);
        command.Parameters.AddWithValue("@path", DiscoveryPath.NormalizeKey(fullPath));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<FileState> ReadFileStateAsync(string databasePath, string fullPath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.file_identity,f.metadata_state,
                   (SELECT count(*) FROM index_queue q WHERE q.file_id=f.id),
                   (SELECT reason FROM index_queue q WHERE q.file_id=f.id),
                   (SELECT count(*) FROM dead_letter d WHERE d.file_id=f.id),
                   (SELECT count(*) FROM properties p WHERE p.file_id=f.id),
                   (SELECT fingerprint FROM index_queue q WHERE q.file_id=f.id),
                   (SELECT fingerprint FROM dead_letter d WHERE d.file_id=f.id),
                   (SELECT lease_owner FROM index_queue q WHERE q.file_id=f.id)
              FROM files f WHERE f.path_norm=@path;
            """;
        command.Parameters.AddWithValue("@path", DiscoveryPath.NormalizeKey(fullPath));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new FileState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static async Task<long> WaitForProjectionIdleAsync(string databasePath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = await OpenAsync(databasePath);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT current_sequence,rebuild_required,
                       (SELECT count(*) FROM search_projection_outbox)
                  FROM search_projection_state WHERE singleton_id=1;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() && reader.GetInt32(1) == 0 && reader.GetInt32(2) == 0)
            {
                return reader.GetInt64(0);
            }
            await Task.Delay(20);
        }

        throw new TimeoutException("RC53 search projection did not become idle.");
    }

    private static async Task<long> ReadScalarAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadTextAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
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

    private sealed record FileState(
        string? Identity,
        int MetadataState,
        int QueueCount,
        string? QueueReason,
        int DeadLetterCount,
        int PropertyCount,
        string? QueueFingerprint,
        string? DeadLetterFingerprint,
        string? LeaseOwner);

    private sealed class Rc53SnapshotProvider(IReadOnlyList<DiscoveredFile> entries)
        : IIncrementalFileDiscoveryProvider
    {
        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(1);

        public DiscoveryScanReport LastReport { get; } = new(
            Modified,
            Modified,
            IsCompleteForDeletion: true,
            [
                new DiscoveryRootReport(
                    Root,
                    DiscoveryRootKind.ExplicitLocal,
                    WasOnline: true,
                    FullyEnumerated: true,
                    VisitedEntries: entries.Count,
                    SolidWorksFiles: entries.Count,
                    ReparsePointsSkipped: 0,
                    OfflineFilesSkipped: 0,
                    new Dictionary<string, int>()),
            ]);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC53_FIXTURE",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry.Candidate;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DiscoveredFile?>(
                entries.FirstOrDefault(entry =>
                    entry.Candidate.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed class Rc53TemporaryDatabase : IAsyncDisposable
    {
        internal Rc53TemporaryDatabase()
        {
            CorePath = Path.Combine(Path.GetTempPath(), $"swsearch-rc53-{Guid.NewGuid():N}.db");
            SearchPath = Path.Combine(
                Path.GetDirectoryName(CorePath)!,
                Path.GetFileNameWithoutExtension(CorePath) + ".search.db");
        }

        internal string CorePath { get; }

        internal string SearchPath { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { CorePath, SearchPath })
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var file = path + suffix;
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
