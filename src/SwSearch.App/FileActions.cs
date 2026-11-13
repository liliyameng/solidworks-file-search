using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SwSearch.App;

public interface IFileActions
{
    void Open(string fullPath);
    void Reveal(string fullPath);
    void CopyPath(string fullPath);
    void ViewProperties(string fullPath);
}

public sealed class WindowsFileActions : IFileActions
{
    public void Open(string fullPath) => Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });

    public void Reveal(string fullPath)
    {
        ValidateExistingAbsoluteFile(fullPath);
        Process.Start(CreateRevealStartInfo(fullPath));
    }

    /// <summary>
    /// Creates the direct Explorer invocation used to select one result file.
    /// The switch and its operand must remain separate process arguments: if
    /// they are combined, .NET quotes the whole value when a path contains
    /// spaces and Explorer no longer sees an unquoted <c>/select,</c> switch.
    /// </summary>
    internal static ProcessStartInfo CreateRevealStartInfo(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var startInfo = new ProcessStartInfo(explorerPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(fullPath);
        return startInfo;
    }

    private static void ValidateExistingAbsoluteFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (fullPath.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("无法在资源管理器中定位：结果路径不是绝对文件路径。", nameof(fullPath));
        }

        // Do not turn an unknown/missing result into a parent-folder or
        // working-directory navigation. Explorer falls back to a shell folder
        // for an unresolved /select target, so reject it before launch.
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("无法在资源管理器中定位：文件不存在或已被移动。", fullPath);
        }
    }

    public void CopyPath(string fullPath) => Clipboard.SetText(fullPath);

    public void ViewProperties(string fullPath)
    {
        var viewerPath = Path.Combine(AppContext.BaseDirectory, "SwSearch.PropertyViewer.exe");
        if (!File.Exists(viewerPath))
        {
            throw new FileNotFoundException("未找到属性查看器，请先完成开发安装。", viewerPath);
        }

        var startInfo = new ProcessStartInfo(viewerPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add(fullPath);
        Process.Start(startInfo);
    }
}
