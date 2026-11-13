using Microsoft.Win32;
using System.Diagnostics;

namespace SwSearch.Integration;

public sealed record IntegrationStatus(
    bool IsAvailable,
    bool IsEnabled,
    string Message,
    bool RequiresSolidWorksRestart = false);

public interface IFolderContextMenuRegistration
{
    ValueTask SetEnabledAsync(bool enabled, string applicationPath, CancellationToken cancellationToken);
    ValueTask<IntegrationStatus> ProbeAsync(string applicationPath, CancellationToken cancellationToken);
}

public interface IFilePropertiesContextMenuRegistration
{
    ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
    ValueTask<IntegrationStatus> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class ClassicFallbackContextMenuRegistration(IRegistryAccess registry) : IFolderContextMenuRegistration
{
    public const string VerbName = "SwSearch.SearchHere";
    public const string DirectoryKey = @"Software\Classes\Directory\shell\" + VerbName;
    public const string BackgroundKey = @"Software\Classes\Directory\Background\shell\" + VerbName;

    public ValueTask SetEnabledAsync(bool enabled, string applicationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ValidateExecutable(applicationPath);
        if (!enabled)
        {
            registry.DeleteTree(RegistryHive.CurrentUser, DirectoryKey);
            registry.DeleteTree(RegistryHive.CurrentUser, BackgroundKey);
            return ValueTask.CompletedTask;
        }

        try
        {
            WriteVerb(DirectoryKey, executable, "%1");
            WriteVerb(BackgroundKey, executable, "%V");
        }
        catch
        {
            registry.DeleteTree(RegistryHive.CurrentUser, DirectoryKey);
            registry.DeleteTree(RegistryHive.CurrentUser, BackgroundKey);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IntegrationStatus> ProbeAsync(string applicationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ValidateExecutable(applicationPath);
        var directoryCommand = $"\"{executable}\" --scope-folder \"%1\"";
        var backgroundCommand = $"\"{executable}\" --scope-folder \"%V\"";
        var enabled = string.Equals(
                          registry.GetValue(RegistryHive.CurrentUser, DirectoryKey + @"\command", null) as string,
                          directoryCommand,
                          StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(
                          registry.GetValue(RegistryHive.CurrentUser, BackgroundKey + @"\command", null) as string,
                          backgroundCommand,
                          StringComparison.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new IntegrationStatus(true, enabled, enabled ? "已注册" : "未注册"));
    }

    private void WriteVerb(string key, string executable, string token)
    {
        registry.SetString(RegistryHive.CurrentUser, key, null, "使用 SOLIDWORKS 文件检索此文件夹");
        registry.SetString(RegistryHive.CurrentUser, key, "MUIVerb", "使用 SOLIDWORKS 文件检索此文件夹");
        registry.SetString(RegistryHive.CurrentUser, key, "Icon", $"\"{executable}\",0");
        registry.SetString(RegistryHive.CurrentUser, key + @"\command", null, $"\"{executable}\" --scope-folder \"{token}\"");
    }

    private static string ValidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || path.Contains('"'))
        {
            throw new ArgumentException("应用程序路径必须是无引号的绝对 .exe 路径。", nameof(path));
        }
        return Path.GetFullPath(path);
    }
}

public sealed class ModernContextMenuRegistration : IFolderContextMenuRegistration
{
    public const string PackageFileName = "SwSearch.ModernContextMenu.msix";
    public const string RegistrationHelperFileName = "SwSearch.ModernContextMenuRegistration.exe";
    private readonly string _installDirectory;
    private readonly OwnedHelperProcessRunner _processRunner;
    private readonly bool _fileProperties;

    public ModernContextMenuRegistration(
        string installDirectory,
        OwnedHelperProcessRunner? processRunner = null,
        bool fileProperties = false)
    {
        _installDirectory = Path.GetFullPath(installDirectory);
        _processRunner = processRunner ?? new OwnedHelperProcessRunner();
        _fileProperties = fileProperties;
    }

    public async ValueTask<IntegrationStatus> ProbeAsync(string applicationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Path.GetFullPath(applicationPath);
        var package = Path.Combine(_installDirectory, PackageFileName);
        var helper = Path.Combine(_installDirectory, RegistrationHelperFileName);
        if (!File.Exists(package) || !File.Exists(helper))
        {
            return new IntegrationStatus(
                false,
                false,
                "Windows 11 一级右键菜单组件未构建：需要 MSVC/Windows SDK/MakeAppx/SignTool 和生产签名证书。");
        }

        try
        {
            var startInfo = CreateHelperStartInfo(helper);
            startInfo.ArgumentList.Add(_fileProperties ? "--status-file" : "--status-folder");
            var exitCode = await _processRunner.RunAsync(
                startInfo, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var enabled = exitCode == 0;
            return new IntegrationStatus(true, enabled, enabled ? "Windows 11 一级右键菜单已注册" : "现代菜单组件已安装但当前用户未注册");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return new IntegrationStatus(true, false, $"读取现代菜单状态失败：{exception.Message}");
        }
    }

    public async ValueTask SetEnabledAsync(bool enabled, string applicationPath, CancellationToken cancellationToken)
    {
        var status = await ProbeAsync(applicationPath, cancellationToken).ConfigureAwait(false);
        if (!status.IsAvailable)
        {
            throw new InvalidOperationException(status.Message);
        }

        var helper = Path.Combine(_installDirectory, RegistrationHelperFileName);
        var startInfo = CreateHelperStartInfo(helper);
        startInfo.ArgumentList.Add(_fileProperties ? "--set-file" : "--set-folder");
        startInfo.ArgumentList.Add(enabled ? "1" : "0");
        var exitCode = await _processRunner.RunAsync(
            startInfo, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException("现代菜单包注册失败；不会强制重启 Explorer。请查看安装日志。");
        }
    }

    private ProcessStartInfo CreateHelperStartInfo(string helper) => new()
    {
        FileName = helper,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = _installDirectory,
    };
}

public sealed class FilePropertiesContextMenuRegistration : IFilePropertiesContextMenuRegistration
{
    private readonly ModernContextMenuRegistration _registration;

    public FilePropertiesContextMenuRegistration(
        string installDirectory,
        OwnedHelperProcessRunner? processRunner = null) =>
        _registration = new ModernContextMenuRegistration(installDirectory, processRunner, fileProperties: true);

    public ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        _registration.SetEnabledAsync(enabled, Path.Combine(AppContext.BaseDirectory, "SwSearch.PropertyViewer.exe"), cancellationToken);

    public ValueTask<IntegrationStatus> ProbeAsync(CancellationToken cancellationToken) =>
        _registration.ProbeAsync(Path.Combine(AppContext.BaseDirectory, "SwSearch.PropertyViewer.exe"), cancellationToken);
}

public sealed class FolderContextMenuService : IFolderContextMenuRegistration
{
    private readonly IFolderContextMenuRegistration _active;

    public FolderContextMenuService(IRegistryAccess registry, bool? useModern = null, string? installDirectory = null)
    {
        var modern = useModern ?? OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        _active = modern
            ? new ModernContextMenuRegistration(installDirectory ?? AppContext.BaseDirectory)
            : new ClassicFallbackContextMenuRegistration(registry);
    }

    public ValueTask SetEnabledAsync(bool enabled, string applicationPath, CancellationToken cancellationToken) =>
        _active.SetEnabledAsync(enabled, applicationPath, cancellationToken);
    public ValueTask<IntegrationStatus> ProbeAsync(string applicationPath, CancellationToken cancellationToken) =>
        _active.ProbeAsync(applicationPath, cancellationToken);
}

public sealed class StartupRegistrationService(IRegistryAccess registry)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "SwSearch.Broker";

    public ValueTask SetEnabledAsync(bool enabled, string applicationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = Command(applicationPath);
        if (enabled)
        {
            registry.SetString(RegistryHive.CurrentUser, RunKey, ValueName, command);
        }
        else
        {
            registry.DeleteValue(RegistryHive.CurrentUser, RunKey, ValueName);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IntegrationStatus> ProbeAsync(string applicationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabled = string.Equals(
            registry.GetValue(RegistryHive.CurrentUser, RunKey, ValueName) as string,
            Command(applicationPath),
            StringComparison.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new IntegrationStatus(true, enabled, enabled ? "已随 Windows 登录启动" : "未启用"));
    }

    private static string Command(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('"'))
        {
            throw new ArgumentException("Broker 路径必须是无引号的绝对路径。", nameof(path));
        }
        return $"\"{Path.GetFullPath(path)}\" --startup";
    }
}

public sealed class SolidWorksTaskpaneRegistrationService(IRegistryAccess registry)
{
    public const string AddinClsid = "{F3A7CC42-C6E5-4CF8-96D6-2D1F1AA1B61D}";
    public const string MachineAddinKey = @"SOFTWARE\SolidWorks\Addins\" + AddinClsid;
    public const string UserStartupKey = @"Software\SolidWorks\AddInsStartup\" + AddinClsid;

    public ValueTask<IntegrationStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installed = registry.KeyExists(RegistryHive.LocalMachine, MachineAddinKey);
        if (!installed)
        {
            return ValueTask.FromResult(new IntegrationStatus(false, false, "未检测到已安装的 SOLIDWORKS 任务窗格组件"));
        }
        var raw = registry.GetValue(RegistryHive.CurrentUser, UserStartupKey, null);
        var enabled = raw is int value && value != 0;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new IntegrationStatus(true, enabled, enabled ? "已启用；重启 SOLIDWORKS 后生效" : "已禁用；重启 SOLIDWORKS 后生效", true));
    }

    public ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!registry.KeyExists(RegistryHive.LocalMachine, MachineAddinKey))
        {
            throw new InvalidOperationException("未安装 SOLIDWORKS 任务窗格组件，无法启用。");
        }
        registry.SetDword(RegistryHive.CurrentUser, UserStartupKey, null, enabled ? 1 : 0);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class OwnedHelperProcessRunner
{
    private int _activeCount;
    public int ActiveCount => Volatile.Read(ref _activeCount);

    public async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动集成设置助手。");
        Interlocked.Increment(ref _activeCount);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("集成设置助手未在期限内退出。");
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
        }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException) { return; }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
}
