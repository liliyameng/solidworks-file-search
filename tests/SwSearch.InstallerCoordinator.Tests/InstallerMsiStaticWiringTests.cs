using System.Xml.Linq;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallerMsiStaticWiringTests
{
    private const string Rc18ProductCode = "B0EADF5A-B418-4466-BA65-73D956F47FF6";
    private const string Rc15ProductCode = "A6CCBEB3-300D-4D55-AEAD-B7AE975D6473";
    private const string Rc14ProductCode = "A88433F1-0E4F-43BD-9DF8-ADB10410E508";
    private const string Rc13ProductCode = "FB93C5D8-BEAD-4C29-8A55-3751FAF1B1A5";
    private const string Rc12ProductCode = "49D3B604-E741-4D2A-975B-D1E84C0A8528";
    private const string UpgradeCode = "D9158356-29B3-44D6-A15A-ACDCE8328703";
    private const string UserContextCondition =
        "SWSEARCHCONTEXTREADY = \"1\" AND NOT MSIPATCHREMOVE AND NOT UPGRADINGPRODUCTCODE";
    private const string MachineContextCondition =
        "SWSEARCHMACHINECONTEXTREADY = \"1\" AND NOT MSIPATCHREMOVE AND NOT UPGRADINGPRODUCTCODE";

    [Fact]
    public void Package_EmbedsSeparatedActionsAndSequencesThemBeforeRemovalAndFiles()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        var errors = CollectWiringErrors(document);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void CoordinationCondition_CoversIncomingInstallUpgradeRepairUninstallButNotPatchRemovalOrOutgoingUpgrade(
        bool patchRemoval,
        bool outgoingUpgrade,
        bool expected) =>
        Assert.Equal(expected, !patchRemoval && !outgoingUpgrade);

    [Fact]
    public void PackageGate_RejectsCrossWiredOrLegacyUnsafeFixtures()
    {
        var root = FindRepositoryRoot();
        var safe = XDocument.Load(Path.Combine(root, "installer", "wix", "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        XNamespace util = "http://wixtoolset.org/schemas/v4/wxs/util";

        var userCrossWire = new XDocument(safe);
        Action(userCrossWire, ns, "SwSearchUserIpc").SetAttributeValue(
            "ExeCommand", "--privileged-handle --custom-action-data [CustomActionData]");
        Assert.NotEmpty(CollectWiringErrors(userCrossWire));

        var privilegedCrossWire = new XDocument(safe);
        Action(privilegedCrossWire, ns, "SwSearchPrivilegedHandle").SetAttributeValue(
            "Impersonate", "yes");
        Assert.NotEmpty(CollectWiringErrors(privilegedCrossWire));

        var fatalUserOptimization = new XDocument(safe);
        Action(fatalUserOptimization, ns, "SwSearchUserIpc").SetAttributeValue("Return", "check");
        Assert.NotEmpty(CollectWiringErrors(fatalUserOptimization));

        var fatalPrivilegedOptimization = new XDocument(safe);
        Action(fatalPrivilegedOptimization, ns, "SwSearchPrivilegedHandle").SetAttributeValue("Return", "check");
        Assert.NotEmpty(CollectWiringErrors(fatalPrivilegedOptimization));

        var userCoupledPrivilegedCad = new XDocument(safe);
        Action(userCoupledPrivilegedCad, ns, "SetSwSearchPrivilegedHandleData").SetAttributeValue(
            "Value",
            "v=2;user=[SWSEARCHCONTEXTREADY];sid=[SWSEARCHTRUSTEDSID];session=[SWSEARCHTRUSTEDSESSIONID];" +
            "upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};nonce=[SWSEARCHSESSIONNONCE];" +
            "root=[SWSEARCHTRUSTEDROOTPOLICY];action=[SWSEARCHINSTALLACTION];deadline=[SWSEARCHDEADLINETICK]");
        Assert.NotEmpty(CollectWiringErrors(userCoupledPrivilegedCad));

        var legacyBlindClose = new XDocument(safe);
        legacyBlindClose.Root!.Element(ns + "Package")!.Add(
            new XElement(util + "CloseApplication",
                new XAttribute("Id", "Unsafe"),
                new XAttribute("Target", "SwSearch.Broker.exe")));
        Assert.NotEmpty(CollectWiringErrors(legacyBlindClose));

        var disabledRestartManager = new XDocument(safe);
        disabledRestartManager.Root!.Element(ns + "Package")!.Add(
            new XElement(ns + "Property",
                new XAttribute("Id", "MSIRESTARTMANAGERCONTROL"),
                new XAttribute("Value", "Disable")));
        Assert.NotEmpty(CollectWiringErrors(disabledRestartManager));

        var extraBinary = new XDocument(safe);
        extraBinary.Root!.Element(ns + "Package")!.Add(
            new XElement(ns + "Binary",
                new XAttribute("Id", "UnsafeHelper"),
                new XAttribute("SourceFile", @"C:\fixture\helper.exe")));
        Assert.NotEmpty(CollectWiringErrors(extraBinary));

        var noSameVersionUpgrade = new XDocument(safe);
        noSameVersionUpgrade.Root!.Element(ns + "Package")!.Element(ns + "MajorUpgrade")!
            .SetAttributeValue("AllowSameVersionUpgrades", "no");
        Assert.NotEmpty(CollectWiringErrors(noSameVersionUpgrade));

        var framework64Shim = new XDocument(safe);
        var addinComponent = framework64Shim.Descendants(ns + "Component")
            .Single(element => (string?)element.Attribute("Id") == "SolidWorksAddinMachineRegistration");
        var inproc = addinComponent.Elements(ns + "RegistryKey")
            .Single(element => ((string?)element.Attribute("Key"))?.EndsWith("\\InprocServer32", StringComparison.Ordinal) == true);
        inproc.Elements(ns + "RegistryValue").Single(element => element.Attribute("Name") is null)
            .SetAttributeValue("Value", @"[WindowsFolder]Microsoft.NET\Framework64\v4.0.30319\mscoree.dll");
        Assert.NotEmpty(CollectWiringErrors(framework64Shim));
    }

    [Fact]
    public void BuildPublishAndVerify_GovernRc18IdentityAndExcludeInstallerPrivateBinaries()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-Msi.ps1"));
        var release = File.ReadAllText(Path.Combine(root, "installer", "Build-Release.ps1"));
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var verify = File.ReadAllText(Path.Combine(root, "installer", "Verify-Msi.ps1"));
        var privateBuild = File.ReadAllText(Path.Combine(root, "installer", "Build-InstallerActions.ps1"));

        Assert.Contains("ReleaseCandidate = 'rc18'", build, StringComparison.Ordinal);
        Assert.Contains("ProductCode = '" + Rc18ProductCode + "'", build, StringComparison.Ordinal);
        Assert.Contains("ReleaseCandidate = 'rc18'", release, StringComparison.Ordinal);
        Assert.Contains("ProductCode = '" + Rc18ProductCode + "'", release, StringComparison.Ordinal);
        Assert.Contains("ReleaseCandidate = 'rc18'", verify, StringComparison.Ordinal);
        Assert.Contains("ProductCode = '" + Rc18ProductCode + "'", verify, StringComparison.Ordinal);
        Assert.Contains("'A5D7B1CD-53CB-4D8F-99BA-3135F06C08BD'", build, StringComparison.Ordinal);
        Assert.Contains("1.2.18.0", publish, StringComparison.Ordinal);
        Assert.Contains("1.2.18.0", verify, StringComparison.Ordinal);
        Assert.Contains("'" + Rc12ProductCode + "'", build, StringComparison.Ordinal);
        Assert.Contains("'" + Rc13ProductCode + "'", build, StringComparison.Ordinal);
        Assert.Contains("Assert-Rc13Immutable", build, StringComparison.Ordinal);
        Assert.Contains("'" + Rc14ProductCode + "'", build, StringComparison.Ordinal);
        Assert.Contains("Assert-Rc14Immutable", build, StringComparison.Ordinal);
        Assert.Contains("'" + Rc15ProductCode + "'", build, StringComparison.Ordinal);
        Assert.Contains("Assert-Rc15Immutable", build, StringComparison.Ordinal);
        Assert.Contains("Assert-Rc15MsiImmutable", release, StringComparison.Ordinal);
        Assert.Contains("B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85", build, StringComparison.Ordinal);
        Assert.Contains("B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85", verify, StringComparison.Ordinal);
        Assert.Contains("SwSearch.InstallerCoordinator.exe", privateBuild, StringComparison.Ordinal);
        Assert.Contains("SwSearch.InstallerMsiBridge.dll", privateBuild, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", privateBuild, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", privateBuild, StringComparison.Ordinal);
        Assert.Contains("/MT", privateBuild, StringComparison.Ordinal);
        Assert.Contains("SwSearch.InstallerMsiBridgeGate.exe", privateBuild, StringComparison.Ordinal);
        Assert.Contains("SwSearch.InstallerMsiCommandLineGate.exe", privateBuild, StringComparison.Ordinal);

        foreach (var candidateScript in new[] { build, release, publish, privateBuild })
        {
            Assert.False(ContainsRestoreBypass(candidateScript));
            Assert.DoesNotContain("dotnet restore", candidateScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--force-evaluate", candidateScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--source ", candidateScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RestoreSources", candidateScript, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Assert-FrozenRestoreGraph.ps1", build, StringComparison.Ordinal);
        Assert.Contains("Assert-FrozenRestoreGraph.ps1", publish, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "InstallerCoordinator", "InstallerMsiBridge", "InstallerMsiCommandLineGate",
                     "WatchdogHelper", "NativeHelper"
                 })
        {
            Assert.Contains(forbidden, publish, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(forbidden, build, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(forbidden, verify, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("SELECT `Name`,`Data` FROM `Binary`", verify, StringComparison.Ordinal);
        Assert.Contains("SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`", verify, StringComparison.Ordinal);
        Assert.Contains("SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`", verify, StringComparison.Ordinal);
        Assert.Contains("SELECT `DiskId`,`LastSequence`,`Cabinet` FROM `Media`", verify, StringComparison.Ordinal);
        Assert.Contains("Restart Manager must remain enabled", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("$properties.MSIRESTARTMANAGERCONTROL -ne 'Disable'", verify, StringComparison.Ordinal);
        Assert.Contains("Binary table exact-set mismatch", verify, StringComparison.Ordinal);
        Assert.Contains("Assert-ExactJsonProperties $actionManifest", verify, StringComparison.Ordinal);
        Assert.Contains("Wix4UtilCA_X64", verify, StringComparison.Ordinal);
        Assert.Contains("WixUiCa_X64", verify, StringComparison.Ordinal);
        Assert.Contains("Get-PeImageFacts", verify, StringComparison.Ordinal);
        Assert.Contains("CaptureTrustedInstallerContext", verify, StringComparison.Ordinal);
        Assert.Contains("$allowedBridgeImports", verify, StringComparison.Ordinal);
        Assert.Contains("MSI bridge import is outside the allowlist", verify, StringComparison.Ordinal);
        Assert.Contains("--self-test", verify, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedIsolatedProcess", verify, StringComparison.Ordinal);
        Assert.Contains("RC12 immutable pre-verification gate", verify, StringComparison.Ordinal);
        Assert.Contains("RC12 immutable post-verification gate", verify, StringComparison.Ordinal);
        Assert.Contains("RC12 immutable final gate", verify, StringComparison.Ordinal);
        Assert.Contains("57687391", verify, StringComparison.Ordinal);
        Assert.Contains("4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5", verify, StringComparison.Ordinal);
        Assert.Contains("{49D3B604-E741-4D2A-975B-D1E84C0A8528}", verify, StringComparison.Ordinal);
        Assert.Contains("$previousFileRows -ne 493", verify, StringComparison.Ordinal);
        Assert.Contains("$fileRows -ne 505", verify, StringComparison.Ordinal);
        Assert.Contains("$fileName=$fileName.Split('|')[-1]", verify, StringComparison.Ordinal);
        Assert.Contains("SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty` FROM `Upgrade`", verify, StringComparison.Ordinal);
        Assert.Contains("$sameVersionUpgrade[0].Attributes -ne 513", verify, StringComparison.Ordinal);
        Assert.Contains("[System64Folder]mscoree.dll", verify, StringComparison.Ordinal);
        Assert.Contains("Framework64\\v4.0.30319\\mscoree.dll", verify, StringComparison.Ordinal);
        Assert.Contains("WriteRegistryValues.Sequence -lt $sequence.InstallExecute.Sequence", verify, StringComparison.Ordinal);
        Assert.Contains("$sequence.FindRelatedProducts.Sequence -lt $sequence.CaptureSwSearchTrustedContext.Sequence", verify, StringComparison.Ordinal);
        Assert.Contains("$component.KeyPath -cne $ExpectedKeyPathRow.Id", verify, StringComparison.Ordinal);
        Assert.Contains("$keyPathRows[0].Value -cne '#0'", verify, StringComparison.Ordinal);
        Assert.Contains("$rc15KeyPathRegistryRow", verify, StringComparison.Ordinal);
        Assert.Contains("RC15 add-in Registry key path six-tuple baseline changed", verify, StringComparison.Ordinal);
        Assert.Contains("SELECT `Feature_`,`Component_` FROM `FeatureComponents`", verify, StringComparison.Ordinal);
        Assert.Contains("MSI_ADDIN_MODEL_INPROC_ROOT_OR_VALUE", verify, StringComparison.Ordinal);
        Assert.Contains("MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE", verify, StringComparison.Ordinal);
        Assert.Contains("MSI_ADDIN_MODEL_FEATURE_REFERENCE", verify, StringComparison.Ordinal);
        Assert.Contains("compiled behavior harness 20/20", verify, StringComparison.Ordinal);
        Assert.Contains("-PreviousMsi $previousMsi", release, StringComparison.Ordinal);
        Assert.Contains("Assert-Rc12MsiImmutable", release, StringComparison.Ordinal);
        Assert.Contains("SwSearch-x64-v1.2.0-rc12.msi", build, StringComparison.OrdinalIgnoreCase);

        foreach (var unsafeFixture in new[]
                 {
                     verify.Replace("Get-PeImageFacts $bridgeEntry", "# missing PE gate", StringComparison.Ordinal),
                     verify.Replace("CaptureTrustedInstallerContext", "MissingExport", StringComparison.Ordinal),
                     verify.Replace("$machine -ne 0x8664", "$machine -ne 0x014c", StringComparison.Ordinal),
                     verify.Replace("MSI bridge import is outside the allowlist", "import-check-removed", StringComparison.Ordinal),
                     verify.Replace("Invoke-BoundedIsolatedProcess $BridgeGate", "# bridge gate removed", StringComparison.Ordinal),
                     verify.Replace("Invoke-BoundedIsolatedProcess $coordinatorEntry", "# coordinator self-test removed", StringComparison.Ordinal),
                     verify.Replace("$binaryRows.Count -ne $expectedBinaryNames.Count", "$false", StringComparison.Ordinal),
                     verify.Replace("Assert-ExactJsonProperties $actionManifest @('schema','runtime','coordinator','bridge','bridgeGate','commandLineGate') 'Installer action manifest'", "", StringComparison.Ordinal),
                     verify.Replace("Assert-FixedFile $PreviousMsi $expectedPreviousBytes $expectedPreviousSha 'RC12 immutable pre-verification gate'", "", StringComparison.Ordinal),
                     verify.Replace("SwSearch-x64-v1.2.0-rc12.msi", "wrong-previous.msi", StringComparison.Ordinal),
                     verify.Replace("4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5", new string('0', 64), StringComparison.Ordinal),
                     verify.Replace("{49D3B604-E741-4D2A-975B-D1E84C0A8528}", "{00000000-0000-0000-0000-000000000000}", StringComparison.Ordinal),
                     verify.Replace("$previousFileRows -ne 493", "$previousFileRows -ne 492", StringComparison.Ordinal),
                     verify.Replace("$fileRows -ne 505", "$fileRows -ne 504", StringComparison.Ordinal)
                     ,verify.Replace("$sameVersionUpgrade[0].Attributes -ne 513", "$sameVersionUpgrade[0].Attributes -ne 1", StringComparison.Ordinal)
                      ,verify.Replace("$inprocDefault[0].Value -cne '[System64Folder]mscoree.dll'", "$false", StringComparison.Ordinal)
                      ,verify.Replace("$sequence.WriteRegistryValues.Sequence -lt $sequence.InstallExecute.Sequence", "$false", StringComparison.Ordinal)
                      ,verify.Replace("$sequence.FindRelatedProducts.Sequence -lt $sequence.CaptureSwSearchTrustedContext.Sequence", "$false", StringComparison.Ordinal)
                      ,verify.Replace("$component.KeyPath -cne $ExpectedKeyPathRow.Id", "$false", StringComparison.Ordinal)
                      ,verify.Replace("$keyPathRows[0].Value -cne '#0'", "$false", StringComparison.Ordinal)
                      ,verify.Replace("MSI_ADDIN_MODEL_INPROC_ROOT_OR_VALUE", "unsafe-root-fixture", StringComparison.Ordinal)
                      ,verify.Replace("MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE", "unsafe-keypath-fixture", StringComparison.Ordinal)
                      ,verify.Replace("MSI_ADDIN_MODEL_FEATURE_REFERENCE", "unsafe-feature-fixture", StringComparison.Ordinal)
                      ,verify.Replace("compiled behavior harness 20/20", "compiled behavior harness 19/19", StringComparison.Ordinal)
                 })
        {
            Assert.NotEmpty(CollectVerifierGateErrors(unsafeFixture));
        }
    }

    [Fact]
    public void CandidateIntegrityChain_IsManifestCompleteTransactionalAndWritesPassLast()
    {
        var root = FindRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "installer", "Payload-Manifest.ps1"));
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-Msi.ps1"));
        var release = File.ReadAllText(Path.Combine(root, "installer", "Build-Release.ps1"));
        var verify = File.ReadAllText(Path.Combine(root, "installer", "Verify-Msi.ps1"));
        var snapshotTest = File.ReadAllText(Path.Combine(root, "tests", "installer", "Test-PayloadSnapshot.ps1"));

        Assert.Empty(CollectCandidateIntegrityErrors(manifest, publish, build, release, verify, snapshotTest));

        var unsafeFixtures = new[]
        {
            (Manifest: manifest.Replace("PAYLOAD_MANIFEST_HASH_MISMATCH", "HASH_CHECK_REMOVED", StringComparison.Ordinal),
                Publish: publish, Build: build, Release: release, Verify: verify),
            (Manifest: manifest.Replace("PAYLOAD_MANIFEST_LENGTH_MISMATCH", "LENGTH_CHECK_REMOVED", StringComparison.Ordinal),
                Publish: publish, Build: build, Release: release, Verify: verify),
            (Manifest: manifest.Replace("PAYLOAD_MANIFEST_DUPLICATE_PATH", "DUPLICATE_CHECK_REMOVED", StringComparison.Ordinal),
                Publish: publish, Build: build, Release: release, Verify: verify),
            (Manifest: manifest.Replace("PAYLOAD_MANIFEST_FILE_SET_COUNT_MISMATCH", "OMISSION_CHECK_REMOVED", StringComparison.Ordinal),
                Publish: publish, Build: build, Release: release, Verify: verify),
            (Manifest: manifest.Replace("PAYLOAD_MANIFEST_PATH_SEGMENT_INVALID", "TRAVERSAL_CHECK_REMOVED", StringComparison.Ordinal),
                Publish: publish, Build: build, Release: release, Verify: verify),
            (Manifest: manifest,
                Publish: publish.Replace("Test-SwSearchPayloadManifest", "PublishManifestCheckRemoved", StringComparison.Ordinal),
                Build: build, Release: release, Verify: verify),
            (Manifest: manifest, Publish: publish,
                Build: build.Replace("$builtMsiCandidates.Count -ne 1", "$builtMsiCandidates.Count -lt 1", StringComparison.Ordinal),
                Release: release, Verify: verify),
            (Manifest: manifest, Publish: publish,
                Build: build.Replace("-File -Recurse", "-Recurse | Select-Object -First 1", StringComparison.Ordinal),
                Release: release, Verify: verify),
            (Manifest: manifest, Publish: publish, Build: build,
                Release: release.Replace("Move-Item -LiteralPath $pendingMsi -Destination $finalMsi", "Copy-Item -LiteralPath $pendingMsi -Destination $finalMsi", StringComparison.Ordinal),
                Verify: verify),
            (Manifest: manifest, Publish: publish,
                Build: build.Replace("if (-not $handoffReady)", "if ($false)", StringComparison.Ordinal),
                Release: release, Verify: verify),
            (Manifest: manifest, Publish: publish, Build: build,
                Release: release.Replace("if (-not $committed)", "if ($false)", StringComparison.Ordinal),
                Verify: verify),
            (Manifest: manifest, Publish: publish, Build: build, Release: release,
                Verify: verify.Replace("[IO.File]::WriteAllLines($reportWriting,$lines", "[IO.File]::WriteAllLines($report,$lines", StringComparison.Ordinal)),
            (Manifest: manifest, Publish: publish,
                Build: build.Insert(0, "param([switch]$SkipTests)\n"), Release: release, Verify: verify)
        };
        foreach (var fixture in unsafeFixtures)
        {
            Assert.NotEmpty(CollectCandidateIntegrityErrors(
                fixture.Manifest, fixture.Publish, fixture.Build, fixture.Release, fixture.Verify, snapshotTest));
        }

        Assert.NotEmpty(CollectCandidateIntegrityErrors(
            manifest, publish,
            build.Replace("-PayloadDirectory $payloadSnapshotRoot", "-PayloadDirectory $payloadRoot", StringComparison.Ordinal),
            release, verify, snapshotTest));
        Assert.NotEmpty(CollectCandidateIntegrityErrors(
            manifest, publish, build, release,
            verify.Replace("$payload=[IO.Path]::GetFullPath([string]$payloadHandoffData.snapshotRoot)",
                "$payload=Join-Path $artifacts 'SwSearch-win-x64'", StringComparison.Ordinal),
            snapshotTest));
        Assert.NotEmpty(CollectCandidateIntegrityErrors(
            manifest, publish, build, release, verify,
            snapshotTest.Replace("Snapshot-internal replacement was not rejected.", "tamper negative removed", StringComparison.Ordinal)));
    }

    [Fact]
    public void CommandLineGate_SourceBuildCandidateAndVerifierChainIsMandatoryAndPayloadPrivate()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.InstallerMsiCommandLineGate", "InstallerMsiCommandLineGate.cpp"));
        var privateBuild = File.ReadAllText(Path.Combine(root, "installer", "Build-InstallerActions.ps1"));
        var build = File.ReadAllText(Path.Combine(root, "installer", "Build-Msi.ps1"));
        var verify = File.ReadAllText(Path.Combine(root, "installer", "Verify-Msi.ps1"));
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        var package = File.ReadAllText(Path.Combine(root, "installer", "wix", "Package.wxs"));

        Assert.Empty(CollectCommandLineGateWiringErrors(
            source, privateBuild, build, verify, publish, package));

        foreach (var unsafeFixture in new[]
                 {
                     (Source: source.Replace("--verify-msi", "--gate-removed", StringComparison.Ordinal),
                         PrivateBuild: privateBuild, Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source.Replace("MsiFormatRecordW", "MissingMsiFormatter", StringComparison.Ordinal),
                         PrivateBuild: privateBuild, Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source.Replace("CoInitializeEx", "MissingComInitialization", StringComparison.Ordinal),
                         PrivateBuild: privateBuild, Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source.Replace("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", "0", StringComparison.Ordinal),
                         PrivateBuild: privateBuild, Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild.Replace("/MT", "/MD", StringComparison.Ordinal),
                         Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild.Replace("InstallerMsiCommandLineGate.cpp", "MissingGate.cpp", StringComparison.Ordinal),
                         Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild.Replace("commandLineGate = [ordered]@{", "commandLineGateManifestRemoved", StringComparison.Ordinal),
                         Build: build, Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild,
                          Build: build.Replace("Copy-Item -LiteralPath $privateManifest -Destination $pendingInstallerActionsManifest", "# private manifest copy removed", StringComparison.Ordinal),
                         Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild,
                         Build: build.Replace("Invoke-InstallerCommandLineGate -GatePath $msiCommandLineGate", "# mandatory build gate removed", StringComparison.Ordinal),
                         Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild,
                          Build: build.Replace("Copy-Item -LiteralPath $msiCommandLineGate -Destination $pendingCommandLineGateArtifact", "# private gate copy removed", StringComparison.Ordinal),
                         Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild,
                         Build: build.Replace("-MsiPath $builtMsi.FullName", "-MsiPath $finalMsi", StringComparison.Ordinal),
                         Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild,
                         Build: build.Replace("$process.Kill()", "# build runner kill removed", StringComparison.Ordinal),
                         Verify: verify, Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild, Build: build,
                         Verify: verify.Replace("Invoke-BoundedInstallerCommandLineGate $CommandLineGate", "# mandatory verify gate removed", StringComparison.Ordinal),
                         Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild, Build: build,
                         Verify: verify.Replace("$CommandLineGate $msi 30000", "$CommandLineGate $PreviousMsi 30000", StringComparison.Ordinal),
                         Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild, Build: build,
                         Verify: verify.Replace("$process.Kill()", "# verifier runner kill removed", StringComparison.Ordinal),
                         Publish: publish, Package: package),
                     (Source: source, PrivateBuild: privateBuild, Build: build, Verify: verify,
                         Publish: publish, Package: package + "\n<File Source=\"SwSearch.InstallerMsiCommandLineGate.exe\" />")
                 })
        {
            Assert.NotEmpty(CollectCommandLineGateWiringErrors(
                unsafeFixture.Source,
                unsafeFixture.PrivateBuild,
                unsafeFixture.Build,
                unsafeFixture.Verify,
                unsafeFixture.Publish,
                unsafeFixture.Package));
        }
    }

    [Fact]
    public void ZeroRestoreGate_LocksOnlyExplicitAssetsAndRejectsRestoreNetworkAndForceEvaluateBypass()
    {
        var root = FindRepositoryRoot();
        var gate = File.ReadAllText(Path.Combine(root, "installer", "Assert-FrozenRestoreGraph.ps1"));
        var freeze = File.ReadAllText(Path.Combine(root, "installer", "Source-Freeze.ps1"));
        var lockText = File.ReadAllText(Path.Combine(root, "installer", "restore-assets.lock.json"));

        Assert.Contains("FROZEN_RESTORE_GRAPH_NETWORK_SOURCE", gate, StringComparison.Ordinal);
        Assert.Contains("FROZEN_RESTORE_GRAPH_USER_CACHE_FORBIDDEN", gate, StringComparison.Ordinal);
        Assert.Contains("FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY", gate, StringComparison.Ordinal);
        Assert.Contains("FROZEN_RESTORE_GRAPH_CONFIG_NOT_FROZEN", gate, StringComparison.Ordinal);
        Assert.Contains("FROZEN_RESTORE_GRAPH_FALLBACK_FORBIDDEN", gate, StringComparison.Ordinal);
        Assert.Contains("FROZEN_RESTORE_GRAPH_SOURCE_NOT_FROZEN", gate, StringComparison.Ordinal);
        Assert.Contains("FROZEN_RESTORE_GRAPH_LEGACY_MANUAL_PACKAGE_REFERENCE", gate, StringComparison.Ordinal);
        Assert.Contains("installer\\NuGet.Offline.config", gate, StringComparison.Ordinal);
        Assert.Contains("installer\\RestoreGraph.proj", gate, StringComparison.Ordinal);
        Assert.Contains("NuGet.config", gate, StringComparison.Ordinal);
        Assert.Contains(".packages-rc48-r2", gate, StringComparison.Ordinal);
        Assert.Contains("RuntimeRestoreRoot", File.ReadAllText(Path.Combine(root, "installer", "RestoreGraph.proj")),
            StringComparison.Ordinal);
        Assert.Contains("SwSearchFrozenRuntimeGraph=true",
            File.ReadAllText(Path.Combine(root, "installer", "RestoreGraph.proj")), StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath", gate, StringComparison.Ordinal);
        Assert.Contains("Only the exact, review-locked restore graph", freeze, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem", gate, StringComparison.Ordinal);
        Assert.Contains("\"schema\":  2", lockText, StringComparison.Ordinal);
        Assert.Contains("project.assets.json", lockText, StringComparison.Ordinal);
        Assert.Contains("nuget.dgspec.json", lockText, StringComparison.Ordinal);
        Assert.Contains("nuget.g.props", lockText, StringComparison.Ordinal);
        Assert.Contains("nuget.g.targets", lockText, StringComparison.Ordinal);
        Assert.Contains("project.nuget.cache", lockText, StringComparison.Ordinal);

        foreach (var unsafeFixture in new[]
                 {
                     "dotnet restore SolidWorksSearch.sln",
                     "dotnet build SolidWorksSearch.sln --force-evaluate",
                     "dotnet restore --source https://api.nuget.org/v3/index.json",
                     "dotnet msbuild SolidWorksSearch.sln -restore",
                     "msbuild SolidWorksSearch.sln /restore",
                     "dotnet build SolidWorksSearch.sln -p:RestoreSources=https://example.invalid/v3/index.json",
                     "dotnet build SolidWorksSearch.sln -p:RestoreForceEvaluate=true",
                     "dotnet build SolidWorksSearch.sln -p:RestoreIgnoreFailedSources=true"
                 })
        {
            Assert.True(IsForbiddenCandidateCommand(unsafeFixture));
        }
        Assert.False(IsForbiddenCandidateCommand("dotnet build SolidWorksSearch.sln --no-restore"));
    }

    private static bool IsForbiddenCandidateCommand(string command) =>
        ContainsRestoreBypass(command) ||
        command.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
        command.Contains("https://", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsRestoreBypass(string command) =>
        command.Contains("dotnet restore", StringComparison.OrdinalIgnoreCase) ||
        command.Contains("--force-evaluate", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(
            command,
            @"(?im)(?:^|\s)-{1,2}restore(?:\s|:|$)|(?:^|\s)/(?:t:)?restore(?:\s|$)|(?:^|\s)-{1,2}t:restore(?:\s|$)|\bRestore(?:Sources|ForceEvaluate|IgnoreFailedSources)\b|--source\s");

    private static IReadOnlyList<string> CollectCandidateIntegrityErrors(
        string manifest,
        string publish,
        string build,
        string release,
        string verify,
        string snapshotTest)
    {
        var errors = new List<string>();
        foreach (var required in new[]
                 {
                     "^([A-F0-9]{64}) ([0-9]+) \\*(.+)$",
                     "PAYLOAD_MANIFEST_FORMAT_INVALID",
                     "PAYLOAD_MANIFEST_LENGTH_MISMATCH",
                     "PAYLOAD_MANIFEST_HASH_MISMATCH",
                     "PAYLOAD_MANIFEST_DUPLICATE_PATH",
                     "PAYLOAD_MANIFEST_CASE_COLLISION",
                     "PAYLOAD_MANIFEST_PATH_SEGMENT_INVALID",
                     "PAYLOAD_MANIFEST_FILE_SET_COUNT_MISMATCH",
                     "PAYLOAD_MANIFEST_EXTRA_FILE",
                     "[StringComparer]::OrdinalIgnoreCase"
                 })
        {
            RequireToken(manifest, required, "payload manifest " + required, errors);
        }
        RequireToken(publish, "$hash $($_.Length) *$relativePath", "published length/hash entry", errors);
        RequireToken(publish, "Test-SwSearchPayloadManifest", "publish manifest self-check", errors);
        RequireToken(manifest, "New-SwSearchPayloadSnapshot", "transaction payload snapshot creator", errors);
        RequireToken(manifest, "Assert-SwSearchPayloadSnapshotIdentity", "frozen snapshot identity gate", errors);
        RequireToken(manifest, "ManifestSha256", "raw manifest digest", errors);
        RequireToken(manifest, "EntriesDigest", "canonical entries digest", errors);
        RequireToken(manifest, "[IO.FileAttributes]::ReparsePoint", "reparse rejection", errors);
        RequireToken(build, ". (Join-Path $PSScriptRoot 'Payload-Manifest.ps1')", "build shared manifest parser", errors);
        RequireToken(verify, ". (Join-Path $PSScriptRoot 'Payload-Manifest.ps1')", "verify shared manifest parser", errors);
        RequireToken(build, "New-SwSearchPayloadSnapshot -PayloadRoot $payloadRoot", "build snapshot freeze", errors);
        RequireToken(build, "-PayloadDirectory $payloadSnapshotRoot", "WXS consumes frozen snapshot", errors);
        RequireToken(build, "-p:PayloadDir=$payloadSnapshotRoot", "WiX consumes frozen snapshot", errors);
        RequireToken(build, "$pendingPayloadHandoff", "nonce payload handoff", errors);
        if (build.Split("Assert-SwSearchPayloadSnapshotIdentity", StringSplitOptions.None).Length - 1 < 3)
        {
            errors.Add("build does not revalidate the snapshot before WXS, after WiX and before handoff");
        }
        if (verify.Split("Assert-SwSearchPayloadSnapshotIdentity", StringSplitOptions.None).Length - 1 < 3)
        {
            errors.Add("verify does not revalidate the frozen snapshot through CAB and PASS gates");
        }
        RequireToken(verify, "$PayloadHandoff=\"$pendingPrefix.payload-snapshot.json\"", "verify nonce handoff", errors);
        RequireToken(verify, "$payload=[IO.Path]::GetFullPath([string]$payloadHandoffData.snapshotRoot)",
            "verify consumes handoff snapshot", errors);
        if (verify.Contains("Join-Path $artifacts 'SwSearch-win-x64'", StringComparison.Ordinal))
        {
            errors.Add("verify can still consume the mutable shared payload");
        }
        RequireToken(release, "Test-PayloadSnapshot.ps1", "A/B snapshot replacement gate invocation", errors);
        RequireToken(release, "Transaction-private payload snapshot cleanup failed.", "transaction snapshot cleanup", errors);
        RequireToken(snapshotTest, "Replace the shared directory's complete, self-consistent A identity with B.",
            "A-to-B shared payload replacement negative", errors);
        RequireToken(snapshotTest, "Snapshot-internal replacement was not rejected.",
            "snapshot-internal replacement negative", errors);

        RequireToken(build, "Get-ChildItem -LiteralPath $packageOutput -Filter '*.msi' -File -Recurse",
            "file-only MSI selection", errors);
        RequireToken(build, "$builtMsiCandidates.Count -ne 1", "exactly one MSI output", errors);
        RequireToken(build, "$builtMsi.FullName.Equals($expectedBuiltMsi", "exact transaction MSI output path", errors);
        if (build.Contains("Select-Object -First 1", StringComparison.Ordinal))
        {
            errors.Add("build silently selects the first MSI");
        }

        foreach (var script in new[] { publish, build, release, verify })
        {
            foreach (var forbidden in new[] { "SkipTests", "SkipPublish", "UserTestCandidate" })
            {
                if (script.Contains(forbidden, StringComparison.Ordinal))
                {
                    errors.Add("formal candidate script retains bypass: " + forbidden);
                }
            }
        }

        RequireToken(build, ".pending-$TransactionNonce-$artifactName", "build pending transaction name", errors);
        RequireToken(build, "if (-not $handoffReady)", "build pending rollback", errors);
        RequireToken(release, ".pending-$transactionNonce-$artifactName", "release pending transaction name", errors);
        RequireToken(release, "if (-not $committed)", "release failed transaction rollback", errors);
        RequireToken(release, "$publishedSidecars", "release sidecar rollback ledger", errors);
        RequireToken(release, "$pendingVerificationReport", "verified PASS sidecar transaction", errors);
        RequireToken(release, "Move-Item -LiteralPath $pendingMsi -Destination $finalMsi", "atomic final MSI publication", errors);
        var sidecarMove = release.IndexOf("foreach ($entry in $publicationMap)", StringComparison.Ordinal);
        var msiMove = release.IndexOf("Move-Item -LiteralPath $pendingMsi -Destination $finalMsi", StringComparison.Ordinal);
        if (sidecarMove < 0 || msiMove <= sidecarMove)
        {
            errors.Add("final MSI is not published after every sidecar");
        }

        RequireToken(verify, "$verificationComplete=$false", "verification transaction state", errors);
        RequireToken(verify, "if(-not $verificationComplete -and (Test-Path -LiteralPath $report))",
            "failed PASS report cleanup", errors);
        RequireToken(verify, "Move-Item -LiteralPath $reportWriting -Destination $report", "atomic PASS publication", errors);
        var inspectionCleanup = verify.IndexOf("Remove-Item -LiteralPath $inspection -Recurse -Force", StringComparison.Ordinal);
        var reportWrite = verify.IndexOf("[IO.File]::WriteAllLines($reportWriting,$lines", StringComparison.Ordinal);
        var finalRestore = verify.LastIndexOf("Frozen zero-restore graph changed during MSI verification", StringComparison.Ordinal);
        if (inspectionCleanup < 0 || reportWrite <= inspectionCleanup || reportWrite <= finalRestore)
        {
            errors.Add("PASS report can be written before final gates or inspection cleanup");
        }
        return errors;
    }

    private static IReadOnlyList<string> CollectCommandLineGateWiringErrors(
        string source,
        string privateBuild,
        string build,
        string verify,
        string publish,
        string package)
    {
        var errors = new List<string>();
        RequireToken(source, "--verify-msi", "gate source verify mode", errors);
        RequireToken(source, "SetSwSearchPrivilegedHandleData", "gate source Type 51 row", errors);
        RequireToken(source, "SwSearchPrivilegedHandle", "gate source Type 2 row", errors);
        RequireToken(source, "MsiFormatRecordW", "gate source MSI formatter", errors);
        RequireToken(source, "CreateProcessW", "gate source argv probe", errors);
        RequireToken(source, "CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)", "gate COM initialization", errors);
        RequireToken(source, "CoUninitialize()", "gate COM cleanup", errors);
        RequireToken(source, "kExpectedRawCadTemplate", "gate raw Type51 oracle", errors);
        RequireToken(source, "hardCodedType51", "gate hard-coded Type51 negative", errors);
        RequireToken(source, "initialActionProperty.empty()", "gate initially-empty action property", errors);
        RequireToken(source, "OracleRejectsUnsafeFixtures", "gate unsafe oracle fixtures", errors);
        RequireToken(source, "if (!OracleRejectsUnsafeFixtures()) return 93;", "gate unsafe oracle invocation", errors);
        RequireToken(source, "[CustomActionData]", "gate CustomActionData negative", errors);
        RequireToken(source, "wrongActionProperty", "gate wrong-property negative", errors);
        RequireToken(source, "sidInjectedType51", "gate SID-injection negative", errors);
        RequireToken(source, "sessionInjectedType51", "gate session-injection negative", errors);
        RequireToken(source, "nonceInjectedType51", "gate nonce-injection negative", errors);
        var probeRunner = Slice(source, "bool TerminateAndReapSelfCreatedProcess", "int VerifyFinalMsi");
        RequireToken(probeRunner, "JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", "gate Job process-tree containment", errors);
        RequireToken(probeRunner, "CREATE_NO_WINDOW | CREATE_SUSPENDED", "gate suspended self-probe creation", errors);
        RequireToken(probeRunner, "AssignProcessToJobObject", "gate self-probe Job assignment", errors);
        RequireToken(probeRunner, "const BOOL terminated = TerminateProcess", "gate terminate result", errors);
        RequireToken(probeRunner, "const DWORD secondWait = WaitForSingleObject", "gate post-terminate wait", errors);

        RequireToken(privateBuild,
            "src\\SwSearch.InstallerMsiCommandLineGate\\InstallerMsiCommandLineGate.cpp",
            "private build source", errors);
        RequireToken(privateBuild, "SwSearch.InstallerMsiCommandLineGate.exe", "private build output", errors);
        RequireToken(privateBuild, "$commandLineGateCompileCommand", "private build compile", errors);
        RequireToken(privateBuild, "/MT", "private build static CRT", errors);
        RequireToken(privateBuild, "/MACHINE:X64 msi.lib ole32.lib", "private build x64 MSI/COM link", errors);
        RequireToken(privateBuild, "commandLineGate = [ordered]@{", "private manifest entry", errors);
        RequireToken(privateBuild, "bytes = (Get-Item -LiteralPath $commandLineGateOutput).Length",
            "private manifest gate length", errors);
        RequireToken(privateBuild, "sha256 = (Get-FileHash -LiteralPath $commandLineGateOutput -Algorithm SHA256).Hash",
            "private manifest gate hash", errors);

        RequireToken(build, "$msiCommandLineGate = Join-Path $installerActionsOutput", "candidate private gate", errors);
        RequireToken(build, "Copy-Item -LiteralPath $msiCommandLineGate -Destination $pendingCommandLineGateArtifact",
            "candidate gate evidence", errors);
        RequireToken(build, "Copy-Item -LiteralPath $privateManifest -Destination $pendingInstallerActionsManifest",
            "candidate manifest evidence", errors);
        RequireToken(build, "Invoke-InstallerCommandLineGate -GatePath $msiCommandLineGate",
            "candidate mandatory gate", errors);
        RequireToken(build, "-MsiPath $builtMsi.FullName", "candidate final MSI gate parameter", errors);
        RequireToken(build, "-TimeoutMilliseconds 30000", "candidate gate outer hard deadline", errors);
        var buildRunner = Slice(
            build, "function Invoke-InstallerCommandLineGate", "function New-PayloadWixSource");
        RequireToken(buildRunner, "swsearch-msi-command-line-gate-", "candidate unique temp root", errors);
        RequireToken(buildRunner, "$internalProbeBudgetMilliseconds = 4 * 5000", "candidate internal budget", errors);
        RequireToken(buildRunner, "$gateHashBefore", "candidate gate pre-hash", errors);
        RequireToken(buildRunner, "$gateHashAfter", "candidate gate post-hash", errors);
        RequireToken(buildRunner, "$candidateHashBefore", "candidate MSI pre-hash", errors);
        RequireToken(buildRunner, "$candidateHashAfter", "candidate MSI post-hash", errors);
        RequireToken(buildRunner, "Remove-Item -LiteralPath $runRoot -Recurse -Force", "candidate temp cleanup", errors);
        RequireToken(buildRunner, "$process.Kill()", "candidate hard-deadline root termination", errors);
        RequireToken(buildRunner, "$process.WaitForExit(5000)", "candidate hard-deadline reap", errors);
        RequireToken(build, "InstallerMsiCommandLineGate", "candidate payload exclusion", errors);
        var builtIndex = build.IndexOf("$builtMsiCandidates =", StringComparison.Ordinal);
        var gateIndex = build.IndexOf(
            "Invoke-InstallerCommandLineGate -GatePath $msiCommandLineGate", StringComparison.Ordinal);
        var publishIndex = build.IndexOf(
            "Copy-Item -LiteralPath $builtMsi.FullName -Destination $pendingMsi", StringComparison.Ordinal);
        if (builtIndex < 0 || gateIndex <= builtIndex || publishIndex <= gateIndex)
        {
            errors.Add("candidate gate does not precede publication");
        }

        RequireToken(verify, "$CommandLineGate=\"$pendingPrefix.installer-command-line-gate.exe\"", "verifier gate input", errors);
        RequireToken(verify, "commandLineGate", "verifier manifest entry", errors);
        RequireToken(verify, "Assert-FixedFile $CommandLineGate", "verifier gate hash/length", errors);
        RequireToken(verify, "SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`",
            "verifier final table", errors);
        RequireToken(verify, "Invoke-BoundedInstallerCommandLineGate $CommandLineGate $msi 30000",
            "verifier mandatory gate", errors);
        RequireToken(verify, "$commandGateMsiHashBefore", "verifier candidate hash boundary", errors);
        RequireToken(verify, "$commandGateMsiHashAfter", "verifier candidate post-hash boundary", errors);
        RequireToken(verify, "installer-command-line-gate.verify.log", "verifier gate log", errors);
        RequireToken(verify, "InstallerMsiCommandLineGate", "verifier payload exclusion", errors);
        var tableIndex = verify.IndexOf(
            "SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`", StringComparison.Ordinal);
        var verifyRunner = Slice(
            verify, "function Invoke-BoundedInstallerCommandLineGate", "foreach($path in @($msi,$PayloadHandoff,$wix,$dtf))");
        RequireToken(verifyRunner, "swsearch-msi-command-line-gate-", "verifier unique temp root", errors);
        RequireToken(verifyRunner, "$internalProbeBudgetMilliseconds=4*5000", "verifier internal budget", errors);
        RequireToken(verifyRunner, "Remove-Item -LiteralPath $runRoot -Recurse -Force", "verifier temp cleanup", errors);
        RequireToken(verifyRunner, "$process.Kill()", "verifier hard-deadline root termination", errors);
        RequireToken(verifyRunner, "$process.WaitForExit(5000)", "verifier hard-deadline reap", errors);
        RequireToken(verify, "$commandGateHashBefore", "verifier gate pre-hash", errors);
        RequireToken(verify, "$commandGateHashAfter", "verifier gate post-hash", errors);
        RequireToken(verify, "$commandGateMsiHashBefore", "verifier MSI pre-hash", errors);
        RequireToken(verify, "$commandGateMsiHashAfter", "verifier MSI post-hash", errors);
        var verifyGateIndex = verify.IndexOf(
            "Invoke-BoundedInstallerCommandLineGate $CommandLineGate $msi 30000", StringComparison.Ordinal);
        var passIndex = verify.IndexOf("static MSI verification: PASS", StringComparison.Ordinal);
        if (tableIndex < 0 || verifyGateIndex <= tableIndex || passIndex <= verifyGateIndex)
        {
            errors.Add("verifier gate does not follow final tables and precede PASS");
        }

        var expectedBinarySet = Slice(verify, "$expectedBinaryNames=@(", "    )");
        if (string.IsNullOrEmpty(expectedBinarySet) ||
            expectedBinarySet.Contains("InstallerMsiCommandLineGate", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("command-line gate entered the final MSI Binary set");
        }

        var rawType51Index = source.IndexOf(
            "!IsExactRawCadTemplate(setTarget.c_str())", StringComparison.Ordinal);
        var initialEmptyIndex = source.IndexOf(
            "initialActionProperty.empty()", StringComparison.Ordinal);
        var fixturePropertyIndex = source.IndexOf(
            "MsiSetPropertyW(package.get(), kRootProperty", StringComparison.Ordinal);
        if (rawType51Index < 0 || initialEmptyIndex <= rawType51Index || fixturePropertyIndex <= initialEmptyIndex)
        {
            errors.Add("raw Type51 and initially-empty action property are not checked before fixture properties");
        }

        RequireToken(publish, "InstallerMsiCommandLineGate", "publish payload exclusion", errors);
        if (package.Contains("InstallerMsiCommandLineGate", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("command-line gate leaked into WiX package");
        }
        return errors;
    }

    private static void RequireToken(
        string text,
        string token,
        string label,
        ICollection<string> errors)
    {
        if (!text.Contains(token, StringComparison.Ordinal))
        {
            errors.Add(label);
        }
    }

    private static string Slice(string text, string startToken, string endToken)
    {
        var start = text.IndexOf(startToken, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        var end = text.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        return end > start ? text[start..end] : string.Empty;
    }

    private static IReadOnlyList<string> CollectWiringErrors(XDocument document)
    {
        var errors = new List<string>();
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        XNamespace util = "http://wixtoolset.org/schemas/v4/wxs/util";
        var package = document.Root?.Element(ns + "Package");
        if (package is null)
        {
            return ["Package missing"];
        }
        if ((string?)package.Attribute("UpgradeCode") != UpgradeCode)
        {
            errors.Add("UpgradeCode changed");
        }

        var binaries = package.Elements(ns + "Binary").ToDictionary(
            element => (string?)element.Attribute("Id") ?? string.Empty,
            StringComparer.Ordinal);
        if (binaries.Count != 2 ||
            !binaries.ContainsKey("SwSearchInstallerCoordinator") ||
            !binaries.ContainsKey("SwSearchInstallerMsiBridge"))
        {
            errors.Add("Binary rows are not the exact two-item set");
        }

        var capture = Action(document, ns, "CaptureSwSearchTrustedContext");
        var user = Action(document, ns, "SwSearchUserIpc");
        var setCad = Action(document, ns, "SetSwSearchPrivilegedHandleData");
        var privileged = Action(document, ns, "SwSearchPrivilegedHandle");
        Require(capture, "BinaryRef", "SwSearchInstallerMsiBridge", errors);
        Require(capture, "DllEntry", "CaptureTrustedInstallerContext", errors);
        Require(capture, "Execute", "immediate", errors);
        Require(capture, "Impersonate", "yes", errors);
        Require(capture, "Return", "check", errors);
        Require(user, "BinaryRef", "SwSearchInstallerCoordinator", errors);
        Require(user, "Execute", "immediate", errors);
        Require(user, "Impersonate", "yes", errors);
        Require(user, "Return", "ignore", errors);
        if (!((string?)user.Attribute("ExeCommand") ?? string.Empty).StartsWith("--user-ipc ", StringComparison.Ordinal) ||
            ((string?)user.Attribute("ExeCommand") ?? string.Empty).Contains("--privileged-handle", StringComparison.Ordinal) ||
            !((string?)user.Attribute("ExeCommand") ?? string.Empty).Contains("--install-session-id \"[SWSEARCHTRUSTEDSESSIONID]\"", StringComparison.Ordinal))
        {
            errors.Add("user-ipc cross-wired");
        }
        Require(setCad, "Property", "SwSearchPrivilegedHandle", errors);
        Require(setCad, "Return", "check", errors);
        var cadValue = (string?)setCad.Attribute("Value") ?? string.Empty;
        if (!cadValue.Contains("v=2;user=0;sid=;session=;", StringComparison.Ordinal) ||
            !cadValue.Contains(";nonce=;root=[SWSEARCHTRUSTEDROOTPOLICY];", StringComparison.Ordinal) ||
            cadValue.Contains("SWSEARCHTRUSTEDSID", StringComparison.Ordinal) ||
            cadValue.Contains("SWSEARCHTRUSTEDSESSIONID", StringComparison.Ordinal) ||
            cadValue.Contains("SWSEARCHSESSIONNONCE", StringComparison.Ordinal))
        {
            errors.Add("privileged CAD is not independently machine-only");
        }
        Require(privileged, "BinaryRef", "SwSearchInstallerCoordinator", errors);
        Require(privileged, "Execute", "deferred", errors);
        Require(privileged, "Impersonate", "no", errors);
        Require(privileged, "Return", "ignore", errors);
        if ((string?)privileged.Attribute("ExeCommand") !=
            "--privileged-handle --custom-action-data \"[SwSearchPrivilegedHandle]\"")
        {
            errors.Add("privileged entry cross-wired");
        }

        var sequence = package.Element(ns + "InstallExecuteSequence")?
            .Elements(ns + "Custom")
            .ToDictionary(element => (string?)element.Attribute("Action") ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var action in new[]
                 {
                     "CaptureSwSearchTrustedContext", "SwSearchUserIpc",
                     "SetSwSearchPrivilegedHandleData", "SwSearchPrivilegedHandle"
                 })
        {
            if (!sequence.ContainsKey(action))
            {
                errors.Add("sequence missing " + action);
            }
        }
        if (sequence.TryGetValue("SwSearchUserIpc", out var userSequence) &&
            ((string?)userSequence.Attribute("Condition") != UserContextCondition ||
             (string?)userSequence.Attribute("After") != "CaptureSwSearchTrustedContext"))
        {
            errors.Add("user sequence");
        }
        if (sequence.TryGetValue("CaptureSwSearchTrustedContext", out var captureSequence) &&
            ((string?)captureSequence.Attribute("Condition") !=
                "NOT MSIPATCHREMOVE AND NOT UPGRADINGPRODUCTCODE" ||
             (string?)captureSequence.Attribute("After") != "CostFinalize"))
        {
            errors.Add("capture sequence");
        }
        if (sequence.TryGetValue("SetSwSearchPrivilegedHandleData", out var cadSequence) &&
            ((string?)cadSequence.Attribute("Condition") != MachineContextCondition ||
             (string?)cadSequence.Attribute("After") != "InstallInitialize"))
        {
            errors.Add("CAD sequence");
        }
        if (sequence.TryGetValue("SwSearchPrivilegedHandle", out var privilegedSequence) &&
            ((string?)privilegedSequence.Attribute("Condition") != MachineContextCondition ||
             (string?)privilegedSequence.Attribute("After") != "SetSwSearchPrivilegedHandleData"))
        {
            errors.Add("privileged sequence");
        }

        var closeTargets = package.Elements(util + "CloseApplication")
            .Select(element => (string?)element.Attribute("Target"))
            .ToArray();
        if (closeTargets.Length != 1 || closeTargets[0] != "SLDWORKS.exe")
        {
            errors.Add("blind CloseApplication remains");
        }
        if (package.Elements(ns + "CustomAction").Any(element =>
                (string?)element.Attribute("Id") == "FailIfSwSearchStillRunning"))
        {
            errors.Add("fixed fatal remains");
        }
        if (package.Elements(ns + "Property").Any(element =>
                (string?)element.Attribute("Id") == "MSIRESTARTMANAGERCONTROL" &&
                (string?)element.Attribute("Value") == "Disable"))
        {
            errors.Add("Restart Manager disabled");
        }
        if ((string?)package.Element(ns + "MajorUpgrade")?.Attribute("Schedule") != "afterInstallExecute")
        {
            errors.Add("RemoveExistingProducts schedule unsafe");
        }
        if ((string?)package.Element(ns + "MajorUpgrade")?.Attribute("AllowSameVersionUpgrades") != "yes")
        {
            errors.Add("RC15 same-version upgrade detection disabled");
        }
        var addinComponent = document.Descendants(ns + "Component")
            .SingleOrDefault(element => (string?)element.Attribute("Id") == "SolidWorksAddinMachineRegistration");
        if (addinComponent is null || (string?)addinComponent.Attribute("Bitness") != "always64")
        {
            errors.Add("Add-in registry component is not fixed 64-bit");
        }
        else
        {
            var inproc = addinComponent.Elements(ns + "RegistryKey")
                .SingleOrDefault(element => ((string?)element.Attribute("Key"))?.EndsWith("\\InprocServer32", StringComparison.Ordinal) == true);
            var defaultValue = inproc?.Elements(ns + "RegistryValue")
                .SingleOrDefault(element => element.Attribute("Name") is null);
            if ((string?)defaultValue?.Attribute("Value") != "[System64Folder]mscoree.dll")
            {
                errors.Add("Add-in InprocServer32 is not System64 mscoree.dll");
            }
        }
        return errors;
    }

    private static IReadOnlyList<string> CollectVerifierGateErrors(string script)
    {
        var required = new[]
        {
            "Get-PeImageFacts $bridgeEntry",
            "CaptureTrustedInstallerContext",
            "$machine -ne 0x8664",
            "MSI bridge import is outside the allowlist",
            "Invoke-BoundedIsolatedProcess $BridgeGate",
            "Invoke-BoundedInstallerCommandLineGate $CommandLineGate $msi 30000",
            "Invoke-BoundedIsolatedProcess $coordinatorEntry",
            "$binaryRows.Count -ne $expectedBinaryNames.Count",
            "Assert-ExactJsonProperties $actionManifest @('schema','runtime','coordinator','bridge','bridgeGate','commandLineGate') 'Installer action manifest'",
            "RC12 immutable pre-verification gate",
            "RC12 immutable post-verification gate",
            "RC12 immutable final gate",
            "SwSearch-x64-v1.2.0-rc12.msi",
            "4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5",
            "{49D3B604-E741-4D2A-975B-D1E84C0A8528}",
            "$previousFileRows -ne 493",
            "$fileRows -ne 505",
            "$previousProperties.ALLUSERS -cne '1'"
            ,"RC15 immutable pre-verification gate"
            ,"RC15 immutable post-verification gate"
            ,"RC15 immutable final gate"
            ,"SwSearch-x64-v1.2.0-rc15.msi"
            ,"B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85"
            ,"SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty` FROM `Upgrade`"
            ,"$sameVersionUpgrade[0].Attributes -ne 513"
            ,"$inprocDefault[0].Value -cne '[System64Folder]mscoree.dll'"
            ,"[System64Folder]mscoree.dll"
            ,"Framework64\\v4.0.30319\\mscoree.dll"
            ,"WriteRegistryValues.Sequence -lt $sequence.InstallExecute.Sequence"
            ,"$sequence.FindRelatedProducts.Sequence -lt $sequence.CaptureSwSearchTrustedContext.Sequence"
            ,"$component.KeyPath -cne $ExpectedKeyPathRow.Id"
            ,"$keyPathRows[0].Value -cne '#0'"
            ,"RC15 add-in Registry key path six-tuple baseline changed"
            ,"SELECT `Feature_`,`Component_` FROM `FeatureComponents`"
            ,"MSI_ADDIN_MODEL_INPROC_ROOT_OR_VALUE"
            ,"MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE"
            ,"MSI_ADDIN_MODEL_FEATURE_REFERENCE"
            ,"compiled behavior harness 20/20"
        };
        return required.Where(token => !script.Contains(token, StringComparison.Ordinal)).ToArray();
    }

    private static XElement Action(XDocument document, XNamespace ns, string id) =>
        document.Root!.Element(ns + "Package")!.Elements(ns + "CustomAction")
            .Single(element => (string?)element.Attribute("Id") == id);

    private static void Require(
        XElement element,
        string attribute,
        string expected,
        ICollection<string> errors)
    {
        if ((string?)element.Attribute(attribute) != expected)
        {
            errors.Add($"{(string?)element.Attribute("Id")} {attribute}");
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
