using System.Diagnostics;
using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class RuntimePolicyTests
{
    [Fact]
    public async Task Runtime_BoundedPipeline_ExceedsFourFilesPerSecond_WithTwoWorkers()
    {
        using var directory = new TemporaryDirectory();
        var files = Enumerable.Range(0, 8)
            .Select(index => directory.CreateFile(
                $"Pipeline-{index}.SLDPRT",
                SolidWorksDocumentKind.Part))
            .ToArray();
        var metadata = new TrackingMetadata(files, TimeSpan.FromMilliseconds(50));
        await using var runtime = CreateRuntime(directory, files, metadata, new IndexingRuntimeOptions(
            TimeSpan.FromHours(6),
            TimeSpan.FromMilliseconds(1),
            8,
            MaximumFilesPerSecond: 100,
            MaximumReadBytesPerSecond: 100_000_000,
            MaximumWriteBytesPerSecond: 100_000_000,
            MaximumConcurrentExtractions: 2,
            PipelineCapacity: 8));

        var clock = Stopwatch.StartNew();
        await runtime.RefreshAndProcessAsync(8);
        clock.Stop();

        var rate = files.Length / clock.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"PIPELINE files={files.Length} elapsed_ms={clock.Elapsed.TotalMilliseconds:F1} " +
            $"rate_files_per_second={rate:F2} max_concurrency={metadata.MaximumConcurrency}");
        Assert.Equal(files.Length, metadata.ExtractCalls);
        Assert.Equal(2, metadata.MaximumConcurrency);
        Assert.True(rate >= 4, $"rate={rate:F2}/s elapsed={clock.Elapsed}");
    }

    [Fact]
    public async Task Runtime_UnchangedRefresh_DoesNotExtractCachedFilesAgain()
    {
        using var directory = new TemporaryDirectory();
        var files = Enumerable.Range(0, 2)
            .Select(index => directory.CreateFile(
                $"Cached-{index}.SLDPRT",
                SolidWorksDocumentKind.Part))
            .ToArray();
        var metadata = new TrackingMetadata(files, TimeSpan.Zero);
        await using var runtime = CreateRuntime(directory, files, metadata, new IndexingRuntimeOptions(
            TimeSpan.FromHours(6), TimeSpan.FromMilliseconds(1), 8,
            MaximumFilesPerSecond: 100,
            MaximumReadBytesPerSecond: 100_000_000,
            MaximumWriteBytesPerSecond: 100_000_000));

        await runtime.RefreshAndProcessAsync(8);
        await runtime.RefreshAndProcessAsync(8);

        Assert.Equal(2, metadata.ExtractCalls);
        Assert.Equal(0, (await runtime.Store.GetStatusAsync()).PendingMetadataCount);
    }

    [Fact]
    public async Task Runtime_LargeFileUsesCappedMetadataReadBudget()
    {
        using var directory = new TemporaryDirectory();
        var file = directory.CreateFile(
            "Large.SLDASM",
            SolidWorksDocumentKind.Assembly,
            contentLength: 128 * 1024);
        var metadata = new TrackingMetadata([file], TimeSpan.Zero);
        await using var runtime = CreateRuntime(directory, [file], metadata, new IndexingRuntimeOptions(
            TimeSpan.FromHours(6), TimeSpan.FromMilliseconds(1), 1,
            MaximumFilesPerSecond: 100,
            MaximumReadBytesPerSecond: 1024 * 1024,
            MaximumWriteBytesPerSecond: 100_000_000,
            MaximumEstimatedReadBytesPerFile: 64 * 1024));

        var clock = Stopwatch.StartNew();
        await runtime.RefreshAndProcessAsync(1);
        clock.Stop();

        Assert.Equal(1, metadata.ExtractCalls);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"elapsed={clock.Elapsed}");
    }

    [Fact]
    public async Task Runtime_PauseReleasesUnstartedLeases_AndResumeCompletesThem()
    {
        using var directory = new TemporaryDirectory();
        var files = Enumerable.Range(0, 6)
            .Select(index => directory.CreateFile(
                $"Pause-{index}.SLDPRT",
                SolidWorksDocumentKind.Part))
            .ToArray();
        var metadata = new TrackingMetadata(files, TimeSpan.FromMilliseconds(100));
        await using var runtime = CreateRuntime(directory, files, metadata, new IndexingRuntimeOptions(
            TimeSpan.FromHours(6), TimeSpan.FromMilliseconds(1), 8,
            MaximumFilesPerSecond: 100,
            MaximumReadBytesPerSecond: 100_000_000,
            MaximumWriteBytesPerSecond: 100_000_000,
            MaximumConcurrentExtractions: 2));

        var refresh = runtime.RefreshAndProcessAsync(6).AsTask();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (metadata.ExtractCalls == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        runtime.SetManualPause(true);
        await refresh;
        var callsAtPause = metadata.ExtractCalls;
        Assert.InRange(callsAtPause, 1, 2);

        runtime.SetManualPause(false);
        while ((await runtime.Store.GetStatusAsync()).PendingMetadataCount > 0)
        {
            await runtime.ProcessPendingMetadataAsync(6);
        }

        Assert.Equal(6, metadata.ExtractCalls);
        Assert.Equal(0, (await runtime.Store.GetStatusAsync()).PendingMetadataCount);
    }
    [Fact]
    public async Task Runtime_DefaultFileRate_IsSingleConcurrencyAndNoMoreThanTwoPerSecond()
    {
        using var directory = new TemporaryDirectory();
        var files = Enumerable.Range(0, 3)
            .Select(index => directory.CreateFile(
                $"Rate-{index}.SLDPRT",
                SolidWorksDocumentKind.Part))
            .ToArray();
        var metadata = new ImmediateMetadata(files);
        await using var runtime = CreateRuntime(directory, files, metadata, new IndexingRuntimeOptions(
            TimeSpan.FromHours(6),
            TimeSpan.FromMilliseconds(1),
            3,
            MaximumFilesPerSecond: 2,
            MaximumReadBytesPerSecond: 100_000_000,
            MaximumWriteBytesPerSecond: 100_000_000));

        var clock = Stopwatch.StartNew();
        await runtime.RefreshAndProcessAsync(3);
        clock.Stop();

        Assert.Equal(3, metadata.ExtractCalls);
        Assert.Equal(1, metadata.MaximumConcurrency);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(850), $"elapsed={clock.Elapsed}");
    }

    [Fact]
    public async Task Runtime_HardTimeoutTripsCircuitAndLeavesRemainingWorkRecoverable()
    {
        using var directory = new TemporaryDirectory();
        var files = Enumerable.Range(0, 4)
            .Select(index =>
            {
                var path = Path.Combine(directory.Path, $"Timeout-{index}.SLDPRT");
                File.WriteAllBytes(path, [1]);
                var info = new FileInfo(path);
                return new FileCandidate(
                    path,
                    SolidWorksDocumentKind.Part,
                    info.Length,
                    info.LastWriteTimeUtc);
            })
            .ToArray();
        var metadata = new SlowMetadata(TimeSpan.FromMilliseconds(350));
        await using var runtime = CreateRuntime(directory, files, metadata, new IndexingRuntimeOptions(
            TimeSpan.FromHours(6),
            TimeSpan.FromMilliseconds(1),
            4,
            MaximumFilesPerSecond: 1_000,
            MaximumReadBytesPerSecond: 100_000_000,
            MaximumWriteBytesPerSecond: 100_000_000,
            SoftFileTimeoutMilliseconds: 100,
            HardFileTimeoutMilliseconds: 150,
            WorkerFailureTripCount: 2,
            WorkerCircuitCooldownSeconds: 60,
            MaximumConcurrentExtractions: 1));

        await runtime.RefreshAndProcessAsync(4);

        Assert.Equal(2, metadata.ExtractCalls);
        Assert.Equal(4, (await runtime.Store.GetStatusAsync()).PendingMetadataCount);
    }

    private static IndexingRuntime CreateRuntime(
        TemporaryDirectory directory,
        IReadOnlyList<FileCandidate> files,
        IMetadataExtractionClient metadata,
        IndexingRuntimeOptions options) =>
        new(
            new SqliteIndexStore(Path.Combine(directory.Path, "index.db")),
            new FixtureDiscovery(files),
            metadata,
            options,
            new RuntimeFileLog(Path.Combine(directory.Path, "logs")));

    private static MetadataResponse CompletedResponse(MetadataRequest request)
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

    private sealed class FixtureDiscovery : IIncrementalFileDiscoveryProvider
    {
        private readonly IReadOnlyList<FileCandidate> _files;

        internal FixtureDiscovery(IReadOnlyList<FileCandidate> files)
        {
            _files = files;
            var now = DateTimeOffset.UtcNow;
            var roots = files
                .Select(file => Path.GetDirectoryName(file.FullPath)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(root => new DiscoveryRootReport(
                    root,
                    DiscoveryRootKind.ExplicitLocal,
                    WasOnline: true,
                    FullyEnumerated: true,
                    VisitedEntries: files.Count,
                    SolidWorksFiles: files.Count,
                    ReparsePointsSkipped: 0,
                    OfflineFilesSkipped: 0,
                    new Dictionary<string, int>()))
                .ToArray();
            LastReport = new DiscoveryScanReport(now, now, IsCompleteForDeletion: true, roots);
        }

        public TimeSpan RecommendedReconciliationInterval => TimeSpan.FromHours(1);

        public DiscoveryScanReport LastReport { get; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<DiscoveredFile> DiscoverEntriesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Capture(file.FullPath);
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<DiscoveryChangeHint> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<DiscoveredFile?> TryInspectFileAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = _files.FirstOrDefault(candidate =>
                candidate.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            return ValueTask.FromResult<DiscoveredFile?>(
                file is null ? null : Capture(file.FullPath));
        }

        private static DiscoveredFile Capture(string path)
        {
            var evidence = StableFileEvidenceReader.Capture(path);
            var identityParts = evidence.FileIdentity.Split(':');
            var identity = identityParts.Length == 2 && identityParts[0] != "PORTABLE"
                ? new FileSystemIdentity(
                    Convert.ToUInt32(identityParts[0], 16),
                    Convert.ToUInt64(identityParts[1], 16))
                : null;
            return new DiscoveredFile(
                evidence.Candidate,
                Path.GetFullPath(path),
                Path.GetDirectoryName(path)!,
                identity);
        }
    }

    private sealed class ImmediateMetadata(IReadOnlyList<FileCandidate> files) : IMetadataExtractionClient
    {
        private int _concurrency;

        public int ExtractCalls { get; private set; }

        public int MaximumConcurrency { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(MetadataRequest request, CancellationToken cancellationToken)
        {
            ExtractCalls++;
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            try
            {
                _ = files.Single(candidate =>
                    candidate.FullPath.Equals(request.FullPath, StringComparison.OrdinalIgnoreCase));
                return ValueTask.FromResult(CompletedResponse(request));
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class SlowMetadata(TimeSpan delay) : IMetadataExtractionClient
    {
        public int ExtractCalls { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            ExtractCalls++;
            var requestTimeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds));
            await Task.Delay(delay <= requestTimeout ? delay : requestTimeout, cancellationToken);
            return new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                Metadata: null,
                ErrorCode: "METADATA_TIMEOUT", // MetadataErrorCodes.Timeout
                ChineseErrorMessage: "timeout",
                Retryable: true,
                PipelineLeaseId: request.PipelineLeaseId,
                Outcome: MetadataPipelineOutcome.Failed);
        }
    }

    private sealed class TrackingMetadata(
        IReadOnlyList<FileCandidate> files,
        TimeSpan delay) : IMetadataExtractionClient
    {
        private int _extractCalls;
        private int _concurrency;
        private int _maximumConcurrency;

        public int ExtractCalls => Volatile.Read(ref _extractCalls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "FIXTURE_READY", "ready"));

        public async ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _extractCalls);
            var concurrency = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(concurrency);
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                _ = files.Single(candidate =>
                    candidate.FullPath.Equals(request.FullPath, StringComparison.OrdinalIgnoreCase));
                return CompletedResponse(request);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (current >= value || Interlocked.CompareExchange(ref _maximumConcurrency, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"swsearch-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal FileCandidate CreateFile(
            string fileName,
            SolidWorksDocumentKind kind,
            int contentLength = 1)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            var content = new byte[contentLength];
            content[0] = 1;
            File.WriteAllBytes(path, content);
            var candidate = StableFileEvidenceReader.Capture(path).Candidate;
            Assert.Equal(kind, candidate.Kind);
            return candidate;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
