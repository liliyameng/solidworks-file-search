using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Principal;

namespace SwSearch.InstallerCoordinator;

internal sealed class ProductShutdownTransport : IProductShutdownTransport
{
    private const string BrokerPipeNamePrefix = "swsearch.broker.v1.session.";
    private const string WorkerPipeNamePrefix = "swsearch.metadata.v1.session.";
    private const string AppShutdownEventPrefix = @"Local\SwSearch.App.Shutdown.v1.session.";
    private const string AppShutdownAcknowledgedEventPrefix = @"Local\SwSearch.App.ShutdownAck.v1.session.";
    private const string ViewerShutdownEventPrefix = @"Local\SwSearch.PropertyViewer.Shutdown.v1.session.";
    private const int BrokerProtocolVersion = 2;
    private const int BrokerShutdownOperation = 9;
    private const int BrokerBeginShutdownOperation = 10;
    private const int WorkerProtocolVersion = 1;
    private const int MaximumControlFrameBytes = 64 * 1024;
    private readonly TimeSpan _timeout;
    private readonly Func<int, string> _brokerPipeName;
    private readonly Func<int, string> _workerPipeName;
    private readonly Func<int, string> _appEventName;
    private readonly Func<int, string> _appAcknowledgedEventName;
    private readonly Func<int, string> _viewerEventName;
    private readonly Func<CoordinatorExecutionIdentity> _captureIdentity;

    internal ProductShutdownTransport(
        TimeSpan timeout,
        Func<int, string>? brokerPipeName = null,
        Func<int, string>? workerPipeName = null,
        Func<int, string>? appEventName = null,
        Func<int, string>? viewerEventName = null,
        Func<int, string>? appAcknowledgedEventName = null,
        Func<CoordinatorExecutionIdentity>? captureIdentity = null)
    {
        _timeout = timeout > TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
        _brokerPipeName = brokerPipeName ?? (sessionId =>
            BrokerPipeNamePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _workerPipeName = workerPipeName ?? (sessionId =>
            WorkerPipeNamePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _appEventName = appEventName ?? (sessionId =>
            AppShutdownEventPrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _appAcknowledgedEventName = appAcknowledgedEventName ?? (sessionId =>
            AppShutdownAcknowledgedEventPrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _viewerEventName = viewerEventName ?? (sessionId =>
            ViewerShutdownEventPrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _captureIdentity = captureIdentity ?? CoordinatorExecutionIdentity.Capture;
    }

    public ValueTask<ShutdownRequestResult> ShutdownAppAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken) =>
        SignalFrontEndAsync(products, context, _appEventName, _appAcknowledgedEventName, cancellationToken);

    public ValueTask<ShutdownRequestResult> ShutdownPropertyViewerAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken) =>
        SignalFrontEndAsync(products, context, _viewerEventName, null, cancellationToken);

    public async ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken) =>
        await SendForProductsAsync(products, context, SendBrokerAsync, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken) =>
        await SendForProductsAsync(products, context, SendWorkerAsync, cancellationToken).ConfigureAwait(false);

    private async ValueTask<ShutdownRequestResult> SignalFrontEndAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        Func<int, string> eventName,
        Func<int, string>? acknowledgedEventName,
        CancellationToken cancellationToken)
    {
        var identity = _captureIdentity();
        if (!products.Any(product => product.IsTrustedRegisteredRoot) ||
            string.IsNullOrWhiteSpace(identity.UserSid) ||
            !string.Equals(identity.UserSid, context.ExpectedUserSid, StringComparison.Ordinal) ||
            identity.SessionId != context.ExpectedSessionId ||
            !InstallerContextContract.IsCryptographicSessionNonce(context.SessionNonce))
        {
            return ShutdownRequestResult.Failed;
        }

        try
        {
            EventWaitHandle? acknowledgedEvent = null;
            if (acknowledgedEventName is not null)
            {
                try
                {
                    acknowledgedEvent = EventWaitHandle.OpenExisting(
                        acknowledgedEventName(context.ExpectedSessionId));
                    acknowledgedEvent.Reset();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // RC30 and older App instances implement the same graceful
                    // shutdown request but predate acknowledgement. Restart
                    // Manager provides the bounded pre-InstallFiles backstop.
                }
            }
            using (acknowledgedEvent)
            {
            using var shutdownEvent = EventWaitHandle.OpenExisting(eventName(context.ExpectedSessionId));
            shutdownEvent.Set();
            try
            {
                // PropertyViewer is multi-instance. Holding a manual-reset
                // signal for a bounded interval releases every listener that
                // already exists without enumerating processes.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                shutdownEvent.Reset();
            }
                if (acknowledgedEvent is not null && !acknowledgedEvent.WaitOne(_timeout))
                {
                    return ShutdownRequestResult.Failed;
                }
            }
            return ShutdownRequestResult.Accepted;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return ShutdownRequestResult.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return ShutdownRequestResult.Failed;
        }
    }

    private async ValueTask<ShutdownRequestResult> SendForProductsAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        Func<InstalledProductIdentity, ShutdownRequestContext, CancellationToken, ValueTask<ShutdownRequestResult>> sendAsync,
        CancellationToken cancellationToken)
    {
        var identity = _captureIdentity();
        if (string.IsNullOrWhiteSpace(identity.UserSid) ||
            !string.Equals(identity.UserSid, context.ExpectedUserSid, StringComparison.Ordinal) ||
            identity.SessionId != context.ExpectedSessionId ||
            context.ExpectedSessionId < 0 ||
            !InstallerContextContract.IsCryptographicSessionNonce(context.SessionNonce))
        {
            return ShutdownRequestResult.Failed;
        }

        var last = ShutdownRequestResult.Unavailable;
        foreach (var product in products.Where(product => product.IsTrustedRegisteredRoot))
        {
            last = await sendAsync(product, context, cancellationToken).ConfigureAwait(false);
            if (last is ShutdownRequestResult.Accepted or ShutdownRequestResult.Unsupported)
            {
                return last;
            }
        }

        return last;
    }

