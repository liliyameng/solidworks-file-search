using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.Broker;

/// <summary>
/// Dedicated current-user/session listener for an explicit desktop application
/// exit. This endpoint intentionally has no installer-update challenge or
/// regular Broker operation envelope.
/// </summary>
internal sealed class BrokerAppExitPipeHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<NamedPipeServerStream> _serverFactory;
    private readonly BrokerHostOptions _options;
    private readonly Action _onShutdownAccepted;
    private readonly Action<string, string> _diagnosticSink;

    internal BrokerAppExitPipeHost(
        Func<NamedPipeServerStream> serverFactory,
        BrokerHostOptions options,
        Action onShutdownAccepted,
        Action<string, string> diagnosticSink)
    {
        _serverFactory = serverFactory ?? throw new ArgumentNullException(nameof(serverFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _onShutdownAccepted = onShutdownAccepted ?? throw new ArgumentNullException(nameof(onShutdownAccepted));
        _diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));
    }

    internal async Task RunAsync(
        CancellationToken acceptCancellationToken,
        CancellationToken handlerCancellationToken)
    {
        var consecutiveAcceptFailures = 0;
        while (!acceptCancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = _serverFactory();
                await server.WaitForConnectionAsync(acceptCancellationToken).ConfigureAwait(false);
                consecutiveAcceptFailures = 0;
                await HandleClientSafelyAsync(server, handlerCancellationToken).ConfigureAwait(false);
                server = null; // Handler owns and disposes the connected stream.
            }
            catch (OperationCanceledException) when (acceptCancellationToken.IsCancellationRequested)
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                break;
            }
            catch (IOException exception)
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                consecutiveAcceptFailures++;
                if (consecutiveAcceptFailures > _options.MaximumConsecutiveAcceptFailures)
                {
                    throw new IOException(
                        "The Broker application-exit listener exceeded its bounded transient-failure budget.",
                        exception);
                }

                var delay = BrokerHost.AcceptRetryDelay(consecutiveAcceptFailures, _options);
                TryReport(
                    "APP_EXIT_PIPE_ACCEPT_RETRY",
                    $"Transient application-exit listener failure {consecutiveAcceptFailures}/{_options.MaximumConsecutiveAcceptFailures}; retrying in {delay.TotalMilliseconds:0} ms: {exception.Message}");
                try
                {
                    await Task.Delay(delay, acceptCancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (acceptCancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    private async Task HandleClientSafelyAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidDataException or JsonException)
        {
            TryReport("APP_EXIT_PIPE_CLIENT_FAILED", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var handlerDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handlerDeadline.CancelAfter(_options.HandlerTimeout);
        await using (pipe.ConfigureAwait(false))
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            BrokerAppExitShutdownRequest? request = null;
            BrokerAppExitShutdownResponse response;
            var accepted = false;
            try
            {
                request = await ReadRequestWithDeadlineAsync(reader, handlerDeadline.Token).ConfigureAwait(false);
                response = Validate(request);
                accepted = response.Success;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                response = new BrokerAppExitShutdownResponse(
                    ContractVersions.BrokerAppExitPipe,
                    request?.RequestId ?? Guid.Empty,
                    false,
                    "BROKER_APP_EXIT_REQUEST_TIMEOUT",
                    "The Broker application-exit request exceeded its bounded processing deadline.");
            }
            catch (TimeoutException)
            {
                response = new BrokerAppExitShutdownResponse(
                    ContractVersions.BrokerAppExitPipe,
                    request?.RequestId ?? Guid.Empty,
                    false,
                    "BROKER_APP_EXIT_REQUEST_TIMEOUT",
                    "The Broker application-exit client did not complete its request within the bounded deadline.");
            }
            catch (Exception exception)
            {
                response = new BrokerAppExitShutdownResponse(
                    ContractVersions.BrokerAppExitPipe,
                    request?.RequestId ?? Guid.Empty,
                    false,
                    "BROKER_APP_EXIT_REQUEST_FAILED",
                    exception.Message);
            }

            try
            {
                // This write is intentionally complete before accepting the
                // shutdown. The caller must receive acknowledgement before the
                // primary listener drains and runtime cancellation begins.
                await WriteResponseWithDeadlineAsync(writer, response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or ObjectDisposedException)
            {
                TryReport("APP_EXIT_PIPE_RESPONSE_FAILED", $"{exception.GetType().Name}: {exception.Message}");
                return;
            }

            if (accepted)
            {
                _onShutdownAccepted();
            }
        }
    }

    private static BrokerAppExitShutdownResponse Validate(BrokerAppExitShutdownRequest request)
    {
        if (request.ProtocolVersion != ContractVersions.BrokerAppExitPipe)
        {
            return new BrokerAppExitShutdownResponse(
                ContractVersions.BrokerAppExitPipe,
                request.RequestId,
                false,
                "BROKER_APP_EXIT_PROTOCOL_MISMATCH",
                "The Broker application-exit protocol version is incompatible.");
        }

        if (request.RequestId == Guid.Empty)
        {
            return new BrokerAppExitShutdownResponse(
                ContractVersions.BrokerAppExitPipe,
                request.RequestId,
                false,
                "BROKER_APP_EXIT_REQUEST_INVALID",
                "The Broker application-exit request identifier is required.");
        }

        return new BrokerAppExitShutdownResponse(
            ContractVersions.BrokerAppExitPipe,
            request.RequestId,
            true);
    }

    private async ValueTask<BrokerAppExitShutdownRequest> ReadRequestWithDeadlineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ClientReadTimeout);
        try
        {
            var line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || line.Length > 64 * 1024)
            {
                throw new InvalidDataException("Broker application-exit request is empty or too large.");
            }

            return JsonSerializer.Deserialize<BrokerAppExitShutdownRequest>(line, JsonOptions)
                   ?? throw new InvalidDataException("Broker application-exit request is invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Broker application-exit client request read timed out.");
        }
    }

    private async ValueTask WriteResponseWithDeadlineAsync(
        StreamWriter writer,
        BrokerAppExitShutdownResponse response,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ClientWriteTimeout);
        try
        {
            var json = JsonSerializer.Serialize(response, JsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Broker application-exit response write timed out.");
        }
    }

    private void TryReport(string code, string message)
    {
        try
        {
            _diagnosticSink(code, message);
        }
        catch
        {
            // Diagnostic sinks are fail-soft at every Broker boundary.
        }
    }
}
