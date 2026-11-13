using SwSearch.Contracts;

namespace SwSearch.Everything;

public sealed record EverythingQueryPage(
    uint TotalItems,
    uint Offset,
    uint ReturnedItems,
    IReadOnlyList<FileCandidate> Items);

public interface IEverythingQueryTransport : IAsyncDisposable
{
    ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken);

    ValueTask<EverythingQueryPage> QueryPageAsync(
        string search,
        uint offset,
        uint maximumResults,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only progress exposed by the paged Everything snapshot. The indexer
/// polls this value and combines it with metadata progress for the UI.
/// </summary>
public interface IEverythingDiscoveryProgressSource
{
    FileDiscoveryProgressSnapshot DiscoveryProgress { get; }
}

public sealed class EverythingQueryException : Exception
{
    public EverythingQueryException(string code, string message)
        : base(message) => Code = code;

    public EverythingQueryException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
