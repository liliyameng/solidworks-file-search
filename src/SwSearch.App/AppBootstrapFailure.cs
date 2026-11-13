using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwSearch.App;

internal static class AppBootstrapFailurePolicy
{
    internal static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

internal static class AppStartupDiagnostics
{
    internal static void Record(string stage, Exception exception, string? logPath = null)
    {
        try
        {
            var path = logPath ?? Path.Combine(
                SwSearchDataPaths.DataDirectory,
                "Logs",
                "app-startup.log");
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 600) message = message[..600];
            var line = $"{DateTimeOffset.UtcNow:O}|{stage}|{exception.GetType().FullName}|0x{exception.HResult:X8}|{message}{Environment.NewLine}";
            BoundedUiDiagnosticLog.TryWrite(path, line);
        }
        catch
        {
            // Diagnostics must never become a second startup failure.
        }
    }

}

internal static class AppBootstrapFailureWindow
{
    internal static Window Create() => new()
    {
        Title = "SOLIDWORKS 文件检索",
        Width = 620,
        Height = 280,
        MinWidth = 520,
        MinHeight = 220,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Background = Brushes.White,
        Content = new Border
        {
            Padding = new Thickness(28),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "应用界面未能完成初始化",
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 14),
                    },
                    new TextBlock
                    {
                        Text = "主程序文件或本地配置暂时不可用。请确认已安装 x64 .NET 8 Desktop Runtime 和 WebView2 Evergreen Runtime，然后重试。损坏或旧版 settings.json 不会阻止下一次启动。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                    },
                    new TextBlock
                    {
                        Text = "诊断记录：%LOCALAPPDATA%\\SwSearch\\Logs\\app-startup.log",
                        Margin = new Thickness(0, 18, 0, 0),
                        Foreground = Brushes.DimGray,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        },
    };

    internal static Window CreateSingleInstanceFailure() => new()
    {
        Title = "SOLIDWORKS 文件检索",
        Width = 620,
        Height = 250,
        MinWidth = 520,
        MinHeight = 210,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Background = Brushes.White,
        Content = new Border
        {
            Padding = new Thickness(28),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "已有实例未能响应",
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 14),
                    },
                    new TextBlock
                    {
                        Text = "程序已尝试唤醒并安全重启后台实例，但该实例仍未退出。请从系统托盘打开或退出旧实例，然后重新启动。安装程序不会静默强制结束前台程序。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                    },
                    new TextBlock
                    {
                        Text = "诊断记录：%LOCALAPPDATA%\\SwSearch\\Logs\\app-startup.log",
                        Margin = new Thickness(0, 18, 0, 0),
                        Foreground = Brushes.DimGray,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        },
    };
}
