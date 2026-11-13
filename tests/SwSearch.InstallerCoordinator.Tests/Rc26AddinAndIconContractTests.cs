using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class Rc26AddinAndIconContractTests
{
    [Fact]
    public void Addin_CallbackHandshakeAndDisconnect_DoNotReleaseSharedSolidWorksRcw()
    {
        var root = FindRepositoryRoot();
        var addin = File.ReadAllText(Path.Combine(root, "src", "SwSearch.SolidWorksAddin", "SwAddin.cs"));
        Assert.Contains("ClassInterfaceType.AutoDispatch", addin, StringComparison.Ordinal);
        Assert.Contains("SetAddinCallbackInfo2(0L, this, cookie)", addin, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseComObject(application", addin, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseOwnedComObject(application", addin, StringComparison.Ordinal);
        Assert.Contains("_swApplication = null; // borrowed/shared RCW: never release it", addin, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _disconnecting, 1)", addin, StringComparison.Ordinal);
        Assert.Contains("ReleaseOwnedComObject(taskpane", addin, StringComparison.Ordinal);
        Assert.Contains("InvalidComObjectException", addin, StringComparison.Ordinal);
        Assert.Contains("COMException", addin, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", addin, StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect", addin, StringComparison.Ordinal);
        Assert.True(addin.IndexOf("control.Dispose()", StringComparison.Ordinal) < addin.IndexOf("taskpane.DeleteView()", StringComparison.Ordinal));
    }

    [Fact]
    public void TaskpaneDispose_UnhooksOwnedEventsBeforeNativeResources()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.SolidWorksAddin", "TaskpaneControl.cs"));
        foreach (var token in new[]
                 {
                     "_monitorTimer.Tick -= MonitorTimerTick",
                     "_searchCoordinator.Completed -= SearchCompleted",
                     "_webHost.WebReady -= WebHostReady",
                     "_webHost.CommandReceived -= WebCommandReceived",
                     "_webHost.HostFailure -= WebHostFailure",
                     "_columnSettings.Changed -= ColumnSettingsChanged",
                     "_columnSettings.Failure -= ColumnSettingsFailure",
                 }) Assert.Contains(token, source, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskpaneMonitor_DropsLateStateAndFailureAfterDisposalStarts()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SwSearch.SolidWorksAddin",
            "TaskpaneControl.cs"));
        var start = source.IndexOf("private async void MonitorTimerTick", StringComparison.Ordinal);
        var end = source.IndexOf("private void ApplyMonitorFailure", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var monitor = source[start..end];

        Assert.Equal(
            2,
            CountOccurrences(monitor, "if (_disposing || IsDisposed || Disposing) return;"));
        Assert.True(
            monitor.IndexOf("await Task.Run", StringComparison.Ordinal) <
            monitor.IndexOf("UpdateState", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplorerCommand_GetIconUsesInstalledAbsoluteIcon_AndMsiPayloadIncludesIt()
    {
        var root = FindRepositoryRoot();
        var native = File.ReadAllText(Path.Combine(root, "src", "SwSearch.ModernContextMenu", "ExplorerCommand.cpp"));
        var start = native.IndexOf("IFACEMETHODIMP GetIcon", StringComparison.Ordinal);
        var end = native.IndexOf("IFACEMETHODIMP GetToolTip", start, StringComparison.Ordinal);
        var getIcon = native[start..end];
        Assert.Contains("GetInstallDirectory(value)", getIcon, StringComparison.Ordinal);
        Assert.Contains("\\\\SwSearch.ico", getIcon, StringComparison.Ordinal);
        Assert.DoesNotContain("SwSearch.App.exe", getIcon, StringComparison.Ordinal);
        var publish = File.ReadAllText(Path.Combine(root, "installer", "Publish.ps1"));
        Assert.Contains("Assets\\Brand\\SwSearch.ico", publish, StringComparison.Ordinal);
        Assert.Contains("'SwSearch.ico'", publish, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolidWorksSearch.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }


    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }
}
