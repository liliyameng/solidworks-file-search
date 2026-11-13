namespace SwSearch.InstallerCoordinator;

public enum ProductComponent
{
    App,
    Broker,
    PropertyViewer,
    MetadataWorker
}

public enum ShutdownRequestResult
{
    Accepted,
    Unsupported,
    Unavailable,
    Failed
}

public enum ProcessLeaseStatus
{
    Acquired,
    Exited,
    AccessDenied,
    Unverifiable
}

public enum SingleProcessTerminationResult
{
    Terminated,
    AlreadyExited,
    Failed
}

public sealed record InstalledProductIdentity(
    string ProductCode,
    string Version,
    string InstallRoot,
    bool IsTrustedRegisteredRoot = true);

public sealed record ProcessSnapshot(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    string ImagePath,
    string FileName,
    string? CompanyName,
    string? ProductName,
    string? ProductVersion,
    string? OriginalFileName,
    bool HasInteractiveWindow,
    string ImageObjectId);

public sealed record ProcessLeaseAcquisition(
    ProcessLeaseStatus Status,
    IProcessTerminationLease? Lease = null);

public interface IProcessTerminationLease : IDisposable
{
    ProcessSnapshot Snapshot { get; }

    SingleProcessTerminationResult TerminateSingleProcess();
}

public sealed record CoordinationOutcome(
    IReadOnlyList<ProcessSnapshot> Terminated,
    IReadOnlyList<ProcessSnapshot> RemainingProductProcesses,
    IReadOnlyList<ProcessSnapshot> IgnoredUnverifiedProcesses,
    int ReconciliationPasses);

public sealed record ShutdownRequestContext(
    string ExpectedUserSid,
    int ExpectedSessionId,
    Guid SessionNonce);

public interface IInstalledProductCatalog
{
    IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(
        string upgradeCode,
        string currentInstallRoot);
}

public interface IProductProcessCatalog
{
    ValueTask<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken cancellationToken);

    ValueTask<ProcessLeaseAcquisition> AcquireTerminationLeaseAsync(
        int processId,
        CancellationToken cancellationToken);
}

public interface IProductShutdownTransport
{
    ValueTask<ShutdownRequestResult> ShutdownAppAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(ShutdownRequestResult.Unavailable);

    ValueTask<ShutdownRequestResult> ShutdownPropertyViewerAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(ShutdownRequestResult.Unavailable);

    ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken);

    ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
        IReadOnlyList<InstalledProductIdentity> products,
        ShutdownRequestContext context,
        CancellationToken cancellationToken);
}

public interface ICoordinatorDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface ICoordinatorLog
{
    void Write(string message);
}

public sealed record InstallerCoordinatorOptions(
    string UpgradeCode,
    string CurrentInstallRoot,
    TimeSpan GracefulExitWait,
    int MaximumReconciliationPasses = 2)
{
    public static InstallerCoordinatorOptions CreateDefault(string upgradeCode, string currentInstallRoot) =>
        new(upgradeCode, currentInstallRoot, TimeSpan.FromMilliseconds(750), 2);
}

internal sealed record ComponentSpecification(
    ProductComponent Component,
    string FileName,
    string AssemblyStem)
{
    internal static IReadOnlyList<ComponentSpecification> All { get; } =
    [
        new(ProductComponent.App, "SwSearch.App.exe", "SwSearch.App"),
        new(ProductComponent.Broker, "SwSearch.Broker.exe", "SwSearch.Broker"),
        new(ProductComponent.PropertyViewer, "SwSearch.PropertyViewer.exe", "SwSearch.PropertyViewer"),
        new(ProductComponent.MetadataWorker, "SwSearch.Metadata.Worker.exe", "SwSearch.Metadata.Worker")
    ];

    internal static ComponentSpecification For(ProductComponent component) =>
        All.Single(item => item.Component == component);
}
