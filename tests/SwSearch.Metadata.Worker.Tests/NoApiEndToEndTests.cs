using System.Runtime.CompilerServices;
using SwSearch.Contracts;
using SwSearch.Indexer;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class NoApiEndToEndTests
{
    [Fact]
    public async Task EverythingUnavailable_BuiltInDiscoveryIndexesModernAndCompoundPropertiesEndToEnd()
    {
        using var modern = new TemporarySolidWorksFile("modern-0404.SLDPRT");
        modern.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/Config-0-Properties.xml"] = System.Text.Encoding.UTF8.GetBytes("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <ConfigProperties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
                      <propertySection name="UserDefinedProperties">
                        <property name="二级分类" pid="5"><vt:lpstr>0404等径接头</vt:lpstr></property>
                      </propertySection>
                    </ConfigProperties>
                    """),
                ["docProps/ISolidWorksInformation.xml"] = System.Text.Encoding.UTF8.GetBytes("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <Information xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
                      <property name="SW-Configuration Name"><vt:lpstr>默认</vt:lpstr></property>
                    </Information>
                    """),
            }));
        using var compound = new TemporarySolidWorksFile("legacy.SLDPRT");
        compound.Write(SyntheticCompoundFile.Build(
            SyntheticOlePropertySet.Build("物料编码", "LEGACY-0404", "LEGACY-0404")));

        var roots = new[]
        {
            Path.GetDirectoryName(modern.Path)!,
            Path.GetDirectoryName(compound.Path)!,
        };
        var accelerator = new UnavailableDiscovery();
        await using var builtIn = new BuiltInFileDiscoveryProvider(new BuiltInDiscoveryOptions
        {
            DiscoverAllLocalVolumes = false,
            ExplicitLocalRoots = roots,
            CaptureFileIdentity = false,
            MaximumEntriesPerSecond = 0,
            YieldEveryEntries = 1,
            YieldDelay = TimeSpan.Zero,
        });
        await using var discovery = new FallbackDiscoveryProvider(accelerator, builtIn);
        var database = Path.Combine(Path.GetTempPath(), "SwSearch.NoApi.E2E", Guid.NewGuid().ToString("N"), "index.db");
        var store = new SqliteIndexStore(database);
        var engine = new MetadataExtractionEngine(
            new FileRequestValidator(new PhysicalFileSystemAccessor()),
            new NoApiMetadataExtractor(),
            new FakePropertyStore(new PropertyStoreExtraction(
                Array.Empty<DocumentProperty>(),
                MetadataErrorCodes.PropertyStoreEmpty)));
        try
        {
            await using var runtime = new IndexingRuntime(
                store,
                discovery,
                new InProcessNoApiClient(engine),
                new IndexingRuntimeOptions(
                    TimeSpan.FromHours(6),
                    TimeSpan.FromMilliseconds(1),
                    MetadataBatchSize: 8,
                    MaximumFilesPerSecond: 100,
                    MaximumReadBytesPerSecond: 100 * 1024 * 1024,
                    MaximumWriteBytesPerSecond: 100 * 1024 * 1024));
            var capability = await runtime.RefreshAndProcessAsync(8, CancellationToken.None);

            Assert.Equal(CapabilityState.Available, capability.State);
            Assert.Equal("BUILTIN_DISCOVERY_FALLBACK", discovery.LastCapability.Code);
            Assert.Equal(2, (await runtime.Store.GetStatusAsync()).PresentFileCount);
            Assert.Equal(0, (await runtime.Store.GetStatusAsync()).PendingMetadataCount);

            var modernProperty = await runtime.SearchAsync(
                new SearchQuery("0404等径接头", SearchMode.Property),
                CancellationToken.None);
            Assert.Contains(modernProperty, hit =>
                hit.FullPath.Equals(modern.Path, StringComparison.OrdinalIgnoreCase) &&
                hit.MatchedProperty == "二级分类");

            var legacyProperty = await runtime.SearchAsync(
                new SearchQuery("LEGACY-0404", SearchMode.All),
                CancellationToken.None);
            Assert.Contains(legacyProperty, hit =>
                hit.FullPath.Equals(compound.Path, StringComparison.OrdinalIgnoreCase) &&
                hit.MatchedProperty == "物料编码");

            var filename = await runtime.SearchAsync(
                new SearchQuery("modern-0404", SearchMode.FileName),
                CancellationToken.None);
            Assert.Single(filename);

            var viewerModel = await runtime.Store.TryGetMetadataAsync(modern.Path, CancellationToken.None);
            Assert.NotNull(viewerModel);
            Assert.Equal(MetadataSource.PersistentCache, viewerModel.Source);
            Assert.Contains(viewerModel.Properties, property =>
                property.Name == "二级分类" && property.ResolvedValue == "0404等径接头");
        }
        finally
        {
            var directory = Path.GetDirectoryName(database)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class UnavailableDiscovery : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Degraded,
                "EVERYTHING_IPC_NOT_FOUND",
                "injected"));
        }

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class InProcessNoApiClient(IMetadataExtractionEngine engine) : IMetadataExtractionClient
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RuntimeCapability(
                CapabilityState.Available,
                "NOAPI_AVAILABLE",
                "ready"));
        }

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                var metadata = engine.Extract(request.FullPath, cancellationToken);
                return ValueTask.FromResult(new MetadataResponse(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    metadata,
                    ErrorCode: null,
                    ChineseErrorMessage: null,
                    Retryable: false));
            }
            catch (MetadataWorkerException exception)
            {
                return ValueTask.FromResult(new MetadataResponse(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    Metadata: null,
                    exception.Code,
                    MetadataErrorCodes.ChineseMessage(exception.Code),
                    exception.Retryable));
            }
        }
    }
}
