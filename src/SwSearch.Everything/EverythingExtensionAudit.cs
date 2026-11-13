using SwSearch.Contracts;

namespace SwSearch.Everything;

public sealed record EverythingExtensionAuditResult(
    uint ReportedTotal,
    long ReturnedTotal,
    long UniquePaths,
    long DuplicatePaths,
    long InvalidExtensions,
    long Parts,
    long Assemblies)
{
    public bool IsValid =>
        ReturnedTotal == ReportedTotal &&
        UniquePaths == ReturnedTotal &&
        DuplicatePaths == 0 &&
        InvalidExtensions == 0 &&
        Parts + Assemblies == ReturnedTotal;
}

public static class EverythingExtensionAudit
{
    public static async ValueTask<EverythingExtensionAuditResult> RunAsync(
        IEverythingQueryTransport transport,
        uint pageSize = 16_384,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (pageSize is 0 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        uint offset = 0;
        uint? reportedTotal = null;
        long returnedTotal = 0;
        long duplicates = 0;
        long invalid = 0;
        long parts = 0;
        long assemblies = 0;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var page = await transport.QueryPageAsync(
                SolidWorksEverythingQuery.Search,
                offset,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            reportedTotal ??= page.TotalItems;
            if (page.TotalItems != reportedTotal || page.Offset != offset || page.Items.Count != page.ReturnedItems)
            {
                throw new EverythingQueryException(
                    "EVERYTHING_AUDIT_SNAPSHOT_INVALID",
                    "Everything 审计期间总数、偏移或返回条数不一致。");
            }

            foreach (var item in page.Items)
            {
                returnedTotal++;
                if (!unique.Add(item.FullPath))
                {
                    duplicates++;
                }

                if (!SolidWorksEverythingQuery.TryGetKind(item.FullPath, out var kind))
                {
                    invalid++;
                    continue;
                }

                switch (kind)
                {
                    case SolidWorksDocumentKind.Part:
                        parts++;
                        break;
                    case SolidWorksDocumentKind.Assembly:
                        assemblies++;
                        break;
                }
            }

            if (page.ReturnedItems == 0 || offset + page.ReturnedItems >= page.TotalItems)
            {
                break;
            }

            offset = checked(offset + page.ReturnedItems);
        }

        return new EverythingExtensionAuditResult(
            reportedTotal ?? 0,
            returnedTotal,
            unique.Count,
            duplicates,
            invalid,
            parts,
            assemblies);
    }
}
