namespace SwSearch.InstallerCoordinator;

internal static class CoordinatorSelfTest
{
    private const string UpgradeCode = "{D9158356-29B3-44D6-A15A-ACDCE8328703}";
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RunAsync()
    {
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("no product process", NoProductProcessAsync),
            ("RC12 unsupported shutdown fallback", Rc12UnsupportedShutdownAsync),
            ("unrelated same-name process", UnrelatedSameNameAsync),
            ("process exits during reconciliation", ExitedDuringReconciliationAsync),
            ("residual pipe without process", ResidualPipeAsync),
            ("old install root differs", OldInstallRootAsync),
            ("uncertain identity", UncertainIdentityAsync),
            ("foreground window retained", ForegroundWindowAsync),
            ("graceful shutdown succeeds", GracefulShutdownAsync),
            ("PID/start-time TOCTOU", ProcessReplacementAsync),
            ("windowless UI is always retained", WindowlessUiRetainedAsync),
            ("bounded respawn reconciliation", BoundedRespawnAsync),
            ("privileged-only CAD remains available", PrivilegedOnlyCadAsync),
            ("privileged-only CAD rejects injected user identity", PrivilegedOnlyCadRejectsIdentityAsync),
            ("privileged CAD diagnostics are fixed and non-sensitive", PrivilegedCadDiagnosticsAsync),
            ("user-bound CAD remains strict", UserBoundCadAsync)
        };

        var passed = 0;
        foreach (var check in checks)
        {
            await check.Run().ConfigureAwait(false);
            passed++;
        }

