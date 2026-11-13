using Microsoft.Win32;

namespace SwSearch.ShellRegistration;

public interface IRegistryStore
{
    void SetValue(string keyPath, string valueName, string value);
    string? GetValue(string keyPath, string valueName);
    bool KeyExists(string keyPath);
    void DeleteTree(string keyPath);
}

public sealed class CurrentUserRegistryStore : IRegistryStore
{
    public void SetValue(string keyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"无法创建注册表键：HKCU\\{keyPath}");
        key.SetValue(NormalizeValueName(valueName), value, RegistryValueKind.String);
    }

    public string? GetValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(NormalizeValueName(valueName), null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public bool KeyExists(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    public void DeleteTree(string keyPath) => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

    private static string? NormalizeValueName(string valueName) => valueName.Length == 0 ? null : valueName;
}
