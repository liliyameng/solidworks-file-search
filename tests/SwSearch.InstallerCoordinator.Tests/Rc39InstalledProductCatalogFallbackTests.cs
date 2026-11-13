using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc39InstalledProductCatalogFallbackTests
{
    private const string UpgradeCode = "{D9158356-29B3-44D6-A15A-ACDCE8328703}";
    private const string ProductCode = "{49D3B604-E741-4D2A-975B-D1E84C0A8528}";
    private const string ProgramFiles = @"C:\Program Files";
    private const string InstallRoot = @"C:\Program Files\SwSearch";

    [Fact]
    public void Rc38RegistryKeyPath_UsesMachineRootAndTwoDenyReplacementVersionedAnchors()
    {
        var evidence = LegacyEvidence();
        var products = new WindowsInstalledProductCatalog(evidence)
            .GetRelatedProducts(UpgradeCode, @"D:\Incoming\Untrusted");

        var product = Assert.Single(products);
        Assert.Equal(ProductCode, product.ProductCode);
        Assert.Equal("1.2.38", product.Version);
        Assert.Equal(InstallRoot, product.InstallRoot, ignoreCase: true);
        Assert.True(product.IsTrustedRegisteredRoot);
        Assert.Equal(1, evidence.MachineRootReads);
        Assert.Contains(evidence.OpenCalls, call =>
            call.Path == Path.Combine(InstallRoot, "SwSearch.App.exe") && call.DenyReplacement);
        Assert.Contains(evidence.OpenCalls, call =>
            call.Path == Path.Combine(InstallRoot, "SwSearch.Broker.exe") && call.DenyReplacement);
    }

    [Fact]
    public void IncomingOrUserRootCannotReplaceMissingMachineRegistration()
    {
        var evidence = LegacyEvidence();
        evidence.MachineInstallDirectory = null;
        evidence.UserInstallDirectory = InstallRoot;

        var products = new WindowsInstalledProductCatalog(evidence)
            .GetRelatedProducts(UpgradeCode, InstallRoot);

        Assert.Empty(products);
        Assert.Equal(1, evidence.MachineRootReads);
        Assert.Equal(0, evidence.UserRootReads);
    }

    [Theory]
    [InlineData("outside-root")]
    [InlineData("reparse-outside")]
    [InlineData("wrong-version")]
    [InlineData("missing-app")]
    [InlineData("missing-broker")]
    public void LegacyFallback_FailsClosedWhenAnyAnchorProofIsMissing(string scenario)
    {
        var evidence = LegacyEvidence();
        switch (scenario)
        {
            case "outside-root":
                evidence.MachineInstallDirectory = @"D:\SwSearch";
                evidence.ResolvedMachineRoot = @"D:\SwSearch";
                break;
            case "reparse-outside":
                evidence.ResolvedMachineRoot = @"D:\Escaped\SwSearch";
                break;
            case "wrong-version":
                evidence.FileVersions[Path.Combine(InstallRoot, "SwSearch.App.exe")] = "1.2.37.0";
                break;
            case "missing-app":
                evidence.Anchors.Remove(Path.Combine(InstallRoot, "SwSearch.App.exe"));
                break;
            case "missing-broker":
                evidence.Anchors.Remove(Path.Combine(InstallRoot, "SwSearch.Broker.exe"));
                break;
        }

        Assert.Empty(new WindowsInstalledProductCatalog(evidence)
            .GetRelatedProducts(UpgradeCode, @"C:\Program Files\SwSearch"));
    }

    [Fact]
    public void Rc39DedicatedFileKeyPathAnchor_IsPreferredWithoutRegistryFallback()
    {
        var evidence = LegacyEvidence();
        var anchorPath = Path.Combine(InstallRoot, "SwSearch.InstallAnchor.dat");
        evidence.ComponentPaths[WindowsInstalledProductCatalog.ProductFileAnchorComponentCode] = anchorPath;
        evidence.Anchors[anchorPath] = new AnchorEvidence(anchorPath, "ANCHOR:39");

        var product = Assert.Single(new WindowsInstalledProductCatalog(evidence)
            .GetRelatedProducts(UpgradeCode, @"D:\Incoming"));

        Assert.Equal(InstallRoot, product.InstallRoot, ignoreCase: true);
        Assert.Equal(0, evidence.MachineRootReads);
    }

    private static FakeInstalledProductEvidence LegacyEvidence()
    {
        var evidence = new FakeInstalledProductEvidence
        {
            MachineInstallDirectory = InstallRoot,
            ResolvedMachineRoot = InstallRoot,
            ProgramFilesRoot = ProgramFiles
        };
        evidence.RelatedProducts.Add(ProductCode);
        evidence.ProductVersions[ProductCode] = "1.2.38";
        evidence.ComponentPaths[WindowsInstalledProductCatalog.BrokerComponentCode] =
            @"Software\SwSearch\Installer\Payload\Broker";
        evidence.ComponentPaths[WindowsInstalledProductCatalog.AppComponentCode] =
            @"Software\SwSearch\Installer\Payload\App";
        var app = Path.Combine(InstallRoot, "SwSearch.App.exe");
        var broker = Path.Combine(InstallRoot, "SwSearch.Broker.exe");
        evidence.Anchors[app] = new AnchorEvidence(app, "APP:OBJECT");
        evidence.Anchors[broker] = new AnchorEvidence(broker, "BROKER:OBJECT");
        evidence.FileVersions[app] = "1.2.38.0";
        evidence.FileVersions[broker] = "1.2.38.0";
        return evidence;
    }

    private sealed class FakeInstalledProductEvidence : IInstalledProductEvidence
    {
        internal List<string> RelatedProducts { get; } = [];
        internal Dictionary<string, string> ProductVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> ComponentPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, AnchorEvidence> Anchors { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> FileVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<(string Path, bool DenyReplacement)> OpenCalls { get; } = [];
        internal string? MachineInstallDirectory { get; set; }
        internal string? UserInstallDirectory { get; set; }
        internal string? ResolvedMachineRoot { get; set; }
        internal string? ProgramFilesRoot { get; set; }
        internal int MachineRootReads { get; private set; }
        internal int UserRootReads { get; private set; }

        public IReadOnlyList<string> EnumerateRelatedProducts(string upgradeCode)
        {
            Assert.Equal(UpgradeCode, upgradeCode, ignoreCase: true);
            return RelatedProducts;
        }

        public string? GetProductVersion(string productCode) =>
            ProductVersions.GetValueOrDefault(productCode);

        public string? GetComponentPath(string productCode, string componentCode) =>
            ComponentPaths.GetValueOrDefault(componentCode);

        public string? ReadMachineInstallDirectory()
        {
            MachineRootReads++;
            return MachineInstallDirectory;
        }

        // Deliberately not part of IInstalledProductEvidence. If production ever
        // starts consulting a user root, this fixture cannot accidentally supply it.
        internal string? ReadUserInstallDirectory()
        {
            UserRootReads++;
            return UserInstallDirectory;
        }

        public string? GetTrustedProgramFilesRoot() => ProgramFilesRoot;

        public string? ResolveDirectory(string path) =>
            string.Equals(path, MachineInstallDirectory, StringComparison.OrdinalIgnoreCase)
                ? ResolvedMachineRoot
                : null;

        public IProductFileAnchor? OpenFileAnchor(string path, bool denyReplacement)
        {
            OpenCalls.Add((path, denyReplacement));
            return Anchors.TryGetValue(path, out var anchor)
                ? new FakeAnchor(anchor.FinalPath, anchor.ObjectId)
                : null;
        }

        public string? GetFileVersion(string path) => FileVersions.GetValueOrDefault(path);
    }

    private sealed record AnchorEvidence(string FinalPath, string ObjectId);

    private sealed class FakeAnchor(string finalPath, string objectId) : IProductFileAnchor
    {
        public string FinalPath { get; } = finalPath;
        public string ObjectId { get; } = objectId;
        public void Dispose() { }
    }
}
