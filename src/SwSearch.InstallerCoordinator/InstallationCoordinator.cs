namespace SwSearch.InstallerCoordinator;

public sealed class InstallationCoordinator(
    IInstalledProductCatalog productCatalog,
    IProductProcessCatalog processCatalog,
    ICoordinatorDelay delay,
    ICoordinatorLog log)
{
    private readonly List<ProcessSnapshot> _terminated = [];
    private readonly Dictionary<(int ProcessId, long StartTicks), ProcessSnapshot> _ignored = [];

    public async ValueTask<CoordinationOutcome> CoordinateAsync(
        InstallerCoordinatorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumReconciliationPasses is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumReconciliationPasses));
        }

        var products = productCatalog.GetRelatedProducts(options.UpgradeCode, options.CurrentInstallRoot);
        log.Write($"related-products={products.Count}");
        foreach (var product in products)
        {
            log.Write($"installed-product code={product.ProductCode} version={product.Version} root={product.InstallRoot}");
        }

        if (products.Count == 0)
        {
            log.Write("No installed SwSearch product identity was found; no process action is authorized.");
            return new CoordinationOutcome([], [], [], 0);
        }

        log.Write("coordination-mode=privileged-strict-handle-only; current-user IPC is physically absent");
        await ReconcileComponentAsync(ProductComponent.Broker, products, options, cancellationToken)
            .ConfigureAwait(false);

        await ReconcileComponentAsync(ProductComponent.MetadataWorker, products, options, cancellationToken)
            .ConfigureAwait(false);

        // Front ends may otherwise recreate the Broker while MSI is validating files.
        // Visible windows are deliberately retained for Restart Manager/FilesInUse.
        await ReconcileComponentAsync(ProductComponent.App, products, options, cancellationToken)
            .ConfigureAwait(false);
        await ReconcileComponentAsync(ProductComponent.PropertyViewer, products, options, cancellationToken)
            .ConfigureAwait(false);

        for (var pass = 0; pass < options.MaximumReconciliationPasses; pass++)
        {
            await ReconcileComponentAsync(ProductComponent.Broker, products, options, cancellationToken)
                .ConfigureAwait(false);
            await ReconcileComponentAsync(ProductComponent.MetadataWorker, products, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var final = await processCatalog.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var remaining = new List<ProcessSnapshot>();
        foreach (var process in final)
        {
            if (TryMatchInstalledProduct(process, products, out _, out var reason))
            {
                remaining.Add(process);
                log.Write(
                    $"remaining component={process.FileName} pid={process.ProcessId} " +
                    $"interactive={process.HasInteractiveWindow}; Restart Manager will present the actual lock and Retry.");
            }
            else if (IsExpectedFileName(process.FileName))
            {
                AddIgnored(process, reason);
            }
        }

        return new CoordinationOutcome(
            _terminated.ToArray(),
            remaining,
            _ignored.Values.ToArray(),
            options.MaximumReconciliationPasses);
    }

    private async ValueTask ReconcileComponentAsync(
        ProductComponent component,
        IReadOnlyList<InstalledProductIdentity> products,
        InstallerCoordinatorOptions options,
        CancellationToken cancellationToken)
    {
        var specification = ComponentSpecification.For(component);
        var snapshot = await processCatalog.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<ProcessSnapshot>();

        foreach (var process in snapshot.Where(process =>
                     string.Equals(process.FileName, specification.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryMatchInstalledProduct(process, products, out var installedProduct, out var reason))
            {
                AddIgnored(process, reason);
                continue;
            }

            if (!CanForceTerminate(component))
            {
                // App and PropertyViewer are interactive products even while their
                // window is hidden, minimized, being created/destroyed, or not
                // visible across sessions. They are never eligible for Kill().
                log.Write(
                    $"retained-ui component={process.FileName} pid={process.ProcessId} " +
                    $"visible-window={process.HasInteractiveWindow}; Restart Manager/FilesInUse must request closure and Retry.");
                continue;
            }

            if (process.HasInteractiveWindow)
            {
                log.Write(
                    $"retained-interactive component={process.FileName} pid={process.ProcessId} " +
                    $"root={installedProduct!.InstallRoot}; no silent termination is permitted.");
                continue;
            }

            candidates.Add(process);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        await delay.DelayAsync(options.GracefulExitWait, cancellationToken).ConfigureAwait(false);
        foreach (var original in candidates)
        {
            var acquisition = await processCatalog.AcquireTerminationLeaseAsync(original.ProcessId, cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Status == ProcessLeaseStatus.Exited)
            {
                log.Write($"graceful-exit component={original.FileName} pid={original.ProcessId}");
                continue;
            }

            if (acquisition.Status != ProcessLeaseStatus.Acquired || acquisition.Lease is null)
            {
                log.Write(
                    $"termination-lease-refused component={original.FileName} pid={original.ProcessId} " +
                    $"status={acquisition.Status}; Restart Manager fallback remains active.");
                continue;
            }

            using var lease = acquisition.Lease;
            var current = lease.Snapshot;
            if (!ProcessIdentityContinuity.IsSameInstance(original, current))
            {
                log.Write($"toctou-refused pid={original.ProcessId}; PID/start-time/final-image-object identity changed.");
                continue;
            }

            if (!TryMatchInstalledProduct(current, products, out var installedProduct, out var reason))
            {
                AddIgnored(current, "termination recheck: " + reason);
                continue;
            }

            if (current.HasInteractiveWindow)
            {
                log.Write($"termination-refused component={current.FileName} pid={current.ProcessId}; window became interactive.");
                continue;
            }

            var termination = lease.TerminateSingleProcess();
            if (termination == SingleProcessTerminationResult.Terminated)
            {
                _terminated.Add(current);
                log.Write(
                    $"terminated-verified-background component={current.FileName} pid={current.ProcessId} " +
                    $"start={current.StartTimeUtc:O} image={current.ImagePath} product={installedProduct!.ProductCode}");
            }
            else if (termination == SingleProcessTerminationResult.AlreadyExited)
            {
                log.Write($"already-exited component={current.FileName} pid={current.ProcessId}");
            }
            else
            {
                log.Write($"terminate-failed component={current.FileName} pid={current.ProcessId}; Restart Manager fallback remains active.");
            }
        }
    }

    private void AddIgnored(ProcessSnapshot process, string reason)
    {
        var key = (process.ProcessId, process.StartTimeUtc.UtcTicks);
        if (_ignored.TryAdd(key, process))
        {
            log.Write($"ignored-unverified name={process.FileName} pid={process.ProcessId}; {reason}");
        }
    }

    private static bool TryMatchInstalledProduct(
        ProcessSnapshot process,
        IReadOnlyList<InstalledProductIdentity> products,
        out InstalledProductIdentity? installedProduct,
        out string reason)
    {
        installedProduct = null;
        var specification = ComponentSpecification.All.FirstOrDefault(item =>
            string.Equals(item.FileName, process.FileName, StringComparison.OrdinalIgnoreCase));
        if (specification is null)
        {
            reason = "unexpected executable name";
            return false;
        }

        if (!string.Equals(process.CompanyName, specification.AssemblyStem, StringComparison.Ordinal) ||
            !string.Equals(process.ProductName, specification.AssemblyStem, StringComparison.Ordinal) ||
            !string.Equals(process.OriginalFileName, specification.AssemblyStem + ".dll", StringComparison.Ordinal))
        {
            reason = "first-party file identity mismatch";
            return false;
        }

        foreach (var product in products)
        {
            if (!product.IsTrustedRegisteredRoot)
            {
                continue;
            }

            if (!string.Equals(process.ProductVersion, product.Version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsWithinRoot(process.ImagePath, product.InstallRoot))
            {
                installedProduct = product;
                reason = string.Empty;
                return true;
            }
        }

        reason = "image path/version is not bound to an installed SwSearch product";
        return false;
    }

    private static bool IsWithinRoot(string imagePath, string installRoot)
        => FinalPathTrustPolicy.IsProcessInsideRegisteredRoot(imagePath, installRoot);

    private static bool IsExpectedFileName(string fileName) =>
        ComponentSpecification.All.Any(item =>
            string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    private static bool CanForceTerminate(ProductComponent component) =>
        component is ProductComponent.Broker or ProductComponent.MetadataWorker;
}

internal static class ProcessIdentityContinuity
{
    internal static bool IsSameInstance(ProcessSnapshot evidence, ProcessSnapshot lease) =>
        evidence.ProcessId == lease.ProcessId &&
        evidence.StartTimeUtc == lease.StartTimeUtc &&
        string.Equals(evidence.ImagePath, lease.ImagePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(evidence.ImageObjectId, lease.ImageObjectId, StringComparison.Ordinal);
}
