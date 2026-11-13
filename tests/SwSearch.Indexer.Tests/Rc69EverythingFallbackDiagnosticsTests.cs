using System.Runtime.CompilerServices;
using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Indexer.Tests;

public sealed class Rc69EverythingFallbackDiagnosticsTests
{
    [Fact]
    public async Task ProbeAsync_UnavailableEverythingReturnsMergedDiagnosticEvidence()
    {
        var accelerator = new DiagnosticDiscoveryProvider(
            new RuntimeCapability(CapabilityState.Degraded, "EVERYTHING_IPC_NOT_FOUND", "missing"),
            EverythingDiscoveryDiagnostics.NotAttempted with
            {
                AvailabilityCode = "EVERYTHING_IPC_NOT_FOUND",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            []);
        var builtIn = new PlainDiscoveryProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            []);
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn);

        var capability = await fallback.ProbeAsync(CancellationToken.None);

        Assert.Equal("BUILTIN_DISCOVERY_READY", capability.Code);
        Assert.Equal("EVERYTHING_IPC_NOT_FOUND", capability.Details!["fallbackReason"]);
        Assert.Equal("false", capability.Details["everythingAttempted"]);
        Assert.Equal("false", capability.Details["everythingAvailable"]);
        Assert.Equal("EVERYTHING_IPC_NOT_FOUND", capability.Details["everythingProbeCode"]);
        Assert.Equal(capability, fallback.LastCapability);
    }

    [Theory]
    [InlineData("EVERYTHING_QUERY_TIMEOUT")]
    [InlineData("EVERYTHING_INVALID_REPLY")]
    public async Task ProtocolFailure_WhenEverythingAvailable_ThrowsInsteadOfFallingBack(string failureCode)
    {
        var accelerator = new EverythingDiscoveryProvider(new FailureTransport(failureCode), pageSize: 16);
        FileCandidate[] candidates =
        [
            new FileCandidate(
                @"C:\fixture\fallback.SLDPRT",
                SolidWorksDocumentKind.Part,
                1,
                new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero)),
        ];
        var builtIn = new PlainDiscoveryProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            candidates);
        await using var fallback = new FallbackDiscoveryProvider(accelerator, builtIn);

        EverythingQueryException? exception = null;
        try
        {
            await ReadAllAsync(fallback.DiscoverAsync(CancellationToken.None));
        }
        catch (EverythingQueryException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Equal(failureCode, exception.Code);
        var details = fallback.LastCapability.Details!;
        Assert.Equal("true", details["everythingAttempted"]);
        Assert.Equal("true", details["everythingAvailable"]);
        Assert.Equal("READY", details["everythingProbeCode"]);
        Assert.Equal("0", details["everythingPageCount"]);
        Assert.Equal("0", details["everythingEmittedEntries"]);
        Assert.Equal(failureCode, details["everythingFailureCode"]);
        Assert.Equal("false", details["everythingCompleted"]);
    }

    [Fact]
    public async Task ConstructionFailure_IsVisibleWhenBuiltInDiscoveryRemainsUsable()
    {
        var builtIn = new PlainDiscoveryProvider(
            new RuntimeCapability(CapabilityState.Available, "BUILTIN_DISCOVERY_READY", "ready"),
            []);
        await using var fallback = new FallbackDiscoveryProvider(
            accelerator: null,
            builtIn: builtIn,
            acceleratorConstructionError: "TypeInitializationException");

        var capability = await fallback.ProbeAsync(CancellationToken.None);

        Assert.Equal("BUILTIN_DISCOVERY_READY", capability.Code);
        var details = capability.Details!;
        Assert.Equal("false", details["everythingAttempted"]);
        Assert.Equal("false", details["everythingAvailable"]);
        Assert.Equal("EVERYTHING_ACCELERATOR_UNAVAILABLE", details["everythingProbeCode"]);
        Assert.Equal("EVERYTHING_ACCELERATOR_CONSTRUCTION_FAILED", details["everythingFailureCode"]);
        Assert.Equal("TypeInitializationException", details["everythingFailureType"]);
    }

    private static async Task<IReadOnlyList<FileCandidate>> ReadAllAsync(
        IAsyncEnumerable<FileCandidate> source)
    {
        var result = new List<FileCandidate>();
        await foreach (var candidate in source)
        {
            result.Add(candidate);
        }

        return result;
    }

    private sealed class DiagnosticDiscoveryProvider(
        RuntimeCapability capability,
        EverythingDiscoveryDiagnostics diagnostics,
        IReadOnlyList<FileCandidate> files) :
        IFileDiscoveryProvider,
        IEverythingDiscoveryDiagnosticsSource
    {
        public EverythingDiscoveryDiagnostics EverythingDiagnostics => diagnostics;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(capability);

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class PlainDiscoveryProvider(
        RuntimeCapability capability,
        IReadOnlyList<FileCandidate> files) : IFileDiscoveryProvider
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(capability);

        public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FailureTransport(string failureCode) : IEverythingQueryTransport
    {
        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "READY", "ready"));

        public ValueTask<EverythingQueryPage> QueryPageAsync(
            string search,
            uint offset,
            uint maximumResults,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<EverythingQueryPage>(
                new EverythingQueryException(failureCode, "simulated protocol failure"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
