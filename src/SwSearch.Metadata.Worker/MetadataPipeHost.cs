using System.IO.Pipes;
using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed class WorkerRestartLatch
{
    private int _required;

    internal bool Required => Volatile.Read(ref _required) != 0;

    internal void Require() => Interlocked.Exchange(ref _required, 1);
}

internal readonly record struct MetadataRequestDeadline(long DeadlineTimestamp)
{
    internal static MetadataRequestDeadline Start(int requestedTimeoutMilliseconds)
    {
        var timeout = requestedTimeoutMilliseconds <= 0
            ? MetadataRequestProcessor.DefaultTimeoutMilliseconds
            : Math.Min(
                requestedTimeoutMilliseconds,
                MetadataRequestProcessor.MaximumTimeoutMilliseconds);
        var ticks = checked((long)Math.Ceiling(
            timeout * (double)System.Diagnostics.Stopwatch.Frequency / 1_000d));
        return new MetadataRequestDeadline(
            checked(System.Diagnostics.Stopwatch.GetTimestamp() + ticks));
    }

    internal int RemainingMilliseconds(long? nowTimestamp = null)
    {
        var remainingTicks = DeadlineTimestamp -
                             (nowTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp());
        if (remainingTicks <= 0)
        {
            return 0;
        }

        return Math.Max(
            1,
            (int)Math.Min(
                MetadataRequestProcessor.MaximumTimeoutMilliseconds,
                Math.Floor(remainingTicks * 1_000d /
                           System.Diagnostics.Stopwatch.Frequency)));
    }
}

public sealed class MetadataPipeHost : IAsyncDisposable
{
    private readonly MetadataRequestProcessor _processor;
    private readonly IMetadataCapabilityProbe _capabilityProbe;
    private readonly string _pipeName;
    private readonly Guid _workerGenerationNonce;
    private readonly EventWaitHandle _instanceSignal;
    private readonly SemaphoreSlim _interactiveExtractionGate;
    private readonly SemaphoreSlim _backgroundExtractionGate;
    private int _inflightExtractions;
    private bool _disposed;

