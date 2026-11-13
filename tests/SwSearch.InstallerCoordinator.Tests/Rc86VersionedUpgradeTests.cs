using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc86VersionedUpgradeTests
{
    private const string Rc86ProductCode = "2ADEDB57-8327-4F5A-8E9F-C7A9623F526B";

    [Fact]
    public void Rc86_ProductCode_RemainsRetiredInBuildAndVerifyGates()
    {
        var root = FindRepositoryRoot();
        var build = Read(root, "installer", "Build-Msi.ps1");
        var verify = Read(root, "installer", "Verify-Msi.ps1");

        foreach (var script in new[] { build, verify })
        {
            var retiredProductCodes = RetiredProductCodes(script);
            Assert.Contains(Rc86ProductCode, retiredProductCodes);
            Assert.Equal(retiredProductCodes.Count, retiredProductCodes.Distinct(StringComparer.Ordinal).Count());
        }
    }

    private static IReadOnlyList<string> RetiredProductCodes(string script)
    {
        var start = script.IndexOf("$retiredProductCode", StringComparison.Ordinal);
        Assert.True(start >= 0, "Retired ProductCode list is missing.");

        var end = script.IndexOf(")) {", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = script.IndexOf(") | ForEach-Object", start, StringComparison.Ordinal);
        }
        Assert.True(end >= 0, "Retired ProductCode list terminator is missing.");

        return script[start..end]
            .Split(new[] { '\r', '\n', '\'', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => Guid.TryParse(value, out _))
            .Select(value => Guid.Parse(value).ToString("D").ToUpperInvariant())
            .ToArray();
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "installer", "wix", "Package.wxs")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