    private async ValueTask<ShutdownRequestResult> SendBrokerAsync(
        InstalledProductIdentity product,
        ShutdownRequestContext context,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _brokerPipeName(context.ExpectedSessionId),
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            var challengeRequestId = Guid.NewGuid();
            var challengeRequest = new BrokerShutdownRequest(
                BrokerProtocolVersion,
                challengeRequestId,
                BrokerBeginShutdownOperation,
                ShutdownChallengeRequest: new ShutdownChallengeRequest(
                    context.ExpectedUserSid,
                    context.ExpectedSessionId,
                    context.SessionNonce));
            await WriteBrokerRequestAsync(writer, challengeRequest, linked.Token).ConfigureAwait(false);
            var challengeResponse = await ReadBrokerResponseAsync(reader, linked.Token).ConfigureAwait(false);
            if (challengeResponse.RequestId != challengeRequestId)
            {
                return ShutdownRequestResult.Failed;
            }

            if (!challengeResponse.Success)
            {
                return IsUnsupportedBrokerResponse(challengeResponse)
                    ? ShutdownRequestResult.Unsupported
                    : ShutdownRequestResult.Failed;
            }

            if (challengeResponse.ShutdownChallenge is null ||
                challengeResponse.ShutdownChallenge.ChallengeId == Guid.Empty)
            {
                return ShutdownRequestResult.Failed;
            }

            var shutdownRequestId = Guid.NewGuid();
            var request = new BrokerShutdownRequest(
                BrokerProtocolVersion,
                shutdownRequestId,
                BrokerShutdownOperation,
                new ShutdownBinding(
                    product.InstallRoot,
                    product.Version,
                    context.ExpectedUserSid,
                    context.ExpectedSessionId,
                    context.SessionNonce,
                    challengeResponse.ShutdownChallenge.ChallengeId));
            await WriteBrokerRequestAsync(writer, request, linked.Token).ConfigureAwait(false);
            var response = await ReadBrokerResponseAsync(reader, linked.Token).ConfigureAwait(false);
            if (response.RequestId != shutdownRequestId)
            {
                return ShutdownRequestResult.Failed;
            }

            return response.Success
                ? ShutdownRequestResult.Accepted
                : IsUnsupportedBrokerResponse(response)
                    ? ShutdownRequestResult.Unsupported
                    : ShutdownRequestResult.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ShutdownRequestResult.Unavailable;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or EndOfStreamException)
        {
            return ShutdownRequestResult.Failed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ShutdownRequestResult.Unavailable;
        }
    }

    private async ValueTask<ShutdownRequestResult> SendWorkerAsync(
        InstalledProductIdentity product,
        ShutdownRequestContext context,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _workerPipeName(context.ExpectedSessionId),
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);

            var challengeRequestId = Guid.NewGuid();
            await WriteWorkerRequestAsync(
                    pipe,
                    new WorkerShutdownRequest(
                        WorkerProtocolVersion,
                        challengeRequestId,
                        "beginShutdownForUpdate",
                        ShutdownChallengeRequest: new ShutdownChallengeRequest(
                            context.ExpectedUserSid,
                            context.ExpectedSessionId,
                            context.SessionNonce)),
                    linked.Token)
                .ConfigureAwait(false);
            var challengeResponse = await ReadWorkerResponseAsync(pipe, linked.Token).ConfigureAwait(false);
            if (challengeResponse.RequestId != challengeRequestId)
            {
                return ShutdownRequestResult.Failed;
            }

            if (challengeResponse.ErrorCode is not null)
            {
                return challengeResponse.ErrorCode == "METADATA_REQUEST_INVALID"
                    ? ShutdownRequestResult.Unsupported
                    : ShutdownRequestResult.Failed;
            }

            if (challengeResponse.ShutdownChallenge is null ||
                challengeResponse.ShutdownChallenge.ChallengeId == Guid.Empty)
            {
                return ShutdownRequestResult.Failed;
            }

            var shutdownRequestId = Guid.NewGuid();
            await WriteWorkerRequestAsync(
                    pipe,
                    new WorkerShutdownRequest(
                        WorkerProtocolVersion,
                        shutdownRequestId,
                        "shutdownForUpdate",
                        new ShutdownBinding(
                            product.InstallRoot,
                            product.Version,
                            context.ExpectedUserSid,
                            context.ExpectedSessionId,
                            context.SessionNonce,
                            challengeResponse.ShutdownChallenge.ChallengeId)),
                    linked.Token)
                .ConfigureAwait(false);
            var response = await ReadWorkerResponseAsync(pipe, linked.Token).ConfigureAwait(false);
            if (response.RequestId != shutdownRequestId)
            {
                return ShutdownRequestResult.Failed;
            }

            return response.ErrorCode is null
                ? ShutdownRequestResult.Accepted
                : response.ErrorCode == "METADATA_REQUEST_INVALID"
                    ? ShutdownRequestResult.Unsupported
                    : ShutdownRequestResult.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ShutdownRequestResult.Unavailable;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or EndOfStreamException)
        {
            return ShutdownRequestResult.Failed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ShutdownRequestResult.Unavailable;
        }
    }

    private static async ValueTask WriteBrokerRequestAsync(
        StreamWriter writer,
        BrokerShutdownRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaximumControlFrameBytes)
        {
            throw new InvalidDataException("Broker control request is too large.");
        }

        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<BrokerShutdownResponse> ReadBrokerResponseAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(line) || line.Length > MaximumControlFrameBytes)
        {
            throw new InvalidDataException("Broker control response is empty or too large.");
        }

        return JsonSerializer.Deserialize<BrokerShutdownResponse>(line, JsonOptions)
               ?? throw new InvalidDataException("Broker control response is invalid.");
    }

    private static bool IsUnsupportedBrokerResponse(BrokerShutdownResponse response) =>
        response.ErrorCode is "BROKER_OPERATION_INVALID" or "BROKER_PROTOCOL_MISMATCH";

    private static async ValueTask WriteWorkerRequestAsync(
        Stream stream,
        WorkerShutdownRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaximumControlFrameBytes)
        {
            throw new InvalidDataException("Worker control request is too large.");
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WorkerShutdownResponse> ReadWorkerResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var length = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        var responseLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (responseLength <= 0 || responseLength > MaximumControlFrameBytes)
        {
            throw new InvalidDataException("Worker control response is empty or too large.");
        }

        var responsePayload = new byte[responseLength];
        await ReadExactlyAsync(stream, responsePayload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<WorkerShutdownResponse>(responsePayload, JsonOptions)
               ?? throw new InvalidDataException("Worker control response is invalid.");
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    private sealed record BrokerShutdownRequest(
        int ProtocolVersion,
        Guid RequestId,
        int Operation,
        ShutdownBinding? ShutdownForUpdate = null,
        ShutdownChallengeRequest? ShutdownChallengeRequest = null);

    private sealed record BrokerShutdownResponse(
        Guid RequestId,
        bool Success,
        string? ErrorCode,
        ShutdownChallengeResponse? ShutdownChallenge = null);

    private sealed record WorkerShutdownRequest(
        int ProtocolVersion,
        Guid RequestId,
        string Operation,
        ShutdownBinding? ShutdownForUpdate = null,
        ShutdownChallengeRequest? ShutdownChallengeRequest = null);

    private sealed record WorkerShutdownResponse(
        Guid RequestId,
        string? ErrorCode,
        ShutdownChallengeResponse? ShutdownChallenge = null);

    private sealed record ShutdownChallengeRequest(
        string ExpectedUserSid,
        int ExpectedSessionId,
        Guid Nonce);

    private sealed record ShutdownChallengeResponse(Guid ChallengeId);

    private sealed record ShutdownBinding(
        string ExpectedInstallRoot,
        string ExpectedProductVersion,
        string ExpectedUserSid,
        int ExpectedSessionId,
        Guid Nonce,
        Guid ChallengeId);
}
