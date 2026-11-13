using SwSearch.Contracts;

namespace SwSearch.Indexer;

public static class DiscoveryPath
{
    public static string Normalize(string path)
    {
        return SearchPath.NormalizeFolder(path);
    }

    public static string NormalizeKey(string path) => SearchPath.NormalizeKey(path);

    public static bool TryGetDocumentKind(string path, out SolidWorksDocumentKind kind)
    {
        if (IsExcludedSystemPath(path))
        {
            kind = default;
            return false;
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            kind = SolidWorksDocumentKind.Part;
            return true;
        }

        if (extension.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase))
        {
            kind = SolidWorksDocumentKind.Assembly;
            return true;
        }

        kind = default;
        return false;
    }

    public static bool IsExcludedSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static segment =>
            segment.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsWithinRoot(string path, string root)
    {
        return SearchPath.Contains(root, path);
    }
}
