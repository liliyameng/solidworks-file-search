using System.Collections.Concurrent;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

/// <summary>
/// Broker-owned, single-consumer metadata engine. Parsing runs on one
/// low-priority STA thread and never invokes SOLIDWORKS, Document Manager, or
/// Windows Shell property handlers.
/// </summary>
public sealed class BrokerHostedMetadataClient : IBrokerHostedMetadataExtractionClient, IAsyncDisposable
{
    private const int QueueCapacity = 256;
    private static readonly TimeSpan MaximumRecoveryDelay = TimeSpan.FromSeconds(5);
    private readonly IMetadataExtractionEngine _engine;
    private readonly ConcurrentQueue<HostedRequest> _interactive = new();
    private readonly ConcurrentQueue<HostedRequest> _background = new();
    private readonly SemaphoreSlim _queueSlots = new(QueueCapacity, QueueCapacity);
    private readonly AutoResetEvent _workAvailable = new(initialState: false);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Thread _consumerThread;
    private readonly TaskCompletionSource _consumerCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public BrokerHostedMetadataClient()
        : this(CreatePureFileEngine())
    {
    }

    internal BrokerHostedMetadataClient(IMetadataExtractionEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _consumerThread = new Thread(Consume)
        {
            IsBackground = true,
            Name = "SwSearch.Broker.Metadata",
            Priority = ThreadPriority.BelowNormal,
        };
        if (OperatingSystem.IsWindows())
        {
            _consumerThread.SetApartmentState(ApartmentState.STA);
        }
        _consumerThread.Start();
    }

    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Volatile.Read(ref _disposed) == 0
            ? new RuntimeCapability(
                CapabilityState.Available,
                "BROKER_METADATA_HOSTED",
                "属性处理由常驻 Broker 托管，正在使用内置只读解析器。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["host"] = "broker",
                    ["concurrency"] = "1",
                    ["threadPriority"] = "below-normal",
                    ["metadataReader"] = "built-in-read-only",
                    ["documentManager"] = "disabled",
                    ["solidWorksApplication"] = "not-started",
                    ["windowsShellPropertyStore"] = "disabled",
                    ["externalWorkerRequired"] = "false",
                })
            : new RuntimeCapability(
                CapabilityState.Unavailable,
                "BROKER_METADATA_STOPPED",
                "Broker 内部属性处理已停止；现有缓存仍可搜索。"));
    }

    public async ValueTask<MetadataResponse> ExtractAsync(
        MetadataRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _queueSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var item = new HostedRequest(request, cancellationToken);
        try
        {
            if (request.WorkClass == MetadataWorkClass.Interactive)
            {
                _interactive.Enqueue(item);
            }
            else
            {
                _background.Enqueue(item);
            }
            _workAvailable.Set();
            return await item.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            item.Dispose();
            _queueSlots.Release();
        }
    }

    private void Consume()
    {
        var recoveryDelay = TimeSpan.FromMilliseconds(100);
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    if (!TryDequeue(out var item))
                    {
                        _workAvailable.WaitOne(TimeSpan.FromMilliseconds(250));
                        continue;
                    }
                    ProcessOne(item);
                    recoveryDelay = TimeSpan.FromMilliseconds(100);
                }
                catch (Exception) when (!_lifetime.IsCancellationRequested)
                {
                    _lifetime.Token.WaitHandle.WaitOne(recoveryDelay);
                    recoveryDelay = TimeSpan.FromMilliseconds(Math.Min(
                        MaximumRecoveryDelay.TotalMilliseconds,
                        recoveryDelay.TotalMilliseconds * 2));
                }
            }
        }
        finally
        {
            while (TryDequeue(out var pending))
            {
                pending.Completion.TrySetCanceled(_lifetime.Token);
            }
            _consumerCompleted.TrySetResult();
        }
    }

    private bool TryDequeue(out HostedRequest request) =>
        _interactive.TryDequeue(out request!) ||
        _background.TryDequeue(out request!);

    private void ProcessOne(HostedRequest item)
    {
        if (item.Completion.Task.IsCompleted)
        {
            return;
        }

        var request = item.Request;
        if (request.ProtocolVersion != ContractVersions.MetadataPipe)
        {
            item.TrySetResponse(Error(request, MetadataErrorCodes.ProtocolVersionUnsupported, false));
            return;
        }
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.FullPath))
        {
            item.TrySetResponse(Error(request, MetadataErrorCodes.InvalidRequest, false));
            return;
        }

        var timeoutMilliseconds = request.TimeoutMilliseconds <= 0
            ? MetadataRequestProcessor.DefaultTimeoutMilliseconds
            : Math.Min(request.TimeoutMilliseconds, MetadataRequestProcessor.MaximumTimeoutMilliseconds);
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            item.CancellationToken,
            timeout.Token);
        try
        {
            var metadata = _engine.Extract(request.FullPath, linked.Token);
            item.TrySetResponse(new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                metadata,
                ErrorCode: null,
                ChineseErrorMessage: null,
                Retryable: false));
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested || item.CancellationToken.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(
                item.CancellationToken.IsCancellationRequested
                    ? item.CancellationToken
                    : _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            item.TrySetResponse(Error(request, MetadataErrorCodes.Timeout, true));
        }
        catch (MetadataWorkerException exception)
        {
            item.TrySetResponse(Error(request, exception.Code, exception.Retryable));
        }
        catch (Exception)
        {
            item.TrySetResponse(Error(request, MetadataErrorCodes.BrokerHostedInternalFault, true));
            throw;
        }
    }

    private static MetadataResponse Error(MetadataRequest request, string code, bool retryable) =>
        new(
            ContractVersions.MetadataPipe,
            request.RequestId,
            Metadata: null,
            code,
            code == MetadataErrorCodes.BrokerHostedInternalFault
                ? "Broker 内部属性处理本轮失败，正在自动恢复；现有缓存仍可搜索。"
                : MetadataErrorCodes.ChineseMessage(code),
            retryable);

    private static IMetadataExtractionEngine CreatePureFileEngine() =>
        new MetadataExtractionEngine(
            new FileRequestValidator(new PhysicalFileSystemAccessor()),
            new NoApiMetadataExtractor(),
            new NoShellPropertyStoreFallback());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _workAvailable.Set();
        try
        {
            await _consumerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A native read may ignore cancellation. The background-only thread
            // must never make Broker shutdown wait indefinitely.
        }

        if (_consumerCompleted.Task.IsCompleted)
        {
            _workAvailable.Dispose();
            _lifetime.Dispose();
        }
        _queueSlots.Dispose();
    }

    private sealed class HostedRequest : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;

        internal HostedRequest(MetadataRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<MetadataResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _registration = cancellationToken.Register(
                static state =>
                {
                    var item = (HostedRequest)state!;
                    item.Completion.TrySetCanceled(item.CancellationToken);
                },
                this);
        }

        internal MetadataRequest Request { get; }
        internal CancellationToken CancellationToken { get; }
        internal TaskCompletionSource<MetadataResponse> Completion { get; }
        internal void TrySetResponse(MetadataResponse response) => Completion.TrySetResult(response);
        public void Dispose() => _registration.Dispose();
    }

    private sealed class NoShellPropertyStoreFallback : IWindowsPropertyStoreExtractor
    {
        public PropertyStoreExtraction Extract(ValidatedFile file) =>
            new(Array.Empty<DocumentProperty>(), MetadataErrorCodes.NoApiFormatUnsupported);
    }
}
