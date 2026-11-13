using System.Text.Json;

namespace SwSearch.WebUi.Security;

/// <summary>
/// A page action bound to one exact row in one current desktop result window.
/// Path text alone is not a result identity: browser page caching and an
/// in-flight result refresh can otherwise replay a path from another window.
/// </summary>
public sealed record ResultActionRequest(
    string Action,
    string Path,
    long ResultSetId,
    long? ResultRevision,
    int ResultStart,
    int ResultIndex,
    int SchemaVersion);

public static class ResultActionProtocol
{
    public const int CurrentSchemaVersion = 2;
    public const string Open = "open";
    public const string Reveal = "reveal";
    public const string CopyPath = "copyPath";
    public const string ViewProperties = "viewProperties";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Open, Reveal, CopyPath, ViewProperties,
    };

    public static IReadOnlySet<string> Actions => Allowed;

    public static bool TryNormalize(string? value, out string action)
    {
        action = value ?? string.Empty;
        return Allowed.Contains(action);
    }

    public static bool TryParse(
        JsonElement payload,
        out ResultActionRequest request,
        out string rejectionCode)
    {
        request = null!;
        rejectionCode = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            rejectionCode = "payload-kind";
            return false;
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name is not (
                "action" or "path" or "schema" or "resultSetId" or
                "resultRevision" or "resultStart" or "resultIndex"))
            {
                rejectionCode = "unexpected-field";
                return false;
            }
        }

        if (!payload.TryGetProperty("action", out var actionElement) ||
            actionElement.ValueKind != JsonValueKind.String ||
            !TryNormalize(actionElement.GetString(), out var action))
        {
            rejectionCode = "action";
            return false;
        }
        if (!payload.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            rejectionCode = "path-kind";
            return false;
        }
        var path = pathElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || path.IndexOf('\0') >= 0)
        {
            rejectionCode = "path-shape";
            return false;
        }

        if (!payload.TryGetProperty("schema", out var schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.Number ||
            !schemaElement.TryGetInt32(out var schema) || schema != CurrentSchemaVersion)
        {
            rejectionCode = "schema";
            return false;
        }

        if (!payload.TryGetProperty("resultSetId", out var resultSetElement) ||
            resultSetElement.ValueKind != JsonValueKind.Number ||
            !resultSetElement.TryGetInt64(out var resultSetId) || resultSetId < 0)
        {
            rejectionCode = "result-set";
            return false;
        }

        if (!payload.TryGetProperty("resultRevision", out var revisionElement))
        {
            rejectionCode = "result-revision";
            return false;
        }
        long? resultRevision = null;
        if (revisionElement.ValueKind != JsonValueKind.Null)
        {
            if (revisionElement.ValueKind != JsonValueKind.Number ||
                !revisionElement.TryGetInt64(out var parsedRevision) || parsedRevision < 0)
            {
                rejectionCode = "result-revision";
                return false;
            }
            resultRevision = parsedRevision;
        }

        if (!payload.TryGetProperty("resultStart", out var resultStartElement) ||
            resultStartElement.ValueKind != JsonValueKind.Number ||
            !resultStartElement.TryGetInt32(out var resultStart) || resultStart < 0)
        {
            rejectionCode = "result-start";
            return false;
        }

        if (!payload.TryGetProperty("resultIndex", out var resultIndexElement) ||
            resultIndexElement.ValueKind != JsonValueKind.Number ||
            !resultIndexElement.TryGetInt32(out var resultIndex) || resultIndex is < 0 or >= 500)
        {
            rejectionCode = "result-index";
            return false;
        }

        // Authorization happens once in DesktopWebBridge by exact current-row
        // lookup. A second extension/path policy here would reject valid
        // broker-owned references before that stronger check can run.
        request = new ResultActionRequest(
            action,
            path,
            resultSetId,
            resultRevision,
            resultStart,
            resultIndex,
            schema);
        return true;
    }
}
