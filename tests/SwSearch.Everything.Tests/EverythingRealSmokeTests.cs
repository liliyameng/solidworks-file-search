using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Everything.Tests;

public sealed class EverythingRealSmokeTests
{
    [Fact]
    [Trait("Category", "RequiresEverything")]
    public async Task RunningEverything_Query2ReturnsWellFormedSolidWorksCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var transport = new EverythingIpcTransport(TimeSpan.FromSeconds(10));
        var capability = await transport.ProbeAsync(CancellationToken.None);
        if (capability.State != CapabilityState.Available)
        {
            return;
        }

        var page = await transport.QueryPageAsync(
            SolidWorksEverythingQuery.Search,
            0,
            128,
            CancellationToken.None);
        Assert.Equal((uint)0, page.Offset);
        Assert.True(page.TotalItems >= page.ReturnedItems);
        Assert.All(page.Items, item =>
        {
            Assert.True(Path.IsPathFullyQualified(item.FullPath));
            Assert.True(SolidWorksEverythingQuery.TryGetKind(item.FullPath, out _));
            Assert.True(item.Size >= -1);
        });
    }
}
