using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

/// <summary>
/// A short-lived observation of the exact file object addressed by a path.
/// The runtime captures it immediately before and after extraction; SQLite
/// then compares the same evidence inside the metadata commit transaction.
/// </summary>
internal sealed record StableFileEvidence(
    string RequestedPath,
    string FinalPath,
    SolidWorksDocumentKind Kind,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string FileIdentity)
{
    internal FileCandidate Candidate => new(RequestedPath, Kind, Size, LastWriteTimeUtc);

    internal string Fingerprint(int extractorVersion = ExtractorVersions.Current) =>
        $"{Size}:{LastWriteTimeUtc.ToUniversalTime().Ticks}:{FileIdentity}:{extractorVersion}";

    internal bool RequestedPathMatchesFinal =>
        string.Equals(RequestedPath, FinalPath, StringComparison.OrdinalIgnoreCase);

    internal bool SameObjectAndVersion(StableFileEvidence other) =>
        string.Equals(FinalPath, other.FinalPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(FileIdentity, other.FileIdentity, StringComparison.Ordinal) &&
        Size == other.Size &&
        LastWriteTimeUtc.ToUniversalTime().Ticks == other.LastWriteTimeUtc.ToUniversalTime().Ticks;

    internal bool Matches(FileCandidate candidate) =>
        candidate.Kind == Kind &&
        candidate.Size == Size &&
        candidate.LastWriteTimeUtc.ToUniversalTime().Ticks == LastWriteTimeUtc.ToUniversalTime().Ticks &&
        string.Equals(
            Path.GetFullPath(candidate.FullPath),
            RequestedPath,
            StringComparison.OrdinalIgnoreCase);
}

internal static class StableFileEvidenceReader
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileNameNormalized = 0;

    internal static StableFileEvidence Capture(string suppliedPath)
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

            return new StableFileEvidence(
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
        return new StableFileEvidence(
            requestedPath,
            finalPath,
            kind,
            size,
            new DateTimeOffset(DateTime.FromFileTimeUtc(writeTime), TimeSpan.Zero),
            $"{information.VolumeSerialNumber:X8}:{fileIndex:X16}");
    }

    internal static SolidWorksDocumentKind ParseKind(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".SLDPRT" => SolidWorksDocumentKind.Part,
            ".SLDASM" => SolidWorksDocumentKind.Assembly,
            _ => throw new ArgumentException(
                "Only .SLDPRT and .SLDASM files are supported.",
                nameof(path)),
        };

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, FileNameNormalized);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve final file path.");
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
