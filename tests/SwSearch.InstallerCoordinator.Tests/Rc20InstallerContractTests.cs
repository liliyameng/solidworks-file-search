using System.Xml.Linq;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc20InstallerContractTests
{
    private const string ProductCode = "33B51BB6-44B5-44EF-B2ED-78A1032C5A25";
    private const string UpgradeCode = "D9158356-29B3-44D6-A15A-ACDCE8328703";

    [Fact]
    public void ReleaseDefaultsAndAddinRegistration_AreRc20()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-Msi.ps1"));
        var release = File.ReadAllText(Path.Combine(root, "installer", "Build-Release.ps1"));
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var package = LoadPackage(root);
        XNamespace ns = package.Name.Namespace;

        Assert.Contains("ReleaseCandidate = 'rc20'", build, StringComparison.Ordinal);
        Assert.Contains("ReleaseCandidate = 'rc20'", release, StringComparison.Ordinal);
        Assert.Contains("ProductCode = '" + ProductCode + "'", build, StringComparison.Ordinal);
        Assert.Contains("ProductCode = '" + ProductCode + "'", release, StringComparison.Ordinal);
        Assert.Contains("1.2.20.0", publish, StringComparison.Ordinal);
        Assert.Equal(UpgradeCode, (string?)package.Attribute("UpgradeCode"));
        Assert.Equal("medium", (string?)package.Element(ns + "MediaTemplate")?.Attribute("CompressionLevel"));

        var packageSource = File.ReadAllText(Path.Combine(root, "installer", "wix", "Package.wxs"));
        Assert.Contains(
            "SwSearch.SolidWorksAddin, Version=1.2.20.0, Culture=neutral, PublicKeyToken=null",
            packageSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FrameworkDependentAndFourPathShutdownContractsRemainEnabled()
    {
        var root = FindRepositoryRoot();
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var actions = File.ReadAllText(Path.Combine(root, "installer", "Build-InstallerActions.ps1"));
        var package = LoadPackage(root);
        XNamespace ns = package.Name.Namespace;

        Assert.Contains("--self-contained false", publish, StringComparison.Ordinal);
        Assert.Contains("--self-contained false", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("--self-contained true", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("--self-contained true", actions, StringComparison.Ordinal);
        foreach (var runtimeFile in new[]
                 {
                     "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
                     "System.Private.CoreLib.dll", "PresentationFramework.dll"
                 })
        {
            Assert.Contains(runtimeFile, publish, StringComparison.OrdinalIgnoreCase);
        }

        var sequence = package.Element(ns + "InstallExecuteSequence")!.Elements(ns + "Custom")
            .ToDictionary(item => (string)item.Attribute("Action")!, StringComparer.Ordinal);
        var userCondition = (string)sequence["SwSearchUserIpc"].Attribute("Condition")!;
        Assert.Contains("REINSTALL", userCondition, StringComparison.Ordinal);
        Assert.Contains("Installed AND NOT REMOVE~=\"ALL\"", userCondition, StringComparison.Ordinal);
        Assert.Contains("REMOVE~=\"ALL\"", userCondition, StringComparison.Ordinal);
        Assert.DoesNotContain(package.Elements(ns + "Property"),
            property => (string?)property.Attribute("Id") == "MSIRESTARTMANAGERCONTROL" &&
                        (string?)property.Attribute("Value") == "Disable");
    }

    [Fact]
    public void NativeDotNet8ChecksRunBeforeManagedCoordinator()
    {
        var root = FindRepositoryRoot();
        var package = LoadPackage(root);
        XNamespace ns = package.Name.Namespace;
        var native = package.Elements(ns + "CustomAction")
            .Single(action => (string?)action.Attribute("Id") == "CheckSwSearchDotNet8");
        Assert.Equal("SwSearchInstallerMsiBridge", (string?)native.Attribute("BinaryRef"));
        Assert.Equal("CheckSwSearchDotNet8", (string?)native.Attribute("DllEntry"));
        foreach (var sequenceName in new[] { "InstallUISequence", "InstallExecuteSequence" })
        {
            Assert.Equal("LaunchConditions", (string?)package.Element(ns + sequenceName)!
                .Elements(ns + "Custom")
                .Single(action => (string?)action.Attribute("Action") == "CheckSwSearchDotNet8")
                .Attribute("Before"));
        }
    }

    private static XElement LoadPackage(string root)
    {
        var document = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        return document.Root!.Element(ns + "Package")!;
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
