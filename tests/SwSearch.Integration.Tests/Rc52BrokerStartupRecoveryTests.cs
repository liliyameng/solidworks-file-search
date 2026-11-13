using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using SwSearch.Broker;
using SwSearch.Contracts;
using SwSearch.Indexer;
using SwSearch.PropertyConfig;

namespace SwSearch.Integration.Tests;

public sealed class Rc52BrokerStartupRecoveryTests
{
    [Fact]
    public void AbandonedMutexOwner_IsRecoveredOnDedicatedOwnerThread()
    {
        var names = UniqueGateNames();
        Mutex? abandonedHandle = null;
        using var ready = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            var handle = new Mutex(false, names.MutexName);
            abandonedHandle = handle;
            handle.WaitOne();
            ready.Set();
            // Deliberately leave this thread while it owns the native mutex.
        });
        owner.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(owner.Join(TimeSpan.FromSeconds(1)));

        try
        {
            var result = BrokerInstanceGuard.Acquire(
                Options(names, TimeSpan.FromMilliseconds(250)),
                AlwaysUnhealthyProbe.Instance);
            using var guard = Assert.IsType<BrokerInstanceGuard>(result.Guard);

            Assert.Equal(BrokerInstanceDisposition.Acquired, result.Disposition);
            Assert.True(result.RecoveredAbandonedOwner);
            Assert.Equal(BrokerProcessExitCodes.Success, result.ExitCode);
        }
        finally
        {
            abandonedHandle?.Dispose();
        }
    }

    [Fact]
    public void UnhealthyLiveOwner_TimesOutWithRetryableExitCodeAndNeverCreatesSecondWriter()
    {
        var names = UniqueGateNames();
        using var owner = NativeMutexOwner.Start(names.MutexName);
        var clock = Stopwatch.StartNew();

        var result = BrokerInstanceGuard.Acquire(
            Options(names, TimeSpan.FromMilliseconds(90)),
            AlwaysUnhealthyProbe.Instance);

        clock.Stop();
        Assert.Equal(BrokerInstanceDisposition.RetryableUnavailable, result.Disposition);
        Assert.Null(result.Guard);
        Assert.Equal(BrokerProcessExitCodes.TemporaryInstanceFailure, result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"gate took {clock.Elapsed}");
        using var legacyProbe = new Semaphore(1, 1, names.LegacySemaphoreName);
        Assert.True(legacyProbe.WaitOne(0));
        legacyProbe.Release();
    }

    [Fact]
    public void Rc51SemaphoreHandoff_IsWaitedAndThenHeldAlongsideNewMutex()
    {
        var names = UniqueGateNames();
        using var legacyOwner = new Semaphore(1, 1, names.LegacySemaphoreName);
        Assert.True(legacyOwner.WaitOne(0));
        var releaser = new Thread(() =>
        {
            Thread.Sleep(40);
            legacyOwner.Release();
        });
        releaser.Start();

        var result = BrokerInstanceGuard.Acquire(
            Options(names, TimeSpan.FromMilliseconds(500)),
            AlwaysUnhealthyProbe.Instance);
        using var guard = Assert.IsType<BrokerInstanceGuard>(result.Guard);

        Assert.True(releaser.Join(TimeSpan.FromSeconds(1)));
        Assert.Equal(BrokerInstanceDisposition.Acquired, result.Disposition);
        Assert.False(legacyOwner.WaitOne(0));
    }

    [Fact]
    public void BootstrapLogSinkFailure_IsAlwaysFailSoft()
    {
        var log = BrokerBootstrapLog.CreateForTesting(static _ => throw new IOException("fixture denied"));

        var failure = Record.Exception(() =>
        {
            log.Write("START", "first");
            log.WriteException("FATAL", new InvalidOperationException("fixture"));
        });

        Assert.Null(failure);
    }

    [Fact]
    public void BootstrapLog_RotatesWithinConfiguredArchiveBound()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "broker-bootstrap.log");
        var log = BrokerBootstrapLog.CreateFile(path, maximumFileBytes: 1024, archiveCount: 1);

        for (var index = 0; index < 24; index++)
        {
            log.Write("ROTATE", $"{index:D2}:{new string('x', 300)}");
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.False(File.Exists(path + ".2"));
        Assert.All(
            new[] { path, path + ".1" },
            file => Assert.True(new FileInfo(file).Length < 4096, $"{file} was not bounded"));
    }

    [Fact]
    public void AcceptRetry_IsExponentialAndCapped()
    {
        var options = BrokerHostOptions.Default with
        {
            InitialAcceptRetryDelay = TimeSpan.FromMilliseconds(10),
            MaximumAcceptRetryDelay = TimeSpan.FromMilliseconds(40),
        };

        Assert.Equal(TimeSpan.FromMilliseconds(10), BrokerHost.AcceptRetryDelay(1, options));
        Assert.Equal(TimeSpan.FromMilliseconds(20), BrokerHost.AcceptRetryDelay(2, options));
        Assert.Equal(TimeSpan.FromMilliseconds(40), BrokerHost.AcceptRetryDelay(3, options));
        Assert.Equal(TimeSpan.FromMilliseconds(40), BrokerHost.AcceptRetryDelay(8, options));
    }

    [Fact]
    public async Task Draining_RejectsBusinessRequestWithoutOpeningDatabase()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "must-not-open.db");
        await using var runtime = new IndexingRuntime(
            new SqliteIndexStore(databasePath),
            new EmptyDiscovery(),
            new EmptyMetadata(),
            new IndexingRuntimeOptions(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(5), 1),
            new RuntimeFileLog(Path.Combine(directory.Path, "logs")));
        await using var propertyConfig = new PropertyCardConfigProvider(new PropertyCardConfigProviderOptions(
            Path.Combine(directory.Path, "defaults.tsv"),
            Path.Combine(directory.Path, "property-config.json"),
            [],
            [],
            WatchForChanges: false));
        await using var host = new BrokerHost(runtime, propertyConfig);
        host.BeginDrainingForTesting();

        var response = await host.DispatchForTestingAsync(new BrokerRequest(
            ContractVersions.BrokerPipe,
            Guid.NewGuid(),
            BrokerOperation.Ping));

        Assert.False(response.Success);
        Assert.Equal(BrokerHost.DrainingErrorCode, response.ErrorCode);
        Assert.Equal(BrokerHost.DrainingErrorCode, response.Capability?.Code);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task DedicatedAppExitPipe_AcknowledgesThenDrainsAndEndsHostNaturally()
    {
        using var directory = new TemporaryDirectory();
        var primaryPipeName = "swsearch.rc62.broker.primary." + Guid.NewGuid().ToString("N");
        var appExitPipeName = "swsearch.rc62.broker.app-exit." + Guid.NewGuid().ToString("N");
        var diagnostics = new ConcurrentQueue<string>();
        await using var runtime = new IndexingRuntime(
            new SqliteIndexStore(Path.Combine(directory.Path, "app-exit.db")),
            new EmptyDiscovery(),
            new EmptyMetadata(),
            new IndexingRuntimeOptions(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(5), 1),
            new RuntimeFileLog(Path.Combine(directory.Path, "logs")));
        await using var propertyConfig = new PropertyCardConfigProvider(new PropertyCardConfigProviderOptions(
            Path.Combine(directory.Path, "defaults.tsv"),
            Path.Combine(directory.Path, "property-config.json"),
            [],
            [],
            WatchForChanges: false));
        await using var host = new BrokerHost(
            runtime,
            propertyConfig,
            diagnosticSink: (code, _) => diagnostics.Enqueue(code),
            serverFactory: () => CreatePipeServer(primaryPipeName),
            appExitServerFactory: () => CreatePipeServer(appExitPipeName));

        var run = host.RunAsync(CancellationToken.None);
        var response = await BrokerAppExitShutdownClient.RequestShutdownAsync(
            appExitPipeName,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        Assert.True(response.Success);
        Assert.Equal(ContractVersions.BrokerAppExitPipe, response.ProtocolVersion);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("BROKER_APP_EXIT_DRAINING", diagnostics);
    }

    private static BrokerInstanceGuardOptions Options(
        GateNames names,
        TimeSpan timeout) =>
        new(
            names.MutexName,
            names.LegacySemaphoreName,
            timeout,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(5));

    private static GateNames UniqueGateNames()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new GateNames(
            @"Local\SwSearch.Broker.Rc52.Test.Mutex." + suffix,
            @"Local\SwSearch.Broker.Rc52.Test.Semaphore." + suffix);
    }

    private sealed record GateNames(string MutexName, string LegacySemaphoreName);

    private static NamedPipeServerStream CreatePipeServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private sealed class AlwaysUnhealthyProbe : IBrokerLivenessProbe
    {
        internal static AlwaysUnhealthyProbe Instance { get; } = new();

        public bool IsHealthy(TimeSpan timeout) => false;
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

    private sealed class EmptyDiscovery : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST", "test"));

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EmptyMetadata : IMetadataExtractionClient
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "TEST", "test"));

        public ValueTask<MetadataResponse> ExtractAsync(
            MetadataRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The draining test must not extract metadata.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "swsearch-rc52-broker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
