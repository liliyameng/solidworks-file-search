using System.Runtime.CompilerServices;
using SwSearch.App;
using SwSearch.Broker;
using SwSearch.Contracts;
using SwSearch.Indexer;
using SwSearch.PropertyConfig;
using SwSearch.PropertyViewer;
using SwSearch.ShellRegistration;

namespace SwSearch.Integration.Tests;

public sealed class EndToEndRuntimeTests
{
    [Fact]
    public async Task BrokerPipeLibraryRequest_WithNoRoots_FailsClosedBeforeIndexQuery()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.PathFor("must-not-open.db");
        await using var runtime = Runtime(
            directory,
            new FakeDiscovery([]),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()),
            databasePath);
        await using var propertyConfig = new PropertyCardConfigProvider(new PropertyCardConfigProviderOptions(
            directory.PathFor("defaults.tsv"),
            directory.PathFor("property-config.json"),
            [],
            [],
            WatchForChanges: false));
        await using var host = new BrokerHost(runtime, propertyConfig);

        var requestId = Guid.NewGuid();
        var requestJson = System.Text.Json.JsonSerializer.Serialize(new BrokerRequest(
            ContractVersions.BrokerPipe,
            requestId,
            BrokerOperation.Search,
            new SearchQuery("0404-009", SearchMode.Library, LibraryRoots: Array.Empty<string>())),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(requestJson + Environment.NewLine));
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var parsed = await BrokerHost.ReadRequestAsync(reader, CancellationToken.None);
        var response = await host.DispatchForTestingAsync(parsed, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(requestId, response.RequestId);
        Assert.Empty(response.Hits!);
        Assert.Equal(0, response.TotalCount);
        Assert.False(response.HasMore);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task BrokerPipeSearch_FirstViewportReturnsWithoutExactTotalCount()
    {
        using var directory = new TestDirectory();
        var first = Candidate(directory.PathFor("Valve-A.SLDPRT"), SolidWorksDocumentKind.Part);
        var second = Candidate(directory.PathFor("Valve-B.SLDPRT"), SolidWorksDocumentKind.Part);
        await using var runtime = Runtime(
            directory,
            new FakeDiscovery([first, second]),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()));
        await runtime.RefreshAndProcessAsync(10);
        await using var propertyConfig = new PropertyCardConfigProvider(new PropertyCardConfigProviderOptions(
            directory.PathFor("defaults.tsv"),
            directory.PathFor("property-config.json"),
            [],
            [],
            WatchForChanges: false));
        await using var host = new BrokerHost(runtime, propertyConfig);

        var response = await host.DispatchForTestingAsync(new BrokerRequest(
            ContractVersions.BrokerPipe,
            Guid.NewGuid(),
            BrokerOperation.Search,
            new SearchQuery("Valve", SearchMode.FileName, Limit: 1)));

        Assert.True(response.Success);
        Assert.Single(response.Hits!);
        Assert.True(response.HasMore);
        Assert.Null(response.TotalCount);
    }

    [Fact]
    public async Task Refresh_ProcessesMetadata_AndSearchesDirectAndDrawingInheritedProperties()
    {
        using var directory = new TestDirectory();
        var model = Candidate(directory.PathFor("Valve.SLDPRT"), SolidWorksDocumentKind.Part);
        var drawing = Candidate(directory.PathFor("Valve.SLDDRW"), SolidWorksDocumentKind.Drawing);
        var discovery = new FakeDiscovery([model, drawing]);
        var metadata = new FakeMetadata(new Dictionary<string, DocumentMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [model.FullPath] = Metadata(model,
                [new("物料编码", "PN-100", "PN-100", PropertyScope.Document, null, MetadataSource.DocumentManager)]),
            [drawing.FullPath] = Metadata(drawing, [], [new(model.FullPath, null, true)])
        });

        await using var runtime = Runtime(directory, discovery, metadata);
        var capability = await runtime.RefreshAndProcessAsync(10);

        Assert.Equal(CapabilityState.Available, capability.State);
        Assert.Equal(2, metadata.ExtractCalls);
        Assert.Equal(0, (await runtime.Store.GetStatusAsync()).PendingMetadataCount);

        var property = await runtime.SearchAsync(
            new SearchQuery("PN-100", SearchMode.Property),
            CancellationToken.None);
        Assert.Equal(2, property.Count);
        var drawingHit = Assert.Single(property, hit => hit.Kind == SolidWorksDocumentKind.Drawing);
        Assert.Equal(model.FullPath, drawingHit.OriginModelPath, ignoreCase: true);

        var fileName = await runtime.SearchAsync(
            new SearchQuery("Valve", SearchMode.FileName),
            CancellationToken.None);
        Assert.Equal(2, fileName.Count);
        Assert.All(fileName, hit => Assert.Null(hit.MatchedProperty));

        var all = await runtime.SearchAsync(
            new SearchQuery("PN-100", SearchMode.All, Kinds: [SolidWorksDocumentKind.Drawing]),
            CancellationToken.None);
        Assert.Single(all);
        Assert.Equal(SolidWorksDocumentKind.Drawing, all[0].Kind);

        var drawingMetadata = await runtime.Store.TryGetMetadataAsync(drawing.FullPath, CancellationToken.None);
        var inherited = Assert.Single(
            drawingMetadata!.Properties,
            property => property.Scope == PropertyScope.ReferencedModel && property.Name == "物料编码");
        Assert.Equal(model.FullPath, inherited.OriginPath, ignoreCase: true);
    }

    [Fact]
    public async Task EverythingOffline_UsesSamePersistentCacheAndMarksHitsStale()
    {
        using var directory = new TestDirectory();
        var file = Candidate(directory.PathFor("Cached.SLDPRT"), SolidWorksDocumentKind.Part);
        var database = directory.PathFor("index.db");
        await using (var online = Runtime(
            directory,
            new FakeDiscovery([file]),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [file.FullPath] = Metadata(file,
                    [new("物料编码", "CACHE-1", "CACHE-1", PropertyScope.Document, null, MetadataSource.DocumentManager)])
            }),
            database))
        {
            await online.RefreshAndProcessAsync(10);
        }

        await using var offline = Runtime(
            directory,
            new FakeDiscovery([], new RuntimeCapability(
                CapabilityState.Unavailable,
                "EVERYTHING_IPC_NOT_FOUND",
                "Everything 不可用")),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()),
            database);
        var capability = await offline.RefreshAndProcessAsync(10);
        var hits = await offline.SearchAsync(
            new SearchQuery("CACHE-1", SearchMode.Property),
            CancellationToken.None);

        Assert.Equal(CapabilityState.Unavailable, capability.State);
        Assert.Single(hits);
        Assert.True(hits[0].CacheMayBeStale);
    }

    [Fact]
    public async Task EmptyQuery_ReturnsAllCachedSolidWorksFilesWithinLimit()
    {
        using var directory = new TestDirectory();
        var part = Candidate(directory.PathFor("A.SLDPRT"), SolidWorksDocumentKind.Part);
        var assembly = Candidate(directory.PathFor("B.SLDASM"), SolidWorksDocumentKind.Assembly);
        await using var runtime = Runtime(directory, new FakeDiscovery([part, assembly]), new FakeMetadata(
            new Dictionary<string, DocumentMetadata>()));
        await runtime.RefreshAndProcessAsync(10);

        var hits = await runtime.SearchAsync(
            new SearchQuery(string.Empty, SearchMode.All, Limit: 500),
            CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal(new[] { "A.SLDPRT", "B.SLDASM" }, hits.Select(static hit => hit.FileName));
    }

    [Fact]
    public async Task CachedSearchCompletesWhileBackgroundDiscoveryIsStillBlocked()
    {
        using var directory = new TestDirectory();
        var database = directory.PathFor("cache-first.db");
        var cached = Candidate(directory.PathFor("Cached Valve.SLDPRT"), SolidWorksDocumentKind.Part);
        await using (var seed = new SqliteIndexStore(database))
        {
            await seed.InitializeAsync();
            await seed.ApplyDiscoverySnapshotAsync(ToAsync([cached]), CancellationToken.None);
        }

        var discovery = new BlockingDiscovery();
        await using var runtime = Runtime(
            directory,
            discovery,
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()),
            database);
        using var stop = new CancellationTokenSource();
        var background = runtime.RunBackgroundAsync(stop.Token);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var hits = await runtime.SearchAsync(
                new SearchQuery(string.Empty, SearchMode.All, Limit: 500),
                CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var state = await runtime.GetStateAsync();

        Assert.Equal(cached.FullPath, Assert.Single(hits).FullPath, ignoreCase: true);
        Assert.True(state.CacheAvailable);
        Assert.True(state.BackgroundUpdating);

        stop.Cancel();
        await background;
    }

    [Fact]
    public async Task KeywordCacheHit_ReturnsWhileRealSharedWriteGateIsHeld_ThenPromotesInBackground()
    {
        using var directory = new TestDirectory();
        var cached = Candidate(directory.PathFor("Cached Valve P1.SLDPRT"), SolidWorksDocumentKind.Part);
        await using var runtime = Runtime(
            directory,
            new FakeDiscovery([]),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()));
        await runtime.Store.InitializeAsync();
        await runtime.Store.ApplyDiscoverySnapshotAsync(ToAsync([cached]), CancellationToken.None);
        await runtime.InitializeAsync();
        var beforePromotion = await runtime.GetStateAsync();

        var writeGate = await runtime.Store.AcquireWriteGateForTestingAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hits = await runtime.SearchAsync(
                new SearchQuery("Valve P1", SearchMode.FileName),
                CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));
        clock.Stop();

        Assert.Equal(cached.FullPath, Assert.Single(hits).FullPath, ignoreCase: true);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), $"cache hit took {clock.Elapsed}");
        Assert.Equal(1, runtime.SearchPromotionSnapshot.AcceptedCount);
        Assert.Equal(0, runtime.SearchPromotionSnapshot.CompletedCount);

        await writeGate.DisposeAsync();
        await WaitUntilAsync(
            () => runtime.SearchPromotionSnapshot.CompletedCount == 1,
            TimeSpan.FromSeconds(2));
        var afterPromotion = await runtime.GetStateAsync();
        Assert.Equal(beforePromotion.Revision, afterPromotion.Revision);

        var lease = Assert.Single(await runtime.Store.LeaseMetadataAsync(
            10,
            TimeSpan.FromSeconds(5),
            "promotion-test"));
        Assert.Equal(cached.FullPath, lease.FullPath, ignoreCase: true);
        Assert.Equal("ActiveSearch", lease.Reason);
    }

    [Fact]
    public async Task RepeatedSearchPromotionCannotFeedBackIntoRevision_ActualPropertyCommitPublishesOnce()
    {
        using var directory = new TestDirectory();
        var file = Candidate(directory.PathFor("Feedback-Loop.SLDPRT"), SolidWorksDocumentKind.Part);
        var metadata = new FakeMetadata(new Dictionary<string, DocumentMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [file.FullPath] = Metadata(file,
                [new("Code", "REV-ONE", "REV-ONE", PropertyScope.Document, null, MetadataSource.DocumentManager)])
        });
        await using var runtime = Runtime(directory, new FakeDiscovery([]), metadata);
        await runtime.Store.InitializeAsync();
        await runtime.Store.ApplyDiscoverySnapshotAsync(ToAsync([file]), CancellationToken.None);
        await runtime.InitializeAsync();
        var before = await runtime.GetStateAsync();

        for (var round = 1; round <= 11; round++)
        {
            var hits = await runtime.SearchAsync(
                new SearchQuery("Feedback-Loop", SearchMode.FileName),
                CancellationToken.None);
            Assert.Single(hits);
            await WaitUntilAsync(
                () => runtime.SearchPromotionSnapshot.CompletedCount >= round,
                TimeSpan.FromSeconds(2));
        }

        Assert.Equal(before.Revision, (await runtime.GetStateAsync()).Revision);

        Assert.Equal(1, await runtime.ProcessPendingMetadataAsync(1));
        var afterProperty = await runtime.GetStateAsync();
        Assert.Equal(before.Revision + 1, afterProperty.Revision);
        Assert.Equal(0, await runtime.ProcessPendingMetadataAsync(1));
        Assert.Equal(afterProperty.Revision, (await runtime.GetStateAsync()).Revision);

        _ = await runtime.SearchAsync(
            new SearchQuery("Feedback-Loop", SearchMode.FileName),
            CancellationToken.None);
        await WaitUntilAsync(
            () => runtime.SearchPromotionSnapshot.CompletedCount >= 12,
            TimeSpan.FromSeconds(2));
        Assert.Equal(afterProperty.Revision, (await runtime.GetStateAsync()).Revision);
    }

    [Fact]
    public async Task RuntimeState_BeforeInitializationIsExplicitlyStartingAndCacheUnavailable()
    {
        using var directory = new TestDirectory();
        await using var runtime = Runtime(
            directory,
            new FakeDiscovery([]),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()));

        var state = await runtime.GetStateAsync();

        Assert.Equal(BrokerRuntimePhase.Starting, state.Phase);
        Assert.False(state.CacheAvailable);
        Assert.False(File.Exists(runtime.Store.DatabasePath));
        await runtime.InitializeAsync();
        Assert.True(File.Exists(runtime.Store.DatabasePath));
    }

    [Fact]
    public async Task RuntimeState_DistinguishesFirstIndexAndCommittedRevision()
    {
        using var directory = new TestDirectory();
        var file = Candidate(directory.PathFor("Revision.SLDPRT"), SolidWorksDocumentKind.Part);
        await using var runtime = Runtime(
            directory,
            new FakeDiscovery([file]),
            new FakeMetadata(new Dictionary<string, DocumentMetadata>()));
        await runtime.InitializeAsync();

        var empty = await runtime.GetStateAsync();
        await runtime.RefreshAndProcessAsync(10);
        var refreshed = await runtime.GetStateAsync();

        Assert.Equal(BrokerRuntimePhase.FirstIndex, empty.Phase);
        Assert.False(empty.CacheAvailable);
        Assert.True(refreshed.CacheAvailable);
        Assert.True(refreshed.Revision > empty.Revision);
        Assert.Equal(empty.Epoch, refreshed.Epoch);
    }

    [Fact]
    public async Task PropertyViewerClient_IsCacheFirst_AndOnDemandWritesUnindexedFile()
    {
        using var directory = new TestDirectory();
        var cached = PhysicalCandidate(directory, "Cached.SLDPRT", SolidWorksDocumentKind.Part);
        var onDemand = PhysicalCandidate(directory, "OnDemand.SLDASM", SolidWorksDocumentKind.Assembly);
        await using var store = new SqliteIndexStore(directory.PathFor("viewer.db"));
        await store.InitializeAsync();
        await store.ApplyDiscoverySnapshotAsync(ToAsync([cached]), CancellationToken.None);
        await store.UpsertMetadataAsync(Metadata(cached,
            [new("名称", "阀体", "阀体", PropertyScope.Document, null, MetadataSource.DocumentManager)]),
            CancellationToken.None);

        var metadata = new FakeMetadata(new Dictionary<string, DocumentMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [onDemand.FullPath] = Metadata(onDemand,
                [new("物料编码", "OD-2", "OD-2", PropertyScope.Document, null, MetadataSource.DocumentManager)])
        });
        await using var runtime = new IndexingRuntime(
            store,
            new FakeDiscovery([]),
            metadata,
            new IndexingRuntimeOptions(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(5), 10),
            new RuntimeFileLog(directory.PathFor("viewer-logs")));

        Assert.NotNull(await store.TryGetMetadataAsync(cached.FullPath, CancellationToken.None));
        Assert.Equal(0, metadata.ExtractCalls);

        Assert.Null(await store.TryGetMetadataAsync(onDemand.FullPath, CancellationToken.None));
        var extracted = await runtime.RefreshMetadataAsync(onDemand.FullPath, CancellationToken.None);
        Assert.NotNull(extracted);
        Assert.Equal(1, metadata.ExtractCalls);
        var persisted = await store.TryGetMetadataAsync(onDemand.FullPath, CancellationToken.None);
        Assert.Equal("OD-2", Assert.Single(persisted!.Properties).ResolvedValue);
    }

    [Fact]
    public void PublishedShellPlan_UsesActualSiblingPropertyViewerPath()
    {
        using var directory = new TestDirectory();
        var viewer = directory.PathFor("SwSearch.PropertyViewer.exe");
        File.WriteAllBytes(viewer, [0x4D, 0x5A]);

        var plan = RegistrationPlanFactory.Create(viewer);

        Assert.Equal($"\"{Path.GetFullPath(viewer)}\" \"%1\"", plan.CommandValue);
        Assert.Equal(3, plan.OwnedVerbKeys.Count);
        Assert.All(plan.OwnedVerbKeys, key => Assert.Contains("SwSearch.ViewProperties", key));
    }

    private static IndexingRuntime Runtime(
        TestDirectory directory,
        IFileDiscoveryProvider discovery,
        IMetadataExtractionClient metadata,
        string? database = null) =>
        new(
            new SqliteIndexStore(database ?? directory.PathFor("index.db")),
            discovery,
            metadata,
            new IndexingRuntimeOptions(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(5), 10),
            new RuntimeFileLog(directory.PathFor("logs")));

    private static FileCandidate Candidate(string path, SolidWorksDocumentKind kind)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "fixture");
        var info = new FileInfo(fullPath);
        return new FileCandidate(
            fullPath,
            kind,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static FileCandidate PhysicalCandidate(
        TestDirectory directory,
        string name,
        SolidWorksDocumentKind kind)
    {
        var path = directory.PathFor(name);
        File.WriteAllText(path, "fixture");
        var info = new FileInfo(path);
        return new FileCandidate(path, kind, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static DocumentMetadata Metadata(
        FileCandidate file,
        IReadOnlyList<DocumentProperty> properties,
        IReadOnlyList<DocumentReference>? references = null) =>
        new(
            file,
            properties,
            references ?? [],
            MetadataSource.DocumentManager,
            DateTimeOffset.UtcNow);

    private static async IAsyncEnumerable<FileCandidate> ToAsync(
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= stopAt)
            {
                throw new TimeoutException("Condition did not become true within the bounded test interval.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeDiscovery(
        IReadOnlyList<FileCandidate> files,
        RuntimeCapability? capability = null) : IFileDiscoveryProvider
    {
        private readonly RuntimeCapability _capability = capability ?? new(
            CapabilityState.Available,
            "EVERYTHING_IPC_READY",
            "Everything 已就绪");

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_capability);

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

    private sealed class FakeMetadata(
        IReadOnlyDictionary<string, DocumentMetadata> metadata) : IMetadataExtractionClient
    {
        public int ExtractCalls { get; private set; }

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "SWDM_AVAILABLE", "ready"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            ExtractCalls++;
            if (metadata.TryGetValue(request.FullPath, out var value))
            {
                var evidence = StableFileEvidenceReader.Capture(request.FullPath);
                var contractEvidence = new MetadataFileEvidence(
                    evidence.RequestedPath,
                    evidence.FinalPath,
                    evidence.Kind,
                    evidence.Size,
                    evidence.LastWriteTimeUtc,
                    evidence.FileIdentity);
                return ValueTask.FromResult(new MetadataResponse(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    value with { File = evidence.Candidate },
                    ErrorCode: null,
                    ChineseErrorMessage: null,
                    Retryable: false,
                    PipelineLeaseId: request.PipelineLeaseId,
                    Outcome: MetadataPipelineOutcome.Completed,
                    BeforeEvidence: contractEvidence,
                    AfterEvidence: contractEvidence));
            }

            return ValueTask.FromResult(new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                null,
                "NOT_FOUND",
                "fixture missing",
                false));
        }
    }

    private sealed class BlockingDiscovery : IFileDiscoveryProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "BLOCKING_DISCOVERY", "fixture"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "swsearch-integration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string PathFor(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
