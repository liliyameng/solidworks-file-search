namespace SwSearch.Everything;

public static class SolidWorksEverythingQuery
{
    // There is deliberately no user-controlled text in discovery queries.
    // Everything's semicolon extension list is an OR list scoped by ext:.
    public const string Search = "file:ext:sldprt;sldasm";

    public static bool TryGetKind(string fullPath, out SwSearch.Contracts.SolidWorksDocumentKind kind)
    {
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            kind = SwSearch.Contracts.SolidWorksDocumentKind.Part;
            return true;
        }

        if (extension.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase))
        {
            kind = SwSearch.Contracts.SolidWorksDocumentKind.Assembly;
            return true;
        }

        kind = default;
        return false;
    }
}
