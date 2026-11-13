using System.Xml.Linq;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc25ModernContextMenuTests
{
    [Fact]
    public void Manifest_DeclaresPackagedComForFolderAndBackground()
    {
        var root = FindRoot();
        var manifest = XDocument.Load(Path.Combine(root, "src", "SwSearch.ModernContextMenu", "AppxManifest.xml.in"));
        var text = manifest.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("windows.fileExplorerContextMenus", text, StringComparison.Ordinal);
        Assert.Contains("Type=\"Directory\"", text, StringComparison.Ordinal);
        Assert.Contains("Type=\"Directory\\Background\"", text, StringComparison.Ordinal);
        Assert.Equal(3, Count(text, "5C2E54CE-4EF3-4CD3-A4A1-6DE134AB7829"));
        Assert.Contains("com:SurrogateServer", File.ReadAllText(Path.Combine(root, "src", "SwSearch.ModernContextMenu", "AppxManifest.xml.in")), StringComparison.Ordinal);
    }

    [Fact]
    public void ModernFileProperties_AcceptsOnlyPartsAndAssemblies()
    {
        var root = FindRoot();
        var manifest = File.ReadAllText(
            Path.Combine(root, "src", "SwSearch.ModernContextMenu", "AppxManifest.xml.in"));
        var command = File.ReadAllText(
            Path.Combine(root, "src", "SwSearch.ModernContextMenu", "ExplorerCommand.cpp"));

        Assert.Contains("Type=\".sldprt\"", manifest, StringComparison.Ordinal);
        Assert.Contains("Type=\".sldasm\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(".slddrw", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".slddrw", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handler_PassesScopeAsQuotedCreateProcessArgumentWithoutShell()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "SwSearch.ModernContextMenu", "ExplorerCommand.cpp"));

        Assert.Contains("QuoteCommandLineArgument(folder)", source, StringComparison.Ordinal);
        Assert.Contains("CreateProcessW(", source, StringComparison.Ordinal);
        Assert.Contains("application.c_str(), mutableCommand.data()", source, StringComparison.Ordinal);
        Assert.Contains(" --scope-folder ", source, StringComparison.Ordinal);
        Assert.Contains("InstallDirectory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellExecute", source, StringComparison.Ordinal);
        Assert.DoesNotContain("system(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationHelper_UsesLocalSignedPackageAndSupportsFullLifecycle()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "SwSearch.ModernContextMenu", "PackageRegistration.cpp"));

        Assert.Contains("--enable-local", source, StringComparison.Ordinal);
        Assert.Contains("AddPackageAsync", source, StringComparison.Ordinal);
        Assert.Contains("--status", source, StringComparison.Ordinal);
        Assert.Contains("--disable", source, StringComparison.Ordinal);
        Assert.Contains("RemovePackageAsync", source, StringComparison.Ordinal);
        Assert.Contains("kDeploymentTimeoutMilliseconds = 3000", source, StringComparison.Ordinal);
        Assert.Contains("WaitForDeployment(operation)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowUnsigned", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DevelopmentMode", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppBridge_UsesStructuredArgumentListForRegistrationHelper()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "SwSearch.Integration", "IntegrationServices.cs"));

        Assert.Contains("_fileProperties ? \"--set-file\" : \"--set-folder\"", source, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(enabled ? \"1\" : \"0\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--external-location", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--package ", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UsesAuthorizedOfflineToolchainAndNeverPackagesPrivateKey()
    {
        var root = FindRoot();
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-ModernContextMenu.ps1"));
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));

        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", build, StringComparison.Ordinal);
        Assert.Contains("MakeAppx.exe", build, StringComparison.Ordinal);
        Assert.Contains("SignTool.exe", build, StringComparison.Ordinal);
        Assert.Contains("SignTool verification", build, StringComparison.Ordinal);
        Assert.Contains(".pfx','.p12','.pvk','.key", build, StringComparison.Ordinal);
        Assert.Contains("SwSearch.ModernContextMenu.msix", publish, StringComparison.Ordinal);
        Assert.Contains("SwSearch.ModernContextMenu.cer", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $privatePfx", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Msi_LifecycleTrustsOwnPublicCertificateAndRegistersAfterUpgradeRemoval()
    {
        var root = FindRoot();
        var package = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var actions = package.Descendants(wix + "CustomAction").ToDictionary(
            element => (string)element.Attribute("Id")!, StringComparer.Ordinal);
        var scheduled = package.Descendants(wix + "InstallExecuteSequence").Elements(wix + "Custom").ToDictionary(
            element => (string)element.Attribute("Action")!, StringComparer.Ordinal);

        Assert.Equal("InstallModernMenuCertificate", (string?)actions["InstallModernMenuCertificate"].Attribute("DllEntry"));
        Assert.Equal("deferred", (string?)actions["InstallModernMenuCertificate"].Attribute("Execute"));
        Assert.Equal("no", (string?)actions["InstallModernMenuCertificate"].Attribute("Impersonate"));
        Assert.Equal("RemoveModernMenuCertificate", (string?)actions["RemoveModernMenuCertificate"].Attribute("DllEntry"));
        Assert.Equal("--enable-local", (string?)actions["RegisterModernContextMenu"].Attribute("ExeCommand"));
        Assert.Equal("--disable", (string?)actions["UnregisterModernContextMenu"].Attribute("ExeCommand"));
        Assert.Equal("ignore", (string?)actions["RegisterModernContextMenu"].Attribute("Return"));
        Assert.Equal("check", (string?)actions["UnregisterModernContextMenu"].Attribute("Return"));
        Assert.Contains("NOT UPGRADINGPRODUCTCODE", (string?)scheduled["UnregisterModernContextMenu"].Attribute("Condition"), StringComparison.Ordinal);
        Assert.Equal("RemoveExistingProducts", (string?)scheduled["RegisterModernContextMenu"].Attribute("After"));
        Assert.Equal("InstallFiles", (string?)scheduled["InstallModernMenuCertificate"].Attribute("After"));
        Assert.Equal("RemoveFiles", (string?)scheduled["RemoveModernMenuCertificate"].Attribute("Before"));
        Assert.Contains("UPGRADINGPRODUCTCODE", (string?)scheduled["SetRemoveModernMenuCertificateData"].Attribute("Condition"), StringComparison.Ordinal);
    }

    [Fact]
    public void ModernFileCommand_DefaultsOnAndClassicPartAssemblyVerbsArePreWindows11Only()
    {
        var root = FindRoot();
        var helper = File.ReadAllText(Path.Combine(root, "src", "SwSearch.ModernContextMenu", "PackageRegistration.cpp"));
        var package = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";

        Assert.Contains("L\"FilePropertiesEnabled\"", helper, StringComparison.Ordinal);
        Assert.Contains("DWORD value = 1", helper, StringComparison.Ordinal);
        foreach (var id in new[] { "ContextMenuSldprt", "ContextMenuSldasm" })
        {
            var component = package.Descendants(wix + "Component").Single(node => (string?)node.Attribute("Id") == id);
            Assert.Equal("WindowsBuild < 22000", (string?)component.Attribute("Condition"));
        }
        Assert.DoesNotContain(package.Descendants(wix + "Component"),
            node => (string?)node.Attribute("Id") == "ContextMenuSlddrw");
    }

    [Fact]
    public void MajorUpgrade_CleansOnlyTheLegacyDrawingPropertyVerb()
    {
        var root = FindRoot();
        var package = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var cleanup = package.Descendants(wix + "Component").Single(node =>
            (string?)node.Attribute("Id") == "LegacyDrawingPropertyVerbCleanup");
        var removal = cleanup.Element(wix + "RemoveRegistryKey");

        Assert.NotNull(removal);
        Assert.Equal("WIX_UPGRADE_DETECTED", (string?)cleanup.Attribute("Condition"));
        Assert.Equal("HKCU", (string?)removal!.Attribute("Root"));
        Assert.Equal(
            "Software\\Classes\\SystemFileAssociations\\.slddrw\\shell\\SwSearch.ViewProperties",
            (string?)removal.Attribute("Key"));
        Assert.Equal("removeOnInstall", (string?)removal.Attribute("Action"));
        Assert.DoesNotContain(package.Descendants(wix + "RemoveRegistryKey"), node =>
            ((string?)node.Attribute("Key"))?.EndsWith("\\.slddrw", StringComparison.OrdinalIgnoreCase) == true ||
            string.Equals((string?)node.Attribute("Key"),
                "Software\\Classes\\SystemFileAssociations\\.slddrw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MsiBridge_UsesOnlyLocalMachineTrustedPeopleAndExactThumbprint()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "SwSearch.InstallerMsiBridge", "InstallerMsiBridge.cpp"));

        Assert.Contains("CERT_SYSTEM_STORE_LOCAL_MACHINE", source, StringComparison.Ordinal);
        Assert.Contains("L\"TrustedPeople\"", source, StringComparison.Ordinal);
        Assert.Contains("CERT_SHA1_HASH_PROP_ID", source, StringComparison.Ordinal);
        Assert.Contains("CertDeleteCertificateFromStore", source, StringComparison.Ordinal);
        Assert.Contains("CertCompareCertificateName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("L\"ROOT\"", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"D:\TC模板库\SolidWorks\插件模板-PDM")]
    [InlineData(@"C:\folder with spaces\A&B (test)\")]
    public void WindowsArgumentQuoterContract_CoversUnicodeSpacesSpecialCharactersAndTrailingSlash(string path)
    {
        var quoted = QuoteWindowsArgument(path);
        Assert.StartsWith("\"", quoted, StringComparison.Ordinal);
        Assert.EndsWith("\"", quoted, StringComparison.Ordinal);
        Assert.Equal(0, CountTrailingBackslashes(quoted[..^1]) % 2);
        Assert.Contains(path.TrimEnd('\\'), quoted, StringComparison.Ordinal);
    }

    private static string QuoteWindowsArgument(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static int CountTrailingBackslashes(string value)
    {
        var count = 0;
        for (var index = value.Length - 1; index >= 0 && value[index] == '\\'; index--) count++;
        return count;
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "installer", "wix", "Package.wxs"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
