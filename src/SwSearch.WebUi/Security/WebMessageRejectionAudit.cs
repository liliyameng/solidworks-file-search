using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwSearch.WebUi.Security;

public readonly record struct WebMessageRejectionAudit(
    string? MessageType,
    string? Action,
    int? SchemaVersion)
{
    public static WebMessageRejectionAudit FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = SafeToken(root, "type", 64);
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return new WebMessageRejectionAudit(type, null, null);
            }
            var action = SafeToken(payload, "action", 32);
            int? schema = payload.TryGetProperty("schema", out var schemaElement) &&
                          schemaElement.ValueKind == JsonValueKind.Number &&
                          schemaElement.TryGetInt32(out var value)
                ? value
                : null;
            return new WebMessageRejectionAudit(type, action, schema);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? SafeToken(JsonElement owner, string name, int maximumLength)
    {
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return null;
        var value = element.GetString();
        return value is { Length: > 0 } && value.Length <= maximumLength &&
               Regex.IsMatch(value, "^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)
            ? value
            : "<invalid>";
    }
}
