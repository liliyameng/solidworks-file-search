using System.IO;

namespace SwSearch.ShellRegistration;

public sealed record RegistryValueWrite(string KeyPath, string ValueName, string Value);

public sealed record RegistrationPlan(
    string ViewerPath,
    string CommandValue,
    IReadOnlyList<RegistryValueWrite> Writes,
    IReadOnlyList<string> OwnedVerbKeys);

public static class RegistrationPlanFactory
{
    public const string VerbName = "SwSearch.ViewProperties";
    public const string DisplayName = "查看 SOLIDWORKS 属性";
    public const string LegacyDrawingExtension = ".slddrw";
    public static readonly string[] Extensions = [".sldprt", ".sldasm"];
    public static string LegacyDrawingVerbKey => VerbKey(LegacyDrawingExtension);

    public static RegistrationPlan Create(string viewerPath, bool requireFileExists = true)
    {
        if (string.IsNullOrWhiteSpace(viewerPath) || !Path.IsPathFullyQualified(viewerPath))
        {
            throw new ArgumentException("属性查看器必须是绝对路径。", nameof(viewerPath));
        }

        var fullPath = Path.GetFullPath(viewerPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("属性查看器路径必须指向 .exe 文件。", nameof(viewerPath));
        }

        if (fullPath.Contains('"'))
        {
            throw new ArgumentException("属性查看器路径不能包含引号。", nameof(viewerPath));
        }

        if (requireFileExists && !File.Exists(fullPath))
        {
            throw new FileNotFoundException("未找到属性查看器可执行文件。", fullPath);
        }

        var command = $"\"{fullPath}\" \"%1\"";
        var writes = new List<RegistryValueWrite>();
        var ownedKeys = new List<string>();
        foreach (var extension in Extensions)
        {
            var verbKey = VerbKey(extension);
            ownedKeys.Add(verbKey);
            writes.Add(new RegistryValueWrite(verbKey, string.Empty, DisplayName));
            writes.Add(new RegistryValueWrite(verbKey, "MUIVerb", DisplayName));
            writes.Add(new RegistryValueWrite(verbKey, "Icon", $"\"{fullPath}\",0"));
            writes.Add(new RegistryValueWrite(verbKey, "MultiSelectModel", "Single"));
            writes.Add(new RegistryValueWrite($@"{verbKey}\command", string.Empty, command));
        }

        return new RegistrationPlan(fullPath, command, writes, ownedKeys);
    }

    internal static string VerbKey(string extension) =>
        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbName}";
}
