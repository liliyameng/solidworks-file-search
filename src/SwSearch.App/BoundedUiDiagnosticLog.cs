using System.IO;
using System.Text;
using System.Threading.Channels;

namespace SwSearch.App;

internal static class BoundedUiDiagnosticLog
{
    private const int Capacity = 64;
    private static readonly Channel<Entry> Queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private static readonly Task Worker = Task.Run(DrainAsync);

    internal static bool TryWrite(string path, string line)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(line))
        {
            return false;
        }

        return Queue.Writer.TryWrite(new Entry(path, line));
    }

    private static async Task DrainAsync()
    {
        await foreach (var entry in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var path = Path.GetFullPath(entry.Path);
                var directory = Path.GetDirectoryName(path)
                    ?? throw new InvalidOperationException("Diagnostic directory is invalid.");
                Directory.CreateDirectory(directory);
                var bytes = Encoding.UTF8.GetBytes(entry.Line);
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // Diagnostics must never surface through the UI path or stop the worker.
            }
        }
    }

    private sealed record Entry(string Path, string Line);
}
