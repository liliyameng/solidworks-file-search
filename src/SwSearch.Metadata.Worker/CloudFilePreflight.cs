using System.Runtime.InteropServices;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

// This probe deliberately obtains only directory-entry metadata.  It never
// opens the file data stream, so an offline Cloud Files placeholder cannot be
// hydrated by the metadata worker just to decide whether it is readable.
internal static class CloudFilePreflight
{
    internal const uint Placeholder = 0x0000_0001;
    internal const uint Partial = 0x0000_0010;
    internal const uint PartiallyOnDisk = 0x0000_0020;
    internal const uint Invalid = 0xffff_ffff;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;
    private static readonly IntPtr InvalidFindHandle = new(-1);

    internal static MetadataFileDisposition ReadDisposition(string fullPath)
    {
        var attributes = File.GetAttributes(fullPath);
        if (HasLegacyUnavailableAttributes(attributes))
        {
            return MetadataFileDisposition.CloudPlaceholder;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return MetadataFileDisposition.Normal;
        }

        // Cloud Files was added in Windows 10 1709.  On every other platform,
        // or when the optional CldApi entry point is unavailable, preserve the
        // existing reparse-point policy rather than guessing.
        return TryReadPlaceholderState(fullPath, out var state)
            ? Classify(attributes, state)
            : Classify(attributes, placeholderState: null);
    }

    internal static MetadataFileDisposition Classify(
        FileAttributes attributes,
        uint? placeholderState)
    {
        // Keep the attribute-only behavior as the conservative fallback.  In
        // particular, a recall attribute must never be opened merely because a
        // provider returned a stale or unrecognised Cloud Files state.
        if (HasLegacyUnavailableAttributes(attributes))
        {
            return MetadataFileDisposition.CloudPlaceholder;
        }

        if (placeholderState is { } state &&
            state != Invalid &&
            (state & Placeholder) != 0)
        {
            // PARTIAL means the provider says the contents are not consumable;
            // PARTIALLY_ON_DISK additionally means bytes are still missing.
            // A fully local placeholder is intentionally allowed through: it
            // has a reparse tag, but reading it does not hydrate cloud content.
            return (state & (Partial | PartiallyOnDisk)) != 0
                ? MetadataFileDisposition.CloudPlaceholder
                : MetadataFileDisposition.Normal;
        }

        return (attributes & FileAttributes.ReparsePoint) != 0
            ? MetadataFileDisposition.ReparsePoint
            : MetadataFileDisposition.Normal;
    }

    private static bool HasLegacyUnavailableAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    private static bool TryReadPlaceholderState(string fullPath, out uint state)
    {
        state = 0;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return false;
        }

        var handle = FindFirstFileW(ToExtendedPath(fullPath), out var findData);
        if (handle == InvalidFindHandle)
        {
            return false;
        }

        try
        {
            try
            {
                state = CfGetPlaceholderStateFromFindData(ref findData);
                return state != Invalid;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        internal FileAttributes FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint ReparseTag;
        internal uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string? FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        internal string? AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(
        string fileName,
        out Win32FindData findFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [DllImport("CldApi.dll", ExactSpelling = true)]
    private static extern uint CfGetPlaceholderStateFromFindData(
        ref Win32FindData findData);
}
