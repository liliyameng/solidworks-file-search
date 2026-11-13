namespace SwSearch.App;

internal enum TrayCloseDisposition
{
    Hide,
    Exit,
}

internal sealed class TrayLifetimeState
{
    internal bool WindowVisible { get; private set; } = true;
    internal bool ExitRequested { get; private set; }

    internal TrayCloseDisposition OnWindowClosing()
    {
        if (ExitRequested) return TrayCloseDisposition.Exit;
        WindowVisible = false;
        return TrayCloseDisposition.Hide;
    }

    internal void Restore() => WindowVisible = true;

    internal void Hide() => WindowVisible = false;

    internal void RequestExit() => ExitRequested = true;
}
