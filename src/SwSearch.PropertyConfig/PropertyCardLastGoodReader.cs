using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.PropertyConfig;

/// <summary>
/// Reads only the persisted last-good property-card snapshot.  It never opens or
/// parses the source workbook, so latency-sensitive viewers can use it safely.
/// </summary>
public static class PropertyCardLastGoodReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<PropertyCardConfigSnapshot?> TryReadAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(snapshotPath);
            if (!File.Exists(fullPath)) return null;
            var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return Deserialize(json);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or JsonException)
        {
            return null;
        }
    }

    private static PropertyCardConfigSnapshot? Deserialize(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<PersistedPropertyCardSnapshot>(json, JsonOptions);
            if (envelope is { SchemaVersion: 2, Snapshot.Fields.Count: > 0 }) return envelope.Snapshot;
        }
        catch (JsonException)
        {
        }

        var snapshot = JsonSerializer.Deserialize<PropertyCardConfigSnapshot>(json, JsonOptions);
        return snapshot is { Fields.Count: > 0 } ? snapshot : null;
    }
}
