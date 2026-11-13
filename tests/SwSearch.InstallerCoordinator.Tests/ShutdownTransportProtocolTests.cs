using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class ShutdownTransportProtocolTests
{
    [Fact]
    public async Task MalformedBrokerJsonMapsToFailedWithoutEscapingTheUserPhase()
    {
        var pipeName = "swsearch.test.broker." + Guid.NewGuid().ToString("N");
        var server = ServeBrokerBytesAsync(pipeName, Encoding.UTF8.GetBytes("{\n"));
        var result = await CreateTransport(pipeName, "unused.worker").ShutdownBrokerAsync(
            Products(), CurrentContext(), CancellationToken.None);

        Assert.Equal(ShutdownRequestResult.Failed, result);
        await server;
    }

    [Fact]
    public async Task MissingBrokerAndWorkerEndpointsMapToUnavailable()
    {
        var transport = new ProductShutdownTransport(
            TimeSpan.FromMilliseconds(100),
            _ => "swsearch.test.missing.broker." + Guid.NewGuid().ToString("N"),
            _ => "swsearch.test.missing.worker." + Guid.NewGuid().ToString("N"));

        Assert.Equal(
            ShutdownRequestResult.Unavailable,
            await transport.ShutdownBrokerAsync(Products(), CurrentContext(), CancellationToken.None));
        Assert.Equal(
            ShutdownRequestResult.Unavailable,
            await transport.ShutdownWorkerAsync(Products(), CurrentContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OversizedBrokerJsonMapsToFailed()
    {
        var pipeName = "swsearch.test.broker." + Guid.NewGuid().ToString("N");
        var response = Encoding.UTF8.GetBytes(new string('x', 64 * 1024 + 1) + "\n");
        var server = ServeBrokerBytesAsync(pipeName, response);

        Assert.Equal(
            ShutdownRequestResult.Failed,
            await CreateTransport(pipeName, "unused.worker").ShutdownBrokerAsync(
                Products(), CurrentContext(), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task OversizedAndTruncatedWorkerFramesMapToFailed()
    {
        var oversizedPipe = "swsearch.test.worker." + Guid.NewGuid().ToString("N");
        var oversizedServer = ServeWorkerHeaderAsync(oversizedPipe, 64 * 1024 + 1, []);
        var oversized = await CreateTransport("unused.broker", oversizedPipe).ShutdownWorkerAsync(
            Products(), CurrentContext(), CancellationToken.None);
        Assert.Equal(ShutdownRequestResult.Failed, oversized);
        await oversizedServer;

        var truncatedPipe = "swsearch.test.worker." + Guid.NewGuid().ToString("N");
        var truncatedServer = ServeWorkerHeaderAsync(truncatedPipe, 32, [0x7B, 0x7D]);
        var truncated = await CreateTransport("unused.broker", truncatedPipe).ShutdownWorkerAsync(
            Products(), CurrentContext(), CancellationToken.None);
        Assert.Equal(ShutdownRequestResult.Failed, truncated);
        await truncatedServer;
    }

    [Fact]
    public async Task CallerCancellationIsPreservedRatherThanConvertedToUnavailable()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CreateTransport(
                    "swsearch.test.missing." + Guid.NewGuid().ToString("N"),
                    "unused.worker")
                .ShutdownBrokerAsync(Products(), CurrentContext(), canceled.Token));
    }

    [Fact]
    public async Task SameSidWrongSessionFailsBeforeAnyPipeNameIsResolved()
    {
        var resolutions = 0;
        var transport = new ProductShutdownTransport(
            TimeSpan.FromMilliseconds(250),
            _ => { resolutions++; return "must-not-be-used"; },
            _ => { resolutions++; return "must-not-be-used"; });
        var context = CurrentContext() with { ExpectedSessionId = CurrentContext().ExpectedSessionId + 1 };

        Assert.Equal(
            ShutdownRequestResult.Failed,
            await transport.ShutdownBrokerAsync(Products(), context, CancellationToken.None));
        Assert.Equal(0, resolutions);
    }

    private static ProductShutdownTransport CreateTransport(string brokerPipe, string workerPipe) =>
        new(TimeSpan.FromSeconds(2), _ => brokerPipe, _ => workerPipe);

    private static ShutdownRequestContext CurrentContext()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return new ShutdownRequestContext(
            identity.User!.Value,
            System.Diagnostics.Process.GetCurrentProcess().SessionId,
            Guid.NewGuid());
    }

    private static IReadOnlyList<InstalledProductIdentity> Products() =>
    [
        new InstalledProductIdentity("{FIXTURE}", "1.2.0", AppContext.BaseDirectory)
    ];

    private static async Task ServeBrokerBytesAsync(string pipeName, byte[] response)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        await server.WriteAsync(response);
        await server.FlushAsync();
    }

    private static async Task ServeWorkerHeaderAsync(string pipeName, int responseLength, byte[] responsePrefix)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        var length = new byte[sizeof(int)];
        await ReadExactlyAsync(server, length);
        var requestLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        var request = new byte[requestLength];
        await ReadExactlyAsync(server, request);
        BinaryPrimitives.WriteInt32LittleEndian(length, responseLength);
        await server.WriteAsync(length);
        if (responsePrefix.Length != 0) await server.WriteAsync(responsePrefix);
        await server.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..]);
            if (current == 0) throw new EndOfStreamException();
            read += current;
        }
    }
}
