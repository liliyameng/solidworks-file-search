using System.Xml.Linq;
using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc21FastUpgradeTests
{
    private const string UpgradeCode = "{D9158356-29B3-44D6-A15A-ACDCE8328703}";
    private static readonly InstalledProductIdentity Product =
        new("{OLD}", "1.2.0", @"C:\Program Files\SwSearch");

    [Fact]
    public async Task CurrentUser_NoRelatedProduct_PerformsNoSignalAndNoDelay()
    {
        var delay = new RecordingDelay();
        var transport = new RecordingTransport();
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new Catalog([]), transport, delay, new NullLog());

        var outcome = await coordinator.ShutdownAsync(
            InstallerCoordinatorOptions.CreateDefault(UpgradeCode, @"C:\Program Files\SwSearch"),
            Context(),
            CancellationToken.None);

        Assert.Equal(0, outcome.TrustedProductCount);
        Assert.Empty(transport.Calls);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task CurrentUser_UnavailableEndpoints_HaveNoFixedSerialDelay()
    {
        var delay = new RecordingDelay();
        var transport = new RecordingTransport();
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new Catalog([Product]), transport, delay, new NullLog());

        var outcome = await coordinator.ShutdownAsync(
            InstallerCoordinatorOptions.CreateDefault(UpgradeCode, Product.InstallRoot),
            Context(),
            CancellationToken.None);

        Assert.All(
            new[] { outcome.App, outcome.PropertyViewer, outcome.Broker, outcome.Worker },
            value => Assert.Equal(ShutdownRequestResult.Unavailable, value));
        Assert.Empty(delay.Delays);
        Assert.Equal(4, transport.Calls.Count);
    }

    [Fact]
    public async Task PrivilegedPhase_RelatedProductButNoProcesses_PerformsNoFixedDelay()
    {
        var delay = new RecordingDelay();
        var processes = new EmptyProcessCatalog();
        var coordinator = new InstallationCoordinator(
            new Catalog([Product]), processes, delay, new NullLog());

        var outcome = await coordinator.CoordinateAsync(
            InstallerCoordinatorOptions.CreateDefault(UpgradeCode, Product.InstallRoot));

        Assert.Empty(outcome.RemainingProductProcesses);
        Assert.Empty(delay.Delays);
        Assert.True(processes.SnapshotCount > 0);
    }

    private static ShutdownRequestContext Context() => new(
        "S-1-5-21-111111111-222222222-333333333-1001",
        42,
        Guid.Parse("B70396A2-CC4E-4AA4-9274-7FB24BEE7BF0"));

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

    private sealed class Catalog(IReadOnlyList<InstalledProductIdentity> products)
        : IInstalledProductCatalog
    {
        public IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(
            string upgradeCode,
            string currentInstallRoot) => products;
    }

    private sealed class RecordingTransport : IProductShutdownTransport
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ShutdownRequestResult> ShutdownAppAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => Record("App");

        public ValueTask<ShutdownRequestResult> ShutdownPropertyViewerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => Record("Viewer");

        public ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => Record("Broker");

        public ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) => Record("Worker");

        private ValueTask<ShutdownRequestResult> Record(string component)
        {
            lock (Calls) Calls.Add(component);
            return ValueTask.FromResult(ShutdownRequestResult.Unavailable);
        }
    }

    private sealed class EmptyProcessCatalog : IProductProcessCatalog
    {
        public int SnapshotCount { get; private set; }

        public ValueTask<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotCount++;
            return ValueTask.FromResult<IReadOnlyList<ProcessSnapshot>>([]);
        }

        public ValueTask<ProcessLeaseAcquisition> AcquireTerminationLeaseAsync(
            int processId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No target may acquire a lease.");
    }

    private sealed class RecordingDelay : ICoordinatorDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullLog : ICoordinatorLog
    {
        public void Write(string message) { }
    }
}
