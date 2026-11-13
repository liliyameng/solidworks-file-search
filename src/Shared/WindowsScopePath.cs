using System;
using System.Collections.Generic;
using System.Text;

namespace SwSearch.Pathing
{
    /// <summary>
    /// Purely lexical Windows path normalization for cached folder scopes.
    /// This code deliberately performs no file-system, shell, drive or network I/O.
    /// Segment Unicode code points are preserved; FormC/FormKC normalization is forbidden.
    /// It is linked into both the modern Contracts assembly and the CLR4 Taskpane.
    /// </summary>
    public static class WindowsScopePath
    {
        public const int MaximumLength = 32767;

        public static string Normalize(string path)
        {
            string normalized;
            string error;
            if (!TryNormalize(path, out normalized, out error))
            {
                throw new ArgumentException(error, "path");
            }
            return normalized;
        }

        public static bool TryNormalize(string path, out string normalized, out string error)
        {
            normalized = String.Empty;
            error = String.Empty;
            if (String.IsNullOrWhiteSpace(path))
            {
                error = "Folder path is required.";
                return false;
            }
            if (path.Length > MaximumLength)
            {
                error = "Folder path exceeds the supported Windows path length.";
                return false;
            }

            string value = path.Replace('/', '\\');
            if (HasForbiddenCharacter(value))
            {
                error = "Folder path contains a control character, wildcard, quote or reserved separator.";
                return false;
            }
            if (HasDeviceNamespacePrefix(value))
            {
                error = "Device and NT namespace paths cannot be used as folder scopes.";
                return false;
            }

            string prefix;
            int offset;
            if (IsAsciiLetter(value[0]) && value.Length >= 3 && value[1] == ':' && value[2] == '\\')
            {
                prefix = Char.ToUpperInvariant(value[0]) + ":\\";
                offset = 3;
            }
            else if (value.Length >= 2 && value[0] == '\\' && value[1] == '\\')
            {
                if (value.Length == 2 || value[2] == '\\')
                {
                    error = "UNC folder path must include a server and share.";
                    return false;
                }

                int serverEnd = value.IndexOf('\\', 2);
                if (serverEnd < 0)
                {
                    error = "UNC folder path must include a share.";
                    return false;
                }
                int shareStart = serverEnd + 1;
                while (shareStart < value.Length && value[shareStart] == '\\') shareStart++;
                if (shareStart >= value.Length)
                {
                    error = "UNC folder path must include a share.";
                    return false;
                }
                int shareEnd = value.IndexOf('\\', shareStart);
                if (shareEnd < 0) shareEnd = value.Length;
                string server = value.Substring(2, serverEnd - 2);
                string share = value.Substring(shareStart, shareEnd - shareStart);
                if (!IsOrdinarySegment(server) || !IsOrdinarySegment(share) ||
                    String.Equals(server, "GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(share, "PIPE", StringComparison.OrdinalIgnoreCase))
                {
                    error = "UNC server or share name is invalid or reserved.";
                    return false;
                }
                prefix = "\\\\" + server + "\\" + share + "\\";
                offset = shareEnd < value.Length ? shareEnd + 1 : shareEnd;
            }
            else
            {
                error = "Folder path must be a drive-absolute path or a standard UNC path.";
                return false;
            }

            List<string> segments = new List<string>();
            int start = offset;
            for (int index = offset; index <= value.Length; index++)
            {
                if (index != value.Length && value[index] != '\\') continue;
                if (index > start)
                {
                    string segment = value.Substring(start, index - start);
                    if (segment == ".")
                    {
                        // Skip a current-directory segment.
                    }
                    else if (segment == "..")
                    {
                        if (segments.Count == 0)
                        {
                            error = "Folder path cannot traverse above its root.";
                            return false;
                        }
                        segments.RemoveAt(segments.Count - 1);
                    }
                    else if (!IsOrdinarySegment(segment))
                    {
                        error = "Folder path contains a reserved or ambiguous segment.";
                        return false;
                    }
                    else
                    {
                        segments.Add(segment);
                    }
                }
                start = index + 1;
            }

            if (segments.Count == 0)
            {
                normalized = prefix;
            }
            else
            {
                StringBuilder result = new StringBuilder(prefix.Length + value.Length);
                result.Append(prefix);
                for (int index = 0; index < segments.Count; index++)
                {
                    if (result[result.Length - 1] != '\\') result.Append('\\');
                    result.Append(segments[index]);
                }
                normalized = result.ToString();
            }

            if (normalized.Length > MaximumLength)
            {
                normalized = String.Empty;
                error = "Normalized folder path exceeds the supported Windows path length.";
                return false;
            }
            return true;
        }

        public static string NormalizeKey(string path)
        {
            return Normalize(path).ToUpperInvariant();
        }

        public static string DescendantPrefix(string path)
        {
            string normalized = Normalize(path);
            return normalized.EndsWith("\\", StringComparison.Ordinal) ? normalized : normalized + "\\";
        }

        public static string DescendantPrefixKey(string path)
        {
            return DescendantPrefix(path).ToUpperInvariant();
        }

        public static bool Contains(string rootPath, string candidatePath)
        {
            string root;
            string candidate;
            string ignored;
            if (!TryNormalize(rootPath, out root, out ignored) ||
                !TryNormalize(candidatePath, out candidate, out ignored)) return false;
            return String.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(DescendantPrefix(root), StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasForbiddenCharacter(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (Char.IsControl(character) || character == '<' || character == '>' ||
                    character == '"' || character == '|' || character == '?' || character == '*') return true;
            }
            return false;
        }

        private static bool HasDeviceNamespacePrefix(string value)
        {
            return value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("\\DEVICE\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("\\GLOBALROOT\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOrdinarySegment(string segment)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." ||
                segment[segment.Length - 1] == '.' || segment[segment.Length - 1] == ' ') return false;
            for (int index = 0; index < segment.Length; index++)
            {
                char character = segment[index];
                if (character == ':' || character == '\\' || character == '/') return false;
            }

            int dot = segment.IndexOf('.');
            string baseName = dot < 0 ? segment : segment.Substring(0, dot);
            if (String.Equals(baseName, "CON", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(baseName, "PRN", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(baseName, "AUX", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(baseName, "NUL", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(baseName, "CLOCK$", StringComparison.OrdinalIgnoreCase)) return false;
            if (baseName.Length == 4)
            {
                char number = baseName[3];
                if (IsReservedDosDeviceNumber(number) &&
                    (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                     baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }

        private static bool IsReservedDosDeviceNumber(char value)
        {
            // Windows treats superscript one, two and three exactly like the ASCII
            // device suffixes for COM/LPT names.  Other superscript, compatibility
            // and full-width characters remain ordinary path code points.
            return (value >= '1' && value <= '9') ||
                value == '\u00B9' || value == '\u00B2' || value == '\u00B3';
        }

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }
    }
}
