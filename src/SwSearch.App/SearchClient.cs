using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.App;

public enum SearchResultAvailability
{
    Available,
    CacheTemporarilyUnavailable,
}

public sealed record SearchClientResult(
    IReadOnlyList<SearchHit> Hits,
    RuntimeCapability Capability,
    BrokerStateSnapshot? State = null,
    bool? HasMore = null,
    long? IndexRevision = null,
    string? PageFingerprint = null,
    bool PageReset = false,
    SearchResultAvailability Availability = SearchResultAvailability.Available);

public interface ISearchClient
{
    ValueTask<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}

public interface ISearchCountClient
{
    ValueTask<long> CountAsync(SearchQuery query, CancellationToken cancellationToken);
}

public interface IRefreshableSearchClient
{
    ValueTask<RuntimeCapability> RefreshAsync(CancellationToken cancellationToken);
}

public interface IRuntimeControlClient
{
    ValueTask<RuntimeProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken);
    void SetBackgroundPaused(bool paused);
}

public interface IBrokerStateClient
{
    ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken);
    void RetryConnection();
}

public sealed record BrokerSearchClientOptions(
    TimeSpan InitialConnectTimeout,
    TimeSpan RetryConnectTimeout,
    TimeSpan ResponseTimeout,
    TimeSpan StartupDeadline,
    TimeSpan RetryDelay)
{
    public TimeSpan BackgroundRecoveryDeadline { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMilliseconds(1200);

    public static BrokerSearchClientOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(650),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromMilliseconds(120));
}

internal static class BrokerChildExitCodes
{
    internal const int ExistingHealthyOrSuccess = 0;
    internal const int StartupFailure = 70;
    internal const int TemporaryInstanceFailure = 75;
}

internal enum BrokerConnectionFailureKind
{
    LaunchFailed,
    ConnectTimeout,
    ResponseTimeout,
    ProtocolError,
    ChildExited,
    Draining,
    TransportFailure,
    RecoveryDeadline
}

internal class BrokerConnectionException : IOException
{
    internal BrokerConnectionException(
        BrokerConnectionFailureKind kind,
        string code,
        string message,
        bool retryable,
        Exception? innerException = null,
        int? childExitCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        Retryable = retryable;
        ChildExitCode = childExitCode;
    }

    internal BrokerConnectionFailureKind Kind { get; }
    internal string Code { get; }
    internal bool Retryable { get; }
    internal int? ChildExitCode { get; }
}

internal sealed class BrokerConnectTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : BrokerConnectionException(
        BrokerConnectionFailureKind.ConnectTimeout,
        "BROKER_CONNECT_TIMEOUT",
        $"连接搜索 Broker 管道超时（{timeout.TotalMilliseconds:0} ms）。",
        retryable: true,
        innerException);

internal sealed class BrokerResponseTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : BrokerConnectionException(
        BrokerConnectionFailureKind.ResponseTimeout,
        "BROKER_RESPONSE_TIMEOUT",
        $"已连接搜索 Broker，但服务在 {timeout.TotalSeconds:0.#} 秒内未响应。",
        retryable: true,
        innerException);

internal sealed class BrokerDrainingException(string? message = null)
    : BrokerConnectionException(
        BrokerConnectionFailureKind.Draining,
        "BROKER_DRAINING",
        string.IsNullOrWhiteSpace(message) ? "旧版搜索 Broker 正在退出，后台将自动重连。" : message,
        retryable: true);

internal sealed class BrokerWireProtocolException(string message, Exception? innerException = null)
    : BrokerConnectionException(
        BrokerConnectionFailureKind.ProtocolError,
        "BROKER_RESPONSE_INVALID",
        message,
        retryable: false,
        innerException);

internal interface IBrokerConnector
{
    ValueTask<BrokerResponse> SendAsync(
        string pipeName,
        BrokerRequest request,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken);
}

internal interface IBrokerProcessLauncher
{
    ILaunchedBrokerProcess Launch(ProcessStartInfo startInfo);
}

internal interface ILaunchedBrokerProcess : IDisposable
{
    bool TryGetExitCode(out int exitCode);
}

internal interface IBrokerRecoveryClock
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp);
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed record BrokerRecoveryOutcome(
    bool Connected,
    BrokerConnectionException? Failure,
    int LaunchCount,
    int ConnectAttemptCount);

internal sealed class SystemBrokerRecoveryClock : IBrokerRecoveryClock
{
    internal static SystemBrokerRecoveryClock Instance { get; } = new();

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}

internal sealed class SystemBrokerProcessLauncher : IBrokerProcessLauncher
{
    public ILaunchedBrokerProcess Launch(ProcessStartInfo startInfo)
    {
        try
        {
            var process = Process.Start(startInfo)
                ?? throw new IOException("Windows 未返回搜索 Broker 进程句柄。");
            return new LaunchedBrokerProcess(process);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new BrokerConnectionException(
                BrokerConnectionFailureKind.LaunchFailed,
                "BROKER_LAUNCH_FAILED",
                $"Windows 无法启动搜索 Broker：{exception.Message}",
                retryable: false,
                exception);
        }
    }

