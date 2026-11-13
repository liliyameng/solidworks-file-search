using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc87VersionedUpgradeTests
{
    private const string Rc87ProductCode = "94DD3DCA-3188-4F92-9472-0A39F23EA628";

    [Fact]
    public void Rc87_ProductCodeIsRetired()
    {
        var root = FindRepositoryRoot();
        foreach (var name in new[] { "Build-Msi.ps1", "Verify-Msi.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(root, "installer", name));
            Assert.Contains(Rc87ProductCode, script, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "installer", "wix", "Package.wxs"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
