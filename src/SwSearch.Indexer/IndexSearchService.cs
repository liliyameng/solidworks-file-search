using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed class IndexSearchService : ISearchService
{
    private readonly IIndexStore _store;

    public IndexSearchService(IIndexStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken) =>
        _store.SearchAsync(query, cancellationToken);
}