    internal MetadataPipeHost(
        MetadataRequestProcessor processor,
        IMetadataCapabilityProbe capabilityProbe,
        string pipeName,
        int maximumConcurrentExtractions = 2,
        Guid workerGenerationNonce = default)
    {
        _processor = processor;
        _capabilityProbe = capabilityProbe;
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        _workerGenerationNonce = workerGenerationNonce;
        if (maximumConcurrentExtractions is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentExtractions));
        }

        _interactiveExtractionGate = new SemaphoreSlim(1, 1);
        _backgroundExtractionGate = maximumConcurrentExtractions == 1
            ? _interactiveExtractionGate
            : new SemaphoreSlim(
                maximumConcurrentExtractions - 1,
                maximumConcurrentExtractions - 1);
        _instanceSignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            WorkerInstanceSignal.Name(_pipeName),
            out var createdNew);
        if (!createdNew)
        {
            _instanceSignal.Dispose();
            throw new IOException("该用户的元数据工作进程已经在运行。");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stopAccepting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connections = new HashSet<Task>();
        try
        {
            while (!stopAccepting.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
                    inBufferSize: PipeFrame.MaximumRequestBytes,
                    outBufferSize: PipeFrame.MaximumResponseBytes);

                try
                {
                    await pipe.WaitForConnectionAsync(stopAccepting.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopAccepting.IsCancellationRequested)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var connection = HandleConnectionAsync(pipe, stopAccepting, stopAccepting.Token);
                connections.Add(connection);
                connections.RemoveWhere(static task => task.IsCompleted);
            }
        }
        finally
        {
            if (connections.Count > 0)
            {
                try
                {
                    await Task.WhenAll(connections)
                        .WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A poisoned extraction can ignore cooperative cancellation.
                    // Process teardown closes any connection that did not unwind.
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _instanceSignal.Dispose();
        if (!ReferenceEquals(_backgroundExtractionGate, _interactiveExtractionGate))
        {
            _backgroundExtractionGate.Dispose();
        }
        _interactiveExtractionGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource stopAccepting,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var restart = new WorkerRestartLatch();
            try
            {
                var requiresRestart = await ProcessConnectionAsync(
                        pipe,
                        restart,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (requiresRestart)
                {
                    restart.Require();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (IOException)
            {
                // A client may close while its request is queued. The listener
                // remains healthy for all other clients.
            }
            catch (Exception exception) when (
                exception is MetadataWorkerException or JsonException or EndOfStreamException)
            {
                // Invalid per-client input must not stop the shared listener.
            }
            finally
            {
                // A response write may fail after the extractor has timed out.
                // The poison decision therefore outlives response delivery.
                if (restart.Required)
                {
                    stopAccepting.Cancel();
                }
            }
        }
    }

    private async ValueTask<bool> ProcessConnectionAsync(
        NamedPipeServerStream pipe,
        WorkerRestartLatch restart,
        CancellationToken cancellationToken)
    {
        PipeRequestEnvelope? request = null;
        PipeResponseEnvelope response;
        var requiresRestart = false;
        ShutdownForUpdateChallengeBinding? shutdownChallenge = null;
        try
        {
            request = await PipeFrame.ReadJsonAsync<PipeRequestEnvelope>(
                    pipe,
                    PipeFrame.MaximumRequestBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (request.ProtocolVersion == ContractVersions.MetadataPipe &&
                request.Operation.Equals("beginShutdownForUpdate", StringComparison.Ordinal))
            {
                shutdownChallenge = ShutdownForUpdateIdentity.CreateChallenge(request.ShutdownChallengeRequest);
                response = shutdownChallenge is null
                    ? ErrorEnvelope(request.RequestId, MetadataErrorCodes.InvalidRequest, retryable: false)
                    : new PipeResponseEnvelope(
                        ContractVersions.MetadataPipe,
                        request.RequestId,
                        ShutdownChallenge: new ShutdownForUpdateChallenge(shutdownChallenge.ChallengeId));
                await PipeFrame.WriteJsonAsync(
                        pipe,
                        response,
                        PipeFrame.MaximumResponseBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (shutdownChallenge is null)
                {
                    return false;
                }

                request = await PipeFrame.ReadJsonAsync<PipeRequestEnvelope>(
                        pipe,
                        PipeFrame.MaximumRequestBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.ProtocolVersion != ContractVersions.MetadataPipe)
            {
                response = ErrorEnvelope(
                    request.RequestId,
                    MetadataErrorCodes.ProtocolVersionUnsupported,
                    retryable: false);
            }
            else if (request.Operation.Equals("probe", StringComparison.Ordinal))
            {
                var probed = await _capabilityProbe.ProbeAsync(cancellationToken)
                    .ConfigureAwait(false);
                var details = new Dictionary<string, string>(
                    probed.Details ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal)
                {
                    ["pipe"] = WorkerPipeIdentity.Hash(_pipeName),
                    ["generation"] = _workerGenerationNonce == Guid.Empty ? "legacy" : "bound",
                    ["inflight"] = Volatile.Read(ref _inflightExtractions)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                var capability = probed with { Details = details };
                response = new PipeResponseEnvelope(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    Capability: capability);
            }
            else if (request.Operation.Equals("extract", StringComparison.Ordinal) &&
                     request.MetadataRequest is not null)
            {
                var deadline = MetadataRequestDeadline.Start(
                    request.MetadataRequest.TimeoutMilliseconds);
                using var requestDeadlineSource = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(Math.Max(
                        1,
                        deadline.RemainingMilliseconds())));
                using var requestSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    requestDeadlineSource.Token);
                var extractionGate = request.MetadataRequest.WorkClass == MetadataWorkClass.Interactive
                    ? _interactiveExtractionGate
                    : _backgroundExtractionGate;
                RequestProcessingResult? processed = null;
                var gateAcquired = false;
                try
                {
                    try
                    {
                        await extractionGate.WaitAsync(requestSource.Token).ConfigureAwait(false);
                        gateAcquired = true;
                    }
                    catch (OperationCanceledException) when (
                        requestDeadlineSource.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        processed = QueueDeadlineExceeded(request.MetadataRequest);
                    }

                    if (processed is null)
                    {
                        var remaining = deadline.RemainingMilliseconds();
                        if (remaining <= 0)
                        {
                            processed = QueueDeadlineExceeded(request.MetadataRequest);
                        }
                        else
                        {
                            Interlocked.Increment(ref _inflightExtractions);
                            try
                            {
                                processed = await _processor.ProcessAsync(
                                        request.MetadataRequest with
                                        {
                                            TimeoutMilliseconds = remaining,
                                        },
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _inflightExtractions);
                            }
                        }
                    }
                }
                finally
                {
                    if (gateAcquired)
                    {
                        extractionGate.Release();
                    }
                }
                requiresRestart = processed.RequiresWorkerRestart;
                if (requiresRestart)
                {
                    restart.Require();
                }
                response = new PipeResponseEnvelope(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    MetadataResponse: processed.Response with
                    {
                        WorkerGenerationNonce = _workerGenerationNonce,
                    });
            }
            else if (request.Operation.Equals("shutdownForUpdate", StringComparison.Ordinal) &&
                     ShutdownForUpdateIdentity.MatchesCurrentProcess(
                         request.ShutdownForUpdate,
                         shutdownChallenge))
            {
                requiresRestart = true;
                restart.Require();
                response = new PipeResponseEnvelope(
                    ContractVersions.MetadataPipe,
                    request.RequestId,
                    Capability: new RuntimeCapability(
                        CapabilityState.Available,
                        "SHUTDOWN_FOR_UPDATE_ACCEPTED",
                        "安装升级关闭请求已接受。"));
            }
            else if (request.Operation.Equals("shutdownForUpdate", StringComparison.Ordinal))
            {
                response = ErrorEnvelope(
                    request.RequestId,
                    MetadataErrorCodes.InvalidRequest,
                    retryable: false);
            }
            else
            {
                response = ErrorEnvelope(
                    request.RequestId,
                    MetadataErrorCodes.InvalidRequest,
                    retryable: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is MetadataWorkerException or JsonException or EndOfStreamException)
        {
            response = ErrorEnvelope(
                request?.RequestId ?? Guid.Empty,
                exception is MetadataWorkerException workerException
                    ? workerException.Code
                    : MetadataErrorCodes.FrameInvalid,
                retryable: false);
        }

        if (pipe.IsConnected)
        {
            await PipeFrame.WriteJsonAsync(
                    pipe,
                    response,
                    PipeFrame.MaximumResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return requiresRestart;
    }

    private static RequestProcessingResult QueueDeadlineExceeded(MetadataRequest request) =>
        new(
            new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                Metadata: null,
                MetadataErrorCodes.Timeout,
                MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.Timeout),
                Retryable: true,
                PipelineLeaseId: request.PipelineLeaseId,
                Outcome: MetadataPipelineOutcome.Failed),
            RequiresWorkerRestart: false);

    private static PipeResponseEnvelope ErrorEnvelope(
        Guid requestId,
        string code,
        bool retryable) =>
        new(
            ContractVersions.MetadataPipe,
            requestId,
            ErrorCode: code,
            ChineseErrorMessage: MetadataErrorCodes.ChineseMessage(code),
            Retryable: retryable);
}
