using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using SwSearch.Contracts;
using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class MetadataPipeIntegrationTests
{
    [Fact]
    public async Task ShutdownChallengeBindsSidSessionNonceAndSameConnectionBeforeWorkerStops()
    {
        var processor = new MetadataRequestProcessor(new FakeExtractionEngine((_, _) => throw new InvalidOperationException()));
        var pipeName = "swsearch.metadata.shutdown." + Guid.NewGuid().ToString("N");
        await using var host = new MetadataPipeHost(processor, new NoApiCapabilityProbe(), pipeName);
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User!.Value;
        var session = Process.GetCurrentProcess().SessionId;

        await using (var wrongSessionPipe = await ConnectAsync(pipeName))
        {
            var requestId = Guid.NewGuid();
            await PipeFrame.WriteJsonAsync(
                wrongSessionPipe,
                new PipeRequestEnvelope(
                    ContractVersions.MetadataPipe,
                    requestId,
                    "beginShutdownForUpdate",
                    ShutdownChallengeRequest: new ShutdownForUpdateChallengeRequest(sid, session + 1, Guid.NewGuid())),
                PipeFrame.MaximumRequestBytes,
                CancellationToken.None);
            var response = await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                wrongSessionPipe, PipeFrame.MaximumResponseBytes, CancellationToken.None);
            Assert.Equal(MetadataErrorCodes.InvalidRequest, response.ErrorCode);
            Assert.Null(response.ShutdownChallenge);
        }

        await using (var arbitraryNoncePipe = await ConnectAsync(pipeName))
        {
            var nonce = Guid.NewGuid();
            var challengeRequestId = Guid.NewGuid();
            await PipeFrame.WriteJsonAsync(
                arbitraryNoncePipe,
                new PipeRequestEnvelope(
                    ContractVersions.MetadataPipe,
                    challengeRequestId,
                    "beginShutdownForUpdate",
                    ShutdownChallengeRequest: new ShutdownForUpdateChallengeRequest(sid, session, nonce)),
                PipeFrame.MaximumRequestBytes,
                CancellationToken.None);
            var challengeResponse = await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                arbitraryNoncePipe, PipeFrame.MaximumResponseBytes, CancellationToken.None);
            Assert.NotNull(challengeResponse.ShutdownChallenge);
            var mismatchId = Guid.NewGuid();
            await PipeFrame.WriteJsonAsync(
                arbitraryNoncePipe,
                new PipeRequestEnvelope(
                    ContractVersions.MetadataPipe,
                    mismatchId,
                    "shutdownForUpdate",
                    ShutdownForUpdate: ShutdownRequest(sid, session, Guid.NewGuid(), challengeResponse.ShutdownChallenge!.ChallengeId)),
                PipeFrame.MaximumRequestBytes,
                CancellationToken.None);
            var mismatch = await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                arbitraryNoncePipe, PipeFrame.MaximumResponseBytes, CancellationToken.None);
            Assert.Equal(MetadataErrorCodes.InvalidRequest, mismatch.ErrorCode);
        }

        await using (var validPipe = await ConnectAsync(pipeName))
        {
            var nonce = Guid.NewGuid();
            var challengeRequestId = Guid.NewGuid();
            await PipeFrame.WriteJsonAsync(
                validPipe,
                new PipeRequestEnvelope(
                    ContractVersions.MetadataPipe,
                    challengeRequestId,
                    "beginShutdownForUpdate",
                    ShutdownChallengeRequest: new ShutdownForUpdateChallengeRequest(sid, session, nonce)),
                PipeFrame.MaximumRequestBytes,
                CancellationToken.None);
            var challengeResponse = await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                validPipe, PipeFrame.MaximumResponseBytes, CancellationToken.None);
            var shutdownId = Guid.NewGuid();
            await PipeFrame.WriteJsonAsync(
                validPipe,
                new PipeRequestEnvelope(
                    ContractVersions.MetadataPipe,
                    shutdownId,
                    "shutdownForUpdate",
                    ShutdownForUpdate: ShutdownRequest(sid, session, nonce, challengeResponse.ShutdownChallenge!.ChallengeId)),
                PipeFrame.MaximumRequestBytes,
                CancellationToken.None);
            var accepted = await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                validPipe, PipeFrame.MaximumResponseBytes, CancellationToken.None);
            Assert.Null(accepted.ErrorCode);
            Assert.Equal("SHUTDOWN_FOR_UPDATE_ACCEPTED", accepted.Capability!.Code);
        }

        await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MalformedWorkerJsonReturnsFrameInvalidAndListenerRemainsAvailable()
    {
        var processor = new MetadataRequestProcessor(new FakeExtractionEngine((_, _) => throw new InvalidOperationException()));
        var pipeName = "swsearch.metadata.malformed." + Guid.NewGuid().ToString("N");
        await using var host = new MetadataPipeHost(processor, new NoApiCapabilityProbe(), pipeName);
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);

        await using (var pipe = await ConnectAsync(pipeName))
        {
            var length = BitConverter.GetBytes(1);
            await pipe.WriteAsync(length);
            await pipe.WriteAsync(new byte[] { (byte)'{' });
            await pipe.FlushAsync();
            var response = await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                pipe, PipeFrame.MaximumResponseBytes, CancellationToken.None);
            Assert.Equal(MetadataErrorCodes.FrameInvalid, response.ErrorCode);
        }

        var client = new MetadataPipeClient(pipeName, 1_000);
        var capability = await client.ProbeAsync(CancellationToken.None);
        Assert.NotNull(capability);
        cancellation.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task BusyBackgroundRequest_DoesNotMakeViewerStartASecondWorkerOrFailAtOneSecond()
    {
        var backgroundEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeExtractionEngine((path, _) =>
        {
            if (path.Contains("background", StringComparison.Ordinal))
            {
                backgroundEntered.TrySetResult();
                Thread.Sleep(1_600);
            }

            return new DocumentMetadata(
                new FileCandidate(path, SolidWorksDocumentKind.Part, 42, DateTimeOffset.UnixEpoch),
                [new DocumentProperty(
                    "物料编码",
                    "P0-OK",
                    "P0-OK",
                    PropertyScope.Document,
                    null,
                    MetadataSource.NoApi)],
                Array.Empty<DocumentReference>(),
                MetadataSource.NoApi,
                DateTimeOffset.UtcNow);
        });
        var processor = new MetadataRequestProcessor(engine);
        var pipeName = "swsearch.metadata.concurrent." + Guid.NewGuid().ToString("N");
        await using var host = new MetadataPipeHost(
            processor,
            new NoApiCapabilityProbe(),
            pipeName);
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);
        var backgroundClient = new MetadataPipeClient(pipeName, connectTimeoutMilliseconds: 1_000);
        var backgroundRequest = MetadataExtractionEngineTests.Request(
            @"C:\design\background.SLDPRT") with
        {
            TimeoutMilliseconds = 5_000,
        };
        var backgroundTask = backgroundClient.ExtractAsync(backgroundRequest, CancellationToken.None).AsTask();

        await backgroundEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // This executable deliberately does not exist. If the client mistakes a
        // busy pipe for a crashed worker, recovery will try this path and the
        // response will be METADATA_WORKER_CRASHED instead of successful P0 data.
        var missingWorker = Path.Combine(
            Path.GetTempPath(),
            "SwSearch.Metadata.Tests",
            Guid.NewGuid().ToString("N"),
            "missing-worker.exe");
        await using var viewerClient = new MetadataWorkerClient(new MetadataWorkerClientOptions(
            missingWorker,
            pipeName,
            ConnectTimeoutMilliseconds: 1_000));
        var viewerRequest = MetadataExtractionEngineTests.Request(
            @"C:\design\viewer.SLDPRT") with
        {
            TimeoutMilliseconds = 5_000,
        };

        var viewerResponse = await viewerClient.ExtractAsync(viewerRequest, CancellationToken.None);
        var backgroundResponse = await backgroundTask;

        Assert.Null(backgroundResponse.ErrorCode);
        Assert.Null(viewerResponse.ErrorCode);
        Assert.NotNull(viewerResponse.Metadata);
        Assert.Contains(viewerResponse.Metadata.Properties, property =>
            property.Name == "物料编码" && property.ResolvedValue == "P0-OK");

        cancellation.Cancel();
        await hostTask;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1_000);
        return pipe;
    }

    private static ShutdownForUpdateRequest ShutdownRequest(
        string sid,
        int session,
        Guid nonce,
        Guid challenge) =>
        new(
            Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Assembly.GetEntryAssembly()!
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion,
            sid,
            session,
            nonce,
            challenge);

    [Fact]
    public async Task CurrentUserPipeCarriesProbeAndExtractionFrames()
    {
        var path = @"C:\design\fixture.SLDPRT";
        var metadata = new DocumentMetadata(
            new FileCandidate(path, SolidWorksDocumentKind.Part, 42, DateTimeOffset.UnixEpoch),
            Array.Empty<DocumentProperty>(),
            Array.Empty<DocumentReference>(),
            MetadataSource.FileSystemOnly,
            DateTimeOffset.UnixEpoch,
            MetadataErrorCodes.DocumentManagerKeyMissing);
        var engine = new FakeExtractionEngine((_, _) => metadata);
        var processor = new MetadataRequestProcessor(engine);
        var keyProvider = new FakeKeyProvider(null);
        var sessionFactory = new FakeSessionFactory(
            isClassRegistered: true,
            () => throw new InvalidOperationException());
        var probe = new DocumentManagerCapabilityProbe(keyProvider, sessionFactory);
        var pipeName = "swsearch.metadata.tests." + Guid.NewGuid().ToString("N");
        await using var host = new MetadataPipeHost(processor, probe, pipeName);
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);
        var client = new MetadataPipeClient(pipeName, connectTimeoutMilliseconds: 2_000);

        var capability = await client.ProbeAsync(CancellationToken.None);
        var request = MetadataExtractionEngineTests.Request(path);
        var response = await client.ExtractAsync(request, CancellationToken.None);

        Assert.Equal(CapabilityState.Degraded, capability.State);
        Assert.Equal(MetadataErrorCodes.DocumentManagerKeyMissing, capability.Code);
        Assert.NotNull(response.Metadata);
        Assert.Equal(metadata.File, response.Metadata.File);
        Assert.Equal(metadata.Source, response.Metadata.Source);
        Assert.Equal(metadata.WarningCode, response.Metadata.WarningCode);
        Assert.Empty(response.Metadata.Properties);
        Assert.Empty(response.Metadata.References);
        cancellation.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task ProcessClientStartsHeadlessWorkerAndExtractsByPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SwSearch.Metadata.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "按需读取.SLDPRT");
        File.WriteAllBytes(path, Array.Empty<byte>());
        var executable = Path.Combine(AppContext.BaseDirectory, "SwSearch.Metadata.Worker.exe");
        await using var client = new MetadataWorkerClient(new MetadataWorkerClientOptions(
            executable,
            ConnectTimeoutMilliseconds: 750));
        try
        {
            var request = new MetadataRequest(
                ContractVersions.MetadataPipe,
                Guid.NewGuid(),
                path,
                ForceRefresh: false,
                TimeoutMilliseconds: 5_000);

            var response = await client.ExtractAsync(request, CancellationToken.None);

            Assert.Null(response.ErrorCode);
            Assert.NotNull(response.Metadata);
            Assert.Equal(path, response.Metadata.File.FullPath);
            Assert.Contains(
                response.Metadata.Source,
                new[] { MetadataSource.WindowsPropertyStore, MetadataSource.FileSystemOnly });
            Assert.NotNull(response.Metadata.WarningCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
