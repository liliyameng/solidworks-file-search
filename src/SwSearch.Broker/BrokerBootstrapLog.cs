using System.Text;

namespace SwSearch.Broker;

internal sealed class BrokerBootstrapLog
{
    private const int MaximumMessageCharacters = 4096;
    private readonly object _gate = new();
    private readonly Action<string> _writeLine;

    private BrokerBootstrapLog(Action<string> writeLine)
    {
        _writeLine = writeLine;
    }

    internal static BrokerBootstrapLog CreateDefault()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(root, "SwSearch", "Logs", "broker-bootstrap.log");
            return CreateFile(path);
        }
        catch
        {
            return NoOp;
        }
    }

    internal static BrokerBootstrapLog CreateFile(
        string path,
        long maximumFileBytes = 256 * 1024,
        int archiveCount = 2)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (maximumFileBytes < 1024 || maximumFileBytes > 4 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
            }

            if (archiveCount is < 0 or > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(archiveCount));
            }

            return new BrokerBootstrapLog(line =>
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded(fullPath, maximumFileBytes, archiveCount);
                File.AppendAllText(fullPath, line, new UTF8Encoding(false));
            });
        }
        catch
        {
            return NoOp;
        }
    }

    internal static BrokerBootstrapLog CreateForTesting(Action<string> writeLine) =>
        new(writeLine);

    internal static BrokerBootstrapLog NoOp { get; } = new(static _ => { });

    internal void Write(string code, string message)
    {
        try
        {
            var cleanCode = Clean(code, 128);
            var cleanMessage = Clean(message, MaximumMessageCharacters);
            var line = $"{DateTimeOffset.Now:O}\t{Environment.ProcessId}\t{cleanCode}\t{cleanMessage}{Environment.NewLine}";
            lock (_gate)
            {
                _writeLine(line);
            }
        }
        catch
        {
            // Diagnostics must never prevent the Broker from starting or
            // obscure the original startup failure.
        }
    }

    internal void WriteException(string code, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(code, $"{exception.GetType().FullName}: {exception.Message}");
    }

    private static void RotateIfNeeded(string path, long maximumFileBytes, int archiveCount)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < maximumFileBytes)
        {
            return;
        }

        if (archiveCount == 0)
        {
            File.Delete(path);
            return;
        }

        var oldest = path + "." + archiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = archiveCount - 1; index >= 1; index--)
        {
            var source = path + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = path + "." + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.Move(source, destination);
        }

        File.Move(path, path + ".1");
    }

    private static string Clean(string? value, int maximumCharacters)
    {
        var cleaned = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        return cleaned.Length <= maximumCharacters
            ? cleaned
            : cleaned[..maximumCharacters];
    }
}
