using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed class IndexingCoordinator
{
    private readonly IFileDiscoveryProvider _discovery;
    private readonly SqliteIndexStore _store;

    public IndexingCoordinator(IFileDiscoveryProvider discovery, SqliteIndexStore store)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<RuntimeCapability> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var capability = await _discovery.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (capability.State != CapabilityState.Available)
        {
            await _store.SetDiscoveryCapabilityAsync(capability, cancellationToken).ConfigureAwait(false);
            return capability;
        }

        try
        {
            if (_discovery is IIncrementalFileDiscoveryProvider incremental)
            {
                await _store.ApplyDiscoverySnapshotAsync(
                    incremental,
                    _discovery.GetType().Name,
                    cancellationToken).ConfigureAwait(false);
                // The store is the publication authority. A provider can
                // report all roots complete and still fault while its
                // enumerator/disposer is unwinding; that observation is
                // intentionally committed merge-only, so its persisted state
                // must drive the capability rather than the optimistic report.
                var snapshotStatus = await _store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (snapshotStatus.SourceState.Equals("partial", StringComparison.Ordinal))
                {
                    var terminalSkippedPrefixes = incremental.LastReport.Roots.Sum(static root =>
                        root.TerminalSkippedPrefixes.Count);
                    var partialDetails = new Dictionary<string, string>
                    {
                        ["incompleteRoots"] = incremental.LastReport.Roots.Count(static root =>
                                !root.FullyEnumerated)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["incompletePrefixes"] = incremental.LastReport.Roots.Sum(static root =>
                                root.IncompletePrefixes.Count)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["terminalSkippedPrefixes"] = terminalSkippedPrefixes
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    };
                    if (_discovery is FallbackDiscoveryProvider fallbackProvider &&
                        fallbackProvider.LastCapability.Details is { } fallbackDetails)
                    {
                        foreach (var pair in fallbackDetails)
                        {
                            partialDetails[pair.Key] = pair.Value;
                        }
                    }

                    var partial = new RuntimeCapability(
                        CapabilityState.Degraded,
                        "DISCOVERY_PARTIAL",
                        "部分文件夹暂时无法读取，已保留原有索引，其余文件夹已更新。",
                        partialDetails);
                    return partial;
                }
            }
            else
            {
                await _store.ApplyDiscoverySnapshotAsync(
                    _discovery.DiscoverAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            // The initial probe can select Everything/MFT, while a root-coverage
            // check may later require the fallback provider for this pass. Return
            // that effective source so the broker log and UI diagnosis describe
            // what actually ran rather than the optimistic preflight result.
            return _discovery is FallbackDiscoveryProvider fallback
                ? fallback.LastCapability
                : capability;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                DiscoverySnapshotIncompleteException => "DISCOVERY_SNAPSHOT_INCOMPLETE",
                DiscoveryUnavailableException unavailable => unavailable.Capability.Code,
                SwSearch.Everything.EverythingQueryException queryException => queryException.Code,
                _ => "DISCOVERY_QUERY_FAILED",
            };
            var details = new Dictionary<string, string>
            {
                ["exception"] = exception.GetType().Name,
            };
            if (_discovery is FallbackDiscoveryProvider fallbackProvider &&
                fallbackProvider.LastCapability.Details is { } fallbackDetails)
            {
                foreach (var pair in fallbackDetails)
                {
                    details[pair.Key] = pair.Value;
                }
            }

            var degraded = new RuntimeCapability(
                CapabilityState.Degraded,
                code,
                "文件发现本轮未完整完成；继续使用上一次成功缓存，不执行删除。",
                details);
            await _store.SetDiscoveryCapabilityAsync(degraded, cancellationToken).ConfigureAwait(false);
            return degraded;
        }
    }
}
