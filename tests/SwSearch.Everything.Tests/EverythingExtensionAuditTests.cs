using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Everything.Tests;

public sealed class EverythingExtensionAuditTests
{
    [Fact]
    public void Query_RecognizesOnlyPartsAndAssemblies()
    {
        Assert.True(SolidWorksEverythingQuery.TryGetKind(
            @"C:\library\part.SLDPRT",
            out var part));
        Assert.Equal(SolidWorksDocumentKind.Part, part);
        Assert.True(SolidWorksEverythingQuery.TryGetKind(
            @"C:\library\assembly.SLDASM",
            out var assembly));
        Assert.Equal(SolidWorksDocumentKind.Assembly, assembly);
        Assert.False(SolidWorksEverythingQuery.TryGetKind(
            @"C:\library\legacy.SLDDRW",
            out _));
    }

    [Fact]
    public async Task Audit_CountsSupportedExtensionsAndRejectsDuplicatesOrUnexpectedTypes()
    {
        var now = DateTimeOffset.UtcNow;
        await using var valid = new FakeTransport(
            new EverythingQueryPage(2, 0, 2,
            [
                new FileCandidate(@"C:\a.SLDPRT", SolidWorksDocumentKind.Part, 1, now),
                new FileCandidate(@"C:\b.SLDASM", SolidWorksDocumentKind.Assembly, 1, now)
            ]));
        var validResult = await EverythingExtensionAudit.RunAsync(valid, pageSize: 2);
        Assert.True(validResult.IsValid);
        Assert.Equal(1, validResult.Parts);
        Assert.Equal(1, validResult.Assemblies);

        await using var invalid = new FakeTransport(
            new EverythingQueryPage(3, 0, 3,
            [
                new FileCandidate(@"C:\legacy.SLDDRW", SolidWorksDocumentKind.Drawing, 1, now),
                new FileCandidate(@"C:\same.SLDASM", SolidWorksDocumentKind.Assembly, 1, now),
                new FileCandidate(@"C:\same.SLDASM", SolidWorksDocumentKind.Assembly, 1, now)
            ]));
        var invalidResult = await EverythingExtensionAudit.RunAsync(invalid, pageSize: 3);
        Assert.False(invalidResult.IsValid);
        Assert.Equal(1, invalidResult.DuplicatePaths);
        Assert.Equal(1, invalidResult.InvalidExtensions);
    }

    private sealed class FakeTransport(params EverythingQueryPage[] pages) : IEverythingQueryTransport
    {
        private int _index;

        public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RuntimeCapability(CapabilityState.Available, "READY", "ready"));

        public ValueTask<EverythingQueryPage> QueryPageAsync(
            string search,
            uint offset,
            uint maximumResults,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(pages[_index++]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
