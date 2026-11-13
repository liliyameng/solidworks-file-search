using System.IO;
using SwSearch.Contracts;

namespace SwSearch.App;

/// <summary>
/// Keeps the user-initiated tray exit sequence independent from normal window
/// closes and installer shutdown.  The marker is published before attempting
/// the bounded Broker request so a SOLIDWORKS taskpane can never race us into
/// launching a replacement Broker.
/// </summary>
internal interface IAppExitSessionMarker
{
    bool TryMark();
    bool TryClear();
    void ClearCurrentProcessLaunchSuppression();
}

/// <summary>
/// This request must be a one-shot transport operation: it is deliberately
/// not allowed to start or recover a Broker when no Broker is already running.
/// </summary>
internal interface IGracefulBrokerShutdownRequester
{
    ValueTask RequestShutdownAsync(CancellationToken cancellationToken);
}

internal sealed class ContractAppExitSessionMarker : IAppExitSessionMarker
{
    internal static ContractAppExitSessionMarker Instance { get; } = new();

    public bool TryMark() => BrokerAppExitSessionMarker.TryMarkCurrentSession();

    public bool TryClear() => BrokerAppExitSessionMarker.TryClearCurrentSession();

    public void ClearCurrentProcessLaunchSuppression() =>
        BrokerAppExitSessionMarker.ClearCurrentProcessLaunchSuppression();
}

internal sealed class ContractGracefulBrokerShutdownRequester : IGracefulBrokerShutdownRequester
{
    internal static ContractGracefulBrokerShutdownRequester Instance { get; } = new();

    public async ValueTask RequestShutdownAsync(CancellationToken cancellationToken)
    {
        var response = await BrokerAppExitShutdownClient.RequestShutdownAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success)
        {
            throw new IOException(
                string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? response.ErrorCode ?? "The Broker rejected the explicit application exit request."
                    : response.ErrorMessage);
        }
    }
}

internal readonly record struct AppExplicitExitResult(
    bool MarkerWritten,
    bool BrokerAcknowledged,
    bool BrokerRequestTimedOut);

internal sealed class AppExplicitExitCoordinator
{
    internal static readonly TimeSpan BrokerShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IAppExitSessionMarker _marker;
    private readonly IGracefulBrokerShutdownRequester _shutdownRequester;
    private readonly TimeSpan _brokerShutdownTimeout;
    private readonly Action<string, Exception> _recordFailure;
    private readonly Action _suppressBrokerRecoveryAfterMarker;
    private readonly object _gate = new();
    private Task<AppExplicitExitResult>? _requestedExit;

    internal AppExplicitExitCoordinator(
        IAppExitSessionMarker marker,
        IGracefulBrokerShutdownRequester shutdownRequester,
        TimeSpan? brokerShutdownTimeout = null,
        Action<string, Exception>? recordFailure = null,
        Action? suppressBrokerRecoveryAfterMarker = null)
    {
        _marker = marker ?? throw new ArgumentNullException(nameof(marker));
        _shutdownRequester = shutdownRequester ?? throw new ArgumentNullException(nameof(shutdownRequester));
        _brokerShutdownTimeout = brokerShutdownTimeout ?? BrokerShutdownTimeout;
        if (_brokerShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(brokerShutdownTimeout));
        }

        _recordFailure = recordFailure ?? (static (stage, exception) =>
            AppStartupDiagnostics.Record(stage, exception));
        _suppressBrokerRecoveryAfterMarker = suppressBrokerRecoveryAfterMarker ?? (static () => { });
    }

    /// <summary>
    /// Every new App launch clears the marker.  The marker itself is scoped to
    /// the current Windows boot/session, so normal startup on the next login
    /// naturally restores ordinary background behavior.
    /// </summary>
    internal void ClearForApplicationLaunch()
    {
        // The Broker launch boundary reads only this process-local bit.  Reset
        // it synchronously, then move the durable marker cleanup off the WPF
        // startup path so a slow profile/volume can never delay the UI or
        // recovery child launch.
        _marker.ClearCurrentProcessLaunchSuppression();
        _ = Task.Run(ClearDurableMarkerForApplicationLaunch);
    }

    private void ClearDurableMarkerForApplicationLaunch()
    {
        try
        {
            if (!_marker.TryClear())
            {
                RecordFailure(
                    "app-exit-marker-clear",
                    new IOException("The explicit Broker exit marker could not be cleared."));
            }
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            RecordFailure("app-exit-marker-clear", exception);
        }
    }

    /// <summary>
    /// Idempotently marks the current session, then gives the already-running
    /// Broker at most two seconds to drain.  Any failure is observational only:
    /// it must never keep the desktop App alive or cause a forced termination.
    /// </summary>
    internal Task<AppExplicitExitResult> RequestExitAsync()
    {
        lock (_gate)
        {
            return _requestedExit ??= RequestExitCoreAsync();
        }
    }

    private async Task<AppExplicitExitResult> RequestExitCoreAsync()
    {
        var markerWritten = false;
        try
        {
            markerWritten = _marker.TryMark();
            if (!markerWritten)
            {
                RecordFailure(
                    "app-exit-marker-write",
                    new IOException("The explicit Broker exit marker could not be written."));
            }
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            RecordFailure("app-exit-marker-write", exception);
        }

        try
        {
            // Marker first, then cancel the App-owned recovery loop before the
            // dedicated Broker exit request is issued. This closes the small
            // window in which an already queued recovery could relaunch a
            // Broker while the tray Exit is draining it.
            _suppressBrokerRecoveryAfterMarker();
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            RecordFailure("app-exit-recovery-suppress", exception);
        }

        var acknowledged = false;
        var timedOut = false;
        using var deadline = new CancellationTokenSource(_brokerShutdownTimeout);
        try
        {
            await _shutdownRequester.RequestShutdownAsync(deadline.Token)
                .AsTask()
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            acknowledged = true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            timedOut = true;
            RecordFailure(
                "app-exit-broker-timeout",
                new TimeoutException(
                    $"The Broker did not acknowledge the explicit exit within {_brokerShutdownTimeout.TotalSeconds:0.#} seconds."));
        }
        catch (TimeoutException exception)
        {
            timedOut = true;
            RecordFailure("app-exit-broker-timeout", exception);
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            RecordFailure("app-exit-broker-request", exception);
        }

        return new AppExplicitExitResult(markerWritten, acknowledged, timedOut);
    }

    private void RecordFailure(string stage, Exception exception)
    {
        try
        {
            _recordFailure(stage, exception);
        }
        catch
        {
            // Exit reliability cannot depend on diagnostics.
        }
    }
}
