using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SwSearch.App;

internal sealed class AppTrayController : IDisposable
{
    private readonly MainWindow _window;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _firstHideNoticeShown;
    private bool _disposed;

    internal AppTrayController(MainWindow window, Action exit)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(exit);

        var menu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem("打开", null, (_, _) => Restore());
        _refreshItem = new ToolStripMenuItem("立即刷新缓存", null, (_, _) => _window.Dispatcher.BeginInvoke(_window.RefreshCacheFromTray));
        _exitItem = new ToolStripMenuItem("退出", null, (_, _) => _window.Dispatcher.BeginInvoke(exit));
        menu.Items.Add(_openItem);
        menu.Items.Add(_refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);
        // The menu re-localizes every time it opens so a language change made
        // in settings applies to the tray without restarting the app.
        menu.Opening += (_, _) => ApplyLanguage(_window.ViewModel.UiLanguageEffective);

        _icon = new NotifyIcon
        {
            Text = "SOLIDWORKS 文件检索",
            Icon = LoadProductIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Restore();
        _window.HiddenToTray += WindowOnHiddenToTray;
        ApplyLanguage(_window.ViewModel.UiLanguageEffective);
    }

    private void ApplyLanguage(string language)
    {
        _openItem.Text = AppUiText.Get(language, "tray.open");
        _refreshItem.Text = AppUiText.Get(language, "tray.refresh");
        _exitItem.Text = AppUiText.Get(language, "tray.exit");
        _icon.Text = AppUiText.Get(language, "product.title");
    }

    internal void Restore() => _window.Dispatcher.BeginInvoke(_window.RestoreFromTray);

    private void WindowOnHiddenToTray()
    {
        if (_firstHideNoticeShown || _disposed) return;
        _firstHideNoticeShown = true;
        var language = _window.ViewModel.UiLanguageEffective;
        _icon.BalloonTipTitle = AppUiText.Get(language, "product.title");
        _icon.BalloonTipText = AppUiText.Get(language, "tray.refreshing");
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
