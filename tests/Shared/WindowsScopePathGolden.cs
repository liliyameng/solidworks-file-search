using System;
using System.Collections.Generic;
using System.Text;

namespace SwSearch.Testing
{
    public sealed class WindowsScopePathCase
    {
        public WindowsScopePathCase(string name, string input, string expected, bool isValid)
        {
            Name = name;
            Input = input;
            Expected = expected;
            IsValid = isValid;
        }

        public string Name { get; private set; }
        public string Input { get; private set; }
        public string Expected { get; private set; }
        public bool IsValid { get; private set; }
    }

    /// <summary>Golden inputs compiled into modern tests and the real CLR4 candidate harness.</summary>
    public static class WindowsScopePathGolden
    {
        public static IList<WindowsScopePathCase> Cases()
        {
            string mediumDrive = BuildLongPath("E:\\Medium", 300);
            string mediumUnc = BuildLongPath("\\\\server\\share\\Medium", 300);
            string longDrive = BuildLongPath("C:\\Long", 1050);
            string longUnc = BuildLongPath("\\\\server\\share\\Long", 1050);
            return new List<WindowsScopePathCase>
            {
                Valid("drive-root", "C:\\", "C:\\"),
                Valid("drive-case-slashes-dot-parent", "c:/Design//Pump/./Old/../Final/", "C:\\Design\\Pump\\Final"),
                Valid("unicode", "D:\\设计 库\\泵体", "D:\\设计 库\\泵体"),
                Valid("unicode-composed-preserved", "C:\\Unicode\\caf\u00E9", "C:\\Unicode\\caf\u00E9"),
                Valid("unicode-decomposed-preserved", "C:\\Unicode\\cafe\u0301", "C:\\Unicode\\cafe\u0301"),
                Valid("unicode-compatibility-preserved", "C:\\Unicode\\\u212B-\uFF21\uFF22\uFF23-\uFB00", "C:\\Unicode\\\u212B-\uFF21\uFF22\uFF23-\uFB00"),
                Valid("superscript-four-preserved", "C:\\folder\\COM\u2074", "C:\\folder\\COM\u2074"),
                Valid("superscript-zero-preserved", "C:\\folder\\COM\u2070", "C:\\folder\\COM\u2070"),
                Valid("superscript-one-suffix-preserved", "C:\\folder\\COM\u00B9foo", "C:\\folder\\COM\u00B9foo"),
                Valid("fullwidth-number-preserved", "C:\\folder\\LPT\uFF11", "C:\\folder\\LPT\uFF11"),
                Valid("unc-superscript-four-extension-preserved", "\\\\server\\share\\com\u2074.txt", "\\\\server\\share\\com\u2074.txt"),
                Valid("unc-root", "\\\\Server\\Share\\", "\\\\Server\\Share\\"),
                Valid("unc-collapse", "\\\\server\\share\\A\\\\B\\.\\C\\..\\", "\\\\server\\share\\A\\B"),
                Valid("medium-drive-283", mediumDrive, mediumDrive),
                Valid("medium-unc-283", mediumUnc, mediumUnc),
                Valid("long-drive-1000", longDrive, longDrive),
                Valid("long-unc-1000", longUnc, longUnc),
                Invalid("empty", ""),
                Invalid("relative", "folder\\child"),
                Invalid("drive-relative", "C:folder"),
                Invalid("single-root", "\\folder"),
                Invalid("single-root-only", "\\"),
                Invalid("unc-server-only", "\\\\server"),
                Invalid("unc-empty-share", "\\\\server\\"),
                Invalid("unc-empty-server", "\\\\\\server\\share"),
                Invalid("extended-drive", "\\\\?\\C:\\folder"),
                Invalid("extended-unc", "\\\\?\\UNC\\server\\share\\folder"),
                Invalid("win32-device", "\\\\.\\C:\\folder"),
                Invalid("nt-object", "\\Device\\HarddiskVolume1\\folder"),
                Invalid("nt-dos-device", "\\??\\C:\\folder"),
                Invalid("globalroot", "\\\\GLOBALROOT\\share\\folder"),
                Invalid("pipe", "\\\\server\\pipe\\name"),
                Invalid("volume-guid", "\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\"),
                Invalid("root-escape-drive", "C:\\..\\folder"),
                Invalid("root-escape-unc", "\\\\server\\share\\..\\folder"),
                Invalid("ads", "C:\\folder\\name:stream"),
                Invalid("wildcard", "C:\\folder\\*.SLDPRT"),
                Invalid("quote", "C:\\folder\\\"name"),
                Invalid("control", "C:\\folder\\bad\u0001name"),
                Invalid("tail-dot", "C:\\folder\\name."),
                Invalid("tail-space", "C:\\folder\\name "),
                Invalid("dos-con", "C:\\folder\\CON"),
                Invalid("dos-nul-extension", "C:\\folder\\nul.txt"),
                Invalid("dos-com", "C:\\folder\\COM1"),
                Invalid("dos-lpt", "\\\\server\\share\\LPT9.log"),
                Invalid("dos-com-superscript-one", "C:\\folder\\COM\u00B9"),
                Invalid("dos-com-superscript-two-extension", "C:\\folder\\COM\u00B2.txt"),
                Invalid("dos-lpt-superscript-three", "C:\\folder\\LPT\u00B3"),
                Invalid("dos-com-superscript-middle-component", "C:\\COM\u00B9\\folder"),
                Invalid("dos-lpt-superscript-unc-case", "\\\\server\\share\\lpt\u00B2\\folder")
            };
        }

        public static string BuildLongPath(string prefix, int minimumLength)
        {
            StringBuilder value = new StringBuilder(prefix);
            int index = 0;
            while (value.Length < minimumLength)
            {
                value.Append("\\segment-");
                value.Append(index.ToString("D4"));
                value.Append("-abcdefghijklmnop");
                index++;
            }
            return value.ToString();
        }

        private static WindowsScopePathCase Valid(string name, string input, string expected)
        {
            return new WindowsScopePathCase(name, input, expected, true);
        }

        private static WindowsScopePathCase Invalid(string name, string input)
        {
            return new WindowsScopePathCase(name, input, String.Empty, false);
        }
    }
}
