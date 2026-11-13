using System.Text;

namespace SwSearch.Integration.Tests;

public sealed class SourceEncodingAuditTests
{
    // These are characteristic leading characters produced when UTF-8 Chinese
    // is decoded as a legacy code page and then saved again.  They are not used
    // by the product's intended Chinese UI text.
    private static readonly string[] MojibakeMarkers =
    [
        // Taskpane Shell-result strings that regressed in RC78 after a
        // GBK/UTF-8 round trip ("文件", "没有", "类型", "所在").
        "鏂囦欢",
        "娌℃湁",
        "绫诲瀷",
        "鎵€鍦�",
        "鏈",
        "锛?",
        "銆?",
        "缁х画",
        "鍐呯疆",
        "鍙戠幇",
    ];

    [Fact]
    public void ProductSources_DoNotContainKnownChineseMojibake()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var findings = new List<string>();

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .Where(static path => !IsBuildOutput(path))
                     .Where(static path => IsTextSource(path)))
        {
            var text = File.ReadAllText(path, new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true));
            foreach (var marker in MojibakeMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    findings.Add($"{Path.GetRelativePath(repositoryRoot, path)}: {marker}");
                }
            }
        }

        Assert.True(findings.Count == 0,
            "Product sources contain likely corrupted Chinese text:" + Environment.NewLine +
            string.Join(Environment.NewLine, findings));
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SolidWorksSearch.sln from test output.");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".xaml" or ".csproj" or ".props" or ".md" or ".txt" or ".json";
}
