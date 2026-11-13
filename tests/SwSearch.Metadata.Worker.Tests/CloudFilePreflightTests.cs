using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class CloudFilePreflightTests
{
    [Fact]
    public void FullyLocalCloudPlaceholder_IsAllowedPastTheReparsePointGate()
    {
        var disposition = CloudFilePreflight.Classify(
            FileAttributes.ReparsePoint,
            CloudFilePreflight.Placeholder | 0x0000_0008);

        Assert.Equal(MetadataFileDisposition.Normal, disposition);
    }

    [Fact]
    public void PartialCloudPlaceholder_IsDeferredBeforeAnyContentRead()
    {
        var disposition = CloudFilePreflight.Classify(
            FileAttributes.ReparsePoint,
            CloudFilePreflight.Placeholder | CloudFilePreflight.Partial);

        Assert.Equal(MetadataFileDisposition.CloudPlaceholder, disposition);
    }

    [Fact]
    public void PartiallyOnDiskCloudPlaceholder_IsDeferredBeforeAnyContentRead()
    {
        var disposition = CloudFilePreflight.Classify(
            FileAttributes.ReparsePoint,
            CloudFilePreflight.Placeholder | CloudFilePreflight.PartiallyOnDisk);

        Assert.Equal(MetadataFileDisposition.CloudPlaceholder, disposition);
    }

    [Fact]
    public void RecallAttribute_RemainsConservativeWhenCloudApiStateIsUnavailable()
    {
        var disposition = CloudFilePreflight.Classify(
            (FileAttributes)0x0004_0000,
            placeholderState: null);

        Assert.Equal(MetadataFileDisposition.CloudPlaceholder, disposition);
    }

    [Fact]
    public void UnknownCloudApiState_PreservesExistingReparsePointFallback()
    {
        var disposition = CloudFilePreflight.Classify(
            FileAttributes.ReparsePoint,
            CloudFilePreflight.Invalid);

        Assert.Equal(MetadataFileDisposition.ReparsePoint, disposition);
    }

    [Fact]
    public void NormalLocalFile_UsesTheExistingNormalPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SwSearch.Metadata.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local.SLDPRT");
        File.WriteAllBytes(path, []);
        try
        {
            Assert.Equal(MetadataFileDisposition.Normal, MetadataFilePipeline.ReadDisposition(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineAttribute_IsDeferredBeforeTheExtractorIsCalled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SwSearch.Metadata.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "offline.SLDPRT");
        File.WriteAllBytes(path, []);
        var originalAttributes = File.GetAttributes(path);
        File.SetAttributes(path, originalAttributes | FileAttributes.Offline);
        try
        {
            var extractorCalled = false;
            var processor = new MetadataRequestProcessor(new FakeExtractionEngine((_, _) =>
            {
                extractorCalled = true;
                throw new InvalidOperationException("The cloud preflight must run first.");
            }));

            var result = await processor.ProcessAsync(
                MetadataExtractionEngineTests.Request(path),
                CancellationToken.None);

            Assert.Equal(MetadataPipelineOutcome.Deferred, result.Response.Outcome);
            Assert.Equal(MetadataFileDisposition.CloudPlaceholder, result.Response.Disposition);
            Assert.Equal("CLOUD_PLACEHOLDER_DEFERRED", result.Response.ErrorCode);
            Assert.True(result.Response.Retryable);
            Assert.False(extractorCalled);
        }
        finally
        {
            File.SetAttributes(path, originalAttributes);
            Directory.Delete(directory, recursive: true);
        }
    }
}
