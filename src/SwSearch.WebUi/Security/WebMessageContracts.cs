using System.Text.Json;

namespace SwSearch.WebUi.Security;

public sealed record ValidatedWebMessage(string Type, JsonElement Payload, string? RequestId);

public sealed class ValidatedWebMessageEventArgs(ValidatedWebMessage message) : EventArgs
{
    public ValidatedWebMessage Message { get; } = message;
}

public sealed class WebMessageRejectedEventArgs(
    string reason,
    string? messageType = null,
    string? action = null,
    int? schemaVersion = null) : EventArgs
{
    public string Reason { get; } = reason;
    public string? MessageType { get; } = messageType;
    public string? Action { get; } = action;
    public int? SchemaVersion { get; } = schemaVersion;
}

public readonly record struct WebMessageValidationResult(bool IsValid, string? Error)
{
    public static WebMessageValidationResult Valid() => new(true, null);

    public static WebMessageValidationResult Invalid(string error) => new(false, error);
}

public delegate WebMessageValidationResult WebPayloadValidator(JsonElement payload);
