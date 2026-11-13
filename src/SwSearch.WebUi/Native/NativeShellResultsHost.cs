using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwSearch.SolidWorksAddin;

namespace SwSearch.WebUi.Native;

/// <summary>
/// Desktop-safe facade over the source-shared ExplorerBrowser/IResultsFolder
/// host.  The Windows Shell supplies the image, thumbnail, context menu and
/// CF_HDROP drag data for each original file-system item.  This facade exposes
/// no SOLIDWORKS API and never opens or parses a model.
/// </summary>
public sealed class NativeShellResultsHost : IDisposable
{
    private readonly TaskpaneShellResultsHost _inner = new();
    private bool _disposed;

    public NativeShellResultsHost()
    {
        _inner.HostReady += (_, _) => Ready?.Invoke(this, EventArgs.Empty);
        _inner.HostFailure += (_, error) =>
            Failure?.Invoke(this, new NativeShellResultsFailureEventArgs(error.Stage, error.Exception));
    }

    public Control View => _inner.View;

    public bool IsReady => _inner.IsShellReady;

    public event EventHandler? Ready;

    public event EventHandler<NativeShellResultsFailureEventArgs>? Failure;

    /// <summary>
    /// Returns the full file-system path of the currently focused item in the
    /// native Shell view, or null when no item has focus.  Callers should
    /// poll this infrequently and only from the Shell owner STA thread.
    /// </summary>
    public bool TryGetFocusedPath(out string? path) => _inner.TryGetFocusedPath(out path);

    /// <summary>
    /// Cheap focus-change probe: a single GetFocusedItem COM call without the
    /// per-item resolution cost of <see cref="TryGetFocusedPath"/>.  Pollers
    /// should compare this index (plus their own result-set version) and only
    /// resolve the full path when focus actually moved.
    /// </summary>
    public bool TryGetFocusedIndex(out int index) => _inner.TryGetFocusedIndex(out index);

    /// <summary>
    /// Applies the exact pixel widths used by the companion native header to
    /// the real Shell DetailsView. The column order is fixed by
    /// <see cref="NativeShellColumnContract.Columns"/>; callers cannot label a
    /// query-only value as though it were a file-system IShellItem property.
    /// </summary>
    public void ConfigureColumns(IReadOnlyList<int> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count != NativeShellColumnContract.Columns.Count)
        {
            throw new ArgumentException("The native Shell column width contract is incomplete.", nameof(widths));
        }

        var snapshot = new int[widths.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = widths[index];
        }

        if (!_disposed)
        {
            _inner.ConfigureColumns(snapshot);
        }
    }

    /// <summary>
    /// Replaces the current result folder with file-system items for a new
    /// broker result generation. The Shell work happens on the host MTA
    /// worker; this method only queues immutable paths. Await the returned
    /// ticket off the UI thread before scheduling the next page.
    /// </summary>
    public NativeShellPopulationTicket Replace(long generation, IEnumerable<string>? paths)
    {
        if (_disposed)
        {
            return NativeShellPopulationTicket.NotApplied(generation);
        }

        return new NativeShellPopulationTicket(
            _inner.Replace(generation, paths ?? Array.Empty<string>()));
    }

    /// <summary>
    /// Appends a further broker page for the same result generation.  A ticket
    /// whose <see cref="NativeShellPopulationTicket.Applied"/> property is
    /// false means that a newer query already replaced this generation.
    /// </summary>
    public NativeShellPopulationTicket Append(long generation, IEnumerable<string>? paths)
    {
        if (_disposed)
        {
            return NativeShellPopulationTicket.NotApplied(generation);
        }

        return new NativeShellPopulationTicket(
            _inner.Append(generation, paths ?? Array.Empty<string>()));
    }

    public void CancelBefore(long generation)
    {
        if (!_disposed)
        {
            _inner.CancelBefore(generation);
        }
    }

    /// <summary>
    /// Starts the explicit ExplorerBrowser teardown handshake.  Consumers
    /// should keep the parent WinForms/WPF HWND alive until the callback runs;
    /// the native worker owns a cross-apartment Shell proxy until then.
    /// </summary>
    public void BeginShutdown(Action<NativeShellShutdownResult>? completed)
    {
        if (_disposed)
        {
            completed?.Invoke(NativeShellShutdownResult.AlreadyDisposed());
            return;
        }

        _inner.BeginShutdown(result => completed?.Invoke(NativeShellShutdownResult.From(result)));
    }

    /// <summary>
    /// Convenience task form of <see cref="BeginShutdown"/> for a caller
    /// which already has an asynchronous window-close path.
    /// </summary>
    public Task<NativeShellShutdownResult> ShutdownAsync()
    {
        var completion = new TaskCompletionSource<NativeShellShutdownResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BeginShutdown(result => completion.TrySetResult(result));
        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
    }
}

