namespace SwSearch.Everything;

/// <summary>
/// Immutable, per-snapshot evidence for the optional Everything accelerator.
/// This intentionally describes what was observed rather than inferring that
/// Everything was used from the outer fallback-provider type.
/// </summary>
public sealed record EverythingDiscoveryDiagnostics(
    bool Attempted,
    bool Available,
    string AvailabilityCode,
    long PageCount,
    long ReturnedEntryCount,
    long EmittedEntryCount,
    string? FailureCode,
    string? FailureType,
    bool Completed,
    DateTimeOffset UpdatedAtUtc)
{
    public static EverythingDiscoveryDiagnostics NotAttempted { get; } = new(
        Attempted: false,
        Available: false,
        AvailabilityCode: "EVERYTHING_NOT_PROBED",
        PageCount: 0,
        ReturnedEntryCount: 0,
        EmittedEntryCount: 0,
        FailureCode: null,
        FailureType: null,
        Completed: false,
        UpdatedAtUtc: DateTimeOffset.MinValue);

    /// <summary>
    /// Stable primitive fields suitable for <see cref="SwSearch.Contracts.RuntimeCapability.Details"/>
    /// and line-oriented diagnostic logs. Values deliberately avoid paths and
    /// user search text.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToCapabilityDetails()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["everythingAttempted"] = Attempted ? "true" : "false",
            ["everythingAvailable"] = Available ? "true" : "false",
            ["everythingProbeCode"] = AvailabilityCode,
            ["everythingPageCount"] = PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["everythingReturnedEntries"] = ReturnedEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["everythingEmittedEntries"] = EmittedEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["everythingCompleted"] = Completed ? "true" : "false",
        };

        if (!string.IsNullOrWhiteSpace(FailureCode))
        {
            details["everythingFailureCode"] = FailureCode;
        }

        if (!string.IsNullOrWhiteSpace(FailureType))
        {
            details["everythingFailureType"] = FailureType;
        }

        return details;
    }
}

/// <summary>
/// Optional diagnostics surface for a provider that talks to Everything. The
/// fallback wrapper can preserve this evidence when it chooses another source
/// for coverage or recovery.
/// </summary>
public interface IEverythingDiscoveryDiagnosticsSource
{
    EverythingDiscoveryDiagnostics EverythingDiagnostics { get; }
}
