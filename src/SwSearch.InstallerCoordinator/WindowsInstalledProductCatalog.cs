using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SwSearch.InstallerCoordinator;

internal interface IInstalledProductEvidence
{
    IReadOnlyList<string> EnumerateRelatedProducts(string upgradeCode);
    string? GetProductVersion(string productCode);
    string? GetComponentPath(string productCode, string componentCode);
    string? ReadMachineInstallDirectory();
    string? GetTrustedProgramFilesRoot();
    string? ResolveDirectory(string path);
    IProductFileAnchor? OpenFileAnchor(string path, bool denyReplacement);
    string? GetFileVersion(string path);
}

internal sealed class WindowsInstalledProductCatalog : IInstalledProductCatalog
{
    internal const string BrokerComponentCode = "{878F2264-C2CF-97C2-6E7A-6A0D06BBDE1D}";
    internal const string AppComponentCode = "{F09390D3-6388-83F5-817A-A57223873C84}";
    internal const string ProductFileAnchorComponentCode = "{6D2AD688-FFB4-4B79-99A7-AB07DE8291D6}";
    private readonly IInstalledProductEvidence evidence;

    internal WindowsInstalledProductCatalog()
        : this(new WindowsInstalledProductEvidence())
    {
    }

    internal WindowsInstalledProductCatalog(IInstalledProductEvidence evidence)
    {
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(
        string upgradeCode,
        string currentInstallRoot)
    {
        if (!Guid.TryParse(upgradeCode, out var parsedUpgradeCode))
        {
            throw new ArgumentException("UpgradeCode is invalid.", nameof(upgradeCode));
        }

        // currentInstallRoot is deliberately not an authorization source. It is
        // supplied by the incoming MSI and cannot establish an older product's root.
        _ = currentInstallRoot;
        var products = new List<InstalledProductIdentity>();
        var programFiles = evidence.GetTrustedProgramFilesRoot();
        if (programFiles is null)
        {
            return products;
        }

        foreach (var code in evidence.EnumerateRelatedProducts(
                     parsedUpgradeCode.ToString("B").ToUpperInvariant()))
        {
            var version = evidence.GetProductVersion(code);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            var root = GetRootFromRegisteredComponent(code);
            if (root is null)
            {
                // RC38 and older used an HKCU RegistryValue as the generated
                // component KeyPath. MsiGetComponentPathEx therefore cannot yield
                // an executable path. The compatibility fallback trusts only the
                // machine-wide root written by the installed MSI and two anchored,
                // version-matched product files beneath its resolved final root.
                root = GetRootFromLegacyMachineRegistration(version, programFiles);
            }

            if (FinalPathTrustPolicy.IsTrustedRegisteredRoot(root, programFiles))
            {
                products.Add(new InstalledProductIdentity(code, version, root!, IsTrustedRegisteredRoot: true));
            }
        }

        return products;
    }

    private string? GetRootFromRegisteredComponent(string productCode)
    {
        foreach (var componentCode in new[]
                 {
                     ProductFileAnchorComponentCode,
                     BrokerComponentCode,
                     AppComponentCode
                 })
        {
            var path = evidence.GetComponentPath(productCode, componentCode);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            using var file = evidence.OpenFileAnchor(path, denyReplacement: true);
            if (file is not null)
            {
                return Path.GetDirectoryName(file.FinalPath);
            }
        }

        return null;
    }

    private string? GetRootFromLegacyMachineRegistration(string productVersion, string programFiles)
    {
        try
        {
            var reportedRoot = evidence.ReadMachineInstallDirectory();
            if (string.IsNullOrWhiteSpace(reportedRoot))
            {
                return null;
            }

            var finalRoot = evidence.ResolveDirectory(reportedRoot);
            if (!FinalPathTrustPolicy.IsTrustedRegisteredRoot(finalRoot, programFiles))
            {
                return null;
            }

            var appPath = Path.Combine(reportedRoot, "SwSearch.App.exe");
            var brokerPath = Path.Combine(reportedRoot, "SwSearch.Broker.exe");
            using var app = evidence.OpenFileAnchor(appPath, denyReplacement: true);
            using var broker = evidence.OpenFileAnchor(brokerPath, denyReplacement: true);
            if (app is null || broker is null ||
                string.IsNullOrWhiteSpace(app.ObjectId) ||
                string.IsNullOrWhiteSpace(broker.ObjectId) ||
                string.Equals(app.ObjectId, broker.ObjectId, StringComparison.Ordinal) ||
                !IsDirectChild(app.FinalPath, finalRoot!) ||
                !IsDirectChild(broker.FinalPath, finalRoot!) ||
                !VersionsMatch(productVersion, evidence.GetFileVersion(app.FinalPath)) ||
                !VersionsMatch(productVersion, evidence.GetFileVersion(broker.FinalPath)))
            {
                return null;
            }

            return finalRoot;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsDirectChild(string finalPath, string finalRoot) =>
        WindowsFinalPath.IsProvenLongFinalPath(finalPath) &&
        string.Equals(Path.GetDirectoryName(finalPath), finalRoot, StringComparison.OrdinalIgnoreCase);

    private static bool VersionsMatch(string productVersion, string? fileVersion)
    {
        if (!Version.TryParse(productVersion, out var product) ||
            !Version.TryParse(fileVersion, out var file))
        {
            return false;
        }

        return product.Major == file.Major &&
               product.Minor == file.Minor &&
               product.Build == file.Build &&
               (product.Revision < 0 || product.Revision == file.Revision);
    }
}

internal sealed class WindowsInstalledProductEvidence : IInstalledProductEvidence
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;
    private const uint MsiInstallContextMachine = 4;

    public IReadOnlyList<string> EnumerateRelatedProducts(string upgradeCode)
    {
        var products = new List<string>();
        for (uint index = 0; ; index++)
        {
            var productCode = new StringBuilder(39);
            var result = MsiEnumRelatedProductsW(upgradeCode, 0, index, productCode);
            if (result == ErrorNoMoreItems)
            {
                break;
            }

            if (result != ErrorSuccess)
            {
                throw new InvalidOperationException($"MsiEnumRelatedProducts failed: {result}.");
            }

            products.Add(productCode.ToString());
        }

        return products;
    }

    public string? GetProductVersion(string productCode) =>
        GetProductProperty(productCode, "VersionString");

    public string? GetComponentPath(string productCode, string componentCode)
    {
        var capacity = 32_767u;
        var buffer = new StringBuilder((int)capacity + 1);
        var state = MsiGetComponentPathExW(
            productCode, componentCode, null, MsiInstallContextMachine, buffer, ref capacity);
        return state < 0 || buffer.Length == 0 ? null : buffer.ToString();
    }

    public string? ReadMachineInstallDirectory()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\SwSearch\Installer", writable: false);
        return key?.GetValue("InstallDirectory", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public string? GetTrustedProgramFilesRoot() => WindowsFinalPath.GetTrustedProgramFilesRoot();

    public string? ResolveDirectory(string path) => WindowsFinalPath.TryResolveDirectory(path);

    public IProductFileAnchor? OpenFileAnchor(string path, bool denyReplacement) =>
        WindowsFinalPath.TryOpenFileAnchor(path, denyReplacement);

    public string? GetFileVersion(string path) => FileVersionInfo.GetVersionInfo(path).FileVersion;

    private static string? GetProductProperty(string productCode, string propertyName)
    {
        var capacity = 32_767u;
        var buffer = new StringBuilder((int)capacity + 1);
        var result = MsiGetProductInfoExW(
            productCode, null, MsiInstallContextMachine, propertyName, buffer, ref capacity);
        return result == ErrorSuccess && buffer.Length > 0 ? buffer.ToString() : null;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProductsW(
        string upgradeCode,
        uint reserved,
        uint productIndex,
        StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(
        string productCode,
        string? userSid,
        uint context,
        string property,
        StringBuilder value,
        ref uint valueLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MsiGetComponentPathExW(
        string productCode,
        string componentCode,
        string? userSid,
        uint context,
        StringBuilder path,
        ref uint pathLength);
}
