using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwSearch.SolidWorksAddin
{
    internal sealed class TaskpaneLibraryLocation
    {
        internal TaskpaneLibraryLocation(
            string root,
            string directory,
            string relativeDirectory,
            string fileName,
            string fullPath)
        {
            Root = root;
            Directory = directory;
            RelativeDirectory = relativeDirectory;
            FileName = fileName;
            FullPath = fullPath;
        }

        internal string Root { get; private set; }
        internal string Directory { get; private set; }
        internal string RelativeDirectory { get; private set; }
        internal string FileName { get; private set; }
        internal string FullPath { get; private set; }
    }

    internal sealed class TaskpaneLibrarySettings
    {
        // swUserPreferenceStringValue_e.swFileLocationsDesignLibrary.
        // Keep the numeric value local so the add-in continues embedding only the
        // official sldworks PIA and does not add a runtime swconst dependency.
        private const int DesignLibraryFileLocations = 38;
        private readonly string[] _roots;

        private TaskpaneLibrarySettings(string[] roots)
        {
            _roots = roots ?? new string[0];
        }

        internal string[] Roots
        {
            get { return (string[])_roots.Clone(); }
        }

        internal static TaskpaneLibrarySettings Load(ISldWorks application)
        {
            if (application == null) return new TaskpaneLibrarySettings(new string[0]);
            try
            {
                // Read only SOLIDWORKS System Options > File Locations > Design Library.
                // The application RCW is borrowed and is never released by this helper.
                return FromSolidWorksValue(
                    application.GetUserPreferenceStringValue(DesignLibraryFileLocations));
            }
            catch (Exception exception)
            {
                AddinDiagnostics.WriteFailure("taskpane-library-settings", exception);
                return new TaskpaneLibrarySettings(new string[0]);
            }
        }

        internal static TaskpaneLibrarySettings FromSolidWorksValue(string value)
        {
            List<string> roots = new List<string>();
            foreach (string item in (value ?? String.Empty).Split(
                         new[] { ';', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized;
                string error;
                if (TaskpaneSearchScope.TryNormalizeFolder(item.Trim(), out normalized, out error) &&
                    !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(normalized);
                }
            }
            return new TaskpaneLibrarySettings(roots.ToArray());
        }

        internal string FindContainingRoot(string path)
        {
            string best = null;
            foreach (string root in _roots)
            {
                if (TaskpaneSearchScope.ContainsPath(root, path) && (best == null || root.Length > best.Length))
                    best = root;
            }
            return best;
        }

        internal bool TryResolveLocation(
            string path,
            out TaskpaneLibraryLocation location,
            out string error)
        {
            location = null;
            error = null;
            if (_roots.Length == 0)
            {
                error = "未配置 SOLIDWORKS 设计库路径。";
                return false;
            }
            string root = FindContainingRoot(path);
            if (root == null)
            {
                error = "该文件不在 SOLIDWORKS 设计库路径内。";
                return false;
            }
            string fileName = Path.GetFileName(path);
            string directory = Path.GetDirectoryName(path);
            if (String.IsNullOrWhiteSpace(fileName) || String.IsNullOrWhiteSpace(directory))
            {
                error = "目标文件路径无效。";
                return false;
            }
            string relative = directory.Length <= root.Length
                ? String.Empty
                : directory.Substring(root.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            location = new TaskpaneLibraryLocation(root, directory, relative, fileName, path);
            return true;
        }

        internal static List<SearchHitDto> FilterToRoots(
            IEnumerable<SearchHitDto> hits,
            IEnumerable<string> roots)
        {
            List<string> rootValues = new List<string>();
            List<string> rootPrefixes = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (String.IsNullOrWhiteSpace(root)) continue;
                    string normalized;
                    string error;
                    if (!SwSearch.Pathing.WindowsScopePath.TryNormalize(root, out normalized, out error) ||
                        !seen.Add(normalized)) continue;
                    rootValues.Add(normalized);
                    rootPrefixes.Add(normalized.EndsWith("\\", StringComparison.Ordinal)
                        ? normalized
                        : normalized + "\\");
                }
            }

            List<SearchHitDto> accepted = new List<SearchHitDto>();
            if (hits == null || rootValues.Count == 0) return accepted;
            foreach (SearchHitDto hit in hits)
            {
                if (hit == null || String.IsNullOrEmpty(hit.FullPath)) continue;
                string normalizedHit;
                string hitError;
                if (!SwSearch.Pathing.WindowsScopePath.TryNormalize(
                        hit.FullPath, out normalizedHit, out hitError)) continue;
                for (int index = 0; index < rootValues.Count; index++)
                {
                    if (String.Equals(rootValues[index], normalizedHit, StringComparison.OrdinalIgnoreCase) ||
                        normalizedHit.StartsWith(rootPrefixes[index], StringComparison.OrdinalIgnoreCase))
                    {
                        accepted.Add(hit);
                        break;
                    }
                }
            }
            return accepted;
        }

        // Diagnostic-only counterpart of FilterToRoots: identical acceptance
        // semantics without materializing the accepted list. Callers derive
        // the rejected count by subtraction, exactly as the previous full
        // filter call did.
        internal static int CountInsideRoots(
            IEnumerable<SearchHitDto> hits,
            IEnumerable<string> roots)
        {
            List<string> rootValues = new List<string>();
            List<string> rootPrefixes = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (String.IsNullOrWhiteSpace(root)) continue;
                    string normalized;
                    string error;
                    if (!SwSearch.Pathing.WindowsScopePath.TryNormalize(root, out normalized, out error) ||
                        !seen.Add(normalized)) continue;
                    rootValues.Add(normalized);
                    rootPrefixes.Add(normalized.EndsWith("\\", StringComparison.Ordinal)
                        ? normalized
                        : normalized + "\\");
                }
            }

            int accepted = 0;
            if (hits == null || rootValues.Count == 0) return accepted;
            foreach (SearchHitDto hit in hits)
            {
                if (hit == null || String.IsNullOrEmpty(hit.FullPath)) continue;
                string normalizedHit;
                string hitError;
                if (!SwSearch.Pathing.WindowsScopePath.TryNormalize(
                        hit.FullPath, out normalizedHit, out hitError)) continue;
                for (int index = 0; index < rootValues.Count; index++)
                {
                    if (String.Equals(rootValues[index], normalizedHit, StringComparison.OrdinalIgnoreCase) ||
                        normalizedHit.StartsWith(rootPrefixes[index], StringComparison.OrdinalIgnoreCase))
                    {
                        accepted++;
                        break;
                    }
                }
            }
            return accepted;
        }
    }
}
