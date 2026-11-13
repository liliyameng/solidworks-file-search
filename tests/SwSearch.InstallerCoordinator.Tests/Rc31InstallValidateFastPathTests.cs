using System.Xml.Linq;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc31InstallValidateFastPathTests
{
    [Fact]
    public void ImpersonatedCurrentUserPhase_QueriesRelatedProductInMachineContext()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRoot(), "src", "SwSearch.InstallerCoordinator", "WindowsInstalledProductCatalog.cs"));

        Assert.Contains("MsiInstallContextMachine = 4", source, StringComparison.Ordinal);
        Assert.Contains("MsiGetProductInfoExW", source, StringComparison.Ordinal);
        Assert.Contains("MsiGetComponentPathExW", source, StringComparison.Ordinal);
        Assert.Contains("null, MsiInstallContextMachine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MsiGetProductInfoW(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MsiGetComponentPathW(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_ClosesProductsBeforeInstallValidate_AndMenuInstallIsNonBlocking()
    {
        var document = XDocument.Load(Path.Combine(FindRoot(), "installer", "wix", "Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var sequence = document.Descendants(wix + "InstallExecuteSequence").Elements(wix + "Custom")
            .ToDictionary(item => (string)item.Attribute("Action")!, StringComparer.Ordinal);
        var actions = document.Descendants(wix + "CustomAction")
            .ToDictionary(item => (string)item.Attribute("Id")!, StringComparer.Ordinal);

        Assert.Equal("CaptureSwSearchTrustedContext", (string?)sequence["SwSearchUserIpc"].Attribute("After"));
        var closeApplications = sequence.Single(item =>
            item.Key.EndsWith("Wix4CloseApplications_$(sys.BUILDARCHSHORT)", StringComparison.Ordinal)).Value;
        Assert.Equal("SwSearchUserIpc", (string?)closeApplications.Attribute("After"));
        Assert.DoesNotContain(document.Descendants(wix + "Property"), item =>
            (string?)item.Attribute("Id") == "MSIRESTARTMANAGERCONTROL" &&
            (string?)item.Attribute("Value") == "Disable");
        Assert.Equal("ignore", (string?)actions["RegisterModernContextMenu"].Attribute("Return"));
        Assert.Equal("check", (string?)actions["UnregisterModernContextMenu"].Attribute("Return"));
    }

    [Fact]
    public void TrayShutdown_AcknowledgesOnlyAfterOwnedResourcesAreDisposed()
    {
        var app = File.ReadAllText(Path.Combine(FindRoot(), "src", "SwSearch.App", "App.xaml.cs"));
        var dispose = app.IndexOf("_tray?.Dispose();", StringComparison.Ordinal);
        var acknowledge = app.IndexOf("_installerShutdownAcknowledgedEvent?.Set();", StringComparison.Ordinal);

        Assert.True(dispose >= 0 && acknowledge > dispose);
        Assert.Contains("AppInstallerShutdownAcknowledgedEventName", app, StringComparison.Ordinal);
        Assert.Contains("acknowledgedEvent.WaitOne(_timeout)", File.ReadAllText(Path.Combine(
            FindRoot(), "src", "SwSearch.InstallerCoordinator", "ProductShutdownTransport.cs")), StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
