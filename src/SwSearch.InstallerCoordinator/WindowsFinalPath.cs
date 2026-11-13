using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwSearch.InstallerCoordinator;

internal interface IProductFileAnchor : IDisposable
{
    string FinalPath { get; }
    string ObjectId { get; }
}

internal sealed class OpenedFileIdentity(SafeFileHandle handle, string finalPath, string objectId) : IProductFileAnchor
{
    internal SafeFileHandle Handle { get; } = handle;
    public string FinalPath { get; } = finalPath;
    public string ObjectId { get; } = objectId;

    public void Dispose() => Handle.Dispose();
}

internal static class WindowsFinalPath
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;
    private static readonly Guid FolderIdProgramFilesX64 = new("6D809377-6AF0-444B-8957-A3773F02200E");

    internal static OpenedFileIdentity? TryOpenFileAnchor(string path, bool denyReplacement)
    {
        if (!IsOrdinaryAbsolutePath(path))
        {
            return null;
        }

        var share = denyReplacement
            ? FileShareRead
            : FileShareRead | FileShareWrite | FileShareDelete;
        var handle = CreateFile(
            path,
            GenericRead | FileReadAttributes,
            share,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var finalPath = GetFinalPath(handle);
        if (finalPath is null || !GetFileInformationByHandle(handle, out var information))
        {
            handle.Dispose();
            return null;
        }

        var objectId = $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
        return new OpenedFileIdentity(handle, finalPath, objectId);
    }

    internal static string? TryResolveDirectory(string path)
    {
        if (!IsOrdinaryAbsolutePath(path))
        {
            return null;
        }

        using var handle = CreateFile(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        return handle.IsInvalid ? null : GetFinalPath(handle);
    }

    internal static string? GetTrustedProgramFilesRoot()
    {
        var result = SHGetKnownFolderPath(FolderIdProgramFilesX64, 0, IntPtr.Zero, out var pointer);
        if (result != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var path = Marshal.PtrToStringUni(pointer);
            return string.IsNullOrWhiteSpace(path) ? null : TryResolveDirectory(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    internal static bool IsWithinBoundary(string candidate, string root)
    {
        if (!IsOrdinaryAbsolutePath(candidate) || !IsOrdinaryAbsolutePath(root))
        {
            return false;
        }

        var boundary = root.TrimEnd('\\');
        return candidate.StartsWith(boundary + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsProvenLongFinalPath(string path) =>
        IsOrdinaryAbsolutePath(path) &&
        !path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.Contains('~'));

    internal static bool ReportedPathMatchesFinal(string reportedPath, string finalPath)
    {
        if (!IsOrdinaryAbsolutePath(reportedPath) || !IsOrdinaryAbsolutePath(finalPath))
        {
            return false;
        }

        var longReported = TryGetLongPath(reportedPath);
        return longReported is not null &&
               string.Equals(longReported, finalPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOrdinaryAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            return false;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal) ||
            path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length >= 3 &&
               char.IsAsciiLetter(path[0]) &&
               path[1] == ':' &&
               path[2] == '\\';
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, FileNameNormalized);
        if (length == 0 || length >= builder.Capacity)
        {
            return null;
        }

        var path = builder.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return IsOrdinaryAbsolutePath(path) ? TryGetLongPath(path) : null;
    }

    private static string? TryGetLongPath(string path)
    {
        var builder = new StringBuilder(32_768);
        var length = GetLongPathName(path, builder, (uint)builder.Capacity);
        if (length == 0 || length >= builder.Capacity)
        {
            return null;
        }

        var longPath = builder.ToString().TrimEnd('\\');
        if (!IsOrdinaryAbsolutePath(longPath) || longPath.Split('\\').Any(segment => segment.Contains('~')))
        {
            // A short-name path must expand to its long form. If the volume cannot
            // provide that proof, retain the process instead of authorizing force.
            return null;
        }

        return longPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}

internal static class FinalPathTrustPolicy
{
    internal static bool IsTrustedRegisteredRoot(string? registeredFinalRoot, string? programFilesFinalRoot) =>
        !string.IsNullOrWhiteSpace(registeredFinalRoot) &&
        !string.IsNullOrWhiteSpace(programFilesFinalRoot) &&
        WindowsFinalPath.IsProvenLongFinalPath(registeredFinalRoot) &&
        WindowsFinalPath.IsProvenLongFinalPath(programFilesFinalRoot) &&
        WindowsFinalPath.IsWithinBoundary(registeredFinalRoot, programFilesFinalRoot);

    internal static bool IsProcessInsideRegisteredRoot(string processFinalPath, string registeredFinalRoot) =>
        WindowsFinalPath.IsProvenLongFinalPath(processFinalPath) &&
        WindowsFinalPath.IsProvenLongFinalPath(registeredFinalRoot) &&
        WindowsFinalPath.IsWithinBoundary(processFinalPath, registeredFinalRoot);
}
