using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed record NoApiExtraction(
    IReadOnlyList<DocumentProperty> Properties,
    IReadOnlyList<DocumentReference> References,
    IReadOnlyList<string> ConfigurationNames,
    int? InternalVersion,
    NoApiContainerKind ContainerKind);

internal enum NoApiContainerKind
{
    ModernChunk,
    CompoundFile
}

internal interface INoApiMetadataExtractor
{
    NoApiExtraction Extract(ValidatedFile file, CancellationToken cancellationToken);
}

internal sealed class NoApiMetadataExtractor : INoApiMetadataExtractor
{
    private static readonly byte[] CompoundFileMagic =
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly NoApiExtractionLimits _limits;
    private readonly Func<string, FileStream> _openReadStream;

    public NoApiMetadataExtractor(NoApiExtractionLimits? limits = null)
        : this(limits, OpenReadStream)
    {
    }

    internal NoApiMetadataExtractor(
        NoApiExtractionLimits? limits,
        Func<string, FileStream> openReadStream)
    {
        _limits = limits ?? NoApiExtractionLimits.Default;
        _openReadStream = openReadStream ?? throw new ArgumentNullException(nameof(openReadStream));
    }

    public NoApiExtraction Extract(ValidatedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportedSolidWorksDocumentKinds.IsSupported(file.Candidate.Kind))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType);
        }

        try
        {
            using var stream = _openReadStream(file.FullPath);
            var probe = ProbeContainer(stream, cancellationToken);

            var openedLength = stream.Length;
            if (openedLength != probe.Length ||
                openedLength != file.Candidate.Size)
            {
                throw new MetadataWorkerException(
                    MetadataErrorCodes.FileChangedDuringRead,
                    retryable: true);
            }

            var extraction = probe.Kind == NoApiContainerKind.CompoundFile
                ? new CompoundFileMetadataReader(_limits)
                    .Extract(stream, file, cancellationToken)
                : new ModernSolidWorksMetadataReader(_limits)
                    .Extract(stream, file, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            // The request pipeline owns the authoritative before/after identity
            // and timestamp comparison.  Reuse the open handle here to reject a
            // size change without issuing a third FileInfo query per document.
            if (stream.Length != openedLength)
            {
                throw new MetadataWorkerException(
                    MetadataErrorCodes.FileChangedDuringRead,
                    retryable: true);
            }

            return extraction;
        }
        catch (MetadataWorkerException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.FileAccessDenied,
                innerException: exception);
        }
        catch (PathTooLongException exception)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.PathTooLong,
                innerException: exception);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.FileSharingViolation,
                retryable: true,
                innerException: exception);
        }
        catch (IOException exception)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.FileCorrupt,
                retryable: true,
                innerException: exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or OverflowException or ArgumentException)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.FileCorrupt,
                innerException: exception);
        }
    }

    private static ContainerProbe ProbeContainer(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var length = stream.Length;
        if (length < CompoundFileMagic.Length)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.FileCorrupt);
        }

        Span<byte> header = stackalloc byte[64];
        var headerLength = (int)Math.Min(header.Length, length);
        ReadExactly(stream, header[..headerLength], cancellationToken);
        if (header[..CompoundFileMagic.Length].SequenceEqual(CompoundFileMagic))
        {
            return new ContainerProbe(NoApiContainerKind.CompoundFile, length);
        }

        if (ModernSolidWorksContainer.HasMarker(header[..headerLength]))
        {
            return new ContainerProbe(NoApiContainerKind.ModernChunk, length);
        }

        throw new MetadataWorkerException(MetadataErrorCodes.NoApiFormatUnsupported);
    }

    private static FileStream OpenReadStream(string path)
    {
        // Container kind is only known after the initial header probe. Modern
        // readers are mostly sequential while compound-file readers seek among
        // sectors, so a neutral cache hint is safer than misclassifying either
        // access pattern before both operations share this one handle.
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.None);
    }

    private readonly record struct ContainerProbe(NoApiContainerKind Kind, long Length);

    internal static void ReadExactly(
        Stream stream,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }
    }
}

internal sealed record NoApiExtractionLimits(
    int MaximumStreamNameBytes,
    int MaximumStreamCount,
    int MaximumCompressedStreamBytes,
    int MaximumDecompressedStreamBytes,
    int MaximumTotalDecompressedBytes,
    int MaximumXmlCharacters,
    int MaximumPropertyCount,
    int MaximumConfigurationCount,
    int MaximumStringCharacters,
    int MaximumCompoundDirectoryBytes,
    int MaximumCompoundFatEntries,
    int MaximumCompoundMiniStreamBytes,
    int MaximumTreeDepth)
{
    public static NoApiExtractionLimits Default { get; } = new(
        MaximumStreamNameBytes: 512,
        MaximumStreamCount: 16_384,
        MaximumCompressedStreamBytes: 16 * 1024 * 1024,
        MaximumDecompressedStreamBytes: 32 * 1024 * 1024,
        MaximumTotalDecompressedBytes: 64 * 1024 * 1024,
        MaximumXmlCharacters: 8 * 1024 * 1024,
        MaximumPropertyCount: 4_096,
        MaximumConfigurationCount: 2_048,
        MaximumStringCharacters: 65_536,
        MaximumCompoundDirectoryBytes: 16 * 1024 * 1024,
        MaximumCompoundFatEntries: 4 * 1024 * 1024,
        MaximumCompoundMiniStreamBytes: 32 * 1024 * 1024,
        MaximumTreeDepth: 32);
}
