using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc50SandboxCommitAndProgressTests
{
    [Fact]
    public async Task InteractiveCommit_UnchangedDbBaselineAllowsStableWorkerV2ToReplaceV1()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        var candidateV1 = Candidate(@"C:\RC50Sandbox\fresh-v2.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([candidateV1]),
            CancellationToken.None);
        var attempt = await store.BeginInteractiveMetadataAttemptAsync(
            candidateV1.FullPath,
            Guid.NewGuid());
        var candidateV2 = candidateV1 with
        {
            Size = candidateV1.Size + 17,
            LastWriteTimeUtc = candidateV1.LastWriteTimeUtc.AddMinutes(2),
        };
        var stableWorkerV2 = Evidence(candidateV2);

        Assert.True(await store.TryCommitInteractiveMetadataAsync(
            Metadata(candidateV2, "FRESH-V2"),
            stableWorkerV2,
            stableWorkerV2,
            attempt));

        var persisted = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(candidateV1.FullPath, CancellationToken.None));
        Assert.Equal(candidateV2.Size, persisted.File.Size);
        Assert.Equal(
            candidateV2.LastWriteTimeUtc.UtcTicks,
            persisted.File.LastWriteTimeUtc.UtcTicks);
        Assert.Equal("FRESH-V2", Assert.Single(persisted.Properties).ResolvedValue);
    }

    [Fact]
    public async Task InteractiveCommit_LatestAttemptRejectsLateCompletionFromAttemptA()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        var candidate = Candidate(@"C:\RC50Sandbox\latest-attempt.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([candidate]),
            CancellationToken.None);

        var evidence = Evidence(candidate);
        var attemptA = await store.BeginInteractiveMetadataAttemptAsync(
            candidate.FullPath,
            Guid.NewGuid());
        var attemptB = await store.BeginInteractiveMetadataAttemptAsync(
            candidate.FullPath,
            Guid.NewGuid());

        Assert.True(await store.TryCommitInteractiveMetadataAsync(
            Metadata(candidate, "B"),
            evidence,
            evidence,
            attemptB));
        Assert.False(await store.TryCommitInteractiveMetadataAsync(
            Metadata(candidate, "A"),
            evidence,
            evidence,
            attemptA));

        var persisted = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(candidate.FullPath, CancellationToken.None));
        Assert.Equal("B", Assert.Single(persisted.Properties).ResolvedValue);
    }

    [Fact]
    public async Task InteractiveCommit_BaselineV2AndAttemptBRejectLateV1AttemptA()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        var candidate = Candidate(@"C:\RC50Sandbox\changed-baseline.SLDPRT");
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([candidate]),
            CancellationToken.None);
        var evidenceV1 = Evidence(candidate);
        var attemptA = await store.BeginInteractiveMetadataAttemptAsync(
            candidate.FullPath,
            Guid.NewGuid());
        var candidateV2 = candidate with
        {
            Size = candidate.Size + 1,
            LastWriteTimeUtc = candidate.LastWriteTimeUtc.AddMinutes(1),
        };

        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE files
                   SET size=@size,modified_utc=@modified
                 WHERE path=@path;
                """;
            update.Parameters.AddWithValue("@path", candidate.FullPath);
            update.Parameters.AddWithValue("@size", candidateV2.Size);
            update.Parameters.AddWithValue("@modified", candidateV2.LastWriteTimeUtc.UtcTicks);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var attemptB = await store.BeginInteractiveMetadataAttemptAsync(
            candidate.FullPath,
            Guid.NewGuid());
        var evidenceV2 = Evidence(candidateV2);
        Assert.True(await store.TryCommitInteractiveMetadataAsync(
            Metadata(candidateV2, "V2"),
            evidenceV2,
            evidenceV2,
            attemptB));
        Assert.False(await store.TryCommitInteractiveMetadataAsync(
            Metadata(candidate, "STALE-V1"),
            evidenceV1,
            evidenceV1,
            attemptA));

        var persisted = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(candidate.FullPath, CancellationToken.None));
        Assert.Equal(candidateV2.Size, persisted.File.Size);
        Assert.Equal("V2", Assert.Single(persisted.Properties).ResolvedValue);
    }

    [Fact]
    public async Task CurrentExtractorTerminalProgress_PreservesHistoricalCacheAndCountsTerminalRowsAsSearchable()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO files(
                    path,path_norm,kind,size,modified_utc,discovery_generation,
                    state,metadata_state,extractor_version,extracted_utc,error_code)
                VALUES
                    ('C:\RC50Sandbox\old-1.SLDPRT','C:\RC50SANDBOX\OLD-1.SLDPRT',
                     @kind,1,@modified,1,0,0,@old,@extracted,NULL),
                    ('C:\RC50Sandbox\old-2.SLDPRT','C:\RC50SANDBOX\OLD-2.SLDPRT',
                     @kind,1,@modified,1,0,0,@old,@extracted,NULL),
                    ('C:\RC50Sandbox\current.SLDPRT','C:\RC50SANDBOX\CURRENT.SLDPRT',
                     @kind,1,@modified,1,0,0,@current,@extracted,NULL),
                    ('C:\RC50Sandbox\failed.SLDPRT','C:\RC50SANDBOX\FAILED.SLDPRT',
                     @kind,1,@modified,1,0,3,@current,NULL,'TEST_TERMINAL_FAILURE'),
                    ('C:\RC50Sandbox\pending.SLDPRT','C:\RC50SANDBOX\PENDING.SLDPRT',
                     @kind,1,@modified,1,0,1,@current,NULL,NULL);
                """;
            seed.Parameters.AddWithValue("@kind", (int)SolidWorksDocumentKind.Part);
            seed.Parameters.AddWithValue(
                "@modified",
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero).UtcTicks);
            seed.Parameters.AddWithValue(
                "@extracted",
                new DateTimeOffset(2026, 7, 25, 0, 1, 0, TimeSpan.Zero).UtcTicks);
            seed.Parameters.AddWithValue("@old", ExtractorVersions.Current - 1);
            seed.Parameters.AddWithValue("@current", ExtractorVersions.Current);
            Assert.Equal(5, await seed.ExecuteNonQueryAsync());
        }

        var progress = await store.GetMetadataProgressAsync();

        Assert.Equal(5, progress.DiscoveredFileCount);
        Assert.Equal(1, progress.CompletedCount);
        Assert.Equal(0, progress.PartialCount);
        Assert.Equal(1, progress.FailedCount);
        Assert.Equal(3, progress.PendingCount);
        Assert.Equal(2, progress.ProcessedCount);
        Assert.Equal(4, progress.CachedReadableFileCount);
        Assert.True(progress.CachedReadableFileCount.GetValueOrDefault() >= progress.ProcessedCount);
        Assert.Equal(progress.DiscoveredFileCount, progress.ProcessedCount + progress.PendingCount);
    }

    [Fact]
    public async Task TerminalMissingAndUnreadableRows_CloseTheActiveBatchAndRemainSearchableCacheCoverage()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO files(
                    path,path_norm,kind,size,modified_utc,discovery_generation,
                    state,metadata_state,extractor_version,extracted_utc,error_code)
                VALUES
                    ('C:\RC50Sandbox\complete.SLDPRT','C:\RC50SANDBOX\COMPLETE.SLDPRT',
                     @kind,1,@modified,1,0,0,@current,@extracted,NULL),
                    ('C:\RC50Sandbox\unreadable.SLDPRT','C:\RC50SANDBOX\UNREADABLE.SLDPRT',
                     @kind,1,@modified,1,0,3,@current,NULL,'FILE_UNREADABLE'),
                    ('C:\RC50Sandbox\missing.SLDPRT','C:\RC50SANDBOX\MISSING.SLDPRT',
                     @kind,1,@modified,1,0,3,@current,NULL,'FILE_NOT_FOUND'),
                    ('C:\RC50Sandbox\pending.SLDPRT','C:\RC50SANDBOX\PENDING.SLDPRT',
                     @kind,1,@modified,1,0,1,@current,NULL,NULL);
                """;
            seed.Parameters.AddWithValue("@kind", (int)SolidWorksDocumentKind.Part);
            seed.Parameters.AddWithValue(
                "@modified",
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero).UtcTicks);
            seed.Parameters.AddWithValue(
                "@extracted",
                new DateTimeOffset(2026, 7, 25, 0, 1, 0, TimeSpan.Zero).UtcTicks);
            seed.Parameters.AddWithValue("@current", ExtractorVersions.Current);
            Assert.Equal(4, await seed.ExecuteNonQueryAsync());
        }

        var unsettled = await store.GetMetadataProgressAsync();

        Assert.Equal(4, unsettled.DiscoveredFileCount);
        Assert.Equal(1, unsettled.CompletedCount);
        Assert.Equal(1, unsettled.FailedCount);
        Assert.Equal(1, unsettled.SkippedCount);
        Assert.Equal(1, unsettled.PendingCount);
        Assert.Equal(3, unsettled.ProcessedCount);
        Assert.Equal(3, unsettled.CachedReadableFileCount);
        Assert.Equal(unsettled.DiscoveredFileCount, unsettled.ProcessedCount + unsettled.PendingCount);

        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var settle = connection.CreateCommand();
            settle.CommandText = """
                UPDATE files
                   SET metadata_state=3,
                       error_code='CLOUD_PLACEHOLDER_RETRY_EXHAUSTED'
                 WHERE path_norm='C:\RC50SANDBOX\PENDING.SLDPRT';
                """;
            Assert.Equal(1, await settle.ExecuteNonQueryAsync());
        }

        var settled = await store.GetMetadataProgressAsync();

        Assert.Equal(0, settled.PendingCount);
        Assert.Equal(4, settled.ProcessedCount);
        Assert.Equal(4, settled.CachedReadableFileCount);
        Assert.Equal(settled.DiscoveredFileCount, settled.ProcessedCount);
    }

    private static FileCandidate Candidate(string path) =>
        new(
            path,
            SolidWorksDocumentKind.Part,
            64,
            new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero));

    private static StableFileEvidence Evidence(FileCandidate candidate) =>
        new(
            candidate.FullPath,
            candidate.FullPath,
            candidate.Kind,
            candidate.Size,
            candidate.LastWriteTimeUtc,
            FileIdentity: string.Empty);

    private static DocumentMetadata Metadata(FileCandidate candidate, string value) =>
        new(
            candidate,
            [
                new DocumentProperty(
                    "Code",
                    value,
                    value,
                    PropertyScope.Document,
                    ConfigurationName: null,
                    MetadataSource.NoApi),
            ],
            [],
            MetadataSource.NoApi,
            new DateTimeOffset(2026, 7, 25, 8, 1, 0, TimeSpan.Zero));
}
