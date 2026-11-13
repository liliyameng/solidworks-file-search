using System.Text.RegularExpressions;
using System.Xml.Linq;
using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc19InstallerContractTests
{
    private const string UpgradeCode = "D9158356-29B3-44D6-A15A-ACDCE8328703";
    private const string FourPathCondition =
        "NOT MSIPATCHREMOVE AND NOT UPGRADINGPRODUCTCODE AND " +
        "(NOT Installed OR REINSTALL OR REMOVE~=\"ALL\" OR (Installed AND NOT REMOVE~=\"ALL\"))";

    [Fact]
    public void Package_DetectsBothDotNet8FrameworksBeforeLaunchConditions()
    {
        var package = LoadPackage();
        XNamespace ns = package.Name.Namespace;

        var check = package.Elements(ns + "CustomAction")
            .Single(element => (string?)element.Attribute("Id") == "CheckSwSearchDotNet8");
        Assert.Equal("SwSearchInstallerMsiBridge", (string?)check.Attribute("BinaryRef"));
        Assert.Equal("CheckSwSearchDotNet8", (string?)check.Attribute("DllEntry"));

        foreach (var sequenceName in new[] { "InstallUISequence", "InstallExecuteSequence" })
        {
            var scheduled = package.Element(ns + sequenceName)!.Elements(ns + "Custom")
                .Single(element => (string?)element.Attribute("Action") == "CheckSwSearchDotNet8");
            Assert.Equal("LaunchConditions", (string?)scheduled.Attribute("Before"));
        }

        var launchConditions = package.Elements(ns + "Launch").Select(element =>
            (string?)element.Attribute("Condition") ?? string.Empty).ToArray();
        Assert.Contains("SWSEARCHDOTNETCORE8 = \"1\"", launchConditions);
        Assert.Contains("SWSEARCHWINDOWSDESKTOP8 = \"1\"", launchConditions);
    }

    [Fact]
    public void Package_ClosesProductsOnInstallRepairUpgradeAndUninstallBeforeFileValidation()
    {
        var package = LoadPackage();
        XNamespace ns = package.Name.Namespace;
        var sequence = package.Element(ns + "InstallExecuteSequence")!.Elements(ns + "Custom")
            .ToDictionary(element => (string)element.Attribute("Action")!, StringComparer.Ordinal);

        Assert.Equal(FourPathCondition, (string?)sequence["CaptureSwSearchTrustedContext"].Attribute("Condition"));
        Assert.Equal("CostFinalize", (string?)sequence["CaptureSwSearchTrustedContext"].Attribute("After"));
        Assert.Equal("CaptureSwSearchTrustedContext", (string?)sequence["SwSearchUserIpc"].Attribute("After"));
        Assert.Equal(
            "SWSEARCHCONTEXTREADY = \"1\" AND " + FourPathCondition,
            (string?)sequence["SwSearchUserIpc"].Attribute("Condition"));
        Assert.Equal(
            "SWSEARCHMACHINECONTEXTREADY = \"1\" AND " + FourPathCondition,
            (string?)sequence["SetSwSearchPrivilegedHandleData"].Attribute("Condition"));
        Assert.Equal("InstallInitialize", (string?)sequence["SetSwSearchPrivilegedHandleData"].Attribute("Before"));
        Assert.Null(sequence["SetSwSearchPrivilegedHandleData"].Attribute("After"));
        Assert.Equal(
            "SWSEARCHMACHINECONTEXTREADY = \"1\" AND " + FourPathCondition,
            (string?)sequence["SwSearchPrivilegedHandle"].Attribute("Condition"));
        Assert.Equal("InstallInitialize", (string?)sequence["SwSearchPrivilegedHandle"].Attribute("After"));
        Assert.Null(sequence["SwSearchPrivilegedHandle"].Attribute("Before"));

        Assert.Contains("REINSTALL", FourPathCondition, StringComparison.Ordinal);
        Assert.Contains("Installed AND NOT REMOVE~=\"ALL\"", FourPathCondition, StringComparison.Ordinal);
        Assert.Contains("REMOVE~=\"ALL\"", FourPathCondition, StringComparison.Ordinal);
        Assert.DoesNotContain(package.Elements(ns + "Property"),
            element => (string?)element.Attribute("Id") == "MSIRESTARTMANAGERCONTROL" &&
                       (string?)element.Attribute("Value") == "Disable");
    }

    [Fact]
    public void Packaging_IsFrameworkDependentVersionedCurrentAndUsesReasonableCompression()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var currentProperties = props.Root!.Elements("PropertyGroup")
            .Single(group => group.Element("Version") is not null);
        var currentVersion = (string)currentProperties.Element("Version")!;
        var currentBinaryVersion = (string)currentProperties.Element("AssemblyVersion")!;
        var assemblyVersion = typeof(InstallerCoordinatorOptions).Assembly.GetName().Version;
        Assert.NotNull(assemblyVersion);
        Assert.Equal(currentVersion, assemblyVersion!.ToString(3));
        Assert.Equal(currentBinaryVersion, assemblyVersion.ToString());
        Assert.Equal(currentVersion + ".0", currentBinaryVersion);

        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var actions = File.ReadAllText(Path.Combine(root, "installer", "Build-InstallerActions.ps1"));
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-Msi.ps1"));
        var release = File.ReadAllText(Path.Combine(root, "installer", "Build-Release.ps1"));
        var verify = File.ReadAllText(Path.Combine(root, "installer", "Verify-Msi.ps1"));
        var package = LoadPackage();
        XNamespace ns = package.Name.Namespace;

        Assert.Contains("--self-contained false", publish, StringComparison.Ordinal);
        Assert.Contains("--self-contained false", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("--self-contained true", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("--self-contained true", actions, StringComparison.Ordinal);
        Assert.Equal(currentVersion, ReadScriptDefault(publish, "Version"));
        Assert.Equal(currentVersion, ReadScriptDefault(build, "Version"));
        Assert.Equal(currentVersion, ReadScriptDefault(release, "Version"));
        Assert.Equal(currentVersion, ReadScriptDefault(verify, "Version"));
        Assert.Contains("$expectedTaskpaneBinaryVersion = \"$Version.0\"", publish, StringComparison.Ordinal);

        var releaseCandidates = new[]
        {
            ReadScriptDefault(build, "ReleaseCandidate"),
            ReadScriptDefault(release, "ReleaseCandidate"),
            ReadScriptDefault(verify, "ReleaseCandidate")
        };
        Assert.Single(releaseCandidates.Distinct(StringComparer.Ordinal));
        Assert.All(releaseCandidates, candidate => Assert.Matches("^rc[0-9]+$", candidate));

        var productCodes = new[]
        {
            ReadScriptDefault(build, "ProductCode"),
            ReadScriptDefault(release, "ProductCode"),
            ReadScriptDefault(verify, "ProductCode")
        };
        Assert.Single(productCodes.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.All(productCodes, productCode => Assert.NotEqual(Guid.Empty, Guid.Parse(productCode)));
        Assert.Equal("medium", (string?)package.Element(ns + "MediaTemplate")?.Attribute("CompressionLevel"));
        Assert.Equal(UpgradeCode, (string?)package.Attribute("UpgradeCode"));

        foreach (var forbidden in new[]
                 {
                     "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
                     "System.Private.CoreLib.dll", "PresentationFramework.dll"
                 })
        {
            Assert.Contains(forbidden, publish, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NativeBridge_UsesRegisteredX64RootAndValidatesFrameworkMarkers()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "SwSearch.InstallerMsiBridge", "InstallerMsiBridge.cpp"));

        Assert.Contains("InstalledVersions\\\\x64", source, StringComparison.Ordinal);
        Assert.Contains("RRF_SUBKEY_WOW6464KEY", source, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App", source, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App", source, StringComparison.Ordinal);
        Assert.Contains("System.Private.CoreLib.dll", source, StringComparison.Ordinal);
        Assert.Contains("PresentationFramework.dll", source, StringComparison.Ordinal);
        Assert.Contains("CheckSwSearchDotNet8", source, StringComparison.Ordinal);
    }

    private static string ReadScriptDefault(string source, string parameter)
    {
        var match = Regex.Match(
            source,
            @"\[string\]\$" + Regex.Escape(parameter) + @"\s*=\s*'(?<value>[^']+)'",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Default value was not found for ${parameter}.");
        return match.Groups["value"].Value;
    }

    private static XElement LoadPackage()
    {
        var root = FindRepositoryRoot();
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
