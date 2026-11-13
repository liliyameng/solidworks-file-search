namespace SwSearch.InstallerCoordinator;

internal sealed record CurrentUserIpcShutdownOutcome(
    int TrustedProductCount,
    ShutdownRequestResult App,
    ShutdownRequestResult PropertyViewer,
    ShutdownRequestResult Broker,
    ShutdownRequestResult Worker);

// This type is the complete current-user immediate action graph. It has no
// IProductProcessCatalog dependency and therefore cannot snapshot, lease, or
// terminate a process. Front ends receive only a current-session graceful
// shutdown event; background components retain their authenticated pipes.
internal sealed class CurrentUserIpcShutdownCoordinator(
    IInstalledProductCatalog productCatalog,
    IProductShutdownTransport shutdownTransport,
    ICoordinatorDelay delay,
    ICoordinatorLog log)
{
    internal async ValueTask<CurrentUserIpcShutdownOutcome> ShutdownAsync(
        InstallerCoordinatorOptions options,
        ShutdownRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        var products = productCatalog
            .GetRelatedProducts(options.UpgradeCode, options.CurrentInstallRoot)
            .Where(product => product.IsTrustedRegisteredRoot)
            .ToArray();
        log.Write($"trusted-related-products={products.Length}");
        foreach (var product in products)
        {
            log.Write(
                $"installed-product code={product.ProductCode} version={product.Version} " +
                $"root={product.InstallRoot}");
        }

        if (products.Length == 0)
        {
            return new CurrentUserIpcShutdownOutcome(
                0,
                ShutdownRequestResult.Unavailable,
                ShutdownRequestResult.Unavailable,
                ShutdownRequestResult.Unavailable,
                ShutdownRequestResult.Unavailable);
        }

        var appTask = shutdownTransport.ShutdownAppAsync(products, context, cancellationToken).AsTask();
        var viewerTask = shutdownTransport.ShutdownPropertyViewerAsync(products, context, cancellationToken).AsTask();
        await Task.WhenAll(appTask, viewerTask).ConfigureAwait(false);
        var app = await appTask.ConfigureAwait(false);
        var viewer = await viewerTask.ConfigureAwait(false);
        log.Write($"app-current-user-ipc={app}");
        log.Write($"property-viewer-current-user-ipc={viewer}");
        if (app == ShutdownRequestResult.Accepted || viewer == ShutdownRequestResult.Accepted)
        {
            // The current-user phase deliberately has no process catalog. Give
            // the two UI dispatchers one bounded grace interval to exit, then
            // continue with authenticated background endpoints. The outer MSI
            // phase deadline remains authoritative even for a hung UI thread.
            await delay.DelayAsync(options.GracefulExitWait, cancellationToken).ConfigureAwait(false);
        }

        var brokerTask = shutdownTransport.ShutdownBrokerAsync(products, context, cancellationToken).AsTask();
        var workerTask = shutdownTransport.ShutdownWorkerAsync(products, context, cancellationToken).AsTask();
        await Task.WhenAll(brokerTask, workerTask).ConfigureAwait(false);
        var broker = await brokerTask.ConfigureAwait(false);
        log.Write($"broker-current-user-ipc={broker}");
        var worker = await workerTask.ConfigureAwait(false);
        log.Write($"worker-current-user-ipc={worker}");
        return new CurrentUserIpcShutdownOutcome(products.Length, app, viewer, broker, worker);
    }
}
