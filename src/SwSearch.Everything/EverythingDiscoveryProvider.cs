using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Everything;

public sealed class EverythingDiscoveryProvider :
    IFileDiscoveryProvider,
    IEverythingDiscoveryProgressSource,
    IEverythingDiscoveryDiagnosticsSource,
    IAsyncDisposable
{
    private readonly IEverythingQueryTransport _transport;
    private readonly uint _pageSize;
    private readonly bool _ownsTransport;
    private FileDiscoveryProgressSnapshot _progress = new(
        0,
        null,
        RuntimeServiceState.Idle,
        DateTimeOffset.UtcNow,
        "Everything",
        "EVERYTHING_SNAPSHOT_IDLE");
    private EverythingDiscoveryDiagnostics _diagnostics =
        EverythingDiscoveryDiagnostics.NotAttempted;

    public EverythingDiscoveryProvider(
        IEverythingQueryTransport? transport = null,
        uint pageSize = 16384)
    {
        if (pageSize is 0 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _transport = transport ?? new EverythingIpcTransport();
        _ownsTransport = transport is null;
        _pageSize = pageSize;
    }

    public FileDiscoveryProgressSnapshot DiscoveryProgress => Volatile.Read(ref _progress);

    public EverythingDiscoveryDiagnostics EverythingDiagnostics => Volatile.Read(ref _diagnostics);

    public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var capability = await _transport.ProbeAsync(cancellationToken).ConfigureAwait(false);
            UpdateDiagnostics(current => current with
            {
                Available = capability.State == CapabilityState.Available,
                AvailabilityCode = capability.Code,
                FailureCode = capability.State == CapabilityState.Available ? null : current.FailureCode,
                FailureType = capability.State == CapabilityState.Available ? null : current.FailureType,
            });
            return capability;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var code = FailureCode(exception, "EVERYTHING_PROBE_FAILED");
            UpdateDiagnostics(current => current with
            {
                Available = false,
                AvailabilityCode = code,
                FailureCode = code,
                FailureType = exception.GetType().Name,
                Completed = false,
            });
            throw;
        }
    }

    public async IAsyncEnumerable<FileCandidate> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var succeeded = false;
        long finalIndexed = 0;
        long finalPages = 0;
        long finalReturnedEntries = 0;
        uint? finalExpectedTotal = null;
        UpdateDiagnostics(current => current with
        {
            Attempted = true,
            PageCount = 0,
            ReturnedEntryCount = 0,
            EmittedEntryCount = 0,
            FailureCode = null,
            FailureType = null,
            Completed = false,
        });
        SetProgress(0, null, RuntimeServiceState.Running, "EVERYTHING_SNAPSHOT_STARTING");
        try
        {
            for (var retry = 0; retry <= MaxSnapshotRetries; retry++)
            {
                var buffer = new List<FileCandidate>();
                uint offset = 0;
                uint? expectedTotal = null;
                long pages = 0;
                long returnedEntries = 0;
                var snapshotChanged = false;

                try
                {
                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw TrackFailure(
                                new OperationCanceledException(cancellationToken),
                                pages,
                                returnedEntries,
                                buffer.Count,
                                cancellationToken);
                        }

                        var page = await QueryPageWithDiagnosticsAsync(
                            offset,
                            pages,
                            returnedEntries,
                            buffer.Count,
                            cancellationToken).ConfigureAwait(false);

                        pages++;
                        returnedEntries += page.ReturnedItems;
                        expectedTotal ??= page.TotalItems;

                        if (page.TotalItems != expectedTotal || page.Offset != offset)
                        {
                            snapshotChanged = true;
                            break;
                        }

                        if (page.ReturnedItems > _pageSize ||
                            page.Offset > page.TotalItems ||
                            page.ReturnedItems > page.TotalItems - page.Offset ||
                            page.Items.Count > page.ReturnedItems)
                        {
                            throw TrackFailure(
                                new EverythingQueryException(
                                    "EVERYTHING_INVALID_PAGE",
                                    "Everything returned a page outside the requested snapshot boundary."),
                                pages,
                                returnedEntries,
                                buffer.Count,
                                cancellationToken);
                        }

                        foreach (var item in page.Items)
                        {
                            if (!SolidWorksEverythingQuery.TryGetKind(item.FullPath, out _))
                            {
                                continue;
                            }

                            buffer.Add(item);
                        }

                        if (page.ReturnedItems == 0 || offset + page.ReturnedItems >= page.TotalItems)
                        {
                            if (page.ReturnedItems == 0 && offset < page.TotalItems)
                            {
                                throw TrackFailure(
                                    new EverythingQueryException(
                                        "EVERYTHING_SNAPSHOT_TRUNCATED",
                                        "Everything returned an empty page before the reported result count."),
                                    pages,
                                    returnedEntries,
                                    buffer.Count,
                                    cancellationToken);
                            }

                            break;
                        }

                        offset = checked(offset + page.ReturnedItems);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                                                      and not StackOverflowException
                                                      && !snapshotChanged)
                {
                    throw TrackFailure(exception, pages, returnedEntries, buffer.Count, cancellationToken);
                }

                if (snapshotChanged)
                {
                    UpdateDiagnostics(current => current with
                    {
                        PageCount = pages,
                        ReturnedEntryCount = returnedEntries,
                        EmittedEntryCount = 0,
                        FailureCode = "EVERYTHING_SNAPSHOT_RETRYING",
                        Completed = false,
                    });
                    SetProgress(0, null, RuntimeServiceState.Running, "EVERYTHING_SNAPSHOT_RETRYING");
                    continue;
                }

                finalPages = pages;
                finalReturnedEntries = returnedEntries;
                finalExpectedTotal = expectedTotal;

                foreach (var item in buffer)
                {
                    yield return item;
                    finalIndexed++;
                    if ((finalIndexed & 0x3ff) == 0)
                    {
                        SetProgress(
                            finalIndexed,
                            finalExpectedTotal,
                            RuntimeServiceState.Running,
                            "EVERYTHING_SNAPSHOT_READING");
                    }
                }

                succeeded = true;
                UpdateDiagnostics(current => current with
                {
                    Available = true,
                    AvailabilityCode = current.AvailabilityCode == "EVERYTHING_NOT_PROBED"
                        ? "EVERYTHING_QUERY_READY"
                        : current.AvailabilityCode,
                    PageCount = pages,
                    ReturnedEntryCount = returnedEntries,
                    EmittedEntryCount = finalIndexed,
                    FailureCode = null,
                    FailureType = null,
                    Completed = true,
                });

                yield break;
            }

            throw TrackFailure(
                new EverythingQueryException(
                    "EVERYTHING_SNAPSHOT_RETRIES_EXHAUSTED",
                    "Everything snapshot changed repeatedly; max retries exhausted."),
                finalPages,
                finalReturnedEntries,
                0,
                cancellationToken);
        }
        finally
        {
            SetProgress(
                finalIndexed,
                finalExpectedTotal,
                succeeded ? RuntimeServiceState.Idle : RuntimeServiceState.Unavailable,
                succeeded ? "EVERYTHING_SNAPSHOT_COMPLETE" : "EVERYTHING_SNAPSHOT_INCOMPLETE");
        }
    }

    public ValueTask DisposeAsync() =>
        _ownsTransport ? _transport.DisposeAsync() : ValueTask.CompletedTask;

    /// <summary>Maximum retries when the Everything snapshot changes during paging.</summary>
    private const int MaxSnapshotRetries = 3;

    private async ValueTask<EverythingQueryPage> QueryPageWithDiagnosticsAsync(
        uint offset,
        long pages,
        long returnedEntries,
        long indexed,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.QueryPageAsync(
                SolidWorksEverythingQuery.Search,
                offset,
                _pageSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw TrackFailure(exception, pages, returnedEntries, indexed, cancellationToken);
        }
    }

    private Exception TrackFailure(
        Exception exception,
        long pages,
        long returnedEntries,
        long indexed,
        CancellationToken cancellationToken)
    {
        var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
        UpdateDiagnostics(current => current with
        {
            PageCount = pages,
            ReturnedEntryCount = returnedEntries,
            EmittedEntryCount = indexed,
            FailureCode = cancelled
                ? "EVERYTHING_QUERY_CANCELLED"
                : FailureCode(exception, "EVERYTHING_QUERY_FAILED"),
            FailureType = exception.GetType().Name,
            Completed = false,
        });
        return exception;
    }

    private void SetProgress(
        long indexed,
        long? total,
        RuntimeServiceState state,
        string code) =>
        Volatile.Write(ref _progress, new FileDiscoveryProgressSnapshot(
            indexed,
            total,
            state,
            DateTimeOffset.UtcNow,
            "Everything",
            code));

    private void UpdateDiagnostics(Func<EverythingDiscoveryDiagnostics, EverythingDiscoveryDiagnostics> update)
    {
        while (true)
        {
            var current = Volatile.Read(ref _diagnostics);
            var next = update(current) with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _diagnostics, next, current), current))
            {
                return;
            }
        }
    }

    private static string FailureCode(Exception exception, string fallback) =>
        exception is EverythingQueryException queryException
            ? queryException.Code
            : fallback;
}
