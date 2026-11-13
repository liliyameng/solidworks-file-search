using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwSearch.WebUi.Security;

/// <summary>
/// Parses WebView messages into an explicitly registered command set. It never
/// performs file, shell, registry, network, or process operations.
/// </summary>
public sealed partial class WebMessageRouter
{
    public const int MaximumMessageCharacters = 128 * 1024;

    private readonly Dictionary<string, WebPayloadValidator> _validators =
        new(StringComparer.Ordinal);

    public WebMessageRouter()
    {
        RegisterDefaults();
    }

    public IReadOnlyCollection<string> AllowedTypes => _validators.Keys;

    public void Register(string type, WebPayloadValidator validator, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (!IsValidType(type))
        {
            throw new ArgumentException("Message type must be a short dotted identifier.", nameof(type));
        }

        if (!replace && _validators.ContainsKey(type))
        {
            throw new InvalidOperationException($"Message type '{type}' is already registered.");
        }

        _validators[type] = validator;
    }

    public bool TryValidate(string json, out ValidatedWebMessage? message, out string? error)
    {
        message = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumMessageCharacters)
        {
            error = "Message is empty or exceeds the size limit.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !WebMessageValidators.HasOnlyProperties(root, "type", "payload", "requestId"))
            {
                error = "Message envelope is invalid.";
                return false;
            }

            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                error = "Message type is required.";
                return false;
            }

            var type = typeElement.GetString() ?? string.Empty;
            if (!IsValidType(type) || !_validators.TryGetValue(type, out var validator))
            {
                error = "Message type is not allowed.";
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                error = "Message payload must be an object.";
                return false;
            }

            string? requestId = null;
            if (root.TryGetProperty("requestId", out var requestIdElement))
            {
                if (requestIdElement.ValueKind != JsonValueKind.String)
                {
                    error = "Request id must be a string.";
                    return false;
                }

                requestId = requestIdElement.GetString();
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ||
                    !RequestIdPattern().IsMatch(requestId))
                {
                    error = "Request id is invalid.";
                    return false;
                }
            }

            var validation = validator(payload);
            if (!validation.IsValid)
            {
                error = validation.Error ?? "Message payload was rejected.";
                return false;
            }

            message = new ValidatedWebMessage(type, payload.Clone(), requestId);
            return true;
        }
        catch (JsonException)
        {
            error = "Message is not valid JSON.";
            return false;
        }
    }

    private void RegisterDefaults()
    {
        Register("ready", WebMessageValidators.Ready);
        Register("search", WebMessageValidators.Search);
        Register("loadMore", WebMessageValidators.LoadMore);
        Register("performance.firstPaint", WebMessageValidators.FirstPaint);
        Register("sort", WebMessageValidators.Sort);
        Register("columnWidth", WebMessageValidators.ColumnWidth);
        Register("columnWidths", WebMessageValidators.ColumnWidths);
        Register("contextAction", WebMessageValidators.ResultAction);
        Register("openResult", WebMessageValidators.OpenResult);
        Register("fileDrag.arm", WebMessageValidators.FileDragArm);
        Register("open", WebMessageValidators.PathOnly);
        Register("reveal", WebMessageValidators.PathOnly);
        Register("copy", WebMessageValidators.PathOnly);
        Register("viewProperties", WebMessageValidators.PathOnly);
        Register("mode", WebMessageValidators.Mode);
        Register("preferences.open", WebMessageValidators.Empty);
        Register("preferences.save", WebMessageValidators.PreferencesSave);
        Register("preferences.close", WebMessageValidators.Empty);
        Register("retry", WebMessageValidators.OptionalTarget);
        Register("refresh", WebMessageValidators.Empty);
        Register("restoreScope", WebMessageValidators.Empty);
        Register("browseScope", WebMessageValidators.Empty);
        Register("property.load", WebMessageValidators.PathOnly);
        Register("property.select", WebMessageValidators.PropertySelect);
        Register("property.close", WebMessageValidators.Empty);
        Register("property.mode", WebMessageValidators.PropertyMode);
        Register("openExternal", WebMessageValidators.ExternalUri);
    }

    private static bool IsValidType(string? value) =>
        value is { Length: > 0 and <= 64 } && TypePattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();
}
