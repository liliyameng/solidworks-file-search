using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SwSearch.Broker;
using SwSearch.Contracts;

namespace SwSearch.Integration.Tests;

public sealed class Rc52BrokerLivenessProbeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HealthyProtocolPing_IsTheOnlyResponseThatProducesNormalExistingOwnerExit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = "swsearch.rc52.probe.healthy." + suffix;
        var mutexName = @"Local\SwSearch.Broker.Rc52.Probe.Mutex." + suffix;
        var semaphoreName = @"Local\SwSearch.Broker.Rc52.Probe.Semaphore." + suffix;
        var server = RunProbeServerAsync(
            pipeName,
            request => JsonSerializer.Serialize(
                new BrokerResponse(
                    ContractVersions.BrokerPipe,
                    request.RequestId,
                    true),
                JsonOptions));
        using var owner = NativeMutexOwner.Start(mutexName);

        var result = BrokerInstanceGuard.Acquire(
            new BrokerInstanceGuardOptions(
                mutexName,
                semaphoreName,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(10)),
            new NamedPipeBrokerLivenessProbe(pipeName));

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BrokerInstanceDisposition.ExistingHealthy, result.Disposition);
        Assert.Null(result.Guard);
        Assert.Equal(BrokerProcessExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public async Task ConnectWithoutResponse_IsUnhealthyWithinOneTotalDeadline()
    {
        var pipeName = UniquePipeName("silent");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunProbeServerAsync(pipeName, async _ =>
        {
            await release.Task.ConfigureAwait(false);
            return null;
        });
        var probe = new NamedPipeBrokerLivenessProbe(pipeName);
        var clock = Stopwatch.StartNew();

        var healthy = probe.IsHealthy(TimeSpan.FromMilliseconds(120));

        clock.Stop();
        release.TrySetResult();
        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(healthy);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"silent pipe probe took {clock.Elapsed}");
    }

    [Fact]
    public async Task ResponseWithWrongRequestId_IsUnhealthy()
    {
        var pipeName = UniquePipeName("request-id");
        var server = RunProbeServerAsync(
            pipeName,
            _ => JsonSerializer.Serialize(
                new BrokerResponse(
                    ContractVersions.BrokerPipe,
                    Guid.NewGuid(),
                    true),
                JsonOptions));

        var healthy = new NamedPipeBrokerLivenessProbe(pipeName)
            .IsHealthy(TimeSpan.FromMilliseconds(500));

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(healthy);
    }

    [Fact]
    public async Task UnsuccessfulProtocolResponse_IsUnhealthy()
    {
        var pipeName = UniquePipeName("failure");
        var server = RunProbeServerAsync(
            pipeName,
            request => JsonSerializer.Serialize(
                new BrokerResponse(
                    ContractVersions.BrokerPipe,
                    request.RequestId,
                    false,
                    "BROKER_TEST_FAILURE"),
                JsonOptions));

        var healthy = new NamedPipeBrokerLivenessProbe(pipeName)
            .IsHealthy(TimeSpan.FromMilliseconds(500));

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(healthy);
    }

    [Fact]
    public async Task OldProtocolResponse_IsUnhealthy()
    {
        var pipeName = UniquePipeName("old-protocol");
        var server = RunProbeServerAsync(
            pipeName,
            request => JsonSerializer.Serialize(
                new BrokerResponse(
                    ContractVersions.BrokerPipe - 1,
                    request.RequestId,
                    true),
                JsonOptions));

        var healthy = new NamedPipeBrokerLivenessProbe(pipeName)
            .IsHealthy(TimeSpan.FromMilliseconds(500));

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(healthy);
    }

    [Fact]
    public async Task MalformedResponse_IsUnhealthy()
    {
        var pipeName = UniquePipeName("malformed");
        var server = RunProbeServerAsync(pipeName, _ => "{not-json");

        var healthy = new NamedPipeBrokerLivenessProbe(pipeName)
            .IsHealthy(TimeSpan.FromMilliseconds(500));

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(healthy);
    }

    private static string UniquePipeName(string scenario) =>
        $"swsearch.rc52.probe.{scenario}.{Guid.NewGuid():N}";

    private static async Task RunProbeServerAsync(
        string pipeName,
        Func<BrokerRequest, string?> responseFactory)
    {
        await RunProbeServerAsync(
            pipeName,
            request => Task.FromResult(responseFactory(request))).ConfigureAwait(false);
    }

    private static async Task RunProbeServerAsync(
        string pipeName,
        Func<BrokerRequest, Task<string?>> responseFactory)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await server.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(
            server,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            server,
            new UTF8Encoding(false),
            4096,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        var requestJson = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        Assert.False(string.IsNullOrWhiteSpace(requestJson));
        var request = Assert.IsType<BrokerRequest>(
            JsonSerializer.Deserialize<BrokerRequest>(requestJson!, JsonOptions));
        Assert.Equal(ContractVersions.BrokerPipe, request.ProtocolVersion);
        Assert.Equal(BrokerOperation.Ping, request.Operation);
        Assert.NotEqual(Guid.Empty, request.RequestId);

        var response = await responseFactory(request).WaitAsync(deadline.Token).ConfigureAwait(false);
        if (response is not null)
        {
            await writer.WriteLineAsync(response.AsMemory(), deadline.Token).ConfigureAwait(false);
        }
    }

    private sealed class NativeMutexOwner : IDisposable
    {
        private readonly ManualResetEventSlim _release;
        private readonly Thread _thread;

        private NativeMutexOwner(ManualResetEventSlim release, Thread thread)
        {
            _release = release;
            _thread = thread;
        }

        internal static NativeMutexOwner Start(string name)
        {
            var ready = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                using var mutex = new Mutex(false, name);
                mutex.WaitOne();
                ready.Set();
                release.Wait();
                mutex.ReleaseMutex();
            });
            thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(1)))
            {
                release.Set();
                thread.Join(TimeSpan.FromSeconds(1));
                ready.Dispose();
                release.Dispose();
                throw new TimeoutException("Native mutex fixture did not acquire its gate.");
            }

            ready.Dispose();
            return new NativeMutexOwner(release, thread);
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(1)));
            _release.Dispose();
        }
    }
}
