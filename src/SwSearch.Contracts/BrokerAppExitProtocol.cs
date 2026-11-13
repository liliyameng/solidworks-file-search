using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SwSearch.Contracts;

/// <summary>
/// Raised when the dedicated application-exit endpoint returns a malformed or
/// mismatched response. A negative response from a healthy Broker is returned
/// as <see cref="BrokerAppExitShutdownResponse.Success"/> = <see langword="false"/>
/// instead of throwing this exception.
/// </summary>
public sealed class BrokerAppExitProtocolException(string message) : IOException(message);

/// <summary>
/// The only request accepted on <see cref="ContractVersions.BrokerAppExitPipeName"/>.
/// Keeping this DTO separate from <see cref="BrokerRequest"/> prevents the
/// regular search endpoint and installer-update challenge from being reused for
/// an ordinary explicit application exit.
/// </summary>
public sealed record BrokerAppExitShutdownRequest(
    int ProtocolVersion,
    Guid RequestId);

/// <summary>
/// One-shot acknowledgement for an explicit application exit request.
/// A successful acknowledgement means the Broker has accepted shutdown and
/// will start draining only after this response has been flushed to the caller.
/// </summary>
public sealed record BrokerAppExitShutdownResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Sends an explicit application-exit request without starting, recovering, or
/// otherwise managing a Broker process. This is intentionally a one-shot
/// client: callers should use it only after the user explicitly exits the app.
/// </summary>
public static class BrokerAppExitShutdownClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Default bounded connect interval for the no-start request.
    /// </summary>
    public static TimeSpan DefaultConnectTimeout { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Default combined write-and-acknowledgement interval for the no-start
    /// request. Together with <see cref="DefaultConnectTimeout"/>, the default
    /// call remains within two seconds.
    /// </summary>
    public static TimeSpan DefaultResponseTimeout { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Sends the current-session app-exit request. Transport failures are
    /// reported as <see cref="IOException"/> or <see cref="TimeoutException"/>;
    /// caller cancellation remains <see cref="OperationCanceledException"/>.
    /// A valid Broker rejection is returned with <c>Success == false</c>.
    /// </summary>
    public static ValueTask<BrokerAppExitShutdownResponse> RequestShutdownAsync(
        CancellationToken cancellationToken = default) =>
        RequestShutdownAsync(
            ContractVersions.BrokerAppExitPipeName,
            DefaultConnectTimeout,
            DefaultResponseTimeout,
            cancellationToken);

    /// <summary>
    /// Sends a no-start app-exit request to an explicitly supplied endpoint.
    /// <paramref name="responseTimeout"/> is one combined post-connect budget
    /// for writing the request and receiving the acknowledgement. The overload
    /// exists for bounded callers and isolated tests; production callers
    /// normally use <see cref="RequestShutdownAsync(CancellationToken)"/>.
    /// </summary>
    public static async ValueTask<BrokerAppExitShutdownResponse> RequestShutdownAsync(
        string pipeName,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A Broker application-exit pipe name is required.", nameof(pipeName));
        }

        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        var request = new BrokerAppExitShutdownRequest(
            ContractVersions.BrokerAppExitPipe,
            Guid.NewGuid());
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectDeadline.CancelAfter(connectTimeout);
            try
            {
                await pipe.ConnectAsync(connectDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The Broker application-exit endpoint did not accept a connection in time.");
            }
        }

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseDeadline.CancelAfter(responseTimeout);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        await WriteWithDeadlineAsync(writer, requestJson, responseDeadline.Token, cancellationToken).ConfigureAwait(false);
        var responseJson = await ReadWithDeadlineAsync(reader, responseDeadline.Token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new IOException("The Broker application-exit endpoint disconnected without an acknowledgement.");
        }

        BrokerAppExitShutdownResponse response;
        try
        {
            response = JsonSerializer.Deserialize<BrokerAppExitShutdownResponse>(responseJson, JsonOptions)
                       ?? throw new BrokerAppExitProtocolException(
                           "The Broker application-exit acknowledgement was invalid.");
        }
        catch (JsonException exception)
        {
            throw new BrokerAppExitProtocolException(
                $"The Broker application-exit acknowledgement was invalid: {exception.Message}");
        }

        if (response.ProtocolVersion != ContractVersions.BrokerAppExitPipe)
        {
            throw new BrokerAppExitProtocolException(
                $"The Broker application-exit protocol version is incompatible (client {ContractVersions.BrokerAppExitPipe}, server {response.ProtocolVersion}).");
        }

        if (response.RequestId != request.RequestId)
        {
            throw new BrokerAppExitProtocolException(
                "The Broker application-exit acknowledgement did not match the request.");
        }

        return response;
    }

    private static async ValueTask WriteWithDeadlineAsync(
        StreamWriter writer,
        string payload,
        CancellationToken deadlineToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            await writer.WriteLineAsync(payload.AsMemory(), deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Broker application-exit request could not be written in time.");
        }
    }

    private static async ValueTask<string?> ReadWithDeadlineAsync(
        StreamReader reader,
        CancellationToken deadlineToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Broker application-exit acknowledgement did not arrive in time.");
        }
    }
}

