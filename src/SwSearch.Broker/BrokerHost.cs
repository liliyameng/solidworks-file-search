using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SwSearch.Contracts;
using SwSearch.Indexer;
using SwSearch.PropertyConfig;

namespace SwSearch.Broker;

internal sealed record BrokerHostOptions(
    TimeSpan ClientReadTimeout,
    TimeSpan ClientWriteTimeout,
    TimeSpan HandlerTimeout,
    TimeSpan ShutdownDrainTimeout,
    TimeSpan ForcedDrainGrace,
    int MaximumConsecutiveAcceptFailures,
    TimeSpan InitialAcceptRetryDelay,
    TimeSpan MaximumAcceptRetryDelay,
    TimeSpan? IdleExitDelay = null)
{
    internal static BrokerHostOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(250),
        8,
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(500));

    internal void Validate()
    {
        if (ClientReadTimeout <= TimeSpan.Zero ||
            ClientWriteTimeout <= TimeSpan.Zero ||
            HandlerTimeout < ClientReadTimeout ||
            ShutdownDrainTimeout <= TimeSpan.Zero ||
            ForcedDrainGrace <= TimeSpan.Zero ||
            MaximumConsecutiveAcceptFailures < 1 ||
            InitialAcceptRetryDelay <= TimeSpan.Zero ||
            MaximumAcceptRetryDelay < InitialAcceptRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(BrokerHostOptions));
        }
        if (IdleExitDelay is { } idleExit && idleExit < TimeSpan.FromSeconds(30))
        {
            // A too-short delay would let an idle exit race the startup catch-up
            // and client reconnects, producing visible status churn.
            throw new ArgumentOutOfRangeException(
                nameof(BrokerHostOptions),
                "IdleExitDelay must be at least 30 seconds.");
        }
    }
}

