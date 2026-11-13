using Microsoft.Data.Sqlite;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    private static ValueTask RebuildShortGramsForFileCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static ValueTask RebuildShortGramsForDiscoveryChangesCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

}
