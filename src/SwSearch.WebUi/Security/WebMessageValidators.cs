using System.Text.Json;

namespace SwSearch.WebUi.Security;

public static class WebMessageValidators
{
    private static readonly HashSet<string> Views = new(StringComparer.Ordinal)
        { "main", "maintoolbar", "property", "taskpane" };
    private static readonly HashSet<string> ResultColumns = new(StringComparer.Ordinal)
        {
            "name", "type", "path", "size", "modified", "configuration", "description", "material",
            "fileName", "kind", "matchedProperty", "matchedValue", "source", "directory", "cacheStatus",
        };
    // "none" is an explicit user action: it clears a previous column sort and
    // restores the Broker's relevance order. Keep it on the same strict
    // message surface as asc/desc rather than accepting an omitted direction.
    private static readonly HashSet<string> SortDirections = new(StringComparer.Ordinal)
        { "asc", "desc", "none" };
    private static readonly HashSet<string> OptionalTargets = new(StringComparer.Ordinal)
        { "broker", "search", "properties", "integration" };

    public static WebMessageValidationResult Empty(JsonElement payload) =>
        HasOnlyProperties(payload) ? Valid() : Invalid("Payload must be empty.");

    public static WebMessageValidationResult Ready(JsonElement payload) =>
        HasOnlyProperties(payload, "view") &&
        TryGetRequiredString(payload, "view", 16, out var view) && Views.Contains(view)
            ? Valid()
            : Invalid("Ready view is invalid.");

