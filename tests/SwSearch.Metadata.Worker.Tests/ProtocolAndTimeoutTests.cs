using System.Buffers.Binary;
using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class ProtocolAndTimeoutTests
{
    [Fact]
    public async Task LengthPrefixedJsonRoundTripsUnicodePath()
    {
        var request = MetadataExtractionEngineTests.Request(@"C:\设计 库\轴&套.SLDPRT");
        await using var stream = new MemoryStream();

        await PipeFrame.WriteJsonAsync(
            stream,
            request,
            PipeFrame.MaximumRequestBytes,
            CancellationToken.None);
        stream.Position = 0;
        var actual = await PipeFrame.ReadJsonAsync<MetadataRequest>(
            stream,
            PipeFrame.MaximumRequestBytes,
            CancellationToken.None);

        Assert.Equal(request, actual);
    }

    [Fact]
    public async Task RejectsFrameLargerThanBoundaryBeforeAllocation()
    {
        await using var stream = new MemoryStream();
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, PipeFrame.MaximumRequestBytes + 1);
        await stream.WriteAsync(length);
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<MetadataWorkerException>(async () =>
            await PipeFrame.ReadJsonAsync<MetadataRequest>(
                stream,
                PipeFrame.MaximumRequestBytes,
                CancellationToken.None));

        Assert.Equal(MetadataErrorCodes.FrameInvalid, exception.Code);
    }

    [Fact]
    public async Task TimeoutReturnsStableErrorAndRequiresWorkerRestart()
    {
        var engine = new FakeExtractionEngine((_, _) =>
        {
            Thread.Sleep(250);
            throw new InvalidOperationException("unreachable before timeout");
        });
        var processor = new MetadataRequestProcessor(engine);
        var request = MetadataExtractionEngineTests.Request(@"C:\design\slow.SLDPRT", timeout: 30);

        var result = await processor.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(MetadataErrorCodes.Timeout, result.Response.ErrorCode);
        Assert.Equal("读取 SOLIDWORKS 属性超时，请重试。", result.Response.ChineseErrorMessage);
        Assert.True(result.Response.Retryable);
        Assert.True(result.RequiresWorkerRestart);
    }

    [Fact]
    public async Task UnsupportedProtocolIsRejectedWithoutCallingExtractor()
    {
        var called = false;
        var engine = new FakeExtractionEngine((_, _) =>
        {
            called = true;
            throw new InvalidOperationException();
        });
        var processor = new MetadataRequestProcessor(engine);
        var request = MetadataExtractionEngineTests.Request(@"C:\design\part.SLDPRT") with
        {
            ProtocolVersion = ContractVersions.MetadataPipe + 1
        };

        var result = await processor.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(MetadataErrorCodes.ProtocolVersionUnsupported, result.Response.ErrorCode);
        Assert.False(called);
    }

    [Fact]
    public async Task CallerCancellationReturnsStableCancellationCode()
    {
        var engine = new FakeExtractionEngine((_, token) =>
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        var processor = new MetadataRequestProcessor(engine);
        var request = MetadataExtractionEngineTests.Request(@"C:\design\cancel.SLDPRT");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await processor.ProcessAsync(request, cancellation.Token);

        Assert.Equal(MetadataErrorCodes.Cancelled, result.Response.ErrorCode);
        Assert.Equal("属性读取已取消。", result.Response.ChineseErrorMessage);
        Assert.False(result.RequiresWorkerRestart);
    }
}
