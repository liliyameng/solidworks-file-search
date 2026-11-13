using Microsoft.Data.Sqlite;
using SwSearch.Contracts;
using System.Runtime.CompilerServices;

namespace SwSearch.Indexer.Tests;

public sealed class Rc46BackendQueueTests
{
    [Fact]
    public void CloudPlaceholderFlags_AreDeferredWithoutTreatingNormalFilesAsOffline()
    {
        Assert.True(IndexingRuntime.IsCloudPlaceholderAttributes(FileAttributes.Offline));
        Assert.True(IndexingRuntime.IsCloudPlaceholderAttributes((FileAttributes)0x0004_0000));
        Assert.True(IndexingRuntime.IsCloudPlaceholderAttributes((FileAttributes)0x0040_0000));
        Assert.False(IndexingRuntime.IsCloudPlaceholderAttributes(FileAttributes.Archive));
        Assert.Equal(
            IndexingRuntime.BackgroundFileDisposition.CloudPlaceholder,
            IndexingRuntime.ClassifyBackgroundFileAttributes(FileAttributes.ReparsePoint | FileAttributes.Offline));
        Assert.Equal(
            IndexingRuntime.BackgroundFileDisposition.ReparsePoint,
            IndexingRuntime.ClassifyBackgroundFileAttributes(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void CloudPlaceholder_IsTerminalOnItsFirstConfirmedBackgroundAttempt()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), IndexingRuntime.CloudPlaceholderRetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(10), IndexingRuntime.CloudPlaceholderRetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(20), IndexingRuntime.CloudPlaceholderRetryDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(40), IndexingRuntime.CloudPlaceholderRetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(60), IndexingRuntime.CloudPlaceholderRetryDelay(5));
        Assert.Equal(TimeSpan.FromMinutes(60), IndexingRuntime.CloudPlaceholderRetryDelay(99));
        Assert.Equal(1, IndexingRuntime.MaximumCloudPlaceholderDeferrals);
        Assert.True(IndexingRuntime.IsCloudPlaceholderRetryExhausted(
            IndexingRuntime.MaximumCloudPlaceholderDeferrals));
    }

