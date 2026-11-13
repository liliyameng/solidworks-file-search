using System.Text;

namespace SwSearch.InstallerCoordinator;

internal sealed class SystemCoordinatorDelay : ICoordinatorDelay
{
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}

internal sealed class FileCoordinatorLog : ICoordinatorLog, IDisposable
{
    private readonly StreamWriter _writer;

    internal FileCoordinatorLog(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(fullPath, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        Write("--- installer coordination started ---");
    }

    public void Write(string message) =>
        _writer.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");

    public void Dispose()
    {
        Write("--- installer coordination finished ---");
        _writer.Dispose();
    }
}
