using SwSearch.InstallerCoordinator;

return await InstallerCoordinatorProgram.RunAsync(args).ConfigureAwait(false);

internal static class InstallerCoordinatorProgram
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.Ordinal))
        {
            return await CoordinatorSelfTest.RunAsync().ConfigureAwait(false);
        }

        if (!InstallerCoordinatorCommandLine.TryParse(args, out var command, out var error) || command is null)
        {
            Console.Error.WriteLine(
                "This executable is an MSI-internal coordinator. " +
                "Use exactly one of --user-ipc or --privileged-handle. " + error);
            var explicitModeCount = args.Count(value =>
                string.Equals(value, "--user-ipc", StringComparison.Ordinal) ||
                string.Equals(value, "--privileged-handle", StringComparison.Ordinal));
            if (explicitModeCount != 1)
            {
                return (int)InstallerCoordinatorExitCode.Usage;
            }

            var exactPrivilegedCadShape =
                args.Length == 3 &&
                string.Equals(args[0], "--privileged-handle", StringComparison.Ordinal) &&
                string.Equals(args[1], "--custom-action-data", StringComparison.Ordinal);
            return exactPrivilegedCadShape
                ? (int)InstallerCustomActionData.GetDiagnosticExitCode(error)
                : (int)InstallerCoordinatorExitCode.ContextDataInvalid;
        }

        FileCoordinatorLog? log = null;
        try
        {
            log = new FileCoordinatorLog(command.LogPath);
            var ownerRunner = new InstallerCoordinationOwnerRunner(
                new SystemInstallerCoordinationMutexFactory(),
                new SelfProcessHardDeadlineWatchdogFactory(),
                log);
            var host = new InstallerCoordinatorHost(
                CoordinatorExecutionIdentity.Capture(),
                ownerRunner,
                new ProductionInstallerCoordinatorPhaseExecutor(log),
                log);
            return await host.RunAsync(command).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log?.Write($"coordinator-failure type={exception.GetType().Name} message={exception.Message}");
            Console.Error.WriteLine(
                $"SwSearch installer coordination failed in {command.Mode}: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return (int)InstallerCoordinatorExitCode.SoftwareFailure;
        }
        finally
        {
            log?.Dispose();
        }
    }

}
