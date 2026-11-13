using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.Broker;

/// <summary>
/// Produces one bounded, machine-readable startup record explaining which
/// discovery policy was selected. The later refresh capability contains the
/// observed Everything query/page diagnostics.
/// </summary>
internal static class BrokerDiscoveryDiagnostics
{
    internal static string FormatSelection(
        BrokerSettings settings,
        IFileDiscoveryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provider);

        var attemptsEverything = settings.DiscoveryPreference != DiscoveryPreference.BuiltInOnly;
        return JsonSerializer.Serialize(new
        {
            eventType = "discovery-provider-selection",
            configuredPreference = settings.DiscoveryPreference.ToString(),
            selectedProvider = provider.GetType().Name,
            selectionOrder = attemptsEverything
                ? "Everything -> NTFS/built-in coverage"
                : "Built-in only",
            everythingAttemptPlanned = attemptsEverything,
            builtInDiscoveryEnabled = true,
            builtInFallbackAvailable = attemptsEverything,
            networkRootCount = settings.NetworkRoots.Count,
        });
    }
}