    public static WebMessageValidationResult Search(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "query", "scope", "types", "clientSequence", "inputDelayMs") ||
            !TryGetRequiredString(payload, "query", 1024, out _))
        {
            return Invalid("Search query is invalid.");
        }

        if (payload.TryGetProperty("scope", out var scope) &&
            (scope.ValueKind != JsonValueKind.String || scope.GetString()!.Length > 32_767))
        {
            return Invalid("Search scope is invalid.");
        }

        if (payload.TryGetProperty("clientSequence", out _) &&
            !TryGetInt(payload, "clientSequence", 0, int.MaxValue, out _))
        {
            return Invalid("Search sequence is invalid.");
        }
        if (payload.TryGetProperty("inputDelayMs", out _) &&
            !TryGetInt(payload, "inputDelayMs", 0, 5_000, out _))
        {
            return Invalid("Search input timing is invalid.");
        }

        // Pre-removal pages can still include their old type envelope. It is
        // structurally validated for compatibility, then ignored by the host.
        if (!payload.TryGetProperty("types", out var types))
        {
            return Valid();
        }

        return types.ValueKind == JsonValueKind.Object &&
               HasOnlyProperties(types, "part", "assembly", "drawing") &&
               AllBooleanProperties(types)
            ? Valid()
            : Invalid("Search type filters are invalid.");
    }

    public static WebMessageValidationResult LoadMore(JsonElement payload) =>
        HasOnlyProperties(payload, "offset", "limit") &&
        TryGetInt(payload, "offset", 0, int.MaxValue, out _) &&
        TryGetInt(payload, "limit", 1, 500, out _)
            ? Valid()
            : Invalid("Page bounds are invalid.");

    public static WebMessageValidationResult FirstPaint(JsonElement payload) =>
        HasOnlyProperties(payload, "clientSequence", "totalMs", "renderMs", "resultCount") &&
        TryGetInt(payload, "clientSequence", 0, int.MaxValue, out _) &&
        TryGetInt(payload, "totalMs", 0, 60_000, out _) &&
        TryGetInt(payload, "renderMs", 0, 60_000, out _) &&
        TryGetInt(payload, "resultCount", 0, 500, out _)
            ? Valid()
            : Invalid("First-paint timing is invalid.");

    public static WebMessageValidationResult Sort(JsonElement payload) =>
        HasOnlyProperties(payload, "column", "direction") &&
        TryGetRequiredString(payload, "column", 32, out var column) && ResultColumns.Contains(column) &&
        TryGetRequiredString(payload, "direction", 4, out var direction) && SortDirections.Contains(direction)
            ? Valid()
            : Invalid("Sort request is invalid.");

    public static WebMessageValidationResult ColumnWidth(JsonElement payload)
    {
        var usesColumn = HasOnlyProperties(payload, "column", "width") &&
                         TryGetRequiredString(payload, "column", 32, out var column) &&
                         ResultColumns.Contains(column);
        var usesId = HasOnlyProperties(payload, "id", "width") &&
                     TryGetRequiredString(payload, "id", 32, out var id) &&
                     ResultColumns.Contains(id);
        return (usesColumn || usesId) && TryGetNumber(payload, "width", 48, 2000, out _)
            ? Valid()
            : Invalid("Column width is invalid.");
    }

    public static WebMessageValidationResult ColumnWidths(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "widths") ||
            !payload.TryGetProperty("widths", out var widths) ||
            widths.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Column widths are invalid.");
        }

        foreach (var property in widths.EnumerateObject())
        {
            if (!ResultColumns.Contains(property.Name) ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetDouble(out var value) || value is < 48 or > 2000)
            {
                return Invalid("Column widths are invalid.");
            }
        }

        return Valid();
    }

    public static WebMessageValidationResult ResultAction(JsonElement payload) =>
        ResultActionProtocol.TryParse(payload, out _, out var rejectionCode)
            ? Valid()
            : Invalid($"Result action is invalid [{rejectionCode}].");

    public static WebMessageValidationResult OpenResult(JsonElement payload) => PathOnly(payload);

    public static WebMessageValidationResult FileDragArm(JsonElement payload) =>
        FileDragProtocol.TryParse(payload, out _, out var rejectionCode)
            ? Valid()
            : Invalid($"File drag arm is invalid [{rejectionCode}].");

    public static WebMessageValidationResult Mode(JsonElement payload) =>
        HasOnlyProperties(payload, "mode") &&
        TryGetRequiredString(payload, "mode", 16, out var mode) &&
        (mode is "all" or "name" or "property" or "library")
            ? Valid()
            : Invalid("Search mode is invalid.");

    public static WebMessageValidationResult PathOnly(JsonElement payload) =>
        HasOnlyProperties(payload, "path") &&
        TryGetRequiredString(payload, "path", 32_767, out var path) && IsSafeSolidWorksPath(path)
            ? Valid()
            : Invalid("SOLIDWORKS file path is invalid.");

    public static WebMessageValidationResult OptionalTarget(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "target"))
        {
            return Invalid("Retry target is invalid.");
        }

        if (!payload.TryGetProperty("target", out _))
        {
            return Valid();
        }

        return TryGetRequiredString(payload, "target", 32, out var target) && OptionalTargets.Contains(target)
            ? Valid()
            : Invalid("Retry target is invalid.");
    }

    public static WebMessageValidationResult PreferencesSave(JsonElement payload)
    {
        if (!HasOnlyProperties(
                payload,
                "taskpane",
                "folderMenu",
                "filePropertiesMenu",
                "startup",
                "propertyDisplayOrder") ||
            !TryGetBoolean(payload, "taskpane", out _) ||
            !TryGetBoolean(payload, "folderMenu", out _) ||
            !TryGetBoolean(payload, "filePropertiesMenu", out _) ||
            !TryGetBoolean(payload, "startup", out _) ||
            !TryGetOptionalStringArray(payload, "propertyDisplayOrder", 256, 512))
        {
            return Invalid("Preferences payload is invalid.");
        }
        return Valid();
    }

    public static WebMessageValidationResult PropertySelect(JsonElement payload) =>
        HasOnlyProperties(payload, "configuration") &&
        TryGetRequiredString(payload, "configuration", 512, out _)
            ? Valid()
            : Invalid("Property configuration is invalid.");

    public static WebMessageValidationResult PropertyMode(JsonElement payload) =>
        HasOnlyProperties(payload, "mode") &&
        TryGetRequiredString(payload, "mode", 32, out var mode) &&
        mode is "custom" or "configuration"
            ? Valid()
            : Invalid("Property display mode is invalid.");

    public static WebMessageValidationResult ExternalUri(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "uri") ||
            !TryGetRequiredString(payload, "uri", 2048, out var raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("External URI is invalid.");
        }

        return Valid();
    }

    public static bool HasOnlyProperties(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSafeSolidWorksPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (!(extension.Equals(".sldprt", StringComparison.OrdinalIgnoreCase) ||
              extension.Equals(".sldasm", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllBooleanProperties(JsonElement value) =>
        value.EnumerateObject().All(static property =>
            property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);

    private static bool TryGetRequiredString(
        JsonElement payload,
        string name,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        return payload.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               (value = property.GetString() ?? string.Empty).Length <= maxLength &&
               value.IndexOf('\0') < 0;
    }

    private static bool TryGetInt(
        JsonElement payload,
        string name,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) && value >= minimum && value <= maximum;
    }

    private static bool TryGetNumber(
        JsonElement payload,
        string name,
        double minimum,
        double maximum,
        out double value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value) && value >= minimum && value <= maximum;
    }

    private static bool TryGetBoolean(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetOptionalStringArray(
        JsonElement payload,
        string name,
        int maximumCount,
        int maximumLength)
    {
        if (!payload.TryGetProperty(name, out var property))
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maximumCount)
        {
            return false;
        }
        return property.EnumerateArray().All(item =>
            item.ValueKind == JsonValueKind.String &&
            (item.GetString()?.Length ?? 0) <= maximumLength &&
            (item.GetString()?.IndexOf('\0') ?? -1) < 0);
    }

    private static WebMessageValidationResult Valid() => WebMessageValidationResult.Valid();

    private static WebMessageValidationResult Invalid(string error) => WebMessageValidationResult.Invalid(error);
}
