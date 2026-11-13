using System.Text.Json;
using SwSearch.Broker;
using SwSearch.Indexer;

namespace SwSearch.Integration.Tests;

public sealed class Rc69EverythingSelectionTests
{
    [Fact]
    public async Task Automatic_DefaultsToEverythingFirstWithoutManualConfiguration()
    {
        // A fresh install, a missing discoveryPreference field, and the
        // persisted automatic value all resolve to this path.  Do not require
        // an operator to choose Everything in a UI or edit settings.json.
        var path = CreateSettingsFile("""
            {
              "networkRoots": []
            }
            """);
        try
        {
            var settings = BrokerSettings.Load(path);
            Assert.Equal(DiscoveryPreference.Automatic, settings.DiscoveryPreference);

            var provider = settings.CreateDiscovery();
            await using var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(provider);
            Assert.IsType<FallbackDiscoveryProvider>(provider);

            using var document = JsonDocument.Parse(BrokerDiscoveryDiagnostics.FormatSelection(settings, provider));
            var entry = document.RootElement;
            Assert.Equal("Automatic", entry.GetProperty("configuredPreference").GetString());
            Assert.Equal("Everything -> NTFS/built-in coverage", entry.GetProperty("selectionOrder").GetString());
            Assert.True(entry.GetProperty("everythingAttemptPlanned").GetBoolean());
            Assert.True(entry.GetProperty("builtInFallbackAvailable").GetBoolean());
        }
        finally
        {
            DeleteSettingsFile(path);
        }
    }

    [Fact]
    public async Task EverythingPreferred_IsSelectedExplicitlyWithBuiltInFallbackPlan()
    {
        var path = CreateSettingsFile("""
            {
              "discoveryPreference": "EverythingPreferred",
              "networkRoots": []
            }
            """);
        try
        {
            var settings = BrokerSettings.Load(path);
            Assert.Equal(DiscoveryPreference.EverythingPreferred, settings.DiscoveryPreference);

            var provider = settings.CreateDiscovery();
            await using var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(provider);
            Assert.IsType<FallbackDiscoveryProvider>(provider);

            using var document = JsonDocument.Parse(BrokerDiscoveryDiagnostics.FormatSelection(settings, provider));
            var entry = document.RootElement;
            Assert.Equal("discovery-provider-selection", entry.GetProperty("eventType").GetString());
            Assert.Equal("EverythingPreferred", entry.GetProperty("configuredPreference").GetString());
            Assert.Equal("FallbackDiscoveryProvider", entry.GetProperty("selectedProvider").GetString());
            Assert.True(entry.GetProperty("everythingAttemptPlanned").GetBoolean());
            Assert.True(entry.GetProperty("builtInDiscoveryEnabled").GetBoolean());
            Assert.True(entry.GetProperty("builtInFallbackAvailable").GetBoolean());
        }
        finally
        {
            DeleteSettingsFile(path);
        }
    }

    [Fact]
    public async Task BuiltInOnly_DisablesEverythingAttemptWithoutRemovingDiscovery()
    {
        var path = CreateSettingsFile("""
            {
              "discoveryPreference": 2,
              "networkRoots": []
            }
            """);
        try
        {
            var settings = BrokerSettings.Load(path);
            Assert.Equal(DiscoveryPreference.BuiltInOnly, settings.DiscoveryPreference);

            var provider = settings.CreateDiscovery();
            await using var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(provider);
            Assert.IsType<BuiltInFileDiscoveryProvider>(provider);

            using var document = JsonDocument.Parse(BrokerDiscoveryDiagnostics.FormatSelection(settings, provider));
            var entry = document.RootElement;
            Assert.Equal("BuiltInOnly", entry.GetProperty("configuredPreference").GetString());
            Assert.False(entry.GetProperty("everythingAttemptPlanned").GetBoolean());
            Assert.True(entry.GetProperty("builtInDiscoveryEnabled").GetBoolean());
            Assert.False(entry.GetProperty("builtInFallbackAvailable").GetBoolean());
        }
        finally
        {
            DeleteSettingsFile(path);
        }
    }

    [Fact]
    public void InvalidDiscoveryPreference_FailsClosedToAutomatic()
    {
        var path = CreateSettingsFile("""
            {
              "discoveryPreference": "UnsupportedProvider",
              "networkRoots": []
            }
            """);
        try
        {
            Assert.Equal(DiscoveryPreference.Automatic, BrokerSettings.Load(path).DiscoveryPreference);
        }
        finally
        {
            DeleteSettingsFile(path);
        }
    }

    private static string CreateSettingsFile(string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), "swsearch-rc69-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, contents);
        return path;
    }

    private static void DeleteSettingsFile(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
