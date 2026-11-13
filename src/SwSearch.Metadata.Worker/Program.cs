using System.Text.Json;
using SwSearch.Metadata.Worker;

WorkerReadySignal.StartupArguments? startupArguments;
try
{
    startupArguments = WorkerReadySignal.ParseStartupArguments(args);
}
catch (ArgumentException)
{
    Console.Error.WriteLine("Unsupported worker arguments.");
    Environment.ExitCode = 2;
    return;
}

var services = WorkerComposition.CreateDefault();

if (args.Length == 1 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
{
    var capability = await services.CapabilityProbe.ProbeAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(capability, PipeJson.Options));
    return;
}

try
{
    await using var host = new MetadataPipeHost(
        services.RequestProcessor,
        services.CapabilityProbe,
        startupArguments?.PipeName ?? ContractOptions.DefaultPipeName,
        maximumConcurrentExtractions: 1,
        workerGenerationNonce: startupArguments?.ReadyNonce ?? Guid.Empty);
    using var readySignal = startupArguments is { } startup
        ? WorkerReadySignal.Create(startup.ReadyNonce)
        : null;
    readySignal?.Set();
    await host.RunAsync(CancellationToken.None);
}
catch (IOException)
{
    // Another worker for this user already owns the single-instance pipe.
    Environment.ExitCode = 2;
}