    private sealed class LaunchedBrokerProcess(Process process) : ILaunchedBrokerProcess
    {
        public bool TryGetExitCode(out int exitCode)
        {
            try
            {
                if (!process.HasExited)
                {
                    exitCode = default;
                    return false;
                }

                exitCode = process.ExitCode;
                return true;
            }
            catch (InvalidOperationException)
            {
                exitCode = default;
                return false;
            }
        }

        public void Dispose() => process.Dispose();
    }
}

internal sealed class NamedPipeBrokerConnector : IBrokerConnector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<BrokerResponse> SendAsync(
        string pipeName,
        BrokerRequest request,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(connectTimeout);
            try
            {
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrokerConnectTimeoutException(connectTimeout, exception);
            }
        }

        using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseCancellation.CancelAfter(responseTimeout);
        try
        {
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false),
                false,
                4096,
                leaveOpen: true);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(request, JsonOptions).AsMemory(),
                responseCancellation.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(responseCancellation.Token).ConfigureAwait(false)
                ?? throw new BrokerConnectionException(
                    BrokerConnectionFailureKind.TransportFailure,
                    "BROKER_DISCONNECTED",
                    "搜索 Broker 已断开连接。",
                    retryable: true);
            BrokerResponse response;
            try
            {
                response = JsonSerializer.Deserialize<BrokerResponse>(line, JsonOptions)
                    ?? throw new JsonException("Response was null.");
            }
            catch (JsonException exception)
            {
                throw new BrokerWireProtocolException("搜索 Broker 返回无效数据。", exception);
            }

            if (response.ProtocolVersion != ContractVersions.BrokerPipe)
            {
                throw new BrokerProtocolMismatchException(
                    $"搜索服务版本不匹配（客户端 {ContractVersions.BrokerPipe}，服务端 {response.ProtocolVersion}）。请完成升级后重试。");
            }
            if (response.RequestId != request.RequestId)
            {
                throw new BrokerWireProtocolException("搜索 Broker 响应与请求不匹配。");
            }
            if (!response.Success)
            {
                if (string.Equals(response.ErrorCode, "BROKER_PROTOCOL_MISMATCH", StringComparison.Ordinal))
                {
                    throw new BrokerProtocolMismatchException(
                        response.ErrorMessage ?? "搜索服务版本不匹配。请完成升级后重试。");
                }
                if (string.Equals(response.ErrorCode, "BROKER_DRAINING", StringComparison.Ordinal))
                {
                    throw new BrokerDrainingException(response.ErrorMessage);
                }
                throw new BrokerConnectionException(
                    BrokerConnectionFailureKind.TransportFailure,
                    response.ErrorCode ?? "BROKER_REQUEST_FAILED",
                    response.ErrorMessage ?? "搜索 Broker 请求失败。",
                    retryable: false);
            }
            return response;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerResponseTimeoutException(responseTimeout, exception);
        }
    }
}

internal sealed class BrokerConnectionDiagnostics
{
    private const long DefaultMaximumBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumBytes;

    internal BrokerConnectionDiagnostics(string? path = null, long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _path = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
            SwSearchDataPaths.DataDirectory,
            "Logs",
            "broker-connection.log"));
        _maximumBytes = maximumBytes;
    }

    internal string Path => _path;

    // Deliberately opens and closes per entry: external readers (tests, tail
    // tools, support diagnostics) use File.ReadAllText-style sharing, and a
    // persistent write handle would lock them out. These records only occur
    // on connection failure paths, never per query.
    internal void Record(
        string stage,
        BrokerConnectionFailureKind? kind,
        string message,
        int? childExitCode = null)
    {
        try
        {
            var safeStage = Sanitize(stage, 80);
            var safeMessage = Sanitize(message, 800);
            var line =
                $"{DateTimeOffset.UtcNow:O}|{safeStage}|{kind?.ToString() ?? "Info"}|" +
                $"{(childExitCode.HasValue ? childExitCode.Value.ToString() : "-")}|{safeMessage}{Environment.NewLine}";
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var existingLength = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                var reserve = Encoding.UTF8.GetByteCount(line) + 128;
                if (existingLength > _maximumBytes - reserve)
                {
                    File.WriteAllText(
                        _path,
                        $"{DateTimeOffset.UtcNow:O}|LOG_RESET|Info|-|Bounded diagnostic log restarted.{Environment.NewLine}",
                        new UTF8Encoding(false));
                }
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must not block cached search or connection recovery.
        }
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= maximumLength ? text : text[..maximumLength];
    }
}

