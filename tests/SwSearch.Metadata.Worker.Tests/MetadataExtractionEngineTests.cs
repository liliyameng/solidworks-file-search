using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class MetadataExtractionEngineTests
{
    [Fact]
    public void NoApiResultIsTheProductionPrimaryAndSkipsWindowsFallback()
    {
        var property = new DocumentProperty(
            "物料编码",
            "0404",
            "0404",
            PropertyScope.Configuration,
            "默认",
            MetadataSource.NoApi);
        var noApi = new FakeNoApiExtractor((_, _) => new NoApiExtraction(
            [property],
            Array.Empty<DocumentReference>(),
            ["默认"],
            17_000,
            NoApiContainerKind.ModernChunk));
        var propertyStore = new FakePropertyStore(
            new PropertyStoreExtraction(Array.Empty<DocumentProperty>(), null));
        var engine = new MetadataExtractionEngine(
            new FileRequestValidator(new FakeFileSystemAccessor()),
            noApi,
            propertyStore);

        var result = engine.Extract(@"C:\design\fixture.SLDPRT", CancellationToken.None);

        Assert.Equal(MetadataSource.NoApi, result.Source);
        Assert.Null(result.WarningCode);
        Assert.Equal([property], result.Properties);
        Assert.Equal(["默认"], result.ConfigurationNames);
        Assert.Equal(1, noApi.ExtractCount);
        Assert.Equal(0, propertyStore.ExtractCount);
    }

    [Fact]
    public void UnsupportedNoApiVersionFallsBackWithoutDocumentManagerKeyWarning()
    {
        var propertyStore = new FakePropertyStore(
            new PropertyStoreExtraction(Array.Empty<DocumentProperty>(), MetadataErrorCodes.PropertyStoreEmpty));
        var engine = new MetadataExtractionEngine(
            new FileRequestValidator(new FakeFileSystemAccessor()),
            new FakeNoApiExtractor((_, _) => throw new MetadataWorkerException(
                MetadataErrorCodes.NoApiVersionUnsupported)),
            propertyStore);

        var result = engine.Extract(@"C:\design\future.SLDPRT", CancellationToken.None);

        Assert.Equal(MetadataSource.FileSystemOnly, result.Source);
        Assert.Equal(MetadataErrorCodes.NoApiVersionUnsupported, result.WarningCode);
        Assert.NotEqual(MetadataErrorCodes.DocumentManagerKeyMissing, result.WarningCode);
        Assert.Equal(1, propertyStore.ExtractCount);
    }

    [Fact]
    public void MissingKeyFallsBackWithoutClaimingThereAreNoProperties()
    {
        var property = new DocumentProperty(
            "System.Title",
            "夹具",
            "夹具",
            PropertyScope.Document,
            null,
            MetadataSource.WindowsPropertyStore);
        var propertyStore = new FakePropertyStore(
            new PropertyStoreExtraction(new[] { property }, WarningCode: null));
        var sessionFactory = new FakeSessionFactory(
            isClassRegistered: true,
            () => throw new InvalidOperationException());
        var engine = CreateEngine(
            new FakeKeyProvider(key: null),
            sessionFactory,
            propertyStore);

        var result = engine.Extract(@"C:\design\fixture.SLDPRT", CancellationToken.None);

        Assert.Equal(MetadataSource.WindowsPropertyStore, result.Source);
        Assert.Equal(MetadataErrorCodes.DocumentManagerKeyMissing, result.WarningCode);
        Assert.Single(result.Properties);
        Assert.Equal(0, sessionFactory.CreateCount);
    }

    [Fact]
    public void DocumentManagerResultPreservesMetadataAndDiscardsLegacyReferences()
    {
        var properties = new[]
        {
            new DocumentProperty("图号", "P-100", "P-100", PropertyScope.Document, null, MetadataSource.DocumentManager),
            new DocumentProperty("名称", "$PRP:model", "轴套", PropertyScope.Configuration, "加工", MetadataSource.DocumentManager)
        };
        var references = new[]
        {
            new DocumentReference(@"C:\design\model.SLDPRT", "加工", true)
        };
        var propertyStore = new FakePropertyStore(
            new PropertyStoreExtraction(Array.Empty<DocumentProperty>(), MetadataErrorCodes.PropertyStoreEmpty));
        var sessionFactory = new FakeSessionFactory(
            isClassRegistered: true,
            () => new FakeSession(_ => new DocumentManagerExtraction(
                properties,
                references,
                new[] { "加工", "采购" })));
        var engine = CreateEngine(
            new FakeKeyProvider("protected-test-key"),
            sessionFactory,
            propertyStore);

        var result = engine.Extract(@"C:\design\part.SLDPRT", CancellationToken.None);

        Assert.Equal(MetadataSource.DocumentManager, result.Source);
        Assert.Null(result.WarningCode);
        Assert.Equal(properties, result.Properties);
        Assert.Empty(result.References);
        Assert.Equal(new[] { "加工", "采购" }, result.ConfigurationNames);
        Assert.Equal(0, propertyStore.ExtractCount);
    }

    [Fact]
    public void LegacyDrawingPathIsRejectedBeforeMetadataExtraction()
    {
        var propertyStore = new FakePropertyStore(
            new PropertyStoreExtraction(Array.Empty<DocumentProperty>(), MetadataErrorCodes.PropertyStoreEmpty));
        var sessionFactory = new FakeSessionFactory(
            isClassRegistered: true,
            () => new FakeSession(_ => throw new InvalidOperationException()));
        var engine = CreateEngine(
            new FakeKeyProvider("protected-test-key"),
            sessionFactory,
            propertyStore);

        var exception = Assert.Throws<MetadataWorkerException>(() =>
            engine.Extract(@"C:\design\legacy.SLDDRW", CancellationToken.None));

        Assert.Equal(MetadataErrorCodes.UnsupportedFileType, exception.Code);
        Assert.Equal(0, sessionFactory.CreateCount);
        Assert.Equal(0, propertyStore.ExtractCount);

        var evidence = new MetadataFileEvidence(
            @"C:\design\legacy.SLDDRW",
            @"C:\design\legacy.SLDDRW",
            SolidWorksDocumentKind.Drawing,
            0,
            DateTimeOffset.UnixEpoch,
            "legacy");
        var evidenceException = Assert.Throws<MetadataWorkerException>(() =>
            ((IEvidenceAwareMetadataExtractionEngine)engine).Extract(evidence, CancellationToken.None));

        Assert.Equal(MetadataErrorCodes.UnsupportedFileType, evidenceException.Code);
    }

    [Fact]
    public async Task PermissionFailureReturnsStableChineseError()
    {
        var fileSystem = new FakeFileSystemAccessor
        {
            Handler = (_, _) => throw new MetadataWorkerException(MetadataErrorCodes.FileAccessDenied)
        };
        var engine = new MetadataExtractionEngine(
            new FileRequestValidator(fileSystem),
            new FakeKeyProvider(null),
            new FakeSessionFactory(false, () => throw new InvalidOperationException()),
            new FakePropertyStore(new PropertyStoreExtraction(Array.Empty<DocumentProperty>(), null)));
        var processor = new MetadataRequestProcessor(engine);
        var request = Request(@"C:\restricted\part.SLDPRT");

        var result = await processor.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(MetadataErrorCodes.FileAccessDenied, result.Response.ErrorCode);
        Assert.Equal("没有权限读取该文件。", result.Response.ChineseErrorMessage);
        Assert.False(result.RequiresWorkerRestart);
    }

    [Fact]
    public void CorruptDocumentManagerReadFallsBackAndPreservesFailureWarning()
    {
        var sessionFactory = new FakeSessionFactory(
            isClassRegistered: true,
            () => new FakeSession(_ => throw new MetadataWorkerException(MetadataErrorCodes.FileCorrupt)));
        var propertyStore = new FakePropertyStore(
            new PropertyStoreExtraction(
                Array.Empty<DocumentProperty>(),
                MetadataErrorCodes.PropertyStoreEmpty));
        var engine = CreateEngine(
            new FakeKeyProvider("protected-test-key"),
            sessionFactory,
            propertyStore);

        var result = engine.Extract(@"C:\design\broken.SLDPRT", CancellationToken.None);

        Assert.Equal(MetadataSource.FileSystemOnly, result.Source);
        Assert.Equal(MetadataErrorCodes.FileCorrupt, result.WarningCode);
        Assert.Empty(result.Properties);
    }

    [Fact]
    public void RealWindowsPropertyFallbackReturnsBasicMetadataWithoutDocumentManager()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SwSearch.Metadata.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "空白样本.SLDPRT");
        File.WriteAllBytes(path, Array.Empty<byte>());
        try
        {
            var engine = new MetadataExtractionEngine(
                new FileRequestValidator(new PhysicalFileSystemAccessor()),
                new FakeKeyProvider(null),
                new FakeSessionFactory(false, () => throw new InvalidOperationException()),
                new WindowsPropertyStoreExtractor());

            var result = engine.Extract(path, CancellationToken.None);

            Assert.Equal(path, result.File.FullPath);
            Assert.Equal(SolidWorksDocumentKind.Part, result.File.Kind);
            Assert.Contains(
                result.Source,
                new[] { MetadataSource.WindowsPropertyStore, MetadataSource.FileSystemOnly });
            Assert.Equal(MetadataErrorCodes.DocumentManagerUnavailable, result.WarningCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MetadataExtractionEngine CreateEngine(
        IDocumentManagerKeyProvider keyProvider,
        IDocumentManagerSessionFactory sessionFactory,
        IWindowsPropertyStoreExtractor propertyStore) =>
        new(
            new FileRequestValidator(new FakeFileSystemAccessor()),
            keyProvider,
            sessionFactory,
            propertyStore);

    internal static MetadataRequest Request(string path, int timeout = 5_000) =>
        new(ContractVersions.MetadataPipe, Guid.NewGuid(), path, ForceRefresh: false, timeout);
}
