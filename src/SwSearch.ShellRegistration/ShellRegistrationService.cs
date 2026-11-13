using System.IO;

namespace SwSearch.ShellRegistration;

public sealed record ExtensionRegistrationStatus(
    string Extension,
    bool IsInstalled,
    string Message,
    string? CommandValue);

public sealed record ShellRegistrationStatus(
    bool IsFullyInstalled,
    IReadOnlyList<ExtensionRegistrationStatus> Extensions);

public sealed class ShellRegistrationService(IRegistryStore registry)
{
    public void Install(RegistrationPlan plan)
    {
        RemoveLegacyDrawingVerb();
        foreach (var write in plan.Writes)
        {
            registry.SetValue(write.KeyPath, write.ValueName, write.Value);
        }
    }

    public void Uninstall()
    {
        foreach (var extension in RegistrationPlanFactory.Extensions)
        {
            registry.DeleteTree(VerbKey(extension));
        }
        RemoveLegacyDrawingVerb();
    }

    public ShellRegistrationStatus GetStatus(string? expectedViewerPath = null)
    {
        string? expectedCommand = null;
        if (!string.IsNullOrWhiteSpace(expectedViewerPath))
        {
            expectedCommand = RegistrationPlanFactory.Create(expectedViewerPath, requireFileExists: false).CommandValue;
        }

        var statuses = new List<ExtensionRegistrationStatus>();
        foreach (var extension in RegistrationPlanFactory.Extensions)
        {
            var verbKey = VerbKey(extension);
            var command = registry.GetValue($@"{verbKey}\command", string.Empty);
            var displayName = registry.GetValue(verbKey, string.Empty);
            var validCommand = expectedCommand is null
                ? IsSafeCommandShape(command)
                : string.Equals(command, expectedCommand, StringComparison.OrdinalIgnoreCase);
            var installed = registry.KeyExists(verbKey) &&
                            string.Equals(displayName, RegistrationPlanFactory.DisplayName, StringComparison.Ordinal) &&
                            validCommand;
            var message = installed
                ? "已安装"
                : command is null
                    ? "未安装"
                    : "键存在，但显示名称或命令不符合当前产品计划";
            statuses.Add(new ExtensionRegistrationStatus(extension, installed, message, command));
        }

        return new ShellRegistrationStatus(statuses.All(static item => item.IsInstalled), statuses);
    }

    private static bool IsSafeCommandShape(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.EndsWith(" \"%1\"", StringComparison.Ordinal))
        {
            return false;
        }

        var closingQuote = command.Length - " \"%1\"".Length - 1;
        if (command[0] != '"' || closingQuote <= 1 || command[closingQuote] != '"')
        {
            return false;
        }

        var executable = command[1..closingQuote];
        return Path.IsPathFullyQualified(executable) &&
               string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string VerbKey(string extension) =>
        RegistrationPlanFactory.VerbKey(extension);

    private void RemoveLegacyDrawingVerb() =>
        registry.DeleteTree(RegistrationPlanFactory.LegacyDrawingVerbKey);
}
