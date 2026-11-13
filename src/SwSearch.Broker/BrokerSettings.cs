using System.Text.Json;
using SwSearch.Contracts;
using SwSearch.Indexer;

namespace SwSearch.Broker;

internal sealed record BrokerSettings(
    DiscoveryPreference DiscoveryPreference,
    IReadOnlyList<string> NetworkRoots,
    bool BackgroundPaused)
{
    internal static BrokerSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var networkRoots = root.TryGetProperty("networkRoots", out var rootsElement) &&
                               rootsElement.ValueKind == JsonValueKind.Array
                ? rootsElement.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString()!)
                    .Select(SearchPath.NormalizeFolder)
                    .Where(static item => item.StartsWith("\\\\", StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            return new BrokerSettings(
                ReadDiscoveryPreference(root),
                networkRoots,
                // Manual pause remains intentionally retired. This migration
                // must not leave a user with a permanently idle Broker.
                false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Default;
        }
    }

    internal static BrokerSettings Default { get; } = new(DiscoveryPreference.Automatic, Array.Empty<string>(), false);

    internal IFileDiscoveryProvider CreateDiscovery()
    {
        var options = new BuiltInDiscoveryOptions
        {
            DiscoverAllLocalVolumes = true,
            ExplicitNetworkRoots = NetworkRoots,
        };
        // EverythingPreferred is explicit about priority, but never makes
        // Everything a runtime dependency: the production wrapper still owns
        // the built-in/MFT coverage and recovery paths.
        return DiscoveryPreference switch
        {
            DiscoveryPreference.BuiltInOnly => new BuiltInFileDiscoveryProvider(options),
            DiscoveryPreference.Automatic or DiscoveryPreference.EverythingPreferred =>
                FallbackDiscoveryProvider.CreateDefault(options),
            _ => FallbackDiscoveryProvider.CreateDefault(options),
        };
    }

    private static DiscoveryPreference ReadDiscoveryPreference(JsonElement root)
    {
        if (!root.TryGetProperty("discoveryPreference", out var element))
        {
            return DiscoveryPreference.Automatic;
        }

        if (element.ValueKind == JsonValueKind.String &&
            Enum.TryParse<DiscoveryPreference>(element.GetString(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(DiscoveryPreference), numeric))
        {
            return (DiscoveryPreference)numeric;
        }

        return DiscoveryPreference.Automatic;
    }
}

internal enum DiscoveryPreference
{
    Automatic,
    EverythingPreferred,
    BuiltInOnly,
}
