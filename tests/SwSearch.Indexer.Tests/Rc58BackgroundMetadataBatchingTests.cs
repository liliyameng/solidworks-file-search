using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc58BackgroundMetadataBatchingTests
{
    [Fact]
    public async Task BackgroundLoop_ClaimsThirtyTwoAndCommitsThemWithoutIncreasingExtractionConcurrency()
    {
        const int fileCount = 33;
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, fileCount);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var metadata = new ImmediateTrackingMetadata();
        await using var runtime = CreateRuntime(sandbox, store, files, metadata);
        var plans = new ConcurrentQueue<MetadataBatchPlan>();
        var backgroundKeepalives = 0;
        runtime.MetadataBatchPlannedForTesting = plans.Enqueue;
        runtime.BackgroundMetadataLeaseKeepaliveStartedForTesting =
            () => Interlocked.Increment(ref backgroundKeepalives);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await WaitUntilAsync(
            async () =>
            {
                var status = await store.GetStatusAsync();
                return metadata.ExtractCalls == fileCount && status.PendingMetadataCount == 0;
            },
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(5));

        var firstPass = Assert.Single(plans, plan => !plan.IsBackground);
        Assert.Equal(1, firstPass.LeaseCount);
        var backgroundPlans = plans.Where(plan => plan.IsBackground).ToArray();
        Assert.NotEmpty(backgroundPlans);
        Assert.All(backgroundPlans, backgroundPlan =>
        {
            Assert.Equal(32, backgroundPlan.LeaseCount);
            Assert.Equal(32, backgroundPlan.PipelineCapacity);
            Assert.Equal(1, backgroundPlan.ExtractionConcurrency);
            Assert.True(backgroundPlan.LeaseDuration > TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.FromSeconds(45), backgroundPlan.CommitMaximumAge);
            Assert.True(
                backgroundPlan.LeaseDuration >= TimeSpan.FromMilliseconds(
                    (long)IndexingRuntimeOptions.Default.HardFileTimeoutMilliseconds *
                    backgroundPlan.LeaseCount +
                    backgroundPlan.CommitMaximumAge.TotalMilliseconds));
        });
        Assert.Equal(1, metadata.MaximumConcurrency);
        Assert.True(backgroundKeepalives > 0);

        var metrics = runtime.RuntimePhaseMetricsForTesting;
        Assert.Equal(fileCount, metrics.ClaimedItems);
        Assert.Equal(fileCount, metrics.CommittedItems);
        Assert.Equal(2, metrics.CommitOperations);
    }

    [Fact]
    public async Task PublicManualProcessing_KeepsTheConservativeLeaseAndPipelinePlan()
    {
        const int fileCount = 64;
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, fileCount);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var metadata = new ImmediateTrackingMetadata();
        await using var runtime = CreateRuntime(sandbox, store, files, metadata);
        var plans = new ConcurrentQueue<MetadataBatchPlan>();
        var backgroundKeepalives = 0;
        runtime.MetadataBatchPlannedForTesting = plans.Enqueue;
        runtime.BackgroundMetadataLeaseKeepaliveStartedForTesting =
            () => Interlocked.Increment(ref backgroundKeepalives);

        var processed = await runtime.ProcessPendingMetadataAsync(fileCount);

        Assert.Equal(4, processed);
        var plan = Assert.Single(plans);
        Assert.False(plan.IsBackground);
        Assert.Equal(4, plan.LeaseCount);
        Assert.Equal(1, plan.PipelineCapacity);
        Assert.Equal(1, plan.ExtractionConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(1), plan.CommitMaximumAge);
        Assert.True(plan.LeaseDuration <= TimeSpan.FromSeconds(30));
        Assert.Equal(1, metadata.MaximumConcurrency);
        Assert.Equal(0, backgroundKeepalives);
        Assert.Equal(fileCount - processed, (await store.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task QueueRenewal_IsExactAndNeverRevivesAnExpiredLease()
    {
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, 1);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var first = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(120),
            "rc59-renew-owner"));
        var wrongFingerprint = first with { Fingerprint = first.Fingerprint + "-wrong" };
        Assert.Empty(await store.RenewMetadataLeasesAsync(
            [wrongFingerprint],
            DateTimeOffset.UtcNow.AddMilliseconds(500)));
        Assert.Empty(await store.RenewMetadataLeasesAsync(
            [first with { LeaseOwner = "rc59-wrong-owner" }],
            DateTimeOffset.UtcNow.AddMilliseconds(500)));
        Assert.Empty(await store.RenewMetadataLeasesAsync(
            [first with { Attempt = first.Attempt + 1 }],
            DateTimeOffset.UtcNow.AddMilliseconds(500)));

        var renewed = await store.RenewMetadataLeasesAsync(
            [first],
            DateTimeOffset.UtcNow.AddMilliseconds(500));
        Assert.Contains(first.FileId, renewed);
        await Task.Delay(180);
        Assert.Empty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-renew-contender"));

        await store.ReleaseMetadataLeasesAsync("rc59-renew-owner");
        var expiring = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(80),
            "rc59-expiring-owner"));
        await Task.Delay(140);
        var replacement = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(500),
            "rc59-replacement-owner"));
        Assert.Empty(await store.RenewMetadataLeasesAsync(
            [expiring],
            DateTimeOffset.UtcNow.AddMilliseconds(500)));
        await store.ReleaseMetadataLeasesAsync(replacement.LeaseOwner);
    }

    [Fact]
    public async Task BackgroundKeepalive_CoversSlowReadWriteAdmissionAndFinalizingWait()
    {
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, 1);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        const string owner = "rc59-keepalive-owner";
        var leaseDuration = TimeSpan.FromMilliseconds(150);
        var lease = Assert.Single(await store.LeaseMetadataAsync(1, leaseDuration, owner));
        using var batchLifetime = new CancellationTokenSource();
        var keepalive = new BackgroundMetadataLeaseKeepalive(
            store,
            [lease],
            new MetadataLeaseKeepaliveSettings(
                leaseDuration,
                TimeSpan.FromSeconds(8),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(75)),
            lease.LeaseUntilUtc.Subtract(leaseDuration),
            batchLifetime);
        keepalive.Start();

        var governor = new ResourceGovernor(IndexingRuntimeOptions.Default with
        {
            MaximumFilesPerSecond = 100,
            MaximumReadBytesPerSecond = 1_000,
            MaximumWriteBytesPerSecond = 1_000,
        });
        await keepalive.EnsureCoverageAsync(batchLifetime.Token);
        await governor.WaitForReadAsync(1_000, batchLifetime.Token);
        await keepalive.EnsureCoverageAsync(batchLifetime.Token);
        await governor.WaitForReadAsync(0, batchLifetime.Token);
        await keepalive.EnsureCoverageAsync(batchLifetime.Token);

        // The item stays renewable while a final write is waiting, not just
        // while it is queued for parser extraction.
        keepalive.BeginFinalizing(lease);
        await governor.WaitForWriteAsync(1_000, batchLifetime.Token);
        await governor.WaitForWriteAsync(0, batchLifetime.Token);
        await Task.Delay(180); // non-cooperative in-flight work analogue
        Assert.Empty(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-keepalive-contender"));

        await keepalive.StopAsync();
        await store.ReleaseMetadataLeasesAsync(owner);
        var recovered = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-keepalive-recovery"));
        await store.ReleaseMetadataLeasesAsync(recovered.LeaseOwner);
    }

    [Fact]
    public async Task BackgroundKeepalive_CancellationOrRenewalFailureStopsBeforeAnotherOwnerCanReuse()
    {
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, 1);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var leaseDuration = TimeSpan.FromMilliseconds(100);
        var settings = new MetadataLeaseKeepaliveSettings(
            leaseDuration,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(40));
        var first = Assert.Single(await store.LeaseMetadataAsync(1, leaseDuration, "rc59-cancel-owner"));
        using (var cancellation = new CancellationTokenSource())
        {
            var keepalive = new BackgroundMetadataLeaseKeepalive(
                store,
                [first],
                settings,
                first.LeaseUntilUtc.Subtract(leaseDuration),
                cancellation);
            keepalive.Start();
            await Task.Delay(45);
            cancellation.Cancel();
            await keepalive.StopAsync();
        }

        await Task.Delay(150);
        var afterCancellation = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-cancel-recovery"));
        await store.ReleaseMetadataLeasesAsync(afterCancellation.LeaseOwner);

        var failing = Assert.Single(await store.LeaseMetadataAsync(1, leaseDuration, "rc59-failure-owner"));
        using var failureLifetime = new CancellationTokenSource();
        var wrongLease = failing with { Fingerprint = failing.Fingerprint + "-wrong" };
        var failedKeepalive = new BackgroundMetadataLeaseKeepalive(
            store,
            [wrongLease],
            settings,
            failing.LeaseUntilUtc.Subtract(leaseDuration),
            failureLifetime);
        failedKeepalive.Start();
        await WaitUntilAsync(
            () => Task.FromResult(failureLifetime.IsCancellationRequested),
            TimeSpan.FromSeconds(1));
        await failedKeepalive.StopAsync();
        await Task.Delay(150);
        var afterFailure = Assert.Single(await store.LeaseMetadataAsync(
            1,
            TimeSpan.FromMilliseconds(100),
            "rc59-failure-recovery"));
        await store.ReleaseMetadataLeasesAsync(afterFailure.LeaseOwner);
    }

    [Fact]
    public async Task BackgroundLoop_SlowSerialExtractionAccumulatesThirtyTwoItemsBeforeOneCommit()
    {
        const int fileCount = 33;
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, fileCount);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var metadata = new DelayedTrackingMetadata(TimeSpan.FromMilliseconds(180));
        await using var runtime = CreateRuntime(sandbox, store, files, metadata);
        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);

        await WaitUntilAsync(
            async () =>
            {
                var status = await store.GetStatusAsync();
                return metadata.ExtractCalls == fileCount && status.PendingMetadataCount == 0;
            },
            TimeSpan.FromSeconds(20));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(5));

        var metrics = runtime.RuntimePhaseMetricsForTesting;
        Assert.Equal(fileCount, metrics.ClaimedItems);
        Assert.Equal(fileCount, metrics.CommittedItems);
        // One conservative first-pass commit plus one 32-file background
        // commit.  A five-second background age split this serial run.
        Assert.Equal(2, metrics.CommitOperations);
        Assert.Equal(1, metadata.MaximumConcurrency);
    }

    [Fact]
    public async Task BackgroundPlan_LeaseCoversSerialHardTimeoutAndLongCommitWindow()
    {
        const int hardFileTimeoutMilliseconds = 30_000;
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, 26);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var metadata = new BlockAfterFirstMetadata();
        await using var runtime = CreateRuntime(
            sandbox,
            store,
            files,
            metadata,
            options: IndexingRuntimeOptions.Default with
            {
                DiscoveryRefreshInterval = TimeSpan.FromHours(1),
                IdleDelay = TimeSpan.FromMilliseconds(1),
                MetadataBatchSize = 64,
                MaximumFilesPerSecond = 1_000,
                MaximumReadBytesPerSecond = 100_000_000,
                MaximumWriteBytesPerSecond = 100_000_000,
                MaximumConcurrentExtractions = 1,
                PipelineCapacity = 1,
                HardFileTimeoutMilliseconds = hardFileTimeoutMilliseconds,
            });
        var backgroundPlan = new TaskCompletionSource<MetadataBatchPlan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.MetadataBatchPlannedForTesting = plan =>
        {
            if (plan.IsBackground)
            {
                backgroundPlan.TrySetResult(plan);
            }
        };

        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        var plan = await backgroundPlan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await metadata.SecondBatchExtractionStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(25, plan.LeaseCount);
        Assert.Equal(TimeSpan.FromSeconds(45), plan.CommitMaximumAge);
        Assert.True(
            plan.LeaseDuration >= TimeSpan.FromMilliseconds(
                (long)hardFileTimeoutMilliseconds * plan.LeaseCount +
                plan.CommitMaximumAge.TotalMilliseconds +
                5_000));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(5));

        var recovered = await store.LeaseMetadataAsync(
            plan.LeaseCount,
            TimeSpan.FromSeconds(5),
            "rc59-lease-window-recovery");
        try
        {
            Assert.Equal(plan.LeaseCount, recovered.Count);
        }
        finally
        {
            await store.ReleaseMetadataLeasesAsync("rc59-lease-window-recovery");
        }
    }

    [Fact]
    public async Task BackgroundCancellation_ReleasesTheUnstartedThirtyTwoItemBatchImmediately()
    {
        const int fileCount = 33;
        using var sandbox = new Sandbox();
        var files = CreateFiles(sandbox, fileCount);
        await using var store = new SqliteIndexStore(sandbox.DatabasePath);
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(AsAsync(files), CancellationToken.None);

        var metadata = new BlockAfterFirstMetadata();
        await using var runtime = CreateRuntime(sandbox, store, files, metadata);
        var backgroundPlan = new TaskCompletionSource<MetadataBatchPlan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.MetadataBatchPlannedForTesting = plan =>
        {
            if (plan.IsBackground && plan.LeaseCount == 32)
            {
                backgroundPlan.TrySetResult(plan);
            }
        };

        using var cancellation = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(cancellation.Token);
        _ = await backgroundPlan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await metadata.SecondBatchExtractionStarted.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await background.WaitAsync(TimeSpan.FromSeconds(5));

        var recovered = await store.LeaseMetadataAsync(
            32,
            TimeSpan.FromSeconds(5),
            "rc58-cancel-recovery");
        try
        {
            Assert.Equal(32, recovered.Count);
        }
        finally
        {
            await store.ReleaseMetadataLeasesAsync("rc58-cancel-recovery");
        }
    }

    private static IndexingRuntime CreateRuntime(
        Sandbox sandbox,
        SqliteIndexStore store,
        IReadOnlyList<FileCandidate> files,
        IMetadataExtractionClient metadata,
        IndexingRuntimeOptions? options = null) =>
        new(
            store,
            new StaticDiscovery(files),
            metadata,
            options ?? (IndexingRuntimeOptions.Default with
            {
                DiscoveryRefreshInterval = TimeSpan.FromHours(1),
                IdleDelay = TimeSpan.FromMilliseconds(1),
                MetadataBatchSize = 64,
                MaximumFilesPerSecond = 1_000,
                MaximumReadBytesPerSecond = 100_000_000,
                MaximumWriteBytesPerSecond = 100_000_000,
                MaximumConcurrentExtractions = 1,
                PipelineCapacity = 1,
            }),
            new RuntimeFileLog(Path.Combine(sandbox.Path, "logs")));

    private static FileCandidate[] CreateFiles(Sandbox sandbox, int count)
    {
        var files = new FileCandidate[count];
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(sandbox.Path, $"Batch-{index:D2}.SLDPRT");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            files[index] = StableFileEvidenceReader.Capture(path).Candidate;
        }

        return files;
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out while waiting for the background metadata batch.");
    }

    private static async IAsyncEnumerable<FileCandidate> AsAsync(
        IEnumerable<FileCandidate> files,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        await Task.CompletedTask;
    }

    private static MetadataResponse Complete(MetadataRequest request)
    {
        var evidence = StableFileEvidenceReader.Capture(request.FullPath);
        var contractEvidence = new MetadataFileEvidence(
            evidence.RequestedPath,
            evidence.FinalPath,
            evidence.Kind,
            evidence.Size,
            evidence.LastWriteTimeUtc,
            evidence.FileIdentity);
        return new MetadataResponse(
            ContractVersions.MetadataPipe,
            request.RequestId,
            new DocumentMetadata(
                evidence.Candidate,
                [],
                [],
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow),
            ErrorCode: null,
            ChineseErrorMessage: null,
            Retryable: false,
            PipelineLeaseId: request.PipelineLeaseId,
            Outcome: MetadataPipelineOutcome.Completed,
            BeforeEvidence: contractEvidence,
            AfterEvidence: contractEvidence);
    }

    private sealed class StaticDiscovery(IReadOnlyList<FileCandidate> files) : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC58_DISCOVERY_READY",
                "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ImmediateTrackingMetadata : IMetadataExtractionClient
    {
        private int _calls;
        private int _concurrency;
        private int _maximumConcurrency;

        internal int ExtractCalls => Volatile.Read(ref _calls);

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC58_METADATA_READY",
                "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(ref _maximumConcurrency, active);
            try
            {
                return ValueTask.FromResult(Complete(request));
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class BlockAfterFirstMetadata : IMetadataExtractionClient
    {
        private int _calls;
        private int _concurrency;

        private readonly TaskCompletionSource _secondBatchExtractionStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task SecondBatchExtractionStarted => _secondBatchExtractionStarted.Task;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC58_METADATA_READY",
                "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _concurrency);
            try
            {
                if (call > 1)
                {
                    _secondBatchExtractionStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return Complete(request);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class DelayedTrackingMetadata(TimeSpan delay) : IMetadataExtractionClient
    {
        private int _calls;
        private int _concurrency;
        private int _maximumConcurrency;

        internal int ExtractCalls => Volatile.Read(ref _calls);

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "RC58_METADATA_READY",
                "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(ref _maximumConcurrency, active);
            try
            {
                await Task.Delay(delay, cancellationToken);
                return Complete(request);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private static void UpdateMaximum(ref int target, int requested)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= requested ||
                Interlocked.CompareExchange(ref target, requested, current) == current)
            {
                return;
            }
        }
    }

    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"swsearch-rc58-background-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string DatabasePath => System.IO.Path.Combine(Path, "index.db");

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
