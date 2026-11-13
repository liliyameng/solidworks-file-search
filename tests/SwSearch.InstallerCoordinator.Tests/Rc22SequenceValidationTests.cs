using System.Xml.Linq;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc22SequenceValidationTests
{
    private const string ProductCode = "23336225-6550-4621-B804-0444797F620E";
    private const string UpgradeCode = "D9158356-29B3-44D6-A15A-ACDCE8328703";

    [Fact]
    public void Rc21IllegalDeferredBeforeRemoveSequence_IsRejected()
    {
        var rc21 = Sequence(
            userIpc: 1002,
            installValidate: 1400,
            installInitialize: 1500,
            privileged: 1501,
            installFiles: 4000,
            installExecute: 6500,
            removeExistingProducts: 1502,
            installFinalize: 6600);

        Assert.False(IsLegalLateRemoveExistingProductsWindow(rc21));
        Assert.True(rc21["SwSearchPrivilegedHandle"] < rc21["RemoveExistingProducts"]);
        Assert.True(rc21["RemoveExistingProducts"] < rc21["InstallExecute"]);
    }

    [Fact]
    public void Rc22LateRemoveSequence_IsLegalAndPreservesShutdownOrdering()
    {
        var rc22 = Sequence(
            userIpc: 1002,
            installValidate: 1400,
            installInitialize: 1500,
            privileged: 1501,
            installFiles: 4000,
            installExecute: 6500,
            removeExistingProducts: 6501,
            installFinalize: 6600);

        Assert.True(IsLegalLateRemoveExistingProductsWindow(rc22));
        Assert.True(rc22["SwSearchUserIpc"] < rc22["InstallValidate"]);
        Assert.True(rc22["InstallInitialize"] < rc22["SwSearchPrivilegedHandle"]);
        Assert.True(rc22["SwSearchPrivilegedHandle"] < rc22["InstallFiles"]);
        Assert.True(rc22["InstallExecute"] < rc22["RemoveExistingProducts"]);
        Assert.True(rc22["RemoveExistingProducts"] < rc22["InstallFinalize"]);
    }

    [Fact]
    public void Rc22Source_UsesHigherVersionStandardWindowAndNoForcedOverwrite()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-Msi.ps1"));
        var release = File.ReadAllText(Path.Combine(root, "installer", "Build-Release.ps1"));
        var packageSource = File.ReadAllText(Path.Combine(root, "installer", "wix", "Package.wxs"));
        var document = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        var package = document.Root!.Element(ns + "Package")!;
        var upgrade = package.Element(ns + "MajorUpgrade")!;

        Assert.Contains("Version = '1.2.43'", build, StringComparison.Ordinal);
        Assert.Contains("ReleaseCandidate = 'rc43'", build, StringComparison.Ordinal);
        Assert.Contains(ProductCode, build, StringComparison.Ordinal);
        Assert.Contains(ProductCode, release, StringComparison.Ordinal);
        Assert.Equal(UpgradeCode, (string?)package.Attribute("UpgradeCode"));
        Assert.Equal("afterInstallExecute", (string?)upgrade.Attribute("Schedule"));
        Assert.Null(upgrade.Attribute("AllowSameVersionUpgrades"));
        var executeSequence = package.Element(ns + "InstallExecuteSequence")!;
        var privileged = executeSequence.Elements(ns + "Custom")
            .Single(element => (string?)element.Attribute("Action") == "SwSearchPrivilegedHandle");
        Assert.Equal("InstallInitialize", (string?)privileged.Attribute("After"));
        Assert.Null(privileged.Attribute("Before"));
        Assert.Contains("RemoveLegacySettingsHtml", build, StringComparison.Ordinal);
        Assert.DoesNotContain("REINSTALLMODE=amus", packageSource + build, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLegalLateRemoveExistingProductsWindow(
        IReadOnlyDictionary<string, int> sequence) =>
        sequence["InstallExecute"] < sequence["RemoveExistingProducts"] &&
        sequence["RemoveExistingProducts"] < sequence["InstallFinalize"];

    private static IReadOnlyDictionary<string, int> Sequence(
        int userIpc,
        int installValidate,
        int installInitialize,
        int privileged,
        int installFiles,
        int installExecute,
        int removeExistingProducts,
        int installFinalize) => new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SwSearchUserIpc"] = userIpc,
            ["InstallValidate"] = installValidate,
            ["InstallInitialize"] = installInitialize,
            ["SwSearchPrivilegedHandle"] = privileged,
            ["InstallFiles"] = installFiles,
            ["InstallExecute"] = installExecute,
            ["RemoveExistingProducts"] = removeExistingProducts,
            ["InstallFinalize"] = installFinalize,
        };

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException();
    }
}
