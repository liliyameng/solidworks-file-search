using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class FileRequestValidatorTests
{
    [Theory]
    [InlineData(@"\\?\C:\design\part.SLDPRT", MetadataErrorCodes.DevicePathRejected)]
    [InlineData(@"\\.\PhysicalDrive0", MetadataErrorCodes.DevicePathRejected)]
    [InlineData(@"C:\design\notes.txt", MetadataErrorCodes.UnsupportedFileType)]
    [InlineData(@"C:\design\legacy.SLDDRW", MetadataErrorCodes.UnsupportedFileType)]
    [InlineData(@"relative\part.SLDPRT", MetadataErrorCodes.InvalidPath)]
    public void RejectsUnsafeOrUnsupportedPaths(string path, string expectedCode)
    {
        var validator = new FileRequestValidator(new FakeFileSystemAccessor());

        var exception = Assert.Throws<MetadataWorkerException>(() => validator.Validate(path));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void PipelineRejectsLegacyDrawingPathBeforeFilePreflight()
    {
        var exception = Assert.Throws<MetadataWorkerException>(() =>
            MetadataFilePipeline.ValidateAndNormalizePath(@"C:\design\legacy.SLDDRW"));

        Assert.Equal(MetadataErrorCodes.UnsupportedFileType, exception.Code);
    }

    [Fact]
    public void AcceptsUncPathAndNormalizesExtensionCase()
    {
        var fileSystem = new FakeFileSystemAccessor();
        var validator = new FileRequestValidator(fileSystem);

        var result = validator.Validate(@"\\server\share\设计\零件.sldprt");

        Assert.Equal(SolidWorksDocumentKind.Part, result.Candidate.Kind);
        Assert.Equal(@"\\server\share\设计\零件.sldprt", result.FullPath);
    }
}