/// <summary>
/// Public projection of the three standard Shell properties which both the
/// real ExplorerBrowser rows and the custom WinForms header display.
/// </summary>
public static class NativeShellColumnContract
{
    private static readonly IReadOnlyList<NativeShellColumnDefinition> Definitions =
        Array.AsReadOnly(ShellResultColumnContract.Columns
            .Select(column => new NativeShellColumnDefinition(
                column.Id,
                column.Label,
                column.MinimumWidth,
                column.Weight))
            .ToArray());

    public static IReadOnlyList<NativeShellColumnDefinition> Columns => Definitions;
}

public sealed class NativeShellColumnDefinition
{
    internal NativeShellColumnDefinition(string id, string label, int minimumWidth, int weight)
    {
        Id = id;
        Label = label;
        MinimumWidth = minimumWidth;
        Weight = weight;
    }

    public string Id { get; }

    public string Label { get; }

    public int MinimumWidth { get; }

    public int Weight { get; }
}

/// <summary>
/// Completion handle for one native Shell population command. It deliberately
/// exposes no local sort operation: ordering must come from the complete
/// Broker query, not from a partially loaded Explorer view.
/// </summary>
public sealed class NativeShellPopulationTicket : IDisposable
{
    private readonly ShellPopulationTicket? _inner;
    private readonly long _generation;
    private readonly bool _notApplied;

    internal NativeShellPopulationTicket(ShellPopulationTicket inner)
    {
        _inner = inner;
        _generation = inner.Generation;
    }

    private NativeShellPopulationTicket(long generation)
    {
        _generation = generation;
        _notApplied = true;
    }

    internal static NativeShellPopulationTicket NotApplied(long generation) => new(generation);

    public long Generation => _generation;

    public bool IsCompleted => _notApplied || _inner?.IsCompleted == true;

    public bool Applied => !_notApplied && _inner?.Applied == true;

    public int Added => _notApplied ? 0 : _inner?.Added ?? 0;

    public int Skipped => _notApplied ? 0 : _inner?.Skipped ?? 0;

    /// <summary>Waits for the native Shell MTA worker without touching model data.</summary>
    public void Wait(CancellationToken cancellationToken)
    {
        if (_notApplied)
        {
            return;
        }

        _inner!.Wait(cancellationToken);
    }

    /// <summary>
    /// Awaitable form intended for a background population loop. Awaits the
    /// worker's own completion task directly so no thread-pool thread is
    /// parked on the blocking Shell wait.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken) =>
        _notApplied
            ? Task.CompletedTask
            : _inner!.Completion.WaitAsync(cancellationToken);

    public void Dispose()
    {
        _inner?.Dispose();
    }
}

/// <summary>Result of the explicit native Shell teardown handshake.</summary>
public sealed class NativeShellShutdownResult
{
    private NativeShellShutdownResult(bool succeeded, string stage, Exception? exception)
    {
        Succeeded = succeeded;
        Stage = stage;
        Exception = exception;
    }

    public bool Succeeded { get; }

    public string Stage { get; }

    public Exception? Exception { get; }

    internal static NativeShellShutdownResult From(ShellShutdownResult? result) =>
        result is null
            ? new NativeShellShutdownResult(false, "shell-shutdown", new InvalidOperationException("The native Shell host returned no shutdown result."))
            : new NativeShellShutdownResult(result.Succeeded, result.Stage, result.Exception);

    internal static NativeShellShutdownResult AlreadyDisposed() =>
        new(true, "", null);
}

public sealed class NativeShellResultsFailureEventArgs(string stage, Exception? exception) : EventArgs
{
    public string Stage { get; } = stage ?? string.Empty;

    public Exception? Exception { get; } = exception;
}
