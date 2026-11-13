using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal static class MetadataFilePipeline
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileNameNormalized = 0;

    internal static string ValidateAndNormalizePath(string? suppliedPath)
    {
        if (string.IsNullOrWhiteSpace(suppliedPath) ||
            suppliedPath.IndexOf('\0') >= 0 ||
            suppliedPath.Length > FileRequestValidator.MaximumPathLength ||
            !Path.IsPathFullyQualified(suppliedPath) ||
            IsDevicePath(suppliedPath))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.InvalidPath);
        }

        try
        {
            var fullPath = Path.GetFullPath(suppliedPath);
            if (IsDevicePath(fullPath))
            {
                throw new MetadataWorkerException(MetadataErrorCodes.DevicePathRejected);
            }
            _ = ParseKind(fullPath);
            return fullPath;
        }
        catch (MetadataWorkerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.InvalidPath,
                innerException: exception);
        }
    }

    internal static MetadataFileDisposition ReadDisposition(string suppliedPath)
        => CloudFilePreflight.ReadDisposition(suppliedPath);

    internal static MetadataFileEvidence Capture(string suppliedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        var requestedPath = Path.GetFullPath(suppliedPath);
        var kind = ParseKind(requestedPath);
        if (!OperatingSystem.IsWindows())
        {
            var info = new FileInfo(requestedPath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The SOLIDWORKS file does not exist.", requestedPath);
            }

            return new MetadataFileEvidence(
                requestedPath,
                requestedPath,
                kind,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                $"PORTABLE:{info.CreationTimeUtc.Ticks:X16}");
        }

        using var handle = CreateFileW(
            ToExtendedPath(requestedPath),
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open '{requestedPath}'.");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot identify '{requestedPath}'.");
        }

        var finalPath = ReadFinalPath(handle);
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        var size = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var writeTime = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        return new MetadataFileEvidence(
            requestedPath,
            finalPath,
            kind,
            size,
            new DateTimeOffset(DateTime.FromFileTimeUtc(writeTime), TimeSpan.Zero),
            $"{information.VolumeSerialNumber:X8}:{fileIndex:X16}");
    }

    internal static bool SameObjectAndVersion(
        MetadataFileEvidence left,
        MetadataFileEvidence right) =>
        string.Equals(left.FinalPath, right.FinalPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal) &&
        left.Size == right.Size &&
        left.LastWriteTimeUtc.ToUniversalTime().Ticks ==
        right.LastWriteTimeUtc.ToUniversalTime().Ticks;

    internal static bool Matches(MetadataFileEvidence evidence, FileCandidate candidate) =>
        candidate.Kind == evidence.Kind &&
        candidate.Size == evidence.Size &&
        candidate.LastWriteTimeUtc.ToUniversalTime().Ticks ==
        evidence.LastWriteTimeUtc.ToUniversalTime().Ticks &&
        string.Equals(
            Path.GetFullPath(candidate.FullPath),
            evidence.RequestedPath,
            StringComparison.OrdinalIgnoreCase);

    internal static string Fingerprint(MetadataFileEvidence evidence, int extractorVersion) =>
        $"{evidence.Size}:{evidence.LastWriteTimeUtc.ToUniversalTime().Ticks}:" +
        $"{evidence.FileIdentity}:{extractorVersion}";

    private static SolidWorksDocumentKind ParseKind(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".SLDPRT" => SolidWorksDocumentKind.Part,
            ".SLDASM" => SolidWorksDocumentKind.Assembly,
            _ => throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType),
        };

    private static bool IsDevicePath(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase);

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                FileNameNormalized);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot resolve final file path.");
            }

            if (length < buffer.Capacity)
            {
                return Path.GetFullPath(FromExtendedPath(buffer.ToString()));
            }

            capacity = checked((int)length + 1);
        }

        throw new PathTooLongException("The final file path is too long.");
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static string FromExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}
