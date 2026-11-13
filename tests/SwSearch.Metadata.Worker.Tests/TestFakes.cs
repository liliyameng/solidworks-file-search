using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

internal sealed class FakeFileSystemAccessor : IFileSystemAccessor
{
    public Func<string, SolidWorksDocumentKind, FileCandidate> Handler { get; set; } =
        (path, kind) => new FileCandidate(path, kind, 123, DateTimeOffset.UnixEpoch);

    public FileCandidate ReadCandidate(string fullPath, SolidWorksDocumentKind kind) =>
        Handler(fullPath, kind);
}

internal sealed class FakeKeyProvider : IDocumentManagerKeyProvider
{
    private readonly string? _key;

    public FakeKeyProvider(string? key)
    {
        _key = key;
    }

    public ValueTask<DocumentManagerKey?> TryGetKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _key is null ? null : new DocumentManagerKey(_key.AsSpan()));
    }
}

internal sealed class FakeSessionFactory : IDocumentManagerSessionFactory
{
    private readonly Func<IDocumentManagerSession> _factory;

    public FakeSessionFactory(bool isClassRegistered, Func<IDocumentManagerSession> factory)
    {
        IsClassRegistered = isClassRegistered;
        _factory = factory;
    }

    public bool IsClassRegistered { get; }

    public int CreateCount { get; private set; }

    public IDocumentManagerSession Create(DocumentManagerKey key)
    {
        CreateCount++;
        return _factory();
    }
}

internal sealed class FakeSession : IDocumentManagerSession
{
    private readonly Func<ValidatedFile, DocumentManagerExtraction> _extract;

    public FakeSession(Func<ValidatedFile, DocumentManagerExtraction> extract)
    {
        _extract = extract;
    }

    public DocumentManagerExtraction Extract(ValidatedFile file) => _extract(file);

    public void Dispose()
    {
    }
}

internal sealed class FakePropertyStore : IWindowsPropertyStoreExtractor
{
    private readonly PropertyStoreExtraction _result;

    public FakePropertyStore(PropertyStoreExtraction result)
    {
        _result = result;
    }

    public int ExtractCount { get; private set; }

    public PropertyStoreExtraction Extract(ValidatedFile file)
    {
        ExtractCount++;
        return _result;
    }
}

internal sealed class FakeExtractionEngine : IMetadataExtractionEngine
{
    private readonly Func<string, CancellationToken, DocumentMetadata> _extract;

    public FakeExtractionEngine(Func<string, CancellationToken, DocumentMetadata> extract)
    {
        _extract = extract;
    }

    public DocumentMetadata Extract(string fullPath, CancellationToken cancellationToken) =>
        _extract(fullPath, cancellationToken);
}

internal sealed class FakeNoApiExtractor : INoApiMetadataExtractor
{
    private readonly Func<ValidatedFile, CancellationToken, NoApiExtraction> _extract;

    public FakeNoApiExtractor(Func<ValidatedFile, CancellationToken, NoApiExtraction> extract)
    {
        _extract = extract;
    }

    public int ExtractCount { get; private set; }

    public NoApiExtraction Extract(ValidatedFile file, CancellationToken cancellationToken)
    {
        ExtractCount++;
        return _extract(file, cancellationToken);
    }
}
