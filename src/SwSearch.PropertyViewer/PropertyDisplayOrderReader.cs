using System.IO;
using System.Text.Json;

namespace SwSearch.PropertyViewer;

internal static class PropertyDisplayOrderReader
{
    internal const int MaximumCount = 256;
    internal const int MaximumNameLength = 512;

    internal static IReadOnlyList<string> Read(string? settingsPath = null)
    {
        try
        {
            settingsPath ??= Path.Combine(DataRoot(), "settings.json");
            if (!File.Exists(settingsPath))
            {
                return Array.Empty<string>();
            }
            using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("propertyDisplayOrder", out var node) ||
                node.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }
            return Normalize(node.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    internal static IReadOnlyList<string> Normalize(IEnumerable<string?>? names)
    {
        var result = new List<string>();
        foreach (var raw in names ?? Array.Empty<string>())
        {
            var name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > MaximumNameLength || name.IndexOf('\0') >= 0)
            {
                continue;
            }
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
            if (result.Count == MaximumCount)
            {
                break;
            }
        }
        return result;
    }

    private static string DataRoot()
    {
        var isolated = Environment.GetEnvironmentVariable("SWSEARCH_DATA_ROOT");
        return string.IsNullOrWhiteSpace(isolated)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwSearch")
            : Path.GetFullPath(isolated);
    }
}
