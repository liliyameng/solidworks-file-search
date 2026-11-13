using System.Text.Json;

namespace SwSearch.WebUi.Security;

/// <summary>
/// Desktop-only arm request for a Shell file drag. It is intentionally separate
/// from result context actions: this message can only arm a host-issued, still
/// held native mouse gesture; it cannot launch a drag by itself.
/// </summary>
public sealed record FileDragArmRequest(
    string GestureId,
    long ResultSetId,
    long? ResultRevision,
    string Path,
    int SchemaVersion);

public static class FileDragProtocol
{
    public const int CurrentSchemaVersion = 1;

    public static bool TryParse(
        JsonElement payload,
        out FileDragArmRequest request,
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
            if (property.Name is not ("schema" or "gestureId" or "resultSetId" or "resultRevision" or "path"))
            {
                rejectionCode = "unexpected-field";
                return false;
            }
        }

        if (!payload.TryGetProperty("schema", out var schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.Number ||
            !schemaElement.TryGetInt32(out var schema) ||
            schema != CurrentSchemaVersion)
        {
            rejectionCode = "schema";
            return false;
        }

        if (!payload.TryGetProperty("gestureId", out var gestureElement) ||
            gestureElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(gestureElement.GetString(), "N", out _))
        {
            rejectionCode = "gesture";
            return false;
        }
        var gestureId = gestureElement.GetString()!;

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

        if (!payload.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            rejectionCode = "path-kind";
            return false;
        }
        var path = pathElement.GetString() ?? string.Empty;
        if (!WebMessageValidators.IsSafeSolidWorksPath(path))
        {
            rejectionCode = "path";
            return false;
        }

        request = new FileDragArmRequest(gestureId, resultSetId, resultRevision, path, schema);
        return true;
    }
}