public sealed class BrokerSearchClient :
    ISearchClient,
    ISearchCountClient,
    IRefreshableSearchClient,
    IRuntimeControlClient,
    IBrokerStateClient,
    IDisposable
{
    private readonly string _brokerPath;
    private readonly string _pipeName;
    private readonly Func<CancellationToken, ValueTask>? _legacyStartBroker;
    private readonly BrokerSearchClientOptions _options;
    private readonly LocalCacheSearchReader _localCacheReader;
    private readonly IBrokerConnector _connector;
    private readonly IBrokerProcessLauncher _processLauncher;
    private readonly IBrokerRecoveryClock _clock;
    private readonly BrokerConnectionDiagnostics _diagnostics;
    private readonly object _recoveryGate = new();
    private readonly SemaphoreSlim _recoverySerial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _recoveryCancellation;
    private Task<BrokerRecoveryOutcome>? _recoveryTask;
    private long _recoveryGeneration;
    private long _foregroundWaitGeneration = -1;
    private BrokerConnectionException? _connectionFailure;
    private int _everConnected;
    private int _recoverySuppressedForExplicitExit;
    private int _disposed;

    public BrokerSearchClient(
        string? brokerPath = null,
        string? pipeName = null,
        Func<CancellationToken, ValueTask>? startBroker = null,
        BrokerSearchClientOptions? options = null,
        LocalCacheSearchReader? localCacheReader = null)
        : this(
            brokerPath,
            pipeName,
            startBroker,
            options ?? BrokerSearchClientOptions.Default,
            localCacheReader ?? new LocalCacheSearchReader(),
            new NamedPipeBrokerConnector(),
            new SystemBrokerProcessLauncher(),
            SystemBrokerRecoveryClock.Instance,
            new BrokerConnectionDiagnostics())
    {
    }

    internal BrokerSearchClient(
        string? brokerPath,
        string? pipeName,
        Func<CancellationToken, ValueTask>? startBroker,
        BrokerSearchClientOptions options,
        LocalCacheSearchReader localCacheReader,
        IBrokerConnector connector,
        IBrokerProcessLauncher processLauncher,
        IBrokerRecoveryClock clock,
        BrokerConnectionDiagnostics diagnostics)
    {
        _brokerPath = System.IO.Path.GetFullPath(
            brokerPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "SwSearch.Broker.exe"));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? ContractVersions.BrokerPipeName : pipeName;
        _legacyStartBroker = startBroker;
        _options = options;
        _localCacheReader = localCacheReader;
        _connector = connector;
        _processLauncher = processLauncher;
        _clock = clock;
        _diagnostics = diagnostics;
        ValidateOptions(_options);
    }

    public async ValueTask<SearchClientResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Offset == 0 &&
            (Volatile.Read(ref _everConnected) == 0 ||
             Volatile.Read(ref _connectionFailure) is not null))
        {
            EnsureRecoveryRound(force: false);
        }

        try
        {
            var page = await _localCacheReader.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var failure = Volatile.Read(ref _connectionFailure);
            return new SearchClientResult(
                page.Hits,
                failure is null
                    ? new RuntimeCapability(CapabilityState.Available, "LOCAL_INDEX_READY", "搜索就绪。")
                    : new RuntimeCapability(
                        CapabilityState.Degraded,
                        failure.Retryable ? "BROKER_RECONNECTING" : failure.Code,
                        $"已显示本地缓存；{failure.Message}"),
                HasMore: page.HasMore,
                PageFingerprint: page.PageFingerprint);
        }
        catch (LocalCacheUnavailableException)
        {
            return new SearchClientResult(
                Array.Empty<SearchHit>(),
                new RuntimeCapability(
                    CapabilityState.Degraded,
                    "LOCAL_INDEX_UNAVAILABLE",
                    "本地文件缓存暂不可用；后台正在尝试恢复搜索服务。"),
                HasMore: false,
                Availability: SearchResultAvailability.CacheTemporarilyUnavailable);
        }
    }

    public ValueTask<long> CountAsync(SearchQuery query, CancellationToken cancellationToken) =>
        _localCacheReader.CountAsync(query, cancellationToken);

    public async ValueTask<RuntimeCapability> RefreshAsync(CancellationToken cancellationToken)
    {
        var response = await SendWithRecoveryAsync(
            new BrokerRequest(ContractVersions.BrokerPipe, Guid.NewGuid(), BrokerOperation.Refresh),
            cancellationToken).ConfigureAwait(false);
        return response.Capability ??
               new RuntimeCapability(CapabilityState.Available, "REFRESH_STARTED", "后台刷新已启动。");
    }

    public async ValueTask<RuntimeProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken)
    {
        var response = await SendWithRecoveryAsync(
            new BrokerRequest(ContractVersions.BrokerPipe, Guid.NewGuid(), BrokerOperation.GetProgress),
            cancellationToken).ConfigureAwait(false);
        return response.Progress ?? throw new BrokerWireProtocolException("Broker 未返回运行状态。");
    }

    public async ValueTask<BrokerStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
    {
        var response = await SendWithRecoveryAsync(
            new BrokerRequest(ContractVersions.BrokerPipe, Guid.NewGuid(), BrokerOperation.GetState),
            cancellationToken).ConfigureAwait(false);
        return response.State ?? throw new BrokerWireProtocolException("Broker 未返回缓存状态。");
    }

    /// <summary>
    /// Passive state probe for the idle monitor: never launches the Broker and
    /// treats an unreachable Broker as the designed quiet state (null) instead
    /// of a connection failure. Explicit user actions own the cold start.
    /// </summary>
    internal async ValueTask<BrokerStateSnapshot?> TryGetStatePassiveAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            var response = await _connector.SendAsync(
                _pipeName,
                new BrokerRequest(ContractVersions.BrokerPipe, Guid.NewGuid(), BrokerOperation.GetState),
                _options.InitialConnectTimeout,
                _options.ResponseTimeout,
                cancellationToken).ConfigureAwait(false);
            return response.State;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is BrokerConnectionException or TimeoutException or IOException)
        {
            return null;
        }
    }

    public void SetBackgroundPaused(bool paused) =>
        _ = SendBestEffortAsync(
            new BrokerRequest(
                ContractVersions.BrokerPipe,
                Guid.NewGuid(),
                BrokerOperation.SetBackgroundPaused,
                Paused: paused));

    internal async ValueTask PingAsync(CancellationToken cancellationToken) =>
        _ = await SendWithRecoveryAsync(
            CreatePingRequest(),
            cancellationToken).ConfigureAwait(false);

    public void RetryConnection()
    {
        ThrowIfDisposed();
        Volatile.Write(ref _connectionFailure, CreateReconnectingFailure());
        EnsureRecoveryRound(force: true);
    }

    /// <summary>
    /// Stops only App-owned recovery work after the user explicitly exits from
    /// the tray. It never enumerates or terminates a process; the dedicated
    /// exit pipe owns graceful Broker shutdown.
    /// </summary>
    internal void SuppressBrokerRecoveryForExplicitExit()
    {
        if (Interlocked.Exchange(ref _recoverySuppressedForExplicitExit, 1) != 0)
        {
            return;
        }

        lock (_recoveryGate)
        {
            _recoveryCancellation?.Cancel();
        }
    }

    public static ProcessStartInfo CreateBrokerStartInfo(string brokerPath)
    {
        var fullPath = System.IO.Path.GetFullPath(brokerPath);
        var info = new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = System.IO.Path.GetDirectoryName(fullPath)!,
        };
        info.ArgumentList.Add("--background");
        return info;
    }

    internal Task<BrokerRecoveryOutcome> StartRecoveryForTestingAsync(bool force = false) =>
        EnsureRecoveryRound(force).Task;

    private async Task SendBestEffortAsync(BrokerRequest request)
    {
        try
        {
            await SendWithRecoveryAsync(request, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Compatibility-only background control must never become an
            // unobserved fire-and-forget exception.
        }
    }

    private async ValueTask<BrokerResponse> SendWithRecoveryAsync(
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        BrokerConnectionException firstFailure;
        try
        {
            var response = await _connector.SendAsync(
                _pipeName,
                request,
                _options.InitialConnectTimeout,
                _options.ResponseTimeout,
                cancellationToken).ConfigureAwait(false);
            MarkConnected();
            return response;
        }
        catch (BrokerProtocolMismatchException exception)
        {
            _diagnostics.Record(
                "PROTOCOL_MISMATCH",
                BrokerConnectionFailureKind.ProtocolError,
                exception.Message);
            Volatile.Write(
                ref _connectionFailure,
                new BrokerConnectionException(
                    BrokerConnectionFailureKind.ProtocolError,
                    "BROKER_PROTOCOL_MISMATCH",
                    exception.Message,
                    retryable: false,
                    exception));
            throw;
        }
        catch (BrokerConnectionException exception) when (!exception.Retryable)
        {
            Volatile.Write(ref _connectionFailure, exception);
            _diagnostics.Record("REQUEST_TERMINAL_FAILURE", exception.Kind, exception.Message);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            var failure = NormalizeFailure(exception);
            Volatile.Write(ref _connectionFailure, failure);
            _diagnostics.Record("REQUEST_ACCESS_DENIED", failure.Kind, failure.Message);
            throw failure;
        }
        catch (Exception exception) when (IsRecoverableTransportFailure(exception))
        {
            firstFailure = NormalizeFailure(exception);
            Volatile.Write(ref _connectionFailure, firstFailure);
            _diagnostics.Record("REQUEST_FAILED", firstFailure.Kind, firstFailure.Message);
        }

        var recovery = EnsureRecoveryRound(force: false);
        if (!TryClaimForegroundWait(recovery.Generation))
        {
            throw Volatile.Read(ref _connectionFailure) ?? firstFailure;
        }

        BrokerRecoveryOutcome outcome;
        try
        {
            outcome = await recovery.Task
                .WaitAsync(_options.StartupDeadline, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var failure = CreateForegroundDeadlineFailure(firstFailure, exception);
            Volatile.Write(ref _connectionFailure, failure);
            _diagnostics.Record("FOREGROUND_DEADLINE", failure.Kind, failure.Message);
            throw failure;
        }

        if (!outcome.Connected)
        {
            throw outcome.Failure ?? firstFailure;
        }

        try
        {
            var response = await _connector.SendAsync(
                _pipeName,
                request,
                _options.RetryConnectTimeout,
                _options.ResponseTimeout,
                cancellationToken).ConfigureAwait(false);
            MarkConnected();
            return response;
        }
        catch (BrokerProtocolMismatchException exception)
        {
            _diagnostics.Record(
                "PROTOCOL_MISMATCH",
                BrokerConnectionFailureKind.ProtocolError,
                exception.Message);
            throw;
        }
        catch (BrokerConnectionException exception) when (!exception.Retryable)
        {
            Volatile.Write(ref _connectionFailure, exception);
            _diagnostics.Record("REQUEST_TERMINAL_FAILURE", exception.Kind, exception.Message);
            throw;
        }
        catch (Exception exception) when (IsRecoverableTransportFailure(exception))
        {
            var failure = NormalizeFailure(exception);
            Volatile.Write(ref _connectionFailure, failure);
            throw failure;
        }
    }

    private RecoveryHandle EnsureRecoveryRound(bool force)
    {
        ThrowIfDisposed();
        if (IsRecoverySuppressedForExplicitExit())
        {
            return CreateExplicitExitRecoveryHandle();
        }
        lock (_recoveryGate)
        {
            if (IsRecoverySuppressedForExplicitExit())
            {
                return CreateExplicitExitRecoveryHandle();
            }
            if (!force && _recoveryTask is { IsCompleted: false } running)
            {
                return new RecoveryHandle(_recoveryGeneration, running);
            }

            if (!force &&
                _recoveryTask is { IsCompleted: true } terminal &&
                Volatile.Read(ref _connectionFailure) is { Retryable: false })
            {
                // Protocol/bootstrap failures require an explicit Retry click.
                // Search keystrokes must not relaunch a known-bad binary.
                return new RecoveryHandle(_recoveryGeneration, terminal);
            }

            if (!force && _recoveryTask is { IsCompletedSuccessfully: true } completed &&
                completed.Result.Connected)
            {
                // The caller's direct request failed after the previous round,
                // so it needs a fresh bounded recovery rather than reusing a
                // stale successful task.
            }

            _recoveryCancellation?.Cancel();
            var generation = ++_recoveryGeneration;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _recoveryCancellation = cancellation;
            Volatile.Write(ref _connectionFailure, CreateReconnectingFailure());
            var task = Task.Run(
                () => RunRecoveryRoundAndCleanupAsync(generation, cancellation),
                CancellationToken.None);
            _recoveryTask = task;
            return new RecoveryHandle(generation, task);
        }
    }

    private async Task<BrokerRecoveryOutcome> RunRecoveryRoundAndCleanupAsync(
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _recoverySerial.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                return await RecoverBrokerAsync(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _recoverySerial.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new BrokerRecoveryOutcome(
                Connected: false,
                new BrokerConnectionException(
                    BrokerConnectionFailureKind.TransportFailure,
                    "BROKER_RECOVERY_SUPERSEDED",
                    "后台重连已由新的重试请求接替。",
                    retryable: true),
                LaunchCount: 0,
                ConnectAttemptCount: 0);
        }
        finally
        {
            lock (_recoveryGate)
            {
                if (_recoveryGeneration == generation &&
                    ReferenceEquals(_recoveryCancellation, cancellation))
                {
                    _recoveryCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task<BrokerRecoveryOutcome> RecoverBrokerAsync(CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        var retryDelay = _options.RetryDelay;
        var connectAttempts = 0;
        var launches = 0;
        BrokerConnectionException lastFailure = CreateReconnectingFailure();
        ILaunchedBrokerProcess? child = null;
        try
        {
            _diagnostics.Record("RECOVERY_START", null, "Background Broker recovery started.");
            while (_clock.GetElapsedTime(started) < _options.BackgroundRecoveryDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsBrokerLaunchSuppressedForExplicitExit())
                {
                    return CreateExplicitExitRecoveryOutcome();
                }
                connectAttempts++;
                try
                {
                    await _connector.SendAsync(
                        _pipeName,
                        CreatePingRequest(),
                        _options.RetryConnectTimeout,
                        _options.ResponseTimeout,
                        cancellationToken).ConfigureAwait(false);
                    MarkConnected();
                    _diagnostics.Record(
                        "RECOVERY_CONNECTED",
                        null,
                        $"Connected after {connectAttempts} attempts and {launches} launches.");
                    return new BrokerRecoveryOutcome(true, null, launches, connectAttempts);
                }
                catch (BrokerProtocolMismatchException exception)
                {
                    var failure = new BrokerConnectionException(
                        BrokerConnectionFailureKind.ProtocolError,
                        "BROKER_PROTOCOL_MISMATCH",
                        exception.Message,
                        retryable: false,
                        exception);
                    Volatile.Write(ref _connectionFailure, failure);
                    _diagnostics.Record("PROTOCOL_MISMATCH", failure.Kind, failure.Message);
                    return new BrokerRecoveryOutcome(false, failure, launches, connectAttempts);
                }
                catch (BrokerConnectionException exception) when (!exception.Retryable)
                {
                    Volatile.Write(ref _connectionFailure, exception);
                    _diagnostics.Record(
                        "RECOVERY_TERMINAL_FAILURE",
                        exception.Kind,
                        exception.Message);
                    return new BrokerRecoveryOutcome(false, exception, launches, connectAttempts);
                }
                catch (UnauthorizedAccessException exception)
                {
                    var failure = NormalizeFailure(exception);
                    Volatile.Write(ref _connectionFailure, failure);
                    _diagnostics.Record(
                        "RECOVERY_ACCESS_DENIED",
                        failure.Kind,
                        failure.Message);
                    return new BrokerRecoveryOutcome(false, failure, launches, connectAttempts);
                }
                catch (Exception exception) when (IsRecoverableTransportFailure(exception))
                {
                    lastFailure = NormalizeFailure(exception);
                    Volatile.Write(ref _connectionFailure, lastFailure);
                    _diagnostics.Record("RECOVERY_RETRY", lastFailure.Kind, lastFailure.Message);
                }

                if (child is not null && child.TryGetExitCode(out var childExitCode))
                {
                    child.Dispose();
                    child = null;
                    var exitFailure = ClassifyChildExit(childExitCode);
                    _diagnostics.Record(
                        "CHILD_EXIT",
                        exitFailure.Kind,
                        exitFailure.Message,
                        childExitCode);
                    if (!exitFailure.Retryable)
                    {
                        Volatile.Write(ref _connectionFailure, exitFailure);
                        return new BrokerRecoveryOutcome(false, exitFailure, launches, connectAttempts);
                    }
                    lastFailure = exitFailure;
                    Volatile.Write(ref _connectionFailure, exitFailure);
                }

                if (child is null && lastFailure.Kind != BrokerConnectionFailureKind.ResponseTimeout)
                {
                    if (IsBrokerLaunchSuppressedForExplicitExit())
                    {
                        return CreateExplicitExitRecoveryOutcome();
                    }
                    try
                    {
                        child = await LaunchBrokerAsync(cancellationToken).ConfigureAwait(false);
                        launches++;
                        _diagnostics.Record(
                            "CHILD_LAUNCHED",
                            null,
                            $"Started owned Broker child attempt {launches}.");
                        if (child.TryGetExitCode(out var immediateExitCode))
                        {
                            child.Dispose();
                            child = null;
                            var exitFailure = ClassifyChildExit(immediateExitCode);
                            _diagnostics.Record(
                                "CHILD_EXIT_IMMEDIATE",
                                exitFailure.Kind,
                                exitFailure.Message,
                                immediateExitCode);
                            if (!exitFailure.Retryable)
                            {
                                Volatile.Write(ref _connectionFailure, exitFailure);
                                return new BrokerRecoveryOutcome(false, exitFailure, launches, connectAttempts);
                            }
                            lastFailure = exitFailure;
                            Volatile.Write(ref _connectionFailure, exitFailure);
                        }
                    }
                    catch (BrokerConnectionException exception)
                    {
                        Volatile.Write(ref _connectionFailure, exception);
                        _diagnostics.Record("LAUNCH_FAILED", exception.Kind, exception.Message);
                        return new BrokerRecoveryOutcome(false, exception, launches, connectAttempts);
                    }
                }

                var remaining = _options.BackgroundRecoveryDeadline - _clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                var delay = Min(retryDelay, remaining);
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                retryDelay = Min(
                    TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 1.75),
                    _options.MaximumRetryDelay);
            }

            var deadlineFailure = CreateBackgroundDeadlineFailure(lastFailure);
            Volatile.Write(ref _connectionFailure, deadlineFailure);
            _diagnostics.Record("RECOVERY_DEADLINE", deadlineFailure.Kind, deadlineFailure.Message);
            return new BrokerRecoveryOutcome(false, deadlineFailure, launches, connectAttempts);
        }
        finally
        {
            // Disposing Process only releases the handle. It never terminates
            // the child and therefore cannot affect an unrelated or surviving
            // Broker instance.
            child?.Dispose();
        }
    }

    private async ValueTask<ILaunchedBrokerProcess> LaunchBrokerAsync(CancellationToken cancellationToken)
    {
        if (IsBrokerLaunchSuppressedForExplicitExit())
        {
            throw CreateExplicitExitRecoveryFailure();
        }
        if (_legacyStartBroker is not null)
        {
            try
            {
                await _legacyStartBroker(cancellationToken).ConfigureAwait(false);
                return UnknownLaunchedBrokerProcess.Instance;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new BrokerConnectionException(
                    BrokerConnectionFailureKind.LaunchFailed,
                    "BROKER_LAUNCH_FAILED",
                    $"Windows 无法启动搜索 Broker：{exception.Message}",
                    retryable: false,
                    exception);
            }
        }

        return _processLauncher.Launch(CreateBrokerStartInfo(_brokerPath));
    }

    private static BrokerConnectionException ClassifyChildExit(int exitCode) => exitCode switch
    {
        BrokerChildExitCodes.ExistingHealthyOrSuccess => new BrokerConnectionException(
            BrokerConnectionFailureKind.ChildExited,
            "BROKER_EXISTING_INSTANCE",
            "已检测到现有 Broker，后台正在等待其管道可用。",
            retryable: true,
            childExitCode: exitCode),
        BrokerChildExitCodes.TemporaryInstanceFailure => new BrokerConnectionException(
            BrokerConnectionFailureKind.ChildExited,
            "BROKER_OWNER_RELEASING",
            "旧版 Broker 仍在退出并持有单实例门，后台将继续有界重试。",
            retryable: true,
            childExitCode: exitCode),
        BrokerChildExitCodes.StartupFailure => new BrokerConnectionException(
            BrokerConnectionFailureKind.ChildExited,
            "BROKER_STARTUP_FAILED",
            "本次启动的搜索 Broker 报告初始化失败（退出码 70）。",
            retryable: false,
            childExitCode: exitCode),
        _ => new BrokerConnectionException(
            BrokerConnectionFailureKind.ChildExited,
            "BROKER_CHILD_EXITED",
            $"本次启动的搜索 Broker 异常退出（退出码 {exitCode}）。",
            retryable: false,
            childExitCode: exitCode),
    };

    private static BrokerConnectionException NormalizeFailure(Exception exception) => exception switch
    {
        BrokerConnectionException classified => classified,
        TimeoutException timeout => new BrokerConnectionException(
            BrokerConnectionFailureKind.ConnectTimeout,
            "BROKER_CONNECT_TIMEOUT",
            $"连接搜索 Broker 管道超时：{timeout.Message}",
            retryable: true,
            timeout),
        IOException io => new BrokerConnectionException(
            BrokerConnectionFailureKind.TransportFailure,
            "BROKER_TRANSPORT_FAILURE",
            $"搜索 Broker 通信失败：{io.Message}",
            retryable: true,
            io),
        UnauthorizedAccessException access => new BrokerConnectionException(
            BrokerConnectionFailureKind.TransportFailure,
            "BROKER_ACCESS_DENIED",
            $"无权连接搜索 Broker：{access.Message}",
            retryable: false,
            access),
        _ => new BrokerConnectionException(
            BrokerConnectionFailureKind.TransportFailure,
            "BROKER_CONNECTION_FAILURE",
            exception.Message,
            retryable: true,
            exception),
    };

    private BrokerConnectionException CreateForegroundDeadlineFailure(
        BrokerConnectionException firstFailure,
        Exception innerException) =>
        firstFailure.Kind switch
        {
            BrokerConnectionFailureKind.ResponseTimeout => new BrokerConnectionException(
                BrokerConnectionFailureKind.ResponseTimeout,
                "BROKER_RESPONSE_TIMEOUT",
                $"已连接搜索 Broker，但服务在 {_options.StartupDeadline.TotalSeconds:0.#} 秒内仍未响应；后台将继续自动重连。",
                retryable: true,
                innerException),
            BrokerConnectionFailureKind.Draining => new BrokerConnectionException(
                BrokerConnectionFailureKind.Draining,
                "BROKER_DRAINING",
                "旧版搜索 Broker 正在退出；已显示本地缓存，后台将继续自动重连。",
                retryable: true,
                innerException),
            _ => new BrokerConnectionException(
                BrokerConnectionFailureKind.ConnectTimeout,
                "BROKER_CONNECT_TIMEOUT",
                $"无法在 {_options.StartupDeadline.TotalSeconds:0.#} 秒内连接搜索 Broker；已显示本地缓存，后台将继续自动重连。",
                retryable: true,
                innerException),
        };

    private BrokerConnectionException CreateBackgroundDeadlineFailure(BrokerConnectionException lastFailure) =>
        lastFailure.Kind switch
        {
            BrokerConnectionFailureKind.ResponseTimeout => new BrokerConnectionException(
                BrokerConnectionFailureKind.RecoveryDeadline,
                "BROKER_RESPONSE_DEADLINE",
                $"已连接搜索 Broker 管道，但在 {_options.BackgroundRecoveryDeadline.TotalSeconds:0.#} 秒恢复期内始终未响应。",
                retryable: true,
                lastFailure),
            BrokerConnectionFailureKind.ChildExited
                when lastFailure.ChildExitCode == BrokerChildExitCodes.TemporaryInstanceFailure =>
                new BrokerConnectionException(
                    BrokerConnectionFailureKind.RecoveryDeadline,
                    "BROKER_OWNER_RELEASING",
                    $"旧版 Broker 在 {_options.BackgroundRecoveryDeadline.TotalSeconds:0.#} 秒恢复期内仍未释放单实例门。",
                    retryable: true,
                    lastFailure,
                    BrokerChildExitCodes.TemporaryInstanceFailure),
            _ => new BrokerConnectionException(
                BrokerConnectionFailureKind.RecoveryDeadline,
                "BROKER_RECOVERY_DEADLINE",
                $"后台在 {_options.BackgroundRecoveryDeadline.TotalSeconds:0.#} 秒内仍无法连接搜索 Broker。",
                retryable: true,
                lastFailure),
        };

    private static BrokerConnectionException CreateReconnectingFailure() => new(
        BrokerConnectionFailureKind.TransportFailure,
        "BROKER_RECONNECTING",
        "后台正在连接搜索服务。",
        retryable: true);

    private bool IsRecoverySuppressedForExplicitExit() =>
        Volatile.Read(ref _recoverySuppressedForExplicitExit) != 0;

    // This probe runs only on the recovery worker, never on the WPF/UI path.
    // The durable marker is read/cleared during ordinary App lifecycle work;
    // the Broker launch boundary must remain a pure in-memory check so it can
    // never perform synchronous file I/O while starting a recovery child.
    private bool IsBrokerLaunchSuppressedForExplicitExit()
    {
        return IsRecoverySuppressedForExplicitExit() ||
               BrokerAppExitSessionMarker.IsCurrentProcessLaunchSuppressed();
    }

    private RecoveryHandle CreateExplicitExitRecoveryHandle()
    {
        var outcome = CreateExplicitExitRecoveryOutcome();
        Volatile.Write(ref _connectionFailure, outcome.Failure);
        return new RecoveryHandle(
            Volatile.Read(ref _recoveryGeneration),
            Task.FromResult(outcome));
    }

    private static BrokerRecoveryOutcome CreateExplicitExitRecoveryOutcome() => new(
        Connected: false,
        Failure: CreateExplicitExitRecoveryFailure(),
        LaunchCount: 0,
        ConnectAttemptCount: 0);

    private static BrokerConnectionException CreateExplicitExitRecoveryFailure() => new(
        BrokerConnectionFailureKind.TransportFailure,
        "BROKER_APP_EXITED",
        "The desktop search application was explicitly exited from the tray. Start it again to resume Broker activity.",
        retryable: false);

    private static bool IsRecoverableTransportFailure(Exception exception) =>
        exception is BrokerConnectionException or IOException or TimeoutException or UnauthorizedAccessException;

    private static BrokerRequest CreatePingRequest() =>
        new(ContractVersions.BrokerPipe, Guid.NewGuid(), BrokerOperation.Ping);

    private bool TryClaimForegroundWait(long generation)
    {
        lock (_recoveryGate)
        {
            if (_foregroundWaitGeneration == generation)
            {
                return false;
            }
            _foregroundWaitGeneration = generation;
            return true;
        }
    }

    private void MarkConnected()
    {
        Interlocked.Exchange(ref _everConnected, 1);
        Volatile.Write(ref _connectionFailure, null);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static void ValidateOptions(BrokerSearchClientOptions options)
    {
        if (options.InitialConnectTimeout <= TimeSpan.Zero ||
            options.RetryConnectTimeout <= TimeSpan.Zero ||
            options.ResponseTimeout <= TimeSpan.Zero ||
            options.StartupDeadline <= TimeSpan.Zero ||
            options.RetryDelay <= TimeSpan.Zero ||
            options.BackgroundRecoveryDeadline <= options.StartupDeadline ||
            options.MaximumRetryDelay < options.RetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        lock (_recoveryGate)
        {
            _recoveryCancellation?.Cancel();
        }
        _lifetime.Dispose();
    }

    private sealed record RecoveryHandle(long Generation, Task<BrokerRecoveryOutcome> Task);

    private sealed class UnknownLaunchedBrokerProcess : ILaunchedBrokerProcess
    {
        internal static UnknownLaunchedBrokerProcess Instance { get; } = new();
        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = default;
            return false;
        }
        public void Dispose()
        {
        }
    }
}
