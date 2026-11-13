using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SwSearch.SolidWorksAddin
{
    internal static class AddinDiagnostics
    {
        private static readonly object WriteLock = new object();
        private static readonly object RootHashCacheLock = new object();
        private static readonly Dictionary<string, string> RootHashCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private const int RootHashCacheCapacity = 64;

        private static string _logDirectory;
        private static bool _logDirectoryReady;

        internal static void WriteFailure(string stage, Exception exception)
        {
            WriteFailure(stage, exception, AppendLine);
        }

        internal static void WriteFailure(string stage, Exception exception, Action<string> sink)
        {
            try
            {
                string safeStage = IsSafeStage(stage) ? stage : "unknown";
                string exceptionType = exception == null ? "System.Exception" : exception.GetType().FullName;
                int hresult = exception == null ? unchecked((int)0x80004005) : exception.HResult;
                string record = String.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:o}\tstage={1}\texception={2}\thresult=0x{3:X8}",
                    DateTime.UtcNow,
                    safeStage,
                    exceptionType,
                    unchecked((uint)hresult));
                sink(record);
            }
            catch
            {
                // Diagnostics must never become a second add-in failure.
            }
        }

        internal static void WriteEvent(string stage, string code)
        {
            try
            {
                string safeStage = IsSafeStage(stage) ? stage : "unknown";
                string safeCode = IsSafeStage(code) ? code : "none";
                AppendLine(String.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:o}\tstage={1}\tevent={2}",
                    DateTime.UtcNow,
                    safeStage,
                    safeCode));
            }
            catch
            {
                // Diagnostics must never become a second add-in failure.
            }
        }

        internal static void WriteSearchEvent(
            LiveSearchRequest request,
            int resultCount,
            int libraryRejectedCount,
            long latencyMilliseconds,
            Exception error)
        {
            WriteSearchEvent(request, resultCount, libraryRejectedCount,
                latencyMilliseconds, error, AppendLine);
        }

        internal static void WriteSearchEvent(
            LiveSearchRequest request,
            int resultCount,
            int libraryRejectedCount,
            long latencyMilliseconds,
            Exception error,
            Action<string> sink)
        {
            try
            {
                string[] roots = request == null || request.LibraryRoots == null
                    ? new string[0]
                    : request.LibraryRoots;
                string kinds = request == null || request.Kinds == null
                    ? String.Empty
                    : String.Join(",", request.Kinds.Select(delegate(int value)
                    {
                        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }).ToArray());
                string errorCode = error == null ? "none" :
                    error is TimeoutException ? "response-timeout" :
                    error is OperationCanceledException ? "canceled" : "request-failed";
                string record = String.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:o}\tstage=taskpane-search\tclient_origin=taskpane\tmode={1}\tkinds={2}" +
                    "\troots_count={3}\tnormalized_roots_hash={4}\tterm_length={5}" +
                    "\tresult_count={6}\tlibrary_rejected_count={7}\tlatency_ms={8}\terror_code={9}",
                    DateTime.UtcNow,
                    request == null ? -1 : request.Mode,
                    kinds,
                    roots.Length,
                    HashNormalizedRoots(roots),
                    request == null || request.Text == null ? 0 : request.Text.Length,
                    Math.Max(0, resultCount),
                    Math.Max(0, libraryRejectedCount),
                    Math.Max(0, latencyMilliseconds),
                    errorCode);
                sink(record);
            }
            catch
            {
                // Diagnostics must never become a second add-in failure.
            }
        }

        private static string HashNormalizedRoots(IEnumerable<string> roots)
        {
            string[] normalized = roots
                .Where(delegate(string value) { return !String.IsNullOrWhiteSpace(value); })
                .Select(delegate(string value)
                {
                    return value.Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
                })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(delegate(string value) { return value; }, StringComparer.Ordinal)
                .ToArray();
            string key = String.Join("\0", normalized);
            lock (RootHashCacheLock)
            {
                string cached;
                if (RootHashCache.TryGetValue(key, out cached)) return cached;
                byte[] bytes = Encoding.UTF8.GetBytes(key);
                using (SHA256 hash = SHA256.Create())
                {
                    cached = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", String.Empty)
                        .Substring(0, 16);
                }
                if (RootHashCache.Count >= RootHashCacheCapacity) RootHashCache.Clear();
                RootHashCache[key] = cached;
                return cached;
            }
        }

        private static bool IsSafeStage(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 48) return false;
            foreach (char character in value)
            {
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9') ||
                      character == '-')) return false;
            }
            return true;
        }

        private static void AppendLine(string record)
        {
            lock (WriteLock)
            {
                string directory = LogDirectory;
                if (String.IsNullOrEmpty(directory)) return;
                if (!_logDirectoryReady)
                {
                    Directory.CreateDirectory(directory);
                    _logDirectoryReady = true;
                }
                File.AppendAllText(
                    Path.Combine(directory, "SolidWorksAddin.log"),
                    record + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }

        private static string LogDirectory
        {
            get
            {
                if (_logDirectory == null)
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _logDirectory = String.IsNullOrWhiteSpace(local)
                        ? String.Empty
                        : Path.Combine(local, "SwSearch", "Logs");
                }
                return _logDirectory;
            }
        }

        internal static void SetLogDirectoryForTesting(string directory)
        {
            lock (WriteLock)
            {
                _logDirectory = directory;
                _logDirectoryReady = false;
            }
        }
    }
}