internal sealed class BrokerHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string DrainingErrorCode = "BROKER_DRAINING";

    private readonly IndexingRuntime _runtime;
    private readonly PropertyCardConfigProvider _propertyConfig;
    private readonly Action<string, string> _diagnosticSink;
    private readonly BrokerHostOptions _options;
    private readonly Func<NamedPipeServerStream> _serverFactory;
    private readonly Func<NamedPipeServerStream> _appExitServerFactory;
    private readonly CancellationTokenSource _stopAccepting = new();
    private readonly CancellationTokenSource _forceHandlers = new();
    private readonly CancellationTokenSource _runtimeLifetime = new();
    private Task? _background;
    private Task? _appExitListener;
    private Task? _idleExitWatchdog;
    private DateTimeOffset _lastClientActivityUtc;
    private DateTimeOffset? _idleSinceUtc;
    private int _draining;
    private int _runStarted;
    private int _disposed;

    internal BrokerHost(
        IndexingRuntime runtime,
        PropertyCardConfigProvider propertyConfig,
        Action<string, string>? diagnosticSink = null,
        BrokerHostOptions? options = null,
        Func<NamedPipeServerStream>? serverFactory = null,
        Func<NamedPipeServerStream>? appExitServerFactory = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _propertyConfig = propertyConfig ?? throw new ArgumentNullException(nameof(propertyConfig));
        _diagnosticSink = diagnosticSink ?? (static (_, _) => { });
        _options = options ?? BrokerHostOptions.Default;
        _options.Validate();
        _serverFactory = serverFactory ?? CreateServer;
        _appExitServerFactory = appExitServerFactory ?? CreateAppExitServer;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("The Broker host can only be run once.");
        }

        using var acceptLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopAccepting.Token);
        using var handlerLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _forceHandlers.Token);
        using var appExitHandlerLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            handlerLifetime.Token,
            acceptLifetime.Token);
        using var runtimeRegistration = cancellationToken.Register(_runtimeLifetime.Cancel);
        _appExitListener = new BrokerAppExitPipeHost(
                _appExitServerFactory,
                _options,
                BeginDrainingForApplicationExit,
                _diagnosticSink)
            .RunAsync(acceptLifetime.Token, appExitHandlerLifetime.Token);
        ObserveAppExitListenerFailure(_appExitListener);
        _background = Task.Run(
            () => _runtime.RunBackgroundAsync(_runtimeLifetime.Token),
            CancellationToken.None);
        ObserveBackgroundFailure(_background);
        _lastClientActivityUtc = DateTimeOffset.UtcNow;
        if (_options.IdleExitDelay is { } idleExitDelay)
        {
            _idleExitWatchdog = Task.Run(
                () => RunIdleExitWatchdogAsync(idleExitDelay, acceptLifetime.Token),
                CancellationToken.None);
        }
        var clients = new HashSet<Task>();
        var consecutiveAcceptFailures = 0;
        try
        {
            while (!acceptLifetime.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = _serverFactory();
                    await server.WaitForConnectionAsync(acceptLifetime.Token).ConfigureAwait(false);
                    _lastClientActivityUtc = DateTimeOffset.UtcNow;
                    _idleSinceUtc = null;
                }
                catch (OperationCanceledException) when (acceptLifetime.IsCancellationRequested)
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
                            "The Broker pipe listener exceeded its bounded transient-failure budget.",
                            exception);
                    }

                    var delay = AcceptRetryDelay(consecutiveAcceptFailures, _options);
                    TryReport(
                        "PIPE_ACCEPT_RETRY",
                        $"Transient listener failure {consecutiveAcceptFailures}/{_options.MaximumConsecutiveAcceptFailures}; retrying in {delay.TotalMilliseconds:0} ms: {exception.Message}");
                    await Task.Delay(delay, acceptLifetime.Token).ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    if (server is not null)
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }

                    throw;
                }

                consecutiveAcceptFailures = 0;
                var client = HandleClientSafelyAsync(server, handlerLifetime.Token);
                ObserveCompletedClients(clients);
                clients.RemoveWhere(static task => task.IsCompleted);
                clients.Add(client);
            }
        }
        catch (OperationCanceledException) when (acceptLifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _stopAccepting.Cancel();
            await ObserveAppExitListenerWithinDeadlineAsync().ConfigureAwait(false);
            await DrainClientsAsync(clients).ConfigureAwait(false);
            _runtimeLifetime.Cancel();
            await ObserveBackgroundWithinDeadlineAsync().ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreateServer() => new(
        ContractVersions.BrokerPipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static NamedPipeServerStream CreateAppExitServer() => new(
        ContractVersions.BrokerAppExitPipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunIdleExitWatchdogAsync(TimeSpan idleExitDelay, CancellationToken acceptLifetime)
    {
        // The resident footprint contract: once indexing has fully drained and no
        // client has talked to us for the whole window, the Broker exits quietly.
        // Any client (App restore, taskpane search, property viewer, installer,
        // autostart) cold-starts it again on demand; the exit reuses the normal
        // drain path so in-flight requests still complete.
        var pollInterval = TimeSpan.FromSeconds(15);
        while (!acceptLifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, acceptLifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (acceptLifetime.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastClientActivityUtc < idleExitDelay)
                {
                    _idleSinceUtc = null;
                    continue;
                }
                if (!await _runtime.IsIdleForBackgroundExitAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    _idleSinceUtc = null;
                    continue;
                }
                _idleSinceUtc ??= now;
                if (now - _idleSinceUtc < idleExitDelay)
                {
                    continue;
                }

                TryReport(
                    "BROKER_IDLE_EXIT",
                    $"No client activity and no pending work for {idleExitDelay.TotalMinutes:0} min; exiting on demand.");
                _stopAccepting.Cancel();
                return;
            }
            catch (OperationCanceledException) when (acceptLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The idle check must never take the Broker down; retry next tick.
                TryReport("IDLE_EXIT_CHECK_FAILED", exception.GetType().Name);
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
            TryReport("PIPE_CLIENT_FAILED", $"{exception.GetType().Name}: {exception.Message}");
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
            BrokerRequest? request = null;
            BrokerResponse response;
            try
            {
                request = await ReadRequestWithDeadlineAsync(reader, handlerDeadline.Token).ConfigureAwait(false);
                if (request.Operation == BrokerOperation.BeginShutdownForUpdate)
                {
                    var challenge = ShutdownForUpdateIdentity.CreateChallenge(request.ShutdownChallengeRequest);
                    response = challenge is null
                        ? Failure(request, "BROKER_SHUTDOWN_IDENTITY_MISMATCH", "Installer shutdown session identity mismatch.")
                        : Success(
                            request,
                            shutdownChallenge: new ShutdownForUpdateChallenge(challenge.ChallengeId));
                    await WriteResponseWithDeadlineAsync(writer, response, handlerDeadline.Token).ConfigureAwait(false);
                    if (challenge is null)
                    {
                        return;
                    }

                    request = await ReadRequestWithDeadlineAsync(reader, handlerDeadline.Token).ConfigureAwait(false);
                    response = await DispatchAsync(request, challenge, handlerDeadline.Token).ConfigureAwait(false);
                }
                else
                {
                    response = await DispatchAsync(request, null, handlerDeadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                response = new BrokerResponse(
                    ContractVersions.BrokerPipe,
                    request?.RequestId ?? Guid.Empty,
                    false,
                    "BROKER_REQUEST_TIMEOUT",
                    "The Broker request exceeded its bounded processing deadline.");
            }
            catch (TimeoutException)
            {
                response = new BrokerResponse(
                    ContractVersions.BrokerPipe,
                    request?.RequestId ?? Guid.Empty,
                    false,
                    "BROKER_REQUEST_TIMEOUT",
                    "The Broker client did not complete its request within the bounded deadline.");
            }
            catch (Exception exception)
            {
                response = new BrokerResponse(
                    ContractVersions.BrokerPipe,
                    Guid.Empty,
                    false,
                    "BROKER_REQUEST_FAILED",
                    exception.Message);
            }

            try
            {
                await WriteResponseWithDeadlineAsync(writer, response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or ObjectDisposedException)
            {
                TryReport("PIPE_RESPONSE_FAILED", $"{exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async ValueTask<BrokerRequest> ReadRequestWithDeadlineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ClientReadTimeout);
        try
        {
            return await ReadRequestAsync(reader, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Broker client request read timed out.");
        }
    }

    private async ValueTask WriteResponseWithDeadlineAsync(
        StreamWriter writer,
        BrokerResponse response,
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
            throw new TimeoutException("The Broker client response write timed out.");
        }
    }

    internal static async ValueTask<BrokerRequest> ReadRequestAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null || line.Length > 1024 * 1024)
        {
            throw new InvalidDataException("Broker request is empty or too large.");
        }

        return JsonSerializer.Deserialize<BrokerRequest>(line, JsonOptions)
               ?? throw new InvalidDataException("Broker request is invalid.");
    }

    private async ValueTask<BrokerResponse> DispatchAsync(
        BrokerRequest request,
        ShutdownForUpdateChallengeBinding? shutdownChallenge,
        CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != ContractVersions.BrokerPipe)
        {
            return Failure(request, "BROKER_PROTOCOL_MISMATCH", "搜索 Broker 协议版本不兼容。");
        }

        if (Volatile.Read(ref _draining) != 0 &&
            request.Operation is not BrokerOperation.BeginShutdownForUpdate and not BrokerOperation.ShutdownForUpdate)
        {
            return Failure(
                request,
                DrainingErrorCode,
                "The Broker is draining for an accepted update shutdown. Reconnect to the replacement Broker.",
                new RuntimeCapability(
                    CapabilityState.Unavailable,
                    DrainingErrorCode,
                    "The search service is restarting for an update."));
        }

        switch (request.Operation)
        {
            case BrokerOperation.Ping:
                return Success(
                    request,
                    capability: _runtime.CurrentCapability,
                    state: await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false));
            case BrokerOperation.Search when request.Query is not null:
                if (request.Query.Mode == SearchMode.Library &&
                    !(request.Query.LibraryRoots?.Any(static root => !string.IsNullOrWhiteSpace(root)) ?? false))
                {
                    var emptyLibraryState = await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        hits: Array.Empty<SearchHit>(),
                        capability: _runtime.CurrentCapability,
                        state: emptyLibraryState,
                        hasMore: false,
                        indexRevision: emptyLibraryState.Revision,
                        pageFingerprint: SearchPagingFingerprint.Create(request.Query),
                        pageReset: request.Query.Offset > 0,
                        totalCount: 0);
                }
                var stateBefore = await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false);
                var fingerprint = SearchPagingFingerprint.Create(request.Query);
                var pageReset = request.Query.Offset > 0 &&
                    (request.Query.ExpectedRevision != stateBefore.Revision ||
                     !string.Equals(request.Query.PageFingerprint, fingerprint, StringComparison.Ordinal));
                var effectiveQuery = request.Query with
                {
                    Offset = pageReset ? 0 : request.Query.Offset,
                    ExpectedRevision = stateBefore.Revision,
                    PageFingerprint = fingerprint,
                };
                var hits = await _runtime.SearchAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
                var stateAfter = await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false);
                if (stateAfter.Revision != stateBefore.Revision)
                {
                    pageReset = true;
                    effectiveQuery = effectiveQuery with { Offset = 0, ExpectedRevision = stateAfter.Revision };
                    hits = await _runtime.SearchAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
                    stateAfter = await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false);
                }
                // The first viewport must be returned as soon as the indexed
                // candidates are available.  A COUNT over a broad property
                // search can be substantially more expensive than its first
                // page, so the desktop client may calculate it from its local
                // read-only cache only after input is idle; it is never part
                // of this pipe round trip.
                // HasMore remains page-based and is therefore immediately
                // usable without an exact total.
                return Success(
                    request,
                    hits,
                    _runtime.CurrentCapability,
                    state: stateAfter,
                    hasMore: hits.Count == effectiveQuery.Limit,
                    indexRevision: stateAfter.Revision,
                    pageFingerprint: fingerprint,
                    pageReset: pageReset);
            case BrokerOperation.Refresh:
                _runtime.NotifyInteractiveActivity();
                _runtime.RequestRefresh();
                return Success(
                    request,
                    capability: new RuntimeCapability(
                        CapabilityState.Available,
                        "REFRESH_QUEUED",
                        "后台刷新已排队；缓存搜索不受影响。"),
                    state: await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false));
            case BrokerOperation.GetProgress:
                return Success(request, progress: await _runtime.GetProgressAsync(cancellationToken).ConfigureAwait(false));
            case BrokerOperation.SetBackgroundPaused when request.Paused.HasValue:
                _runtime.SetManualPause(request.Paused.Value);
                return Success(request, capability: _runtime.CurrentCapability);
            case BrokerOperation.TryGetMetadata when !string.IsNullOrWhiteSpace(request.FullPath):
                return Success(
                    request,
                    metadata: await _runtime.Store.TryGetMetadataAsync(request.FullPath, cancellationToken).ConfigureAwait(false));
            case BrokerOperation.RefreshMetadata when !string.IsNullOrWhiteSpace(request.FullPath):
            {
                var refresh = await _runtime.RefreshMetadataNowWithStatusAsync(request.FullPath, cancellationToken)
                    .ConfigureAwait(false);
                return Success(
                    request,
                    metadata: refresh.Metadata,
                    metadataRefresh: refresh.Status);
            }
            case BrokerOperation.GetPropertyCardConfig:
                return Success(
                    request,
                    propertyCardConfig: await _propertyConfig.GetCurrentAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
            case BrokerOperation.GetState:
                return Success(
                    request,
                    capability: _runtime.CurrentCapability,
                    state: await _runtime.GetStateAsync(cancellationToken).ConfigureAwait(false));
            case BrokerOperation.ShutdownForUpdate when
                ShutdownForUpdateIdentity.MatchesCurrentProcess(request.ShutdownForUpdate, shutdownChallenge):
                BeginDraining(BrokerDrainReason.Update);
                return Success(
                    request,
                    capability: new RuntimeCapability(
                        CapabilityState.Available,
                        "SHUTDOWN_FOR_UPDATE_ACCEPTED",
                        "安装升级关闭请求已接受。"));
            case BrokerOperation.ShutdownForUpdate:
                return Failure(
                    request,
                    "BROKER_SHUTDOWN_IDENTITY_MISMATCH",
                    "安装升级关闭请求与当前 Broker 实例身份不匹配。" );
            case BrokerOperation.BeginShutdownForUpdate:
                return Failure(
                    request,
                    "BROKER_SHUTDOWN_CHALLENGE_REQUIRED",
                    "Installer shutdown challenge must be completed on one connection.");
            default:
                return Failure(request, "BROKER_OPERATION_INVALID", "Broker 操作或参数无效。");
        }
    }

    internal ValueTask<BrokerResponse> DispatchForTestingAsync(
        BrokerRequest request,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(request, null, cancellationToken);

    private static BrokerResponse Success(
        BrokerRequest request,
        IReadOnlyList<SearchHit>? hits = null,
        RuntimeCapability? capability = null,
        RuntimeProgressSnapshot? progress = null,
        DocumentMetadata? metadata = null,
        PropertyCardConfigSnapshot? propertyCardConfig = null,
        BrokerStateSnapshot? state = null,
        ShutdownForUpdateChallenge? shutdownChallenge = null,
        bool? hasMore = null,
        long? indexRevision = null,
        string? pageFingerprint = null,
        bool pageReset = false,
        long? totalCount = null,
        MetadataRefreshStatus? metadataRefresh = null) =>
        new(ContractVersions.BrokerPipe, request.RequestId, true, Hits: hits, Capability: capability, Progress: progress,
            Metadata: metadata, PropertyCardConfig: propertyCardConfig, State: state,
            ShutdownChallenge: shutdownChallenge, HasMore: hasMore, IndexRevision: indexRevision,
            PageFingerprint: pageFingerprint, PageReset: pageReset, TotalCount: totalCount,
            MetadataRefresh: metadataRefresh);

    private static BrokerResponse Failure(
        BrokerRequest request,
        string code,
        string message,
        RuntimeCapability? capability = null) =>
        new(ContractVersions.BrokerPipe, request.RequestId, false, code, message, Capability: capability);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopAccepting.Cancel();
        _forceHandlers.Cancel();
        await ObserveAppExitListenerWithinDeadlineAsync().ConfigureAwait(false);
        _runtimeLifetime.Cancel();
        await ObserveBackgroundWithinDeadlineAsync().ConfigureAwait(false);
        _stopAccepting.Dispose();
        _forceHandlers.Dispose();
        _runtimeLifetime.Dispose();
    }

    internal void BeginDrainingForTesting() => BeginDraining(BrokerDrainReason.Update);

    internal static TimeSpan AcceptRetryDelay(int consecutiveFailure, BrokerHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (consecutiveFailure < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailure));
        }

        var exponent = Math.Min(consecutiveFailure - 1, 20);
        var ticks = options.InitialAcceptRetryDelay.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(ticks, options.MaximumAcceptRetryDelay.Ticks));
    }

    private void BeginDrainingForApplicationExit() => BeginDraining(BrokerDrainReason.ApplicationExit);

    private void BeginDraining(BrokerDrainReason reason)
    {
        if (Interlocked.Exchange(ref _draining, 1) == 0)
        {
            var (code, message) = reason switch
            {
                BrokerDrainReason.Update => (
                    "BROKER_DRAINING",
                    "An authenticated update shutdown was accepted; draining active clients."),
                BrokerDrainReason.ApplicationExit => (
                    "BROKER_APP_EXIT_DRAINING",
                    "An explicit application-exit shutdown was accepted; draining active clients."),
                _ => throw new ArgumentOutOfRangeException(nameof(reason)),
            };
            TryReport(code, message);
            _stopAccepting.Cancel();
        }
    }

    private async Task DrainClientsAsync(HashSet<Task> clients)
    {
        ObserveCompletedClients(clients);
        var remaining = clients.Where(static task => !task.IsCompleted).ToArray();
        if (remaining.Length == 0)
        {
            return;
        }

        var allClients = Task.WhenAll(remaining);
        var gracefulDeadline = Task.Delay(_options.ShutdownDrainTimeout);
        if (await Task.WhenAny(allClients, gracefulDeadline).ConfigureAwait(false) == allClients)
        {
            ObserveTask(allClients, "PIPE_CLIENT_DRAIN_FAILED");
            return;
        }

        TryReport(
            "PIPE_DRAIN_DEADLINE",
            $"Client drain exceeded {_options.ShutdownDrainTimeout.TotalMilliseconds:0} ms; cancelling remaining handlers.");
        _forceHandlers.Cancel();
        var forceDeadline = Task.Delay(_options.ForcedDrainGrace);
        if (await Task.WhenAny(allClients, forceDeadline).ConfigureAwait(false) == allClients)
        {
            ObserveTask(allClients, "PIPE_CLIENT_FORCE_DRAIN_FAILED");
            return;
        }

        TryReport(
            "PIPE_DRAIN_DETACHED",
            $"Handlers remained after the total drain deadline; {remaining.Count(static task => !task.IsCompleted)} task(s) detached.");
    }

    private void ObserveCompletedClients(HashSet<Task> clients)
    {
        foreach (var task in clients.Where(static task => task.IsCompleted))
        {
            ObserveTask(task, "PIPE_CLIENT_FAILED");
        }
    }

    private void ObserveBackgroundFailure(Task background)
    {
        _ = background.ContinueWith(
            task => TryReport(
                "BACKGROUND_FAILED",
                task.Exception?.GetBaseException().Message ?? "The indexing background task failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveAppExitListenerFailure(Task listener)
    {
        _ = listener.ContinueWith(
            task => TryReport(
                "APP_EXIT_PIPE_LISTENER_FAILED",
                task.Exception?.GetBaseException().Message ?? "The application-exit pipe listener failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveAppExitListenerWithinDeadlineAsync()
    {
        var listener = _appExitListener;
        if (listener is null)
        {
            return;
        }

        if (await Task.WhenAny(listener, Task.Delay(_options.ForcedDrainGrace)).ConfigureAwait(false) != listener)
        {
            TryReport(
                "APP_EXIT_PIPE_LISTENER_DETACHED",
                "The application-exit pipe listener exceeded its shutdown observation deadline.");
            return;
        }

        ObserveTask(listener, "APP_EXIT_PIPE_LISTENER_FAILED");
    }

    private async Task ObserveBackgroundWithinDeadlineAsync()
    {
        var background = _background;
        if (background is null)
        {
            return;
        }

        if (await Task.WhenAny(background, Task.Delay(_options.ForcedDrainGrace)).ConfigureAwait(false) != background)
        {
            TryReport("BACKGROUND_DRAIN_DETACHED", "The indexing background task exceeded its shutdown observation deadline.");
            return;
        }

        ObserveTask(background, "BACKGROUND_FAILED");
    }

    private void ObserveTask(Task task, string code)
    {
        if (task.IsFaulted)
        {
            TryReport(code, task.Exception?.GetBaseException().Message ?? "Asynchronous Broker work failed.");
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

    private enum BrokerDrainReason
    {
        Update,
        ApplicationExit,
    }
}
