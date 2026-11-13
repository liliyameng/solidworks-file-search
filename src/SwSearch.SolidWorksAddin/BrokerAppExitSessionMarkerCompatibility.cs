using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SwSearch.SolidWorksAddin
{
    /// <summary>
    /// Mirrors SwSearch.Contracts.BrokerAppExitSessionMarker without adding a
    /// runtime Contracts dependency to the legacy in-process SOLIDWORKS add-in.
    /// The marker remains valid only for the current Windows boot, so an old
    /// per-session file cannot suppress normal activity after a reboot.
    /// </summary>
    internal static class BrokerAppExitSessionMarkerCompatibility
    {
        // Keep these values in lockstep with BrokerAppExitSessionMarker. The
        // taskpane is compiled as a standalone .NET Framework payload and
        // therefore intentionally cannot reference SwSearch.Contracts.dll.
        private const string DataRootEnvironmentVariable = "SWSEARCH_DATA_ROOT";
        private const string MarkerDirectoryName = "Session";
        private const string MarkerFilePrefix = "app-exit.v1.session.";
        private const string MarkerFileSuffix = ".marker";

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        internal static bool IsMarkedForCurrentSession()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return IsMarkedForSession(process.SessionId);
                }
            }
            catch (Exception exception)
            {
                if (IsExpectedMarkerFileException(exception)) return false;
                throw;
            }
        }

        internal static bool IsMarkedForSession(int sessionId)
        {
            try
            {
                string path = GetMarkerPathForSession(sessionId);
                if (!File.Exists(path)) return false;
                if (File.GetLastWriteTimeUtc(path) >= GetCurrentBootStartedUtc()) return true;

                // A marker from an earlier boot must never suppress normal
                // autostart. Delete is best effort because another process may
                // already be clearing it for a new App launch.
                try { File.Delete(path); }
                catch (Exception exception)
                {
                    if (!IsExpectedMarkerFileException(exception)) throw;
                }
                return false;
            }
            catch (Exception exception)
            {
                if (IsExpectedMarkerFileException(exception)) return false;
                throw;
            }
        }

        internal static string GetMarkerPathForSession(int sessionId)
        {
            if (sessionId < 0) throw new ArgumentOutOfRangeException("sessionId");
            string dataRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            string normalizedDataRoot = String.IsNullOrWhiteSpace(dataRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwSearch")
                : Path.GetFullPath(dataRoot);
            return Path.Combine(
                normalizedDataRoot,
                MarkerDirectoryName,
                MarkerFilePrefix + sessionId.ToString(CultureInfo.InvariantCulture) + MarkerFileSuffix);
        }

        internal static DateTime GetCurrentBootStartedUtc()
        {
            return DateTime.UtcNow - TimeSpan.FromMilliseconds((double)GetTickCount64());
        }

        private static bool IsExpectedMarkerFileException(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException ||
                exception is ArgumentException || exception is NotSupportedException ||
                exception is PathTooLongException || exception is System.Security.SecurityException;
        }
    }
}
