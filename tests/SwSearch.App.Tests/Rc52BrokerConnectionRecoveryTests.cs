using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc52BrokerConnectionRecoveryTests
{
    [TestMethod]
    public async Task RetryableOwnerExit_IsObservedAndRecoveryConnectsWithoutTerminatingAnyProcess()
    {
        var connector = new ScriptedConnector(
            static _ => throw new BrokerConnectTimeoutException(TimeSpan.FromMilliseconds(10)),
            static _ => throw new BrokerConnectTimeoutException(TimeSpan.FromMilliseconds(10)),
            Success);
        var firstChild = new FakeChildProcess(BrokerChildExitCodes.TemporaryInstanceFailure);
        var secondChild = new FakeChildProcess();
        var launcher = new FakeProcessLauncher(firstChild, secondChild);
        var clock = new FakeClock();
        using var fixture = CreateClient(connector, launcher, clock);

        var outcome = await fixture.Client.StartRecoveryForTestingAsync();

        Assert.IsTrue(outcome.Connected);
        Assert.AreEqual(3, outcome.ConnectAttemptCount);
        Assert.AreEqual(2, outcome.LaunchCount);
        Assert.AreEqual(1, firstChild.DisposeCalls);
        Assert.AreEqual(1, secondChild.DisposeCalls);
        Assert.AreEqual(0, launcher.TerminationCalls);
    }

    [TestMethod]
    public async Task StartupExit70_IsReportedAsNonRetryableBootstrapFailure()
    {
        var connector = new ScriptedConnector(
            static _ => throw new BrokerConnectTimeoutException(TimeSpan.FromMilliseconds(10)));
        var launcher = new FakeProcessLauncher(
            new FakeChildProcess(BrokerChildExitCodes.StartupFailure));
        using var fixture = CreateClient(connector, launcher, new FakeClock());

        var outcome = await fixture.Client.StartRecoveryForTestingAsync();

        Assert.IsFalse(outcome.Connected);
        Assert.IsNotNull(outcome.Failure);
        Assert.AreEqual("BROKER_STARTUP_FAILED", outcome.Failure.Code);
        Assert.AreEqual(BrokerConnectionFailureKind.ChildExited, outcome.Failure.Kind);
        Assert.AreEqual(70, outcome.Failure.ChildExitCode);
        Assert.IsFalse(outcome.Failure.Retryable);

        var repeatedWithoutUserRetry = await fixture.Client.StartRecoveryForTestingAsync();
        Assert.AreSame(outcome, repeatedWithoutUserRetry);
        Assert.AreEqual(1, launcher.LaunchCalls);
    }

    [TestMethod]
    public async Task ResponseTimeout_RemainsResponseFailureAndDoesNotLaunchDuplicateBroker()
    {
        var connector = new ScriptedConnector(
            static _ => throw new BrokerResponseTimeoutException(TimeSpan.FromMilliseconds(20)));
        var launcher = new FakeProcessLauncher();
        using var fixture = CreateClient(connector, launcher, new FakeClock());

        var outcome = await fixture.Client.StartRecoveryForTestingAsync();

        Assert.IsFalse(outcome.Connected);
        Assert.IsNotNull(outcome.Failure);
        Assert.AreEqual("BROKER_RESPONSE_DEADLINE", outcome.Failure.Code);
        StringAssert.Contains(outcome.Failure.Message, "已连接");
        Assert.AreEqual(0, outcome.LaunchCount);
        Assert.AreEqual(0, launcher.LaunchCalls);
    }

    [TestMethod]
    public async Task RetryConnection_CancelsOldRoundAndImmediatelyStartsFreshRound()
    {
        var connector = new FirstCallBlocksConnector();
        var launcher = new FakeProcessLauncher();
        using var fixture = CreateClient(connector, launcher, new FakeClock());
        var first = fixture.Client.StartRecoveryForTestingAsync();
        await connector.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        fixture.Client.RetryConnection();
        var second = fixture.Client.StartRecoveryForTestingAsync();

        var firstOutcome = await first.WaitAsync(TimeSpan.FromSeconds(1));
        var secondOutcome = await second.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(firstOutcome.Connected);
        Assert.AreEqual("BROKER_RECOVERY_SUPERSEDED", firstOutcome.Failure?.Code);
        Assert.IsTrue(secondOutcome.Connected);
        Assert.AreEqual(2, connector.CallCount);
    }

    [TestMethod]
    public async Task ProtocolMismatch_IsTerminalAndNeverStartsBroker()
    {
        var connector = new ScriptedConnector(
            static _ => throw new BrokerProtocolMismatchException("fixture mismatch"));
        var launcher = new FakeProcessLauncher();
        using var fixture = CreateClient(connector, launcher, new FakeClock());

        var outcome = await fixture.Client.StartRecoveryForTestingAsync();

        Assert.IsFalse(outcome.Connected);
        Assert.AreEqual("BROKER_PROTOCOL_MISMATCH", outcome.Failure?.Code);
        Assert.IsFalse(outcome.Failure?.Retryable);
        Assert.AreEqual(0, launcher.LaunchCalls);
    }

    [TestMethod]
    public async Task ExplicitTrayExit_SuppressesRecoveryBeforeAnyBrokerLaunch()
    {
        var connector = new ScriptedConnector(
            static _ => throw new BrokerConnectTimeoutException(TimeSpan.FromMilliseconds(10)));
        var launcher = new FakeProcessLauncher();
        using var fixture = CreateClient(connector, launcher, new FakeClock());

        fixture.Client.SuppressBrokerRecoveryForExplicitExit();
        var outcome = await fixture.Client.StartRecoveryForTestingAsync();

        Assert.IsFalse(outcome.Connected);
        Assert.AreEqual("BROKER_APP_EXITED", outcome.Failure?.Code);
        Assert.AreEqual(0, outcome.LaunchCount);
        Assert.AreEqual(0, launcher.LaunchCalls);
    }

    [TestMethod]
    public void ConnectionDiagnostics_RemainsBoundedAndClassifiesStagesAndExitCodes()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "swsearch-rc52-diagnostic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "broker-connection.log");
        try
        {
            var diagnostics = new BrokerConnectionDiagnostics(path, maximumBytes: 4096);
            for (var index = 0; index < 100; index++)
            {
                diagnostics.Record(
                    "CHILD_EXIT",
                    BrokerConnectionFailureKind.ChildExited,
                    new string('x', 700),
                    BrokerChildExitCodes.TemporaryInstanceFailure);
            }

            var info = new FileInfo(path);
            Assert.IsTrue(info.Exists);
            Assert.IsTrue(info.Length <= 4096, $"Diagnostic grew to {info.Length} bytes.");
            var text = File.ReadAllText(path);
            StringAssert.Contains(text, "CHILD_EXIT");
            StringAssert.Contains(text, "|75|");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public void ExitCodeMirrorAndProductionSource_ProhibitProcessEnumerationOrTermination()
    {
        Assert.AreEqual(0, BrokerChildExitCodes.ExistingHealthyOrSuccess);
        Assert.AreEqual(70, BrokerChildExitCodes.StartupFailure);
        Assert.AreEqual(75, BrokerChildExitCodes.TemporaryInstanceFailure);

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SwSearch.App",
            "SearchClient.cs"));
        Assert.IsFalse(source.Contains("GetProcesses", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(".Kill(", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("TerminateProcess", StringComparison.Ordinal));
        StringAssert.Contains(source, "child?.Dispose()");
    }

    private static ClientFixture CreateClient(
        IBrokerConnector connector,
        IBrokerProcessLauncher launcher,
        IBrokerRecoveryClock clock)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "swsearch-rc52-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new BrokerSearchClientOptions(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100))
        {
            BackgroundRecoveryDeadline = TimeSpan.FromMilliseconds(500),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(200),
        };
        var client = new BrokerSearchClient(
            Path.Combine(root, "SwSearch.Broker.exe"),
            "swsearch-rc52-" + Guid.NewGuid().ToString("N"),
            null,
            options,
            new LocalCacheSearchReader(Path.Combine(root, "unused.db")),
            connector,
            launcher,
            clock,
            new BrokerConnectionDiagnostics(Path.Combine(root, "broker-connection.log"), 4096));
        return new ClientFixture(client, root);
    }

    private static BrokerResponse Success(BrokerRequest request) =>
        new(ContractVersions.BrokerPipe, request.RequestId, Success: true);

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class ClientFixture(BrokerSearchClient client, string root) : IDisposable
    {
        internal BrokerSearchClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class ScriptedConnector(
        params Func<BrokerRequest, BrokerResponse>[] steps) : IBrokerConnector
    {
        private readonly ConcurrentQueue<Func<BrokerRequest, BrokerResponse>> _steps = new(steps);
        private Func<BrokerRequest, BrokerResponse>? _last = steps.LastOrDefault();

        public ValueTask<BrokerResponse> SendAsync(
            string pipeName,
            BrokerRequest request,
            TimeSpan connectTimeout,
            TimeSpan responseTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_steps.TryDequeue(out var step))
            {
                _last = step;
            }
            return ValueTask.FromResult((_last ?? Success)(request));
        }
    }

    private sealed class FirstCallBlocksConnector : IBrokerConnector
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);
        internal TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<BrokerResponse> SendAsync(
            string pipeName,
            BrokerRequest request,
            TimeSpan connectTimeout,
            TimeSpan responseTimeout,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Success(request);
        }
    }

    private sealed class FakeProcessLauncher(params FakeChildProcess[] children) : IBrokerProcessLauncher
    {
        private readonly ConcurrentQueue<FakeChildProcess> _children = new(children);
        private int _launchCalls;
        internal int LaunchCalls => Volatile.Read(ref _launchCalls);
        internal int TerminationCalls => 0;

        public ILaunchedBrokerProcess Launch(ProcessStartInfo startInfo)
        {
            Interlocked.Increment(ref _launchCalls);
            return _children.TryDequeue(out var child) ? child : new FakeChildProcess();
        }
    }

    private sealed class FakeChildProcess(int? exitCode = null) : ILaunchedBrokerProcess
    {
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public bool TryGetExitCode(out int observedExitCode)
        {
            observedExitCode = exitCode.GetValueOrDefault();
            return exitCode.HasValue;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class FakeClock : IBrokerRecoveryClock
    {
        private long _ticks;

        public long GetTimestamp() => Volatile.Read(ref _ticks);

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(Volatile.Read(ref _ticks) - startingTimestamp);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Add(ref _ticks, delay.Ticks);
            return ValueTask.CompletedTask;
        }
    }
}
