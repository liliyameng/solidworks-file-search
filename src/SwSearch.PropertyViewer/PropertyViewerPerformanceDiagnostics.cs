using System.Diagnostics;
using System.IO;

namespace SwSearch.PropertyViewer;

internal static class PropertyViewerPerformanceDiagnostics
{
    private static readonly object Gate = new();

    internal static void Record(string phase, double elapsedMilliseconds, string outcome = "ok")
    {
        try
        {
            var isolated = Environment.GetEnvironmentVariable("SWSEARCH_DATA_ROOT");
            var root = string.IsNullOrWhiteSpace(isolated)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwSearch")
                : Path.GetFullPath(isolated);
            var directory = Path.Combine(root, "Logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTimeOffset.UtcNow:O}\t{phase}\t{elapsedMilliseconds:0.00}\t{outcome}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(directory, "property-viewer-performance.log"), line);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }
}
