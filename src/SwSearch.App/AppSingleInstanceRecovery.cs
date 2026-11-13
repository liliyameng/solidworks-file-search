namespace SwSearch.App;

internal static class AppSingleInstanceRecovery
{
    internal static async Task<bool> SignalAndTakeOwnershipAsync(
        Mutex mutex,
        string shutdownEventName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        ArgumentException.ThrowIfNullOrWhiteSpace(shutdownEventName);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        TrySignalShutdown(shutdownEventName);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTakeOwnership(mutex)) return true;
            await Task.Delay(50, cancellationToken);
        }
        return TryTakeOwnership(mutex);
    }

    private static void TrySignalShutdown(string shutdownEventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(shutdownEventName, out var shutdown)) return;
            using (shutdown) shutdown.Set();
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private static bool TryTakeOwnership(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
