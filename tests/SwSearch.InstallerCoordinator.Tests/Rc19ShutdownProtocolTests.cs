using System.Diagnostics;
using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc19ShutdownProtocolTests
{
    private const string UserSid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const int SessionId = 42;
    private static readonly Guid Nonce = Guid.Parse("87D8EC95-3AD7-4806-885B-264101E6D332");
    private static readonly InstalledProductIdentity[] Products =
    [
        new("{OLD-PRODUCT}", "1.2.0", @"C:\Program Files\SwSearch")
    ];

    [Fact]
    public async Task CurrentUserPhase_RequestsAllFourComponentsAndUsesBoundedUiGrace()
    {
        var transport = new RecordingTransport
        {
            AppResult = ShutdownRequestResult.Accepted,
            ViewerResult = ShutdownRequestResult.Accepted,
            BrokerResult = ShutdownRequestResult.Accepted,
            WorkerResult = ShutdownRequestResult.Accepted
        };
        var delay = new RecordingDelay();
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new ProductCatalog(Products), transport, delay, new NullLog());
        var options = new InstallerCoordinatorOptions(
            "{D9158356-29B3-44D6-A15A-ACDCE8328703}",
            @"C:\Program Files\SwSearch",
            TimeSpan.FromMilliseconds(375));

        var outcome = await coordinator.ShutdownAsync(options, ValidContext(), CancellationToken.None);

        Assert.Equal(new[] { "App", "PropertyViewer", "Broker", "Worker" }, transport.Calls);
        Assert.Equal(ShutdownRequestResult.Accepted, outcome.App);
        Assert.Equal(ShutdownRequestResult.Accepted, outcome.PropertyViewer);
        Assert.Equal(ShutdownRequestResult.Accepted, outcome.Broker);
        Assert.Equal(ShutdownRequestResult.Accepted, outcome.Worker);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(375) }, delay.Delays);
    }

    [Fact]
    public async Task CurrentUserPhase_RepeatedAndMissingEndpointsRemainIdempotent()
    {
        var transport = new RecordingTransport();
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new ProductCatalog(Products), transport, new RecordingDelay(), new NullLog());
        var options = InstallerCoordinatorOptions.CreateDefault(
            "{D9158356-29B3-44D6-A15A-ACDCE8328703}", @"C:\Program Files\SwSearch");

        var first = await coordinator.ShutdownAsync(options, ValidContext(), CancellationToken.None);
        var second = await coordinator.ShutdownAsync(options, ValidContext(), CancellationToken.None);

        Assert.All(
            new[] { first.App, first.PropertyViewer, first.Broker, first.Worker,
                    second.App, second.PropertyViewer, second.Broker, second.Worker },
            result => Assert.Equal(ShutdownRequestResult.Unavailable, result));
        Assert.Equal(2, transport.Calls.Count(call => call == "App"));
        Assert.Equal(2, transport.Calls.Count(call => call == "PropertyViewer"));
        Assert.Equal(2, transport.Calls.Count(call => call == "Broker"));
        Assert.Equal(2, transport.Calls.Count(call => call == "Worker"));
    }

    [Fact]
    public async Task CurrentUserPhase_NonResponsiveDependencyIsCancelledWithinShortDeadline()
    {
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new ProductCatalog(Products), new NonResponsiveTransport(), new RecordingDelay(), new NullLog());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.ShutdownAsync(
                InstallerCoordinatorOptions.CreateDefault(
                    "{D9158356-29B3-44D6-A15A-ACDCE8328703}", @"C:\Program Files\SwSearch"),
                ValidContext(),
                timeout.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task FrontEndSignal_ValidCurrentUserSessionClosesBothEndpointsAndCanRepeat()
    {
        var appName = @"Local\SwSearch.Rc19.App." + Guid.NewGuid().ToString("N");
        var viewerName = @"Local\SwSearch.Rc19.Viewer." + Guid.NewGuid().ToString("N");
        using var appEvent = new EventWaitHandle(false, EventResetMode.ManualReset, appName);
        using var viewerEvent = new EventWaitHandle(false, EventResetMode.ManualReset, viewerName);
        var transport = CreateFrontEndTransport(appName, viewerName);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var appObserved = Task.Run(() => appEvent.WaitOne(TimeSpan.FromSeconds(2)));
            using var viewerReady = new CountdownEvent(2);
            var viewerObserved = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                viewerReady.Signal();
                return viewerEvent.WaitOne(TimeSpan.FromSeconds(2));
            })).ToArray();
            Assert.True(viewerReady.Wait(TimeSpan.FromSeconds(1)));

            Assert.Equal(
                ShutdownRequestResult.Accepted,
                await transport.ShutdownAppAsync(Products, ValidContext(), CancellationToken.None));
            Assert.Equal(
                ShutdownRequestResult.Accepted,
                await transport.ShutdownPropertyViewerAsync(Products, ValidContext(), CancellationToken.None));
            Assert.True(await appObserved);
            Assert.All(await Task.WhenAll(viewerObserved), Assert.True);
        }
    }

    [Fact]
    public async Task FrontEndSignal_MissingEndpointsReturnUnavailableWithoutRetryLoop()
    {
        var transport = CreateFrontEndTransport(
            @"Local\SwSearch.Rc19.MissingApp." + Guid.NewGuid().ToString("N"),
            @"Local\SwSearch.Rc19.MissingViewer." + Guid.NewGuid().ToString("N"));
        var stopwatch = Stopwatch.StartNew();

        Assert.Equal(
            ShutdownRequestResult.Unavailable,
            await transport.ShutdownAppAsync(Products, ValidContext(), CancellationToken.None));
        Assert.Equal(
            ShutdownRequestResult.Unavailable,
            await transport.ShutdownPropertyViewerAsync(Products, ValidContext(), CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("S-1-5-21-999999999-222222222-333333333-1001", SessionId, "87D8EC95-3AD7-4806-885B-264101E6D332")]
    [InlineData(UserSid, SessionId + 1, "87D8EC95-3AD7-4806-885B-264101E6D332")]
    [InlineData(UserSid, SessionId, "11111111-1111-1111-1111-111111111111")]
    public async Task FrontEndSignal_WrongIdentityContextIsRejectedWithoutSettingEvent(
        string sid,
        int sessionId,
        string nonce)
    {
        var eventName = @"Local\SwSearch.Rc19.Reject." + Guid.NewGuid().ToString("N");
        using var endpoint = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var transport = CreateFrontEndTransport(eventName, eventName);

        var result = await transport.ShutdownAppAsync(
            Products,
            new ShutdownRequestContext(sid, sessionId, Guid.Parse(nonce)),
            CancellationToken.None);

        Assert.Equal(ShutdownRequestResult.Failed, result);
        Assert.False(endpoint.WaitOne(TimeSpan.Zero));
    }

    [Fact]
    public void AppAndViewer_CreateManualResetShutdownEndpointBeforeShowingWindow()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "SwSearch.App", "App.xaml.cs"));
        var viewer = File.ReadAllText(Path.Combine(root, "src", "SwSearch.PropertyViewer", "App.xaml.cs"));

        AssertEndpointBeforeShow(
            app,
            "ContractVersions.AppInstallerShutdownEventName",
            "Dispatcher.BeginInvoke((Action)RequestApplicationExit)");
        Assert.Contains("window.RequestProcessExit();", app, StringComparison.Ordinal);
        AssertEndpointBeforeShow(
            viewer,
            "ContractVersions.PropertyViewerInstallerShutdownEventName",
            "Dispatcher.BeginInvoke((Action)(() => Shutdown()))");
    }

    private static void AssertEndpointBeforeShow(
        string source,
        string contractName,
        string dispatcherShutdown)
    {
        Assert.Contains(contractName, source, StringComparison.Ordinal);
        Assert.Contains("EventResetMode.ManualReset", source, StringComparison.Ordinal);
        var listener = source.IndexOf("StartInstallerShutdownListener();", StringComparison.Ordinal);
        var show = source.IndexOf("window.Show();", StringComparison.Ordinal);
        Assert.True(listener >= 0 && show > listener, "Installer shutdown listener must be armed before the window is shown.");
        Assert.Contains(dispatcherShutdown, source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Kill", source, StringComparison.Ordinal);
    }

    private static ProductShutdownTransport CreateFrontEndTransport(string appName, string viewerName) =>
        new(
            TimeSpan.FromMilliseconds(250),
            appEventName: _ => appName,
            viewerEventName: _ => viewerName,
            captureIdentity: () => new CoordinatorExecutionIdentity(UserSid, false, SessionId));

    private static ShutdownRequestContext ValidContext() => new(UserSid, SessionId, Nonce);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class ProductCatalog(IReadOnlyList<InstalledProductIdentity> products)
        : IInstalledProductCatalog
    {
        public IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(
            string upgradeCode,
            string currentInstallRoot) => products;
    }

    private sealed class RecordingTransport : IProductShutdownTransport
    {
        internal ShutdownRequestResult AppResult { get; init; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult ViewerResult { get; init; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult BrokerResult { get; init; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult WorkerResult { get; init; } = ShutdownRequestResult.Unavailable;
        internal List<string> Calls { get; } = [];

        public ValueTask<ShutdownRequestResult> ShutdownAppAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("App");
            return ValueTask.FromResult(AppResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownPropertyViewerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("PropertyViewer");
            return ValueTask.FromResult(ViewerResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("Broker");
            return ValueTask.FromResult(BrokerResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("Worker");
            return ValueTask.FromResult(WorkerResult);
        }
    }

    private sealed class NonResponsiveTransport : IProductShutdownTransport
    {
        public ValueTask<ShutdownRequestResult> ShutdownAppAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => NeverAsync(cancellationToken);

        public ValueTask<ShutdownRequestResult> ShutdownPropertyViewerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => NeverAsync(cancellationToken);

        public ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => NeverAsync(cancellationToken);

        public ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => NeverAsync(cancellationToken);

        private static async ValueTask<ShutdownRequestResult> NeverAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ShutdownRequestResult.Failed;
        }
    }

    private sealed class RecordingDelay : ICoordinatorDelay
    {
        internal List<TimeSpan> Delays { get; } = [];

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullLog : ICoordinatorLog
    {
        public void Write(string message)
        {
        }
    }
}
