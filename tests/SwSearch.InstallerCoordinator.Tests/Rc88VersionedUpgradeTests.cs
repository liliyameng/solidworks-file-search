using System.Xml.Linq;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc88VersionedUpgradeTests
{
    private const string ProductVersion = "1.3.13";
    private const string FileVersion = "1.3.13.0";
    private const string ReleaseCandidate = "rc88";
    private const string ProductCode = "E5A97259-595F-4C23-8F9D-B5F423E7B69A";
    private const string Rc87ProductCode = "94DD3DCA-3188-4F92-9472-0A39F23EA628";
    private const string UpgradeCode = "D9158356-29B3-44D6-A15A-ACDCE8328703";

    [Fact]
    public void Rc88_UsesHigherVersionAndRetiresRc87()
    {
        var root = FindRepositoryRoot();
        foreach (var name in new[] { "Build-Release.ps1", "Build-Msi.ps1", "Verify-Msi.ps1" })
        {
            var script = Read(root, "installer", name);
            Assert.Contains($"[string]$Version = '{ProductVersion}'", script, StringComparison.Ordinal);
            Assert.Contains($"ReleaseCandidate = '{ReleaseCandidate}'", script, StringComparison.Ordinal);
            Assert.Contains($"ProductCode = '{ProductCode}'", script, StringComparison.Ordinal);
            Assert.Contains("$latestShippedProductVersion = [Version]'1.3.12'", script, StringComparison.Ordinal);
        }
        foreach (var name in new[] { "Build-Msi.ps1", "Verify-Msi.ps1" })
            Assert.Contains(Rc87ProductCode, Read(root, "installer", name), StringComparison.Ordinal);

        var props = Read(root, "Directory.Build.props");
        Assert.Contains($"<Version>{ProductVersion}</Version>", props, StringComparison.Ordinal);
        Assert.Contains($"<AssemblyVersion>{FileVersion}</AssemblyVersion>", props, StringComparison.Ordinal);
        Assert.Contains($"<FileVersion>{FileVersion}</FileVersion>", props, StringComparison.Ordinal);
        Assert.Contains($"<InformationalVersion>{ProductVersion}</InformationalVersion>", props, StringComparison.Ordinal);
        var assembly = Read(root, "src", "SwSearch.SolidWorksAddin", "AssemblyInfo.cs");
        Assert.Contains($"AssemblyVersion(\"{FileVersion}\")", assembly, StringComparison.Ordinal);
        Assert.Contains($"AssemblyFileVersion(\"{FileVersion}\")", assembly, StringComparison.Ordinal);
        Assert.Contains($"AssemblyInformationalVersion(\"{ProductVersion}\")", assembly, StringComparison.Ordinal);
        Assert.Contains($"[string]$Version = '{ProductVersion}'", Read(root, "installer", "Publish.ps1"), StringComparison.Ordinal);
        Assert.Contains($"[string]$Version = '{FileVersion}'", Read(root, "installer", "Build-ModernContextMenu.ps1"), StringComparison.Ordinal);
        Assert.Contains($"[string]$ProductVersion = '{ProductVersion}'", Read(root, "installer", "Build-ModernContextMenu.ps1"), StringComparison.Ordinal);
        Assert.Contains($"[string]$Version = '{ProductVersion}'", Read(root, "installer", "Verify-Taskpane.ps1"), StringComparison.Ordinal);
        Assert.Contains($"[string]$BinaryVersion = '{FileVersion}'", Read(root, "installer", "Verify-Taskpane.ps1"), StringComparison.Ordinal);

        var document = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        var package = Assert.IsType<XElement>(document.Root?.Element(ns + "Package"));
        var majorUpgrade = Assert.IsType<XElement>(package.Element(ns + "MajorUpgrade"));
        Assert.Equal(UpgradeCode, (string?)package.Attribute("UpgradeCode"));
        Assert.Equal("afterInstallExecute", (string?)majorUpgrade.Attribute("Schedule"));
        Assert.Null(majorUpgrade.Attribute("AllowSameVersionUpgrades"));
    }

    private static string Read(string root, params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "installer", "wix", "Package.wxs"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