/// <summary>
/// A per-user/session marker used only to suppress add-in restarts after the
/// user explicitly exits the desktop application. It remains valid through the
/// current Windows boot and is invalidated automatically after reboot. It is
/// not an IPC authorization mechanism: the dedicated pipe remains protected by
/// its current-user, current-session endpoint.
/// </summary>
public static class BrokerAppExitSessionMarker
{
    // The durable marker is shared with the legacy SOLIDWORKS add-in, but a
    // Broker launch must never synchronously touch it.  The App initializes
    // this process-local bit during its normal startup clear and sets it
    // before writing the durable marker on tray Exit.  Launch/recovery code
    // can therefore make its final decision with one atomic memory read.
    private static int _currentProcessLaunchSuppressed;

    /// <summary>Shared with non-Contracts consumers such as the legacy add-in.</summary>
    public const string DataRootEnvironmentVariable = "SWSEARCH_DATA_ROOT";

    /// <summary>Shared marker directory below the SwSearch data root.</summary>
    public const string MarkerDirectoryName = "Session";

    /// <summary>Shared marker filename prefix.</summary>
    public const string MarkerFilePrefix = "app-exit.v1.session.";

    /// <summary>Shared marker filename suffix.</summary>
    public const string MarkerFileSuffix = ".marker";

    /// <summary>
    /// Publishes the current-process launch suppression before attempting the
    /// durable write.  If the write is unavailable, the explicitly-exiting App
    /// must still not race its own recovery loop into relaunching a Broker.
    /// </summary>
    public static bool TryMarkCurrentSession()
    {
        Volatile.Write(ref _currentProcessLaunchSuppressed, 1);
        return TryMarkForSession(GetCurrentSessionId());
    }

    /// <summary>
    /// Clears the process-local launch suppression before deleting the durable
    /// marker.  A deliberate new App launch is the user's request to resume
    /// Broker activity even when an old marker can no longer be removed.
    /// </summary>
    public static bool TryClearCurrentSession()
    {
        Volatile.Write(ref _currentProcessLaunchSuppressed, 0);
        return TryClearForSession(GetCurrentSessionId());
    }

    public static bool IsMarkedForCurrentSession() => IsMarkedForSession(GetCurrentSessionId());

    /// <summary>
    /// Returns only process memory.  This is intentionally the sole
    /// application-side Broker launch gate: no file-system probe is permitted
    /// on the recovery/launch path.  The durable marker continues to be read
    /// by the add-in, while normal App startup clears both representations.
    /// </summary>
    public static bool IsCurrentProcessLaunchSuppressed() =>
        Volatile.Read(ref _currentProcessLaunchSuppressed) != 0;

    /// <summary>
    /// Resets only this process's launch gate.  The App invokes this before it
    /// begins normal startup work, then clears the durable add-in marker on a
    /// background lifecycle task.  Keeping the reset separate avoids putting
    /// marker file I/O on the Broker launch/UI path.
    /// </summary>
    public static void ClearCurrentProcessLaunchSuppression() =>
        Volatile.Write(ref _currentProcessLaunchSuppressed, 0);

    /// <summary>
    /// Returns the UTC instant at which the current Windows boot began, derived
    /// from the monotonic system tick counter. A marker older than this instant
    /// is from a previous boot and must not suppress a new session.
    /// </summary>
    public static DateTime GetCurrentBootStartedUtc() =>
        DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// Returns the shared marker path. The data root is <c>SWSEARCH_DATA_ROOT</c>
    /// when non-empty, otherwise <c>%LOCALAPPDATA%\SwSearch</c>.
    /// </summary>
    public static string GetMarkerPathForSession(int sessionId)
    {
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        var dataRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        var normalizedDataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwSearch")
            : Path.GetFullPath(dataRoot);
        return Path.Combine(
            normalizedDataRoot,
            MarkerDirectoryName,
            MarkerFilePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + MarkerFileSuffix);
    }

    public static bool TryMarkForSession(int sessionId)
    {
        try
        {
            var path = GetMarkerPathForSession(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (IsExpectedMarkerFileException(exception))
        {
            return false;
        }
    }

    public static bool TryClearForSession(int sessionId)
    {
        try
        {
            var path = GetMarkerPathForSession(sessionId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (IsExpectedMarkerFileException(exception))
        {
            return false;
        }
    }

    public static bool IsMarkedForSession(int sessionId)
    {
        try
        {
            var path = GetMarkerPathForSession(sessionId);
            if (!File.Exists(path))
            {
                return false;
            }

            if (File.GetLastWriteTimeUtc(path) >= GetCurrentBootStartedUtc())
            {
                return true;
            }

            _ = TryClearForSession(sessionId);
            return false;
        }
        catch (Exception exception) when (IsExpectedMarkerFileException(exception))
        {
            return false;
        }
    }

    private static int GetCurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static bool IsExpectedMarkerFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException or System.Security.SecurityException;
}