    [Fact]
    public async Task CloudPlaceholderDeferredLease_PreservesAttemptForBackoff()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var candidate = new FileCandidate(
            @"C:\RC46\Cloud\placeholder.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);

        var first = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(10),
            "cloud-defer-first"));
        Assert.Equal(1, first.Attempt);
        await store.DeferMetadataLeaseAsync(
            first.FileId,
            first.LeaseOwner,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            "CLOUD_PLACEHOLDER_DEFERRED");

        var second = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromSeconds(10),
            "cloud-defer-second"));
        Assert.Equal(2, second.Attempt);
        await store.CompleteMetadataLeaseAsync(second.FileId, second.LeaseOwner, succeeded: true);
    }

    [Fact]
    public async Task ExhaustedCloudPlaceholder_IsRecordedTerminalInsteadOfRemainingPendingForever()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var candidate = new FileCandidate(
            @"C:\RC46\Cloud\retired-sync-conflict.SLDASM",
            SolidWorksDocumentKind.Assembly,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);

        MetadataWorkLease? exhaustedLease = null;
        for (var attempt = 1; attempt <= IndexingRuntime.MaximumCloudPlaceholderDeferrals; attempt++)
        {
            var lease = Assert.Single(await store.LeaseMetadataAsync(
                1,
                TimeSpan.FromSeconds(10),
                $"cloud-exhaust-{attempt}"));
            Assert.Equal(attempt, lease.Attempt);

            if (attempt == IndexingRuntime.MaximumCloudPlaceholderDeferrals)
            {
                exhaustedLease = lease;
                break;
            }

            await store.DeferMetadataLeaseAsync(
                lease.FileId,
                lease.LeaseOwner,
                DateTimeOffset.UtcNow.AddSeconds(-1),
                IndexingRuntime.CloudPlaceholderDeferredErrorCode);
        }

        var terminal = Assert.IsType<MetadataWorkLease>(exhaustedLease);
        await store.FinalizeDeferredMetadataLeaseAsync(
            terminal.FileId,
            terminal.LeaseOwner,
            IndexingRuntime.CloudPlaceholderRetryExhaustedErrorCode);

        var progress = await store.GetMetadataProgressAsync();
        // An exhausted cloud placeholder is settled work, mirroring the
        // FILE_NOT_FOUND terminal outcome below: Skipped, counted as
        // readable-cache coverage (its filename row stays searchable), and
        // never pending — otherwise both user-visible percentages freeze
        // below 100% for files that cannot be read while cloud-only.
        Assert.Equal(0, progress.FailedCount);
        Assert.Equal(1, progress.SkippedCount);
        Assert.Equal(1, progress.CachedReadableFileCount);
        Assert.Equal(0, progress.PendingCount);
        Assert.Equal(1, progress.ProcessedCount);
        Assert.Equal(1, (await store.GetStatusAsync()).PendingMetadataCount);
        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "cloud-exhaust-after"));
    }

    [Fact]
    public async Task RediscoveredFileNotFound_ReopensEvenWhenItsFingerprintDidNotChange()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var candidate = new FileCandidate(
            @"C:\RC46\reappeared-same-fingerprint.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);

        var first = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "missing-first"));
        await store.CompleteMetadataLeaseAsync(
            first.FileId,
            first.LeaseOwner,
            succeeded: false,
            errorCode: "FILE_NOT_FOUND",
            retryDelay: TimeSpan.Zero);
        var second = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "missing-second"));
        await store.CompleteMetadataLeaseAsync(
            second.FileId,
            second.LeaseOwner,
            succeeded: false,
            errorCode: "FILE_NOT_FOUND");

        var terminal = await store.GetMetadataProgressAsync();
        Assert.Equal(0, terminal.PendingCount);
        Assert.Equal(0, terminal.FailedCount);
        Assert.Equal(1, terminal.SkippedCount);
        Assert.Equal(1, terminal.CachedReadableFileCount);
        Assert.Equal(terminal.DiscoveredFileCount, terminal.ProcessedCount);

        // The same unchanged fingerprint is deliberately used again: discovery,
        // not a changed timestamp, is the proof that this missing file returned.
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);
        var reopened = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "missing-returned"));
        Assert.Equal(candidate.FullPath, reopened.FullPath);
        Assert.Equal(1, (await store.GetMetadataProgressAsync()).PendingCount);
    }

    [Fact]
    public async Task Rediscovery_DoesNotReopenNonFileNotFoundDeadLetter()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var candidate = new FileCandidate(
            @"C:\RC46\terminal-parser-error.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);

        var first = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "parser-first"));
        await store.CompleteMetadataLeaseAsync(
            first.FileId,
            first.LeaseOwner,
            succeeded: false,
            errorCode: "TEST_PARSER_FAILURE",
            retryDelay: TimeSpan.Zero);
        var second = Assert.Single(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "parser-second"));
        await store.CompleteMetadataLeaseAsync(
            second.FileId,
            second.LeaseOwner,
            succeeded: false,
            errorCode: "TEST_PARSER_FAILURE");

        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);
        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(10), "parser-unchanged"));
        var terminal = await store.GetMetadataProgressAsync();
        Assert.Equal(1, terminal.FailedCount);
        Assert.Equal(0, terminal.SkippedCount);
    }

    [Fact]
    public async Task StartupRecovery_FinalizesOnlyUnleasedExhaustedCloudPlaceholders()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var abandoned = new FileCandidate(
            @"C:\RC46\Cloud\abandoned-placeholder.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            now);
        var owned = new FileCandidate(
            @"C:\RC46\Cloud\active-placeholder.SLDPRT",
            SolidWorksDocumentKind.Part,
            2,
            now);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([abandoned, owned]), CancellationToken.None);

        for (var attempt = 1; attempt <= IndexingRuntime.MaximumCloudPlaceholderDeferrals; attempt++)
        {
            var lease = Assert.IsType<MetadataWorkLease>(await store.TryLeaseMetadataPathAsync(
                abandoned.FullPath,
                TimeSpan.FromSeconds(10),
                $"abandoned-{attempt}"));
            await store.DeferMetadataLeaseAsync(
                lease.FileId,
                lease.LeaseOwner,
                attempt == IndexingRuntime.MaximumCloudPlaceholderDeferrals
                    ? DateTimeOffset.UtcNow.AddHours(1)
                    : DateTimeOffset.UtcNow.AddSeconds(-1),
                IndexingRuntime.CloudPlaceholderDeferredErrorCode);
        }

        for (var attempt = 1; attempt < IndexingRuntime.MaximumCloudPlaceholderDeferrals; attempt++)
        {
            var lease = Assert.IsType<MetadataWorkLease>(await store.TryLeaseMetadataPathAsync(
                owned.FullPath,
                TimeSpan.FromSeconds(10),
                $"owned-{attempt}"));
            await store.DeferMetadataLeaseAsync(
                lease.FileId,
                lease.LeaseOwner,
                DateTimeOffset.UtcNow.AddSeconds(-1),
                IndexingRuntime.CloudPlaceholderDeferredErrorCode);
        }

        var activeLease = Assert.IsType<MetadataWorkLease>(await store.TryLeaseMetadataPathAsync(
            owned.FullPath,
            TimeSpan.FromSeconds(10),
            "owned-active"));
        Assert.Equal(IndexingRuntime.MaximumCloudPlaceholderDeferrals, activeLease.Attempt);

        var finalized = await store.FinalizeExhaustedDeferredMetadataAsync(
            IndexingRuntime.CloudPlaceholderDeferredErrorCode,
            IndexingRuntime.MaximumCloudPlaceholderDeferrals,
            IndexingRuntime.CloudPlaceholderRetryExhaustedErrorCode);

        Assert.Equal(1, finalized);
        var progress = await store.GetMetadataProgressAsync();
        // The abandoned placeholder is finalized terminal (Skipped); the
        // actively-leased one is still unsettled work (Pending) until its
        // own lease completes.
        Assert.Equal(0, progress.FailedCount);
        Assert.Equal(1, progress.SkippedCount);
        Assert.Equal(1, progress.PendingCount);
        await store.FinalizeDeferredMetadataLeaseAsync(
            activeLease.FileId,
            activeLease.LeaseOwner,
            IndexingRuntime.CloudPlaceholderRetryExhaustedErrorCode);
    }

    [Fact]
    public async Task BackgroundPureReparsePoint_IsClassifiedByWorkerAndDoesNotRequeue()
    {
        await using var database = new TemporaryDatabase();
        var path = database.Path + ".SLDPRT";
        await File.WriteAllTextAsync(path, "fixture");
        try
        {
            var info = new FileInfo(path);
            var candidate = new FileCandidate(
                path,
                SolidWorksDocumentKind.Part,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            var store = new SqliteIndexStore(database.Path);
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);
            var metadata = new CountingMetadataClient(request => new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                Metadata: null,
                ErrorCode: "METADATA_SKIPPED_REPARSE_POINT",
                ChineseErrorMessage: "reparse point",
                Retryable: false,
                PipelineLeaseId: request.PipelineLeaseId,
                WorkerGenerationNonce: Guid.NewGuid(),
                Outcome: MetadataPipelineOutcome.Unsupported,
                Disposition: MetadataFileDisposition.ReparsePoint));
            await using var runtime = new IndexingRuntime(
                store,
                new NoChangeJournalDiscovery(new DiscoveryJournalCheckpoint("test", [], DateTimeOffset.UtcNow)),
                metadata);
            var brokerFileReadCalls = 0;
            runtime.BackgroundFileAttributesReaderForTesting = _ =>
            {
                Interlocked.Increment(ref brokerFileReadCalls);
                return FileAttributes.ReparsePoint;
            };

            Assert.Equal(0, await runtime.ProcessPendingMetadataAsync(1));
            Assert.Equal(0, await runtime.ProcessPendingMetadataAsync(1));

            Assert.Equal(1, metadata.ExtractCalls);
            Assert.Equal(0, brokerFileReadCalls);
            Assert.Equal(0, (await store.GetStatusAsync()).PendingMetadataCount);
            var progress = await store.GetMetadataProgressAsync();
            Assert.Equal(1, progress.PartialCount);
            Assert.Equal(0, progress.PendingCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task InteractiveRefresh_SymlinkOutsideRequestedPathIsRejectedAndCacheRemains()
    {
        await using var database = new TemporaryDatabase();
        var link = database.Path + ".SLDPRT";
        var outsideDirectory = database.Path + ".outside";
        Directory.CreateDirectory(outsideDirectory);
        var target = Path.Combine(outsideDirectory, "target.SLDPRT");
        await File.WriteAllTextAsync(target, "outside-target");
        await File.WriteAllTextAsync(link, "requested-path");
        try
        {
            var info = new FileInfo(link);
            var candidate = new FileCandidate(
                link,
                SolidWorksDocumentKind.Part,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            var store = new SqliteIndexStore(database.Path);
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(
                candidate,
                [new DocumentProperty("Code", "OLD", "OLD", PropertyScope.Document, null, MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow), CancellationToken.None);
            var targetInfo = new FileInfo(target);
            var redirectedEvidence = new MetadataFileEvidence(
                Path.GetFullPath(link),
                Path.GetFullPath(target),
                SolidWorksDocumentKind.Part,
                targetInfo.Length,
                new DateTimeOffset(targetInfo.LastWriteTimeUtc, TimeSpan.Zero),
                "00000001:0000000000000001");
            var metadata = new CountingMetadataClient(request => new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                new DocumentMetadata(
                    candidate,
                    [new DocumentProperty("Code", "NEW", "NEW", PropertyScope.Document, null, MetadataSource.NoApi)],
                    [],
                    MetadataSource.NoApi,
                    DateTimeOffset.UtcNow),
                "INTERACTIVE_REPARSE_REDIRECT_REJECTED",
                "redirected reparse point",
                Retryable: false,
                PipelineLeaseId: request.PipelineLeaseId,
                WorkerGenerationNonce: Guid.NewGuid(),
                Outcome: MetadataPipelineOutcome.Unsupported,
                Disposition: MetadataFileDisposition.ReparsePoint,
                BeforeEvidence: redirectedEvidence,
                AfterEvidence: redirectedEvidence));
            await using var runtime = new IndexingRuntime(
                store,
                new NoChangeJournalDiscovery(new DiscoveryJournalCheckpoint("test", [], DateTimeOffset.UtcNow)),
                metadata);
            var brokerEvidenceReadCalls = 0;
            runtime.InteractiveEvidenceReaderForTesting = requested =>
            {
                Interlocked.Increment(ref brokerEvidenceReadCalls);
                return new StableFileEvidence(
                    Path.GetFullPath(requested),
                    Path.GetFullPath(target),
                    SolidWorksDocumentKind.Part,
                    targetInfo.Length,
                    new DateTimeOffset(targetInfo.LastWriteTimeUtc, TimeSpan.Zero),
                    "00000001:0000000000000001");
            };

            var refresh = await runtime.RefreshMetadataNowWithStatusAsync(link);

            Assert.False(refresh.Status.Fresh);
            Assert.True(refresh.Status.CacheStale);
            Assert.Equal("INTERACTIVE_REPARSE_REDIRECT_REJECTED", refresh.Status.ErrorCode);
            Assert.Equal(1, metadata.ExtractCalls);
            Assert.Equal(0, brokerEvidenceReadCalls);
            Assert.Equal("OLD", Assert.Single(refresh.Metadata!.Properties).ResolvedValue);
            Assert.Equal(
                "OLD",
                Assert.Single((await store.TryGetMetadataAsync(link, CancellationToken.None))!.Properties).ResolvedValue);
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JournalCheckpoint_RoundTripsVolumeIdentityAndCursor()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var checkpoint = new DiscoveryJournalCheckpoint(
            "NTFS-MFT-USN-v1",
            [new DiscoveryVolumeCursor(@"C:\", 0xFFEEDDCCBBAA0099UL, 42_001, 0xA1B2C3D4)],
            DateTimeOffset.UtcNow);

        await store.SaveDiscoveryCheckpointAsync(checkpoint);
        var restored = Assert.IsType<DiscoveryJournalCheckpoint>(
            await store.LoadLatestDiscoveryCheckpointAsync());

        Assert.Equal(checkpoint.Source, restored.Source);
        var cursor = Assert.Single(restored.Volumes);
        Assert.Equal(0xA1B2C3D4U, cursor.VolumeSerialNumber);
        Assert.Equal(0xFFEEDDCCBBAA0099UL, cursor.JournalId);
        Assert.Equal(42_001, cursor.NextUsn);
    }

    [Fact]
    public async Task RestartWithNoJournalChanges_DoesNotFullScanOrExtractMetadata()
    {
        await using var database = new TemporaryDatabase();
        var checkpoint = new DiscoveryJournalCheckpoint(
            "NTFS-MFT-USN-v1",
            [new DiscoveryVolumeCursor(@"C:\", 91, 1_234, 77)],
            DateTimeOffset.UtcNow);
        var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        await store.SaveDiscoveryCheckpointAsync(checkpoint);

        var discovery = new NoChangeJournalDiscovery(checkpoint);
        var metadata = new CountingMetadataClient();
        await using var runtime = new IndexingRuntime(
            store,
            discovery,
            metadata,
            IndexingRuntimeOptions.Default with
            {
                DiscoveryRefreshInterval = TimeSpan.FromHours(1),
                IdleDelay = TimeSpan.FromMilliseconds(10),
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        await discovery.DeltaRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, discovery.DeltaReadCalls);
        Assert.Equal(0, discovery.FullDiscoveryCalls);
        Assert.Equal(0, metadata.ExtractCalls);
    }

    [Fact]
    public async Task LeaseOrder_PreservesExplicitPriorityThenAssemblyPartDrawing()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var assembly = new FileCandidate(@"C:\RC46\background.SLDASM", SolidWorksDocumentKind.Assembly, 30, now);
        var part = new FileCandidate(@"C:\RC46\background.SLDPRT", SolidWorksDocumentKind.Part, 20, now);
        var drawing = new FileCandidate(@"C:\RC46\background.SLDDRW", SolidWorksDocumentKind.Drawing, 10, now);
        var interactiveDrawing = new FileCandidate(@"C:\RC46\interactive.SLDDRW", SolidWorksDocumentKind.Drawing, 40, now);
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([drawing, part, assembly, interactiveDrawing]),
            CancellationToken.None);

        foreach (var initial in await store.LeaseMetadataAsync(4, TimeSpan.FromSeconds(5), "initial-clear"))
        {
            await store.CompleteMetadataLeaseAsync(initial.FileId, initial.LeaseOwner, succeeded: true);
        }

        await store.EnqueueMetadataAsync(drawing.FullPath, "Historical", MetadataQueuePriority.Historical);
        await store.EnqueueMetadataAsync(part.FullPath, "Historical", MetadataQueuePriority.Historical);
        await store.EnqueueMetadataAsync(assembly.FullPath, "Historical", MetadataQueuePriority.Historical);
        await store.EnqueueMetadataAsync(
            interactiveDrawing.FullPath,
            "PropertyViewer",
            MetadataQueuePriority.PropertyViewer);

        var leases = await store.LeaseMetadataAsync(4, TimeSpan.FromSeconds(5), "rc46-order");

        Assert.Equal(
            [interactiveDrawing.FullPath, assembly.FullPath, part.FullPath, drawing.FullPath],
            leases.Select(static lease => lease.FullPath));
        Assert.Equal(MetadataQueuePriority.PropertyViewer, leases[0].Priority);
        Assert.All(leases.Skip(1), static lease => Assert.Equal(MetadataQueuePriority.Historical, lease.Priority));
    }

    [Fact]
    public async Task SameExtractorVersion_DoesNotRequeueCompletedMetadata()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var candidate = new FileCandidate(
            @"C:\RC46\completed.SLDASM",
            SolidWorksDocumentKind.Assembly,
            64,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([candidate]), CancellationToken.None);
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                candidate,
                [],
                [],
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        _ = await store.RequeueForExtractorVersionAsync(ExtractorVersions.Current);

        Assert.Empty(await store.LeaseMetadataAsync(1, TimeSpan.FromSeconds(5), "same-version"));
    }

    [Fact]
    public async Task CachedReadableProgress_SurvivesExtractorUpgradeAndFreshReadAddsOne()
    {
        const int discoveredCount = 147_779;
        const int legacyCachedCount = 9_602;
        await using var database = new TemporaryDatabase();
        await using var schemaStore = new SqliteIndexStore(database.Path);
        await schemaStore.InitializeAsync();

        var modified = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM index_maintenance_state WHERE key='cached-readable-v1';
                WITH d(n) AS (
                    VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)
                ), numbers(n) AS (
                    SELECT 1 + a.n + 10*b.n + 100*c.n + 1000*d.n + 10000*e.n + 100000*f.n
                      FROM d a CROSS JOIN d b CROSS JOIN d c
                               CROSS JOIN d d CROSS JOIN d e CROSS JOIN d f
                )
                INSERT INTO files(
                    path,path_norm,kind,size,modified_utc,discovery_generation,
                    state,metadata_state,extractor_version,extracted_utc)
                SELECT 'C:\RC46\CACHE\F' || printf('%06d',n) || '.SLDPRT',
                       'C:\RC46\CACHE\F' || printf('%06d',n) || '.SLDPRT',
                       @kind,1,@modified,1,0,
                       CASE WHEN n<=@cached THEN 0 ELSE 1 END,
                       @oldExtractor,NULL
                  FROM numbers WHERE n<=@discovered;
                INSERT INTO properties(
                    file_id,scope,configuration_name,name,name_norm,raw_value,resolved_value,
                    display_value,cached_value,is_linked,value_norm,source)
                SELECT id,@scope,NULL,'Code','CODE','OLD','OLD',NULL,NULL,0,'OLD',@source
                  FROM files ORDER BY id LIMIT @cached;
                """;
            command.Parameters.AddWithValue("@kind", (int)SolidWorksDocumentKind.Part);
            command.Parameters.AddWithValue("@modified", modified.UtcTicks);
            command.Parameters.AddWithValue("@cached", legacyCachedCount);
            command.Parameters.AddWithValue("@discovered", discoveredCount);
            command.Parameters.AddWithValue("@oldExtractor", ExtractorVersions.Current - 1);
            command.Parameters.AddWithValue("@scope", (int)PropertyScope.Document);
            command.Parameters.AddWithValue("@source", (int)MetadataSource.NoApi);
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var beforeUpgrade = await store.GetMetadataProgressAsync();
        Assert.Equal(discoveredCount, beforeUpgrade.DiscoveredFileCount);
        Assert.Equal(legacyCachedCount, beforeUpgrade.CachedReadableFileCount);

        _ = await store.RequeueForExtractorVersionAsync(ExtractorVersions.Current);
        var afterUpgrade = await store.GetMetadataProgressAsync();
        Assert.Equal(discoveredCount, afterUpgrade.PendingCount);
        Assert.Equal(legacyCachedCount, afterUpgrade.CachedReadableFileCount);

        var oldCachedPath = @"C:\RC46\CACHE\F000001.SLDPRT";
        var failedLease = Assert.IsType<MetadataWorkLease>(
            await store.TryLeaseMetadataPathAsync(
                oldCachedPath,
                TimeSpan.FromSeconds(5),
                "cached-failure"));
        await store.CompleteMetadataLeaseAsync(
            failedLease.FileId,
            failedLease.LeaseOwner,
            succeeded: false,
            errorCode: "TEST_FAILURE");
        Assert.Equal(
            legacyCachedCount,
            (await store.GetMetadataProgressAsync()).CachedReadableFileCount);

        var freshPath = @"C:\RC46\CACHE\F009603.SLDPRT";
        await store.UpsertMetadataAsync(
            new DocumentMetadata(
                new FileCandidate(freshPath, SolidWorksDocumentKind.Part, 1, modified),
                [new DocumentProperty(
                    "Code",
                    "FRESH",
                    "FRESH",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                [],
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var afterFreshRead = await store.GetMetadataProgressAsync();
        Assert.Equal(discoveredCount, afterFreshRead.DiscoveredFileCount);
        Assert.Equal(legacyCachedCount + 1, afterFreshRead.CachedReadableFileCount);
    }

    private sealed class NoChangeJournalDiscovery(DiscoveryJournalCheckpoint checkpoint) :
        IFileDiscoveryProvider,
        IJournalIncrementalDiscoveryProvider
    {
        public TaskCompletionSource DeltaRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FullDiscoveryCalls { get; private set; }

        public int DeltaReadCalls { get; private set; }

        public DiscoveryJournalCheckpoint? SnapshotCheckpoint => null;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            FullDiscoveryCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DiscoveryJournalCheckpoint?> CaptureCheckpointAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<DiscoveryJournalCheckpoint?>(checkpoint);

        public ValueTask<DiscoveryDeltaBatch> ReadDeltaBatchAsync(
            DiscoveryJournalCheckpoint previous,
            int maximumChanges,
            CancellationToken cancellationToken)
        {
            DeltaReadCalls++;
            DeltaRead.TrySetResult();
            return ValueTask.FromResult(new DiscoveryDeltaBatch(
                previous,
                previous with { CapturedAtUtc = DateTimeOffset.UtcNow },
                [],
                HasMore: false,
                RequiresFullReconciliation: false,
                "TEST_NO_CHANGES"));
        }
    }

    private sealed class CountingMetadataClient(
        Func<MetadataRequest, MetadataResponse>? responseFactory = null) : IMetadataExtractionClient
    {
        public int ExtractCalls { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST_READY", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            ExtractCalls++;
            return ValueTask.FromResult(
                responseFactory?.Invoke(request) ??
                new MetadataResponse(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    null,
                    "UNEXPECTED",
                    "unexpected",
                    false));
        }
    }
}
