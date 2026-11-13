using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

public static class MetadataWorkerDefaults
{
    public static string PipeName => ContractVersions.MetadataPipeName;
    public const int DefaultTimeoutMilliseconds = MetadataRequestProcessor.DefaultTimeoutMilliseconds;
    public const int MaximumTimeoutMilliseconds = MetadataRequestProcessor.MaximumTimeoutMilliseconds;
}

internal static class ContractOptions
{
    public static string DefaultPipeName => ContractVersions.MetadataPipeName;
}

internal static class WorkerPipeIdentity
{
    internal static string Canonicalize(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        }

        // Windows named-pipe object names compare case-insensitively.  All
        // companion kernel objects must use the same equivalence relation or
        // differently-cased clients could bypass the single-start boundary.
        return pipeName.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    internal static string Hash(string pipeName)
    {
        var canonical = Canonicalize(pipeName);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 12));
    }
}

internal static class WorkerInstanceSignal
{
    internal static string Name(string pipeName)
    {
        return @"Local\SwSearch.Metadata.Worker." + WorkerPipeIdentity.Hash(pipeName);
    }

    internal static bool Exists(string pipeName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(Name(pipeName));
            return signal is not null;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A current-user-only worker may still be present. Conservatively
            // avoid starting a competing process.
            return true;
        }
    }
}

internal static class WorkerReadySignal
{
    private const string ReadyNonceArgument = "--ready-nonce";
    private const string PipeNameArgument = "--pipe-name-base64";

    internal sealed record StartupArguments(Guid ReadyNonce, string PipeName);

    internal static Guid? ParseArguments(string[] args)
        => ParseStartupArguments(args)?.ReadyNonce;

    internal static StartupArguments? ParseStartupArguments(string[] args)
    {
        if (args.Length == 0 ||
            (args.Length == 1 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (args.Length == 2 &&
            args[0].Equals(ReadyNonceArgument, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(args[1], "N", out var defaultPipeNonce))
        {
            return new StartupArguments(defaultPipeNonce, ContractOptions.DefaultPipeName);
        }

        if (args.Length == 4 &&
            args[0].Equals(ReadyNonceArgument, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(args[1], "N", out var nonce) &&
            args[2].Equals(PipeNameArgument, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pipeName = Encoding.UTF8.GetString(Convert.FromBase64String(args[3]));
                _ = WorkerPipeIdentity.Canonicalize(pipeName);
                return new StartupArguments(nonce, pipeName);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new ArgumentException("Unsupported worker arguments.", nameof(args), exception);
            }
        }

        throw new ArgumentException("Unsupported worker arguments.", nameof(args));
    }

    internal static EventWaitHandle Create(Guid nonce)
    {
        var signal = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            Name(nonce),
            out var createdNew);
        if (!createdNew)
        {
            signal.Dispose();
            throw new IOException("The worker readiness nonce is already in use.");
        }

        return signal;
    }

    internal static bool Exists(Guid nonce)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(Name(nonce));
            return signal.WaitOne(0);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Name(Guid nonce) => @"Local\SwSearch.Metadata.Worker.Ready." + nonce.ToString("N");
}

internal static class PipeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
}

internal sealed record PipeRequestEnvelope(
    int ProtocolVersion,
    Guid RequestId,
    string Operation,
    MetadataRequest? MetadataRequest = null,
    ShutdownForUpdateRequest? ShutdownForUpdate = null,
    ShutdownForUpdateChallengeRequest? ShutdownChallengeRequest = null);

internal sealed record PipeResponseEnvelope(
    int ProtocolVersion,
    Guid RequestId,
    MetadataResponse? MetadataResponse = null,
    RuntimeCapability? Capability = null,
    string? ErrorCode = null,
    string? ChineseErrorMessage = null,
    bool Retryable = false,
    ShutdownForUpdateChallenge? ShutdownChallenge = null);

internal static class PipeFrame
{
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;

    public static async ValueTask WriteJsonAsync<T>(
        Stream stream,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, PipeJson.Options);
        if (payload.Length == 0 || payload.Length > maximumBytes)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.FrameInvalid);
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadJsonAsync<T>(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (payloadLength <= 0 || payloadLength > maximumBytes)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.FrameInvalid);
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, PipeJson.Options)
                ?? throw new MetadataWorkerException(MetadataErrorCodes.FrameInvalid);
        }
        catch (JsonException exception)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.FrameInvalid,
                innerException: exception);
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var current = await stream.ReadAsync(destination[read..], cancellationToken)
                .ConfigureAwait(false);
            if (current == 0)
            {
                throw new EndOfStreamException();
            }

            read += current;
        }
    }
}
