using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class LateBoundDocumentManagerSessionTests
{
    [Fact]
    public void LegacyDrawingCandidateIsRejectedBeforeOpeningDocument()
    {
        var document = new FakeComDocument();
        var application = new FakeComApplication(document);
        using var session = new LateBoundDocumentManagerSession(new object(), application);
        var file = new ValidatedFile(
            @"C:\design\legacy.SLDDRW",
            new FileCandidate(
                @"C:\design\legacy.SLDDRW",
                SolidWorksDocumentKind.Drawing,
                0,
                DateTimeOffset.UtcNow));

        var exception = Assert.Throws<MetadataWorkerException>(() => session.Extract(file));

        Assert.Equal(MetadataErrorCodes.UnsupportedFileType, exception.Code);
        Assert.Equal(0, application.GetDocumentCallCount);
        Assert.False(document.Closed);
    }

    [Fact]
    public void PartExtractionReadsDocumentAndConfigurationPropertiesWithoutReferences()
    {
        var document = new FakeComDocument();
        var application = new FakeComApplication(document);
        using var session = new LateBoundDocumentManagerSession(new object(), application);
        var file = new ValidatedFile(
            @"C:\design\part.SLDPRT",
            new FileCandidate(
                @"C:\design\part.SLDPRT",
                SolidWorksDocumentKind.Part,
                0,
                DateTimeOffset.UtcNow));

        var result = session.Extract(file);

        Assert.Equal(1, application.GetDocumentCallCount);
        Assert.Contains(result.Properties, property =>
            property.Scope == PropertyScope.Configuration &&
            property.ConfigurationName == "Machined" &&
            property.Name == "Name" &&
            property.RawValue == "$PRP:description" &&
            property.ResolvedValue == "Sleeve");
        Assert.Equal(["Machined"], result.ConfigurationNames);
        Assert.Empty(result.References);
        Assert.True(document.Closed);
    }

    public sealed class FakeComApplication
    {
        private readonly FakeComDocument _document;

        public FakeComApplication(FakeComDocument document)
        {
            _document = document;
        }

        public int GetDocumentCallCount { get; private set; }

        public object GetDocument(string path, int documentType, bool readOnly, out object openError)
        {
            GetDocumentCallCount++;
            openError = 0;
            return _document;
        }
    }

    public sealed class FakeComDocument
    {
        public FakeComDocument()
        {
            ConfigurationManager = new FakeComConfigurationManager();
        }

        public FakeComConfigurationManager ConfigurationManager { get; }

        public bool Closed { get; private set; }

        public object GetCustomPropertyNames() => new[] { "Number" };

        public string GetCustomPropertyValues(string name, out object type, out string linkedTo)
        {
            type = 30;
            linkedTo = "$PRP:model-code";
            return "A-100";
        }

        public void CloseDoc() => Closed = true;
    }

    public sealed class FakeComConfigurationManager
    {
        public object GetConfigurationNames() => new[] { "Machined" };

        public object GetConfigurationByName(string name) => new FakeComConfiguration();
    }

    public sealed class FakeComConfiguration
    {
        public object GetCustomPropertyNames() => new[] { "Name" };

        public string GetCustomPropertyValues(string name, out object type, out string linkedTo)
        {
            type = 30;
            linkedTo = "$PRP:description";
            return "Sleeve";
        }
    }
}
