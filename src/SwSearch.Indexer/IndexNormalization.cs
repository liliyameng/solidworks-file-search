using System.Text;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

internal static class IndexNormalization
{
    internal const string ExcludedRecycleBinSegment = @"\$RECYCLE.BIN\";

    internal static string PathKey(string path)
    {
        return SearchPath.NormalizeKey(path);
    }

    internal static string Text(string value) =>
        value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();

    internal static string Extension(SolidWorksDocumentKind kind) => kind switch
    {
        SolidWorksDocumentKind.Part => ".SLDPRT",
        SolidWorksDocumentKind.Assembly => ".SLDASM",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static void ValidateCandidate(FileCandidate candidate)
    {
        if (!SupportedSolidWorksDocumentKinds.IsSupported(candidate.Kind))
        {
            throw new ArgumentException(
                $"Only part and assembly documents can be indexed: {candidate.FullPath}",
                nameof(candidate));
        }
        var expected = Extension(candidate.Kind);
        if (!Path.GetExtension(candidate.FullPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"文件类型与扩展名不一致：{candidate.FullPath}");
        }
    }
}
