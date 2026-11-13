using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc85VersionedUpgradeTests
{
    private const string ProductCode = "02F35BA5-F73C-4D82-AB56-29A4C4FB1349";

    [Fact]
    public void Rc85_ProductCodeRemainsRetiredAfterTheNextVersionShips()
    {
        var root = FindRepositoryRoot();
        foreach (var scriptPath in new[]
                 {
                     Path.Combine(root, "installer", "Build-Msi.ps1"),
                     Path.Combine(root, "installer", "Verify-Msi.ps1"),
                 })
        {
            var script = File.ReadAllText(scriptPath);
            Assert.Contains($"'{ProductCode}'", script, StringComparison.Ordinal);
        }
    }

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
