using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed record ValidatedFile(string FullPath, FileCandidate Candidate);

internal interface IFileSystemAccessor
{
    FileCandidate ReadCandidate(string fullPath, SolidWorksDocumentKind kind);
}

internal sealed class PhysicalFileSystemAccessor : IFileSystemAccessor
{
    public FileCandidate ReadCandidate(string fullPath, SolidWorksDocumentKind kind)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                throw new MetadataWorkerException(MetadataErrorCodes.FileNotFound);
            }

            return new FileCandidate(
                fullPath,
                kind,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (MetadataWorkerException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.FileAccessDenied, innerException: exception);
        }
        catch (PathTooLongException exception)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.PathTooLong, innerException: exception);
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
    }
}

internal sealed class FileRequestValidator
{
    internal const int MaximumPathLength = 32_767;
    private readonly IFileSystemAccessor _fileSystem;

    public FileRequestValidator(IFileSystemAccessor fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public ValidatedFile Validate(string? suppliedPath)
    {
        if (string.IsNullOrWhiteSpace(suppliedPath) || suppliedPath.IndexOf('\0') >= 0)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.InvalidPath);
        }

        if (suppliedPath.Length > MaximumPathLength)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.PathTooLong);
        }

        if (IsDevicePath(suppliedPath))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.DevicePathRejected);
        }

        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(suppliedPath))
            {
                throw new MetadataWorkerException(MetadataErrorCodes.InvalidPath);
            }

            fullPath = Path.GetFullPath(suppliedPath);
        }
        catch (MetadataWorkerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.InvalidPath, innerException: exception);
        }

        if (IsDevicePath(fullPath))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.DevicePathRejected);
        }

        var kind = Path.GetExtension(fullPath).ToUpperInvariant() switch
        {
            ".SLDPRT" => SolidWorksDocumentKind.Part,
            ".SLDASM" => SolidWorksDocumentKind.Assembly,
            _ => throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType)
        };

        var candidate = _fileSystem.ReadCandidate(fullPath, kind);
        return new ValidatedFile(fullPath, candidate);
    }

    private static bool IsDevicePath(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase);
}
