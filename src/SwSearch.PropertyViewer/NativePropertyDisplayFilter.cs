using System.Globalization;
using System.Text;
using SwSearch.Contracts;

namespace SwSearch.PropertyViewer;

internal static class NativePropertyDisplayFilter
{
    private const string MaterialProjectionKey = "@material-name";

    private static readonly IReadOnlyDictionary<string, int> MaterialNameAliases =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["材料名称"] = 0,
            ["材质"] = 1,
            ["材料"] = 2,
            ["swmaterial"] = 3,
            ["swmaterialname"] = 4,
            ["materialname"] = 5,
            ["material"] = 6,
            ["solidworksmaterial"] = 7,
            ["solidworksmaterialname"] = 8,
        };

    private static readonly IReadOnlySet<string> HiddenMaterialFields =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "材料分类", "材料库", "材料id",
            "比热", "热容", "导热率", "热传导率", "热膨胀系数", "热扩张系数",
            "密度", "质量密度", "弹性模量", "杨氏模量", "剪切模量", "泊松比",
            "屈服强度", "屈服力", "张力强度", "抗拉强度", "硬化因子",
            "materialcategory", "materialclassification", "materiallibrary", "materialdatabase", "materialid",
            "specificheat", "heatcapacity", "thermalconductivity", "coefficientofthermalexpansion",
            "thermalexpansioncoefficient", "density", "massdensity", "elasticmodulus", "youngsmodulus",
            "shearmodulus", "poissonratio", "poissonsratio", "yieldstrength", "yieldstress",
            "tensilestrength", "ultimatetensilestrength", "hardeningfactor",
        };

    private static readonly string[] SolidWorksMaterialPrefixes =
        ["swmaterial", "solidworksmaterial"];

    internal static bool ShouldShow(DocumentProperty property)
    {
        var normalized = Normalize(property.Name);
        if (MaterialNameAliases.ContainsKey(normalized)) return true;
        if (HiddenMaterialFields.Contains(normalized)) return false;
        foreach (var prefix in SolidWorksMaterialPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal) &&
                HiddenMaterialFields.Contains(normalized[prefix.Length..]))
            {
                return false;
            }
        }
        return true;
    }

    internal static string ProjectionKey(DocumentProperty property) =>
        MaterialNameAliases.ContainsKey(Normalize(property.Name))
            ? MaterialProjectionKey
            : property.Name;

    internal static DocumentProperty SelectPreferred(
        string projectionKey,
        IEnumerable<DocumentProperty> properties)
    {
        var values = properties.ToArray();
        if (!string.Equals(projectionKey, MaterialProjectionKey, StringComparison.Ordinal))
            return values[^1];
        return values
            .OrderBy(property => MaterialNameAliases.GetValueOrDefault(Normalize(property.Name), int.MaxValue))
            .First();
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }
        return builder.ToString();
    }
}
