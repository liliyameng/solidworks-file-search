using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SwSearch.App;

internal sealed class AppTrayController : IDisposable
{
    private readonly MainWindow _window;
    private readonly NotifyIcon _icon;
    private bool _firstHideNoticeShown;
    private bool _disposed;

    internal AppTrayController(MainWindow window, Action exit)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(exit);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Restore());
        menu.Items.Add("立即刷新缓存", null, (_, _) => _window.Dispatcher.BeginInvoke(_window.RefreshCacheFromTray));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _window.Dispatcher.BeginInvoke(exit));

        _icon = new NotifyIcon
        {
            Text = "SOLIDWORKS 文件检索",
            Icon = LoadProductIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Restore();
        _window.HiddenToTray += WindowOnHiddenToTray;
    }

    internal void Restore() => _window.Dispatcher.BeginInvoke(_window.RestoreFromTray);

    private void WindowOnHiddenToTray()
    {
        if (_firstHideNoticeShown || _disposed) return;
        _firstHideNoticeShown = true;
        _icon.BalloonTipTitle = "SOLIDWORKS 文件检索";
        _icon.BalloonTipText = "程序仍在后台刷新缓存";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(2500);
    }

    private static Icon LoadProductIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable) && Icon.ExtractAssociatedIcon(executable) is { } icon)
            {
                return icon;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
        {
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.HiddenToTray -= WindowOnHiddenToTray;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
