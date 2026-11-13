namespace SwSearch.Indexer;

public static class SwSearchPaths
{
    public const string DataRootEnvironmentVariable = "SWSEARCH_DATA_ROOT";

    public static string DataDirectory
    {
        get
        {
            var isolatedRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            return string.IsNullOrWhiteSpace(isolatedRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwSearch")
                : Path.GetFullPath(isolatedRoot);
        }
    }

    public static string CoreDatabasePath => Path.Combine(DataDirectory, "core.db");

    public static string LegacyDatabasePath => Path.Combine(DataDirectory, "index-v2.db");

    public static string SearchDatabasePath => Path.Combine(DataDirectory, "search.db");

    /// <summary>
    /// Existing installations keep index-v2.db as their durable core source.
    /// New installations use core.db.  The legacy file is deliberately not
    /// renamed while it may have a WAL/SHM companion.
    /// </summary>
    public static string DatabasePath =>
        File.Exists(CoreDatabasePath) || !File.Exists(LegacyDatabasePath)
            ? CoreDatabasePath
            : LegacyDatabasePath;

    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}

public sealed class RuntimeFileLog
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public RuntimeFileLog(string? logDirectory = null)
    {
        LogDirectory = Path.GetFullPath(logDirectory ?? SwSearchPaths.LogDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string LogDirectory { get; }

    public async ValueTask WriteAsync(
        string level,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        var line = $"{DateTimeOffset.Now:O}\t{Clean(level)}\t{Clean(code)}\t{Clean(message)}{Environment.NewLine}";
        var path = Path.Combine(LogDirectory, $"swsearch-{DateTime.Now:yyyyMMdd}.log");
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Diagnostics must never take down search or indexing.
        }
        catch (UnauthorizedAccessException)
        {
            // The UI will still expose the runtime capability state.
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static string Clean(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
