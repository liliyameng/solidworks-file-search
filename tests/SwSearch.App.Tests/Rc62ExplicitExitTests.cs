using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc62ExplicitExitTests
{
    [TestMethod]
    public async Task ExplicitExit_MarksBeforeNoStartBrokerShutdown_AndIsIdempotent()
    {
        var marker = new FakeMarker();
        var requester = new RecordingShutdownRequester(marker);
        var recoverySuppressedAfterMarker = false;
        var coordinator = new AppExplicitExitCoordinator(
            marker,
            requester,
            TimeSpan.FromSeconds(1),
            static (_, _) => { },
            () => recoverySuppressedAfterMarker = marker.Marked);

        var first = coordinator.RequestExitAsync();
        var second = coordinator.RequestExitAsync();
        var result = await first;

        Assert.AreSame(first, second);
        Assert.IsTrue(marker.Marked);
        Assert.IsTrue(recoverySuppressedAfterMarker);
        Assert.IsTrue(requester.MarkerWasSetWhenRequested);
        Assert.AreEqual(1, marker.MarkCount);
        Assert.AreEqual(1, requester.RequestCount);
        Assert.IsTrue(result.MarkerWritten);
        Assert.IsTrue(result.BrokerAcknowledged);
        Assert.IsFalse(result.BrokerRequestTimedOut);
    }

    [TestMethod]
    public async Task ExplicitExit_BrokerTimeoutIsBounded_AndDoesNotPreventCompletion()
    {
        var marker = new FakeMarker();
        var requester = new NeverCompletingShutdownRequester();
        var coordinator = new AppExplicitExitCoordinator(
            marker,
            requester,
            TimeSpan.FromMilliseconds(80),
            static (_, _) => { });
        var watch = Stopwatch.StartNew();

        var result = await coordinator.RequestExitAsync();

        watch.Stop();
        Assert.IsTrue(marker.Marked);
        Assert.IsTrue(result.MarkerWritten);
        Assert.IsFalse(result.BrokerAcknowledged);
        Assert.IsTrue(result.BrokerRequestTimedOut);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Explicit exit exceeded its bounded Broker wait: {watch.Elapsed}.");
    }

    [TestMethod]
    public async Task EveryNewApplicationLaunch_ClearsTheSessionMarkerWithoutBlockingStartup()
    {
        var marker = new FakeMarker { Marked = true };
        var coordinator = new AppExplicitExitCoordinator(
            marker,
            new RecordingShutdownRequester(marker),
            recordFailure: static (_, _) => { });

        coordinator.ClearForApplicationLaunch();
        Assert.IsFalse(marker.Marked);
        await marker.DurableClearObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, marker.ClearCount);
    }

    [TestMethod]
    public void ProductionWiring_UsesTheExplicitExitPath_AndTaskpaneNeverStartsPastMarker()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "App.xaml.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "AppExplicitExitCoordinator.cs"));
        var protocol = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "BrokerProtocol.cs"));
        var taskpane = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "TaskpaneControl.cs"));

        StringAssert.Contains(app, "new AppTrayController(window, RequestExplicitUserExit)");
        StringAssert.Contains(app, "await _explicitExitCoordinator.RequestExitAsync();");
        StringAssert.Contains(app, "_explicitExitCoordinator.ClearForApplicationLaunch();");
        StringAssert.Contains(coordinator, "BrokerAppExitShutdownClient.RequestShutdownAsync(cancellationToken)");
        StringAssert.Contains(coordinator, "TimeSpan.FromSeconds(2)");
        StringAssert.Contains(protocol, "ThrowIfExplicitAppExitRequested();");
        StringAssert.Contains(protocol, "BrokerAppExitSessionMarkerCompatibility.IsMarkedForCurrentSession()");
        StringAssert.Contains(taskpane, "BrokerAppExitMarkedException");
        StringAssert.Contains(taskpane, "canRetry = _connectionFailed && !_explicitAppExitMarked");
    }

    [TestMethod]
    public void BrokerLaunchBoundary_UsesOnlyTheProcessMemoryExitGate()
    {
        var root = FindRepositoryRoot();
        var client = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "SearchClient.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "AppExplicitExitCoordinator.cs"));
        var marker = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Contracts", "BrokerAppExitProtocol.cs"));

        StringAssert.Contains(client, "BrokerAppExitSessionMarker.IsCurrentProcessLaunchSuppressed()");
        Assert.IsFalse(client.Contains("BrokerAppExitSessionMarker.IsMarkedForCurrentSession()", StringComparison.Ordinal));
        StringAssert.Contains(marker, "Volatile.Read(ref _currentProcessLaunchSuppressed)");
        StringAssert.Contains(coordinator, "_marker.ClearCurrentProcessLaunchSuppression();");
        StringAssert.Contains(coordinator, "Task.Run(ClearDurableMarkerForApplicationLaunch)");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FakeMarker : IAppExitSessionMarker
    {
        public bool Marked { get; set; }
        public int MarkCount { get; private set; }
        public int ClearCount { get; private set; }
        public TaskCompletionSource DurableClearObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryMark()
        {
            MarkCount++;
            Marked = true;
            return true;
        }

        public bool TryClear()
        {
            ClearCount++;
            Marked = false;
            DurableClearObserved.TrySetResult();
            return true;
        }

        public void ClearCurrentProcessLaunchSuppression() => Marked = false;
    }

    private sealed class RecordingShutdownRequester(FakeMarker marker) : IGracefulBrokerShutdownRequester
    {
        public int RequestCount { get; private set; }
        public bool MarkerWasSetWhenRequested { get; private set; }

        public ValueTask RequestShutdownAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            MarkerWasSetWhenRequested = marker.Marked;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverCompletingShutdownRequester : IGracefulBrokerShutdownRequester
    {
        public async ValueTask RequestShutdownAsync(CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }
}
