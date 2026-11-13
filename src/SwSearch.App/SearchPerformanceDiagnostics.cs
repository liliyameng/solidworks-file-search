using System.Globalization;
using System.IO;

namespace SwSearch.App;

internal static class SearchPerformanceDiagnostics
{
    private static readonly bool IsProductProcess = string.Equals(
        Path.GetFileName(Environment.ProcessPath),
        "SwSearch.App.exe",
        StringComparison.OrdinalIgnoreCase);

    internal static void RecordPhase(long generation, string phase, double elapsedMilliseconds, int resultCount)
    {
        Write(string.Join('|',
            DateTimeOffset.UtcNow.ToString("O"),
            "search",
            generation.ToString(CultureInfo.InvariantCulture),
            phase,
            Math.Round(elapsedMilliseconds).ToString(CultureInfo.InvariantCulture),
            resultCount.ToString(CultureInfo.InvariantCulture)));
    }

    // Baseline metrics for the native Explorer population crawl: first-page
    // latency and full-result completion feed the same bounded log so large
    // result sets can be compared before/after population changes.
    internal static void RecordNativeShellPhase(long generation, string phase, double elapsedMilliseconds, int addedCount)
    {
        Write(string.Join('|',
            DateTimeOffset.UtcNow.ToString("O"),
            "native-shell",
            generation.ToString(CultureInfo.InvariantCulture),
            phase,
            Math.Round(elapsedMilliseconds).ToString(CultureInfo.InvariantCulture),
            addedCount.ToString(CultureInfo.InvariantCulture)));
    }

    internal static void RecordFirstPaint(
        int clientSequence,
        int inputDelayMilliseconds,
        int hostMilliseconds,
        int renderMilliseconds,
        int totalMilliseconds,
        int resultCount)
    {
        Write(string.Join('|',
            DateTimeOffset.UtcNow.ToString("O"),
            "first-paint",
            clientSequence.ToString(CultureInfo.InvariantCulture),
            inputDelayMilliseconds.ToString(CultureInfo.InvariantCulture),
            hostMilliseconds.ToString(CultureInfo.InvariantCulture),
            renderMilliseconds.ToString(CultureInfo.InvariantCulture),
            totalMilliseconds.ToString(CultureInfo.InvariantCulture),
            resultCount.ToString(CultureInfo.InvariantCulture)));
    }

    private static void Write(string line)
    {
        try
        {
            if (!IsProductProcess)
            {
                return;
            }
            var path = Path.Combine(SwSearchDataPaths.DataDirectory, "Logs", "search-performance.log");
            BoundedUiDiagnosticLog.TryWrite(path, line + Environment.NewLine);
        }
        catch
        {
            // Timing diagnostics are optional and must not affect search.
        }
    }
}
