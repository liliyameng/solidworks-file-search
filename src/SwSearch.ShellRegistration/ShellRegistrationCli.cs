namespace SwSearch.ShellRegistration;

public static class ShellRegistrationCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var viewer = ReadViewerArgument(args.Skip(1).ToArray());
            var service = new ShellRegistrationService(new CurrentUserRegistryStore());
            switch (command)
            {
                case "install":
                    viewer ??= Path.Combine(AppContext.BaseDirectory, "SwSearch.PropertyViewer.exe");
                    var plan = RegistrationPlanFactory.Create(viewer);
                    service.Install(plan);
                    output.WriteLine("已为当前用户安装右键菜单（HKCU）。");
                    output.WriteLine($"查看器：{plan.ViewerPath}");
                    output.WriteLine("Windows 11 请在“显示更多选项”中使用。无需管理员权限。");
                    return 0;

                case "uninstall":
                    service.Uninstall();
                    output.WriteLine("已卸载当前用户右键菜单；仅删除 SwSearch.ViewProperties 产品键。");
                    return 0;

                case "status":
                    viewer ??= Path.Combine(AppContext.BaseDirectory, "SwSearch.PropertyViewer.exe");
                    var status = service.GetStatus(viewer);
                    foreach (var item in status.Extensions)
                    {
                        output.WriteLine($"{item.Extension}: {item.Message}");
                        if (item.CommandValue is not null)
                        {
                            output.WriteLine($"  {item.CommandValue}");
                        }
                    }

                    return status.IsFullyInstalled ? 0 : 2;

                default:
                    error.WriteLine($"未知命令：{args[0]}");
                    WriteUsage(error);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            error.WriteLine($"操作失败：{ex.Message}");
            return 1;
        }
    }

    private static string? ReadViewerArgument(string[] args)
    {
        if (args.Length == 0) return null;
        if (args.Length == 2 && string.Equals(args[0], "--viewer", StringComparison.OrdinalIgnoreCase)) return args[1];
        if (args.Length == 1 && args[0].StartsWith("--viewer=", StringComparison.OrdinalIgnoreCase)) return args[0]["--viewer=".Length..];
        throw new ArgumentException("仅支持 --viewer <绝对路径> 参数。");
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "/?" or "help";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("SwSearch.ShellRegistration install [--viewer <绝对路径>]");
        writer.WriteLine("SwSearch.ShellRegistration uninstall");
        writer.WriteLine("SwSearch.ShellRegistration status [--viewer <绝对路径>]");
        writer.WriteLine("说明：只有 install/uninstall 会修改 HKCU；status 只读。不会写入 HKLM。");
    }
}