        Console.WriteLine($"Installer coordinator policy self-test: {passed}/{checks.Count} PASS");
        return 0;
    }

    private static async Task NoProductProcessAsync()
    {
        var fixture = Fixture.Create();
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 0 && outcome.RemainingProductProcesses.Count == 0, "empty state changed");
    }

    private static async Task Rc12UnsupportedShutdownAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(11, ProductComponent.Broker));
        fixture.Processes.Add(Owned(12, ProductComponent.MetadataWorker));
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Select(item => item.FileName).SequenceEqual(
            new[] { "SwSearch.Broker.exe", "SwSearch.Metadata.Worker.exe" }), "old background stop order changed");
    }

    private static async Task UnrelatedSameNameAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(21, ProductComponent.Broker) with { ImagePath = @"D:\Unrelated\SwSearch.Broker.exe" });
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 0 && outcome.IgnoredUnverifiedProcesses.Count == 1, "same-name isolation failed");
    }

    private static async Task ExitedDuringReconciliationAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(31, ProductComponent.Broker));
        fixture.Processes.ExitOnAcquire.Add(31);
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 0 && outcome.RemainingProductProcesses.Count == 0, "exited process treated as live");
    }

    private static async Task ResidualPipeAsync()
    {
        var fixture = Fixture.Create();
        fixture.Transport.BrokerResult = ShutdownRequestResult.Accepted;
        fixture.Transport.WorkerResult = ShutdownRequestResult.Accepted;
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            fixture.Products,
            fixture.Transport,
            new NoDelay(),
            new MemoryLog());
        var outcome = await coordinator.ShutdownAsync(
                new InstallerCoordinatorOptions(UpgradeCode, fixture.CurrentRoot, TimeSpan.Zero, 2),
                new ShutdownRequestContext(
                    "S-1-5-21-111111111-222222222-333333333-1001",
                    1,
                    Guid.Parse("87D8EC95-3AD7-4806-885B-264101E6D332")),
                CancellationToken.None)
            .ConfigureAwait(false);
        Require(
            outcome.Broker == ShutdownRequestResult.Accepted &&
            outcome.Worker == ShutdownRequestResult.Accepted,
            "residual transport state blocked");
    }

    private static async Task OldInstallRootAsync()
    {
        var fixture = Fixture.Create(oldRoot: @"D:\旧版 SwSearch", currentRoot: @"C:\Program Files\SwSearch");
        fixture.Processes.Add(Owned(41, ProductComponent.Broker, @"D:\旧版 SwSearch"));
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 1, "old install root was not honored");
    }

    private static async Task UncertainIdentityAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(51, ProductComponent.MetadataWorker) with { CompanyName = "Other.Company" });
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 0 && outcome.IgnoredUnverifiedProcesses.Count == 1, "uncertain identity was terminated");
    }

    private static async Task ForegroundWindowAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(61, ProductComponent.App) with { HasInteractiveWindow = true });
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 0 && outcome.RemainingProductProcesses.Count == 1, "foreground window was not retained");
    }

    private static async Task GracefulShutdownAsync()
    {
        var fixture = Fixture.Create();
        fixture.Transport.BrokerResult = ShutdownRequestResult.Accepted;
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            fixture.Products,
            fixture.Transport,
            new NoDelay(),
            new MemoryLog());
        var outcome = await coordinator.ShutdownAsync(
                new InstallerCoordinatorOptions(UpgradeCode, fixture.CurrentRoot, TimeSpan.Zero, 2),
                new ShutdownRequestContext(
                    "S-1-5-21-111111111-222222222-333333333-1001",
                    1,
                    Guid.Parse("87D8EC95-3AD7-4806-885B-264101E6D332")),
                CancellationToken.None)
            .ConfigureAwait(false);
        Require(
            outcome.Broker == ShutdownRequestResult.Accepted &&
            fixture.Processes.TerminationAttempts.Count == 0,
            "graceful shutdown used force");
    }

    private static async Task ProcessReplacementAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(81, ProductComponent.Broker));
        fixture.Processes.ReplacementOnAcquire[81] = Owned(81, ProductComponent.Broker) with
        {
            StartTimeUtc = Start.AddMinutes(1),
            CompanyName = "Unverified.Replacement"
        };
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 0 && fixture.Processes.TerminationAttempts.Count == 0, "replacement PID was terminated");
    }

    private static async Task WindowlessUiRetainedAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(91, ProductComponent.PropertyViewer) with { HasInteractiveWindow = false });
        fixture.Processes.Add(Owned(92, ProductComponent.App) with { HasInteractiveWindow = false });
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(
            outcome.Terminated.Count == 0 &&
            outcome.RemainingProductProcesses.Count == 2 &&
            fixture.Processes.TerminationAttempts.Count == 0,
            "windowless UI entered force-termination fallback");
    }

    private static async Task BoundedRespawnAsync()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(101, ProductComponent.Broker));
        var nextPid = 102;
        fixture.Processes.OnTerminate = snapshot =>
        {
            if (snapshot.FileName == "SwSearch.Broker.exe" && nextPid <= 104)
            {
                fixture.Processes.Add(Owned(nextPid++, ProductComponent.Broker));
            }
        };
        var outcome = await fixture.RunAsync().ConfigureAwait(false);
        Require(outcome.Terminated.Count == 3 && outcome.RemainingProductProcesses.Count == 1, "respawn reconciliation was not bounded");
    }

    private static Task PrivilegedOnlyCadAsync()
    {
        var cad =
            $"v=2;user=0;sid=;session=;upgrade={UpgradeCode};nonce=;" +
            $"root={InstallerContextContract.RegisteredRootPolicy};action=upgrade;deadline=12345";
        Require(
            InstallerCustomActionData.TryParse(cad, out var command, out _) &&
            command is
            {
                Mode: InstallerCoordinatorMode.PrivilegedHandle,
                HasInstallUserContext: false,
                InstallSessionId: -1,
                InstanceNonce: var nonce
            } &&
            nonce == Guid.Empty &&
            command.InstallUserSid == InstallerContextContract.MachineOnlyMutexSidMarker,
            "privileged-only CAD did not remain machine-only");
        return Task.CompletedTask;
    }

    private static Task PrivilegedOnlyCadRejectsIdentityAsync()
    {
        var cad =
            $"v=2;user=0;sid=S-1-5-18;session=0;upgrade={UpgradeCode};nonce=;" +
            $"root={InstallerContextContract.RegisteredRootPolicy};action=repair;deadline=12345";
        Require(
            !InstallerCustomActionData.TryParse(cad, out _, out _),
            "machine-only CAD accepted an injected identity");
        return Task.CompletedTask;
    }

    private static Task PrivilegedCadDiagnosticsAsync()
    {
        Require(
            InstallerCustomActionData.GetDiagnosticExitCode("CustomActionData is empty or too long.") ==
            InstallerCoordinatorExitCode.CadEnvelopeInvalid,
            "empty CAD diagnostic changed");
        Require(
            InstallerCustomActionData.GetDiagnosticExitCode("CustomActionData schema is incomplete or unsupported.") ==
            InstallerCoordinatorExitCode.CadSchemaInvalid,
            "schema CAD diagnostic changed");
        Require(
            InstallerCustomActionData.GetDiagnosticExitCode(
                "Machine-only CustomActionData must not contain user identity fields.") ==
            InstallerCoordinatorExitCode.CadMachineIdentityInvalid,
            "machine-only CAD diagnostic changed");
        Require(
            InstallerCustomActionData.GetDiagnosticExitCode("secret-value") ==
            InstallerCoordinatorExitCode.ContextDataInvalid,
            "unknown diagnostic leaked input-dependent data");
        return Task.CompletedTask;
    }

    private static Task UserBoundCadAsync()
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        const string nonce = "87D8EC95-3AD7-4806-885B-264101E6D332";
        var cad =
            $"v=2;user=1;sid={sid};session=1;upgrade={UpgradeCode};nonce={nonce};" +
            $"root={InstallerContextContract.RegisteredRootPolicy};action=install;deadline=12345";
        Require(
            InstallerCustomActionData.TryParse(cad, out var command, out _) &&
            command is { HasInstallUserContext: true, InstallSessionId: 1 } &&
            command.InstallUserSid == sid &&
            command.InstanceNonce == Guid.Parse(nonce),
            "user-bound CAD lost identity validation");
        return Task.CompletedTask;
    }

    private static ProcessSnapshot Owned(int processId, ProductComponent component, string root = @"C:\Program Files\SwSearch")
    {
        var specification = ComponentSpecification.For(component);
        return new ProcessSnapshot(
            processId,
            Start.AddSeconds(processId),
            Path.Combine(root, specification.FileName),
            specification.FileName,
            specification.AssemblyStem,
            specification.AssemblyStem,
            "1.2.0",
            specification.AssemblyStem + ".dll",
            false,
            $"VOL:FILE:{processId}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + message);
        }
    }

    private sealed class Fixture
    {
        private Fixture(
            FakeInstalledProductCatalog products,
            FakeProductProcessCatalog processes,
            FakeShutdownTransport transport,
            string currentRoot)
        {
            Products = products;
            Processes = processes;
            Transport = transport;
            CurrentRoot = currentRoot;
        }

        internal FakeInstalledProductCatalog Products { get; }
        internal FakeProductProcessCatalog Processes { get; }
        internal FakeShutdownTransport Transport { get; }
        internal string CurrentRoot { get; }

        internal static Fixture Create(
            string oldRoot = @"C:\Program Files\SwSearch",
            string currentRoot = @"C:\Program Files\SwSearch") =>
            new(
                new FakeInstalledProductCatalog(
                    new InstalledProductIdentity("{49D3B604-E741-4D2A-975B-D1E84C0A8528}", "1.2.0", oldRoot)),
                new FakeProductProcessCatalog(),
                new FakeShutdownTransport(),
                currentRoot);

        internal async Task<CoordinationOutcome> RunAsync()
        {
            var coordinator = new InstallationCoordinator(
                Products,
                Processes,
                new NoDelay(),
                new MemoryLog());
            return await coordinator.CoordinateAsync(
                    new InstallerCoordinatorOptions(UpgradeCode, CurrentRoot, TimeSpan.Zero, 2))
                .ConfigureAwait(false);
        }
    }

    private sealed class FakeInstalledProductCatalog(params InstalledProductIdentity[] products) : IInstalledProductCatalog
    {
        public IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(string upgradeCode, string currentInstallRoot) => products;
    }

    private sealed class FakeProductProcessCatalog : IProductProcessCatalog
    {
        private readonly Dictionary<int, ProcessSnapshot> _processes = [];

        internal HashSet<int> ExitOnAcquire { get; } = [];
        internal Dictionary<int, ProcessSnapshot> ReplacementOnAcquire { get; } = [];
        internal List<ProcessSnapshot> TerminationAttempts { get; } = [];
        internal Action<ProcessSnapshot>? OnTerminate { get; set; }

        internal void Add(ProcessSnapshot process) => _processes[process.ProcessId] = process;
        internal void Remove(int processId) => _processes.Remove(processId);

        public ValueTask<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessSnapshot>>(_processes.Values.OrderBy(item => item.ProcessId).ToArray());

        public ValueTask<ProcessLeaseAcquisition> AcquireTerminationLeaseAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            if (ExitOnAcquire.Remove(processId))
            {
                _processes.Remove(processId);
                return ValueTask.FromResult(new ProcessLeaseAcquisition(ProcessLeaseStatus.Exited));
            }

            if (ReplacementOnAcquire.Remove(processId, out var replacement))
            {
                _processes[processId] = replacement;
            }

            if (!_processes.TryGetValue(processId, out var process))
            {
                return ValueTask.FromResult(new ProcessLeaseAcquisition(ProcessLeaseStatus.Exited));
            }

            return ValueTask.FromResult(new ProcessLeaseAcquisition(
                ProcessLeaseStatus.Acquired,
                new FakeLease(this, process)));
        }

        private sealed class FakeLease(FakeProductProcessCatalog owner, ProcessSnapshot snapshot) : IProcessTerminationLease
        {
            public ProcessSnapshot Snapshot { get; } = snapshot;

            public SingleProcessTerminationResult TerminateSingleProcess()
            {
                owner.TerminationAttempts.Add(Snapshot);
                if (!owner._processes.Remove(Snapshot.ProcessId))
                {
                    return SingleProcessTerminationResult.AlreadyExited;
                }

                owner.OnTerminate?.Invoke(Snapshot);
                return SingleProcessTerminationResult.Terminated;
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeShutdownTransport : IProductShutdownTransport
    {
        internal ShutdownRequestResult BrokerResult { get; set; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult WorkerResult { get; set; } = ShutdownRequestResult.Unavailable;
        internal Action? OnBrokerShutdown { get; set; }

        public ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            OnBrokerShutdown?.Invoke();
            return ValueTask.FromResult(BrokerResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(WorkerResult);
    }

    private sealed class NoDelay : ICoordinatorDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class MemoryLog : ICoordinatorLog
    {
        public void Write(string message)
        {
        }
    }
}
