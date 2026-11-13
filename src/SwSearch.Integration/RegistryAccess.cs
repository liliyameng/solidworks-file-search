using Microsoft.Win32;

namespace SwSearch.Integration;

public interface IRegistryAccess
{
    object? GetValue(RegistryHive hive, string keyPath, string? valueName);
    bool KeyExists(RegistryHive hive, string keyPath);
    void SetString(RegistryHive hive, string keyPath, string? valueName, string value);
    void SetDword(RegistryHive hive, string keyPath, string? valueName, int value);
    void DeleteTree(RegistryHive hive, string keyPath);
    void DeleteValue(RegistryHive hive, string keyPath, string? valueName);
}

public sealed class WindowsRegistryAccess : IRegistryAccess
{
    public object? GetValue(RegistryHive hive, string keyPath, string? valueName)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public bool KeyExists(RegistryHive hive, string keyPath)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    public void SetString(RegistryHive hive, string keyPath, string? valueName, string value)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"无法创建注册表键：{hive}\\{keyPath}");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void SetDword(RegistryHive hive, string keyPath, string? valueName, int value)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"无法创建注册表键：{hive}\\{keyPath}");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void DeleteTree(RegistryHive hive, string keyPath)
    {
        using var baseKey = OpenBaseKey(hive);
        baseKey.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
    }

    public void DeleteValue(RegistryHive hive, string keyPath, string? valueName)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
    }

    private static RegistryKey OpenBaseKey(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
}
