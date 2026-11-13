using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SwSearch.App;

internal static class AppActivationForwarder
{
    internal static async Task<bool> TryForwardAsync(
        string pipeName,
        string? scopeFolder,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(scopeFolder ?? string.Empty).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A primary instance running at a different integrity level may
            // reject the pipe connection. Treat it exactly like an
            // unresponsive endpoint so the caller performs the bounded,
            // visible recovery path instead of faulting silently.
            return false;
        }
    }
}
