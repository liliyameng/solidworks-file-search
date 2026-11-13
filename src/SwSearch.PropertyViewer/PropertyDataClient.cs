using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwSearch.Contracts;
using SwSearch.Indexer;

namespace SwSearch.PropertyViewer;

public sealed record PropertyExtractionResult(
    DocumentMetadata? Metadata,
    string? ErrorCode,
    string? ChineseErrorMessage,
    bool Retryable,
    bool? Fresh = null,
    bool CacheStale = false);

public interface IPropertyDataClient
{
    ValueTask<DocumentMetadata?> TryGetCachedAsync(string fullPath, CancellationToken cancellationToken);

    ValueTask<PropertyExtractionResult> TryRefreshFromBrokerAsync(
        string fullPath,
        CancellationToken cancellationToken);
}

public sealed class BrokerPropertyDataClient(string? brokerPath = null) : IPropertyDataClient
{
    // The Broker owns the interactive extraction hard window (5 seconds by
    // default).  The viewer allows bounded IPC/commit overhead beyond that
    // window without ever waiting indefinitely on its UI lifetime.
    internal const int BrokerRefreshTimeoutMilliseconds = 6_500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };
    private readonly string _brokerPath = Path.GetFullPath(
        brokerPath ?? Path.Combine(AppContext.BaseDirectory, "SwSearch.Broker.exe"));
    private readonly SqliteMetadataSnapshotReader _snapshotReader = new();

    public async ValueTask<DocumentMetadata?> TryGetCachedAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _snapshotReader.TryReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException ||
            exception.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException")
        {
            return null;
        }
    }

    public async ValueTask<PropertyExtractionResult> TryRefreshFromBrokerAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_brokerPath))
        {
            return Unavailable("BROKER_UNAVAILABLE", "后台更新组件不可用。", false);
        }
        try
        {
            var response = await SendAsync(
                new BrokerRequest(
                    ContractVersions.BrokerPipe,
                    Guid.NewGuid(),
                    BrokerOperation.RefreshMetadata,
                    FullPath: fullPath),
                BrokerRefreshTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            var metadata = response.Metadata;
            var refresh = response.MetadataRefresh;
            return new PropertyExtractionResult(
                metadata,
                refresh?.ErrorCode ?? (metadata is null ? "METADATA_UNAVAILABLE" : null),
                refresh?.ChineseMessage ?? (metadata is null ? "后台暂未返回属性。" : null),
                refresh?.Retryable ?? metadata is null,
                refresh?.Fresh,
                refresh?.CacheStale ?? false);
        }
        catch (BrokerProtocolMismatchException exception)
        {
            return Unavailable("BROKER_PROTOCOL_MISMATCH", exception.Message, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable("BROKER_TIMEOUT", "属性鲜读超时。", true);
        }
        catch (TimeoutException)
        {
            return Unavailable("BROKER_TIMEOUT", "属性鲜读超时。", true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return Unavailable("BROKER_UNAVAILABLE", exception.Message, true);
        }
    }

    private static async ValueTask<BrokerResponse> SendAsync(
        BrokerRequest request,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            ContractVersions.BrokerPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("后台更新暂不可用。");
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeout.Token)
            .ConfigureAwait(false);
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (line is null) throw new IOException("后台连接已断开。");
        var response = JsonSerializer.Deserialize<BrokerResponse>(line, JsonOptions)
            ?? throw new InvalidDataException("后台返回无效数据。");
        if (response.ProtocolVersion != ContractVersions.BrokerPipe)
        {
            throw new BrokerProtocolMismatchException("后台组件版本不匹配。");
        }
        if (!response.Success || response.RequestId != request.RequestId)
        {
            throw new IOException(response.ErrorMessage ?? response.ErrorCode ?? "后台更新失败。");
        }
        return response;
    }

    private static PropertyExtractionResult Unavailable(
        string code,
        string message,
        bool retryable) => new(null, code, message, retryable);
}

public sealed class UnavailablePropertyDataClient : IPropertyDataClient
{
    public ValueTask<DocumentMetadata?> TryGetCachedAsync(string fullPath, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DocumentMetadata?>(null);

    public ValueTask<PropertyExtractionResult> TryRefreshFromBrokerAsync(
        string fullPath,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PropertyExtractionResult(
            null, "BROKER_UNAVAILABLE", "后台更新暂不可用。", true));
}
