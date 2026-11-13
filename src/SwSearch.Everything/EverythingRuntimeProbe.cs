using System.Runtime.InteropServices;
using SwSearch.Contracts;

namespace SwSearch.Everything;

public sealed class EverythingRuntimeProbe
{
    private const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    public RuntimeCapability Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(CapabilityState.Unavailable, "EVERYTHING_WINDOWS_ONLY", "Everything 仅支持 Windows。");
        }

        var window = FindWindow(IpcWindowClass, null);
        if (window != nint.Zero)
        {
            return new(
                CapabilityState.Available,
                "EVERYTHING_IPC_READY",
                "Everything IPC 已就绪。",
                new Dictionary<string, string> { ["transport"] = "WM_COPYDATA" });
        }

        return new(
            CapabilityState.Degraded,
            "EVERYTHING_IPC_NOT_FOUND",
            "未检测到 Everything IPC；保留缓存只读搜索并后台重试，不遍历磁盘。");
    }

    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Probe());
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindow(string? className, string? windowName);
}
