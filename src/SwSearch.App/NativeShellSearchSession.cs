using SwSearch.Contracts;

namespace SwSearch.App;

/// <summary>
/// An immutable, read-only Broker paging session for the native Explorer
/// surface.  It deliberately does not touch <see cref="MainViewModel.Results"/>
/// or the interactive search cancellation source: the Shell can consume all
/// result pages while the Web toolbar continues to serve the next user query.
/// </summary>
internal sealed class NativeShellSearchSession : IDisposable
{
    internal const int PageSize = 500;

    private readonly ISearchClient _searchClient;
    private readonly SearchQuery _template;
    private readonly CancellationTokenSource _lifetime;
    private int _nextOffset;
    private long? _expectedRevision;
    private string? _pageFingerprint;
    private bool _completed;
    private bool _disposed;

    internal NativeShellSearchSession(
        ISearchClient searchClient,
        SearchQuery template,
        long generation,
        CancellationToken applicationLifetime)
    {
        _searchClient = searchClient ?? throw new ArgumentNullException(nameof(searchClient));
        _template = template ?? throw new ArgumentNullException(nameof(template));
        Generation = generation;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime);
    }

    public long Generation { get; }

    public bool IsCompleted => _completed;

    /// <summary>
    /// Reads one stable Broker page.  A revision/page-fingerprint reset is
    /// explicitly surfaced to the host rather than allowing a stale page to be
    /// appended to a newer native Shell result generation.
    /// </summary>
    public async ValueTask<NativeShellSearchPage> ReadNextPageAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_completed)
        {
            return NativeShellSearchPage.Completed(Generation, _nextOffset);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var query = _template with
        {
            Offset = _nextOffset,
            Limit = PageSize,
            ExpectedRevision = _expectedRevision,
            PageFingerprint = _pageFingerprint,
            // The foreground search already supplied the first visible result.
            // This independent native population pass must stay background work.
            IsInteractive = false,
        };
        var result = await _searchClient.SearchAsync(query, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();

        if (result.Availability == SearchResultAvailability.CacheTemporarilyUnavailable)
        {
            return NativeShellSearchPage.Unavailable(Generation, _nextOffset, result.Capability);
        }

        // The Broker may reset paging if a revision changes after page zero.
        // Stop this generation before it can blend two snapshots in Explorer.
        if (_nextOffset != 0 && result.PageReset)
        {
            return NativeShellSearchPage.ResetRequired(Generation, _nextOffset, result.Capability);
        }

        var rawCount = result.Hits.Count;
        // One pass builds both outputs: the deduplicated path array for the
        // Shell population worker and the per-path match metadata overlay.
        // The upstream query already constrains kinds; the defensive filter
        // stays, but no longer materializes a second intermediate collection.
        var matchEntries = new List<NativeShellMatchEntry>(result.Hits.Count);
        var paths = new List<string>(result.Hits.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in result.Hits)
        {
            if (hit.Kind is not (SolidWorksDocumentKind.Part or SolidWorksDocumentKind.Assembly))
            {
                continue;
            }
            var fullPath = hit.FullPath;
            if (string.IsNullOrWhiteSpace(fullPath) || !seenPaths.Add(fullPath))
            {
                continue;
            }
            paths.Add(fullPath);
            matchEntries.Add(new NativeShellMatchEntry(fullPath, hit.MatchedProperty, hit.MatchedValue));
        }

        _expectedRevision = result.IndexRevision ?? result.State?.Revision ?? _expectedRevision;
        _pageFingerprint ??= result.PageFingerprint ?? SearchPagingFingerprint.Create(_template);
        _nextOffset += rawCount;
        var hasMore = rawCount > 0 && (result.HasMore ?? rawCount == PageSize);
        _completed = !hasMore;

        return new NativeShellSearchPage(
            Generation,
            _nextOffset - rawCount,
            paths,
            matchEntries,
            hasMore,
            Reset: false,
            TemporarilyUnavailable: false,
            result.Capability);
    }

    public void Cancel() => _lifetime.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>One immutable page supplied to the native Shell population loop.</summary>
internal sealed record NativeShellSearchPage(
    long Generation,
    int Offset,
    IReadOnlyList<string> Paths,
    IReadOnlyList<NativeShellMatchEntry> MatchEntries,
    bool HasMore,
    bool Reset,
    bool TemporarilyUnavailable,
    RuntimeCapability Capability)
{
    public static NativeShellSearchPage Completed(long generation, int offset) =>
        new(generation, offset, Array.Empty<string>(), Array.Empty<NativeShellMatchEntry>(), false, false, false,
            new RuntimeCapability(CapabilityState.Available, "NATIVE_SHELL_COMPLETE", "complete"));

    public static NativeShellSearchPage ResetRequired(long generation, int offset, RuntimeCapability capability) =>
        new(generation, offset, Array.Empty<string>(), Array.Empty<NativeShellMatchEntry>(), false, true, false, capability);

    public static NativeShellSearchPage Unavailable(long generation, int offset, RuntimeCapability capability) =>
        new(generation, offset, Array.Empty<string>(), Array.Empty<NativeShellMatchEntry>(), false, false, true, capability);
}

/// <summary>Carries match metadata for one file in a native Shell result page.</summary>
internal sealed record NativeShellMatchEntry(string Path, string? MatchedProperty, string? MatchedValue);
