using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

internal sealed class TemporaryDatabase : IAsyncDisposable
{
    internal TemporaryDatabase() =>
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"swsearch-test-{Guid.NewGuid():N}.db");

    internal string Path { get; }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = Path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal static class AsyncFiles
{
    internal static async IAsyncEnumerable<FileCandidate> From(
        IEnumerable<FileCandidate> files,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
            await Task.Yield();
        }
    }

    internal static async IAsyncEnumerable<FileCandidate> FailAfter(
        FileCandidate file,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return file;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("injected discovery failure");
    }
}
