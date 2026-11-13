using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallerBinaryEntryStaticTests
{
    [Fact]
    public void IsolatedBridgeGateHashesBeforeLoadResolvesExactExportAndCallsOnlyInvalidMsiHandle()
    {
        var root = FindRepositoryRoot();
        var gate = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.InstallerMsiBridgeGate", "InstallerMsiBridgeGate.cpp"));

        Assert.Empty(GateErrors(gate));
        foreach (var unsafeFixture in new[]
                 {
                     gate.Replace("ComputeSha256(argv[1], actualHash)", "true", StringComparison.Ordinal),
                     gate.Replace("CaptureTrustedInstallerContext", "WrongExport", StringComparison.Ordinal),
                     gate.Replace("entry(0)", "entry(1)", StringComparison.Ordinal),
                     gate.Replace("LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32", "0", StringComparison.Ordinal),
                     gate.Replace("argc != 3", "argc < 2", StringComparison.Ordinal)
                 })
        {
            Assert.NotEmpty(GateErrors(unsafeFixture));
        }
    }

    [Fact]
    public void VerifyUsesOnlyMsiExtractedEntriesAndRejectsWorkspaceCopyShortcut()
    {
        var verify = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Verify-Msi.ps1"));
        Assert.True(UsesExtractedBinaryEntries(verify));
        Assert.Contains("$stream.CopyTo($outputStream)", verify, StringComparison.Ordinal);
        Assert.Contains("$binaryRows[$name]=[pscustomobject]", verify, StringComparison.Ordinal);
        Assert.Contains("$bridgeEntry=[string]$binaryRows.SwSearchInstallerMsiBridge.Path", verify, StringComparison.Ordinal);
        Assert.Contains("$coordinatorEntry=[string]$binaryRows.SwSearchInstallerCoordinator.Path", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("src\\SwSearch.InstallerMsiBridge", verify, StringComparison.OrdinalIgnoreCase);

        var unsafeWorkspaceCopy = verify.Replace(
            "$bridgeEntry=[string]$binaryRows.SwSearchInstallerMsiBridge.Path",
            "$bridgeEntry=Join-Path $root 'src\\SwSearch.InstallerMsiBridge\\bridge.dll'",
            StringComparison.Ordinal);
        Assert.False(UsesExtractedBinaryEntries(unsafeWorkspaceCopy));
    }

    private static IReadOnlyList<string> GateErrors(string source)
    {
        var required = new[]
        {
            "argc != 3",
            "ComputeSha256(argv[1], actualHash)",
            "_wcsicmp(actualHash.c_str(), argv[2]) != 0",
            "LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32",
            "GetProcAddress(module, \"CaptureTrustedInstallerContext\")",
            "entry(0)",
            "ERROR_INSTALL_FAILURE",
            "FreeLibrary(module)"
        };
        return required.Where(token => !source.Contains(token, StringComparison.Ordinal)).ToArray();
    }

    private static bool UsesExtractedBinaryEntries(string source) =>
        source.Contains("$stream.CopyTo($outputStream)", StringComparison.Ordinal) &&
        source.Contains("$bridgeEntry=[string]$binaryRows.SwSearchInstallerMsiBridge.Path", StringComparison.Ordinal) &&
        source.Contains("$coordinatorEntry=[string]$binaryRows.SwSearchInstallerCoordinator.Path", StringComparison.Ordinal);

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
