using SwSearch.Contracts;
using SwSearch.Indexer;

namespace SwSearch.Integration;

public sealed record LocalCacheSearchPage(
    IReadOnlyList<SearchHit> Hits,
    bool HasMore,
    string PageFingerprint);

public sealed class LocalCacheUnavailableException(string message, Exception innerException)
    : IOException(message, innerException);

/// <summary>Query-only adapter; it cannot initialize or mutate the index.</summary>
public sealed class LocalCacheSearchReader(string? databasePath = null)
{
    private readonly string? _databasePath = databasePath;

    public async ValueTask<LocalCacheSearchPage> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var hits = await Task.Run(async () =>
            {
                await using var store = SqliteIndexStore.OpenReadOnlyExisting(_databasePath);
                return await store.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return new LocalCacheSearchPage(
                hits,
                hits.Count == query.Limit,
                SearchPagingFingerprint.Create(query with { Offset = 0, ExpectedRevision = null, PageFingerprint = null }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or
                UnauthorizedAccessException or InvalidOperationException)
        {
            throw new LocalCacheUnavailableException("本地只读索引暂不可用。", exception);
        }
        catch (Exception exception) when (
            string.Equals(exception.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
        {
            throw new LocalCacheUnavailableException("本地只读索引暂不可用。", exception);
        }
    }

    public async ValueTask<long> CountAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(async () =>
            {
                await using var store = SqliteIndexStore.OpenReadOnlyExisting(_databasePath);
                return await store.CountSearchAsync(query, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or
                UnauthorizedAccessException or InvalidOperationException)
        {
            throw new LocalCacheUnavailableException("本地只读索引暂不可用。", exception);
        }
        catch (Exception exception) when (
            string.Equals(exception.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
        {
            throw new LocalCacheUnavailableException("本地只读索引暂不可用。", exception);
        }
    }
}
