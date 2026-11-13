using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc69DiscoveryDiagnosticLogTests
{
    [Fact]
    public void RefreshLog_IsStructuredAndPreservesEverythingEvidence()
    {
        var capability = new RuntimeCapability(
            CapabilityState.Available,
            "BUILTIN_DISCOVERY_FALLBACK",
            "fallback",
            new Dictionary<string, string>
            {
                ["everythingAttempted"] = "true",
                ["everythingAvailable"] = "true",
                ["everythingPageCount"] = "3",
                ["everythingEmittedEntries"] = "42",
                ["everythingFailureCode"] = "EVERYTHING_QUERY_TIMEOUT",
                ["fallbackReason"] = "EVERYTHING_QUERY_TIMEOUT",
            });
        var status = new IndexStatus(
            DiscoveryGeneration: 7,
            CacheMayBeStale: false,
            LastSuccessfulDiscoveryUtc: null,
            SourceState: "BUILTIN_DISCOVERY_FALLBACK",
            PresentFileCount: 42,
            PendingMetadataCount: 9);

        var message = IndexingRuntime.FormatDiscoveryLogMessage(
            capability,
            status,
            TimeSpan.FromMilliseconds(12.34));

        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        Assert.Equal("discovery-refresh", root.GetProperty("eventType").GetString());
        Assert.Equal("Available", root.GetProperty("capabilityState").GetString());
        Assert.Equal("BUILTIN_DISCOVERY_FALLBACK", root.GetProperty("capabilityCode").GetString());
        Assert.Equal(42, root.GetProperty("files").GetInt64());
        Assert.Equal(9, root.GetProperty("pending").GetInt64());
        var details = root.GetProperty("details");
        Assert.Equal("true", details.GetProperty("everythingAttempted").GetString());
        Assert.Equal("3", details.GetProperty("everythingPageCount").GetString());
        Assert.Equal("42", details.GetProperty("everythingEmittedEntries").GetString());
        Assert.Equal("EVERYTHING_QUERY_TIMEOUT", details.GetProperty("everythingFailureCode").GetString());
        Assert.Equal("EVERYTHING_QUERY_TIMEOUT", details.GetProperty("fallbackReason").GetString());
    }
}
