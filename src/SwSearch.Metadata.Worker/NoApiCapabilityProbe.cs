using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal interface IMetadataCapabilityProbe
{
    ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken);
}

internal sealed class NoApiCapabilityProbe : IMetadataCapabilityProbe
{
    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RuntimeCapability(
            CapabilityState.Available,
            "NOAPI_AVAILABLE",
            "内置 SOLIDWORKS 属性解析器可用；无需 SOLIDWORKS、Document Manager 或外部索引程序。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["metadataReader"] = "built-in-read-only",
                ["modernVersions"] = "8000-19000",
                ["legacyContainer"] = "ole2-cfb",
                ["fallback"] = "windows-property-store,file-system",
                ["externalRuntime"] = "none",
                ["solidWorksApplication"] = "not-started"
            }));
    }
}
