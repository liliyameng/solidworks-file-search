using System.Diagnostics;

namespace SwSearch.SolidWorksAddin;

// TaskpaneShellResultsHost is source-shared with the desktop Web UI.  The host
// deliberately reports only bounded stage/exception diagnostics; the desktop
// assembly must not take a dependency on the legacy add-in assembly merely to
// write that information.
internal static class AddinDiagnostics
{
    internal static void WriteFailure(string stage, Exception exception)
    {
        try
        {
            Trace.WriteLine($"SwSearch Shell host failure: {stage}; {exception?.GetType().Name}");
        }
        catch
        {
            // Diagnostics must never interfere with Shell ownership or teardown.
        }
    }
}
