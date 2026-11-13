using SwSearch.Contracts;
using SwSearch.Indexer;
using SwSearch.Metadata.Worker;
using SwSearch.PropertyConfig;

namespace SwSearch.Broker;

internal static class Program
{
    public static async Task<int> Main()
    {
        var bootstrapLog = BrokerBootstrapLog.CreateDefault();
        try
        {
            bootstrapLog.Write("START", "Broker bootstrap entered.");
            // The Broker is background infrastructure: under CPU contention
            // the foreground (SOLIDWORKS, user apps) must always win the
            // scheduler. BelowNormal only bites when the machine is busy;
            // idle indexing proceeds at full speed.
            try
            {
                using var current = System.Diagnostics.Process.GetCurrentProcess();
                current.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Priority is best-effort; access-restricted hosts keep the default.
            }
            var acquisition = BrokerInstanceGuard.Acquire(
                BrokerInstanceGuardOptions.Default,
                new NamedPipeBrokerLivenessProbe(ContractVersions.BrokerPipeName));
            if (acquisition.Disposition == BrokerInstanceDisposition.ExistingHealthy)
            {
                bootstrapLog.Write("EXISTING_HEALTHY", "A responsive Broker already owns this session.");
                return acquisition.ExitCode;
            }

            if (acquisition.Disposition == BrokerInstanceDisposition.RetryableUnavailable)
            {
                bootstrapLog.Write(
                    "OWNER_UNHEALTHY_RETRY",
                    "The instance owner did not publish a pipe or release ownership before the bounded deadline.");
                return acquisition.ExitCode;
            }

            using var singleInstance = acquisition.Guard
                ?? throw new InvalidOperationException("The acquired Broker guard lease is missing.");
            if (acquisition.RecoveredAbandonedOwner)
            {
                bootstrapLog.Write("OWNER_ABANDONED_RECOVERED", "Recovered the Broker mutex from an abandoned owner.");
            }

            SwSearchPaths.EnsureDirectories();
            var settings = BrokerSettings.Load(Path.Combine(SwSearchPaths.DataDirectory, "settings.json"));
            var store = new SqliteIndexStore(SwSearchPaths.DatabasePath);
            var discovery = settings.CreateDiscovery();
            var runtimeLog = new RuntimeFileLog();
            var discoverySelection = BrokerDiscoveryDiagnostics.FormatSelection(settings, discovery);
            bootstrapLog.Write("DISCOVERY_PROVIDER_SELECTED", discoverySelection);
            await runtimeLog.WriteAsync(
                "INFO",
                "DISCOVERY_PROVIDER_SELECTED",
                discoverySelection,
                CancellationToken.None).ConfigureAwait(false);
            var metadataPipeName =
                $"{ContractVersions.MetadataPipeName}.broker.{Environment.ProcessId}.{Guid.NewGuid():N}";
            await using var metadata = new MetadataWorkerPoolClient(new MetadataWorkerPoolOptions(
                new MetadataWorkerClientOptions(
                    Path.Combine(AppContext.BaseDirectory, "SwSearch.Metadata.Worker.exe"),
                    PipeName: metadataPipeName,
                    ConnectTimeoutMilliseconds: 750,
                    MaximumProcessMemoryBytes: 256L * 1024 * 1024,
                    DiagnosticSink: (diagnostic, cancellationToken) => runtimeLog.WriteAsync(
                        diagnostic.Level,
                        diagnostic.Code,
                        diagnostic.Message,
                        cancellationToken)),
                WorkerCount: 2));
            var runtimeOptions = IndexingRuntimeOptions.Default with
            {
                MaximumConcurrentExtractions = 2,
                WorkerCircuitCooldownSeconds = 5,
            };
            await using var runtime = new IndexingRuntime(store, discovery, metadata, runtimeOptions, runtimeLog);
            // RC18 removed manual pause from the product. Always clear a stale RC17 pause
            // value so indexing resumes automatically after upgrade.
            runtime.SetManualPause(false);
            await using var propertyConfig = new PropertyCardConfigProvider(
                PropertyCardConfigProviderOptions.CreateDefault(SwSearchPaths.DataDirectory, AppContext.BaseDirectory));
            // One immutable parse per Broker session. Queries/property viewers always read
            // the in-memory/last-good snapshot and never wait for XLSX I/O.
            _ = propertyConfig.StartPreload();
            await using var host = new BrokerHost(
                runtime,
                propertyConfig,
                diagnosticSink: bootstrapLog.Write,
                // IdleExitDelay stays disabled in production: the Broker is
                // the resident file listener (watcher/USN hints) and metadata
                // parser. Listening requires a live handle, so the resident
                // footprint contract is "stay lean while listening", not
                // "exit". The opt-in remains for an explicit future
                // on-demand preference.
                options: BrokerHostOptions.Default);
            // BrokerHost publishes the listener before its background task awaits
            // IndexingRuntime.InitializeAsync/database migration.
            bootstrapLog.Write("PIPE_START", "Starting the Broker pipe listener.");
            await host.RunAsync(CancellationToken.None);
            bootstrapLog.Write("STOP", "Broker stopped normally.");
            return BrokerProcessExitCodes.Success;
        }
        catch (Exception exception)
        {
            bootstrapLog.WriteException("STARTUP_FATAL", exception);
            return BrokerProcessExitCodes.SoftwareFailure;
        }
    }
}
