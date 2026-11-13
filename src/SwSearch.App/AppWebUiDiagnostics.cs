using System.Globalization;
using System.IO;
using System.Text;
using SwSearch.WebUi;
using SwSearch.WebUi.Security;

namespace SwSearch.App;

internal static class AppWebUiDiagnostics
{
    internal static void RecordRejection(WebMessageRejectedEventArgs rejection)
    {
        try
        {
            var path = Path.Combine(SwSearchDataPaths.DataDirectory, "Logs", "app-webui.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = string.Join('|',
                DateTimeOffset.UtcNow.ToString("O"),
                "rejected",
                rejection.MessageType ?? "<unknown>",
                rejection.Action ?? "<none>",
                rejection.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
                rejection.Reason.Replace('|', '_')) + Environment.NewLine;
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never affect the UI path.
        }
    }

    /// <summary>
    /// Records the full exception behind a WebView runtime fault. The fault
    /// label rendered inside the 48px toolbar clips the exception line, so
    /// without this record only the exception type survives (in
    /// app-settings.log), which is not enough to diagnose environment
    /// creation failures.
    /// </summary>
    internal static void RecordFault(string stage, WebViewRuntimeUnavailableEventArgs fault)
    {
        try
        {
            var path = Path.Combine(SwSearchDataPaths.DataDirectory, "Logs", "app-webui.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var exception = fault.Exception;
            var line = string.Join('|',
                DateTimeOffset.UtcNow.ToString("O"),
                "fault",
                string.IsNullOrWhiteSpace(stage) ? "<unknown>" : stage,
                exception?.GetType().FullName ?? "<none>",
                exception is null
                    ? "<none>"
                    : exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                Sanitize(exception?.Message ?? fault.Message)) + Environment.NewLine;
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never affect the UI path.
        }
    }

    private static string Sanitize(string value) => value
        .Replace('|', '_')
        .Replace("\r", " ")
        .Replace("\n", " ");
}
