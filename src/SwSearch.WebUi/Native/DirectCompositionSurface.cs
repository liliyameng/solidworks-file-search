using System.Runtime.InteropServices;

namespace SwSearch.WebUi.Native;

internal sealed class DirectCompositionSurface : IDisposable
{
    private IDCompositionDevice? _device;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _rootVisual;

    private DirectCompositionSurface(
        IDCompositionDevice device,
        IDCompositionTarget target,
        IDCompositionVisual rootVisual)
    {
        _device = device;
        _target = target;
        _rootVisual = rootVisual;
    }

    internal object RootVisual => _rootVisual ?? throw new ObjectDisposedException(nameof(DirectCompositionSurface));

    internal static DirectCompositionSurface Create(nint windowHandle)
    {
        var iid = typeof(IDCompositionDevice).GUID;
        Marshal.ThrowExceptionForHR(NativeMethods.DCompositionCreateDevice(0, ref iid, out var created));
        var device = (IDCompositionDevice)created;

        try
        {
            Marshal.ThrowExceptionForHR(device.CreateTargetForHwnd(windowHandle, true, out var target));
            try
            {
                Marshal.ThrowExceptionForHR(device.CreateVisual(out var visual));
                try
                {
                    Marshal.ThrowExceptionForHR(target.SetRoot(visual));
                    Marshal.ThrowExceptionForHR(device.Commit());
                    return new DirectCompositionSurface(device, target, visual);
                }
                catch
                {
                    ReleaseComObject(visual);
                    throw;
                }
            }
            catch
            {
                ReleaseComObject(target);
                throw;
            }
        }
        catch
        {
            ReleaseComObject(device);
            throw;
        }
    }

    internal void Commit()
    {
        if (_device is not null)
        {
            Marshal.ThrowExceptionForHR(_device.Commit());
        }
    }

    public void Dispose()
    {
        ReleaseComObject(Interlocked.Exchange(ref _rootVisual, null));
        ReleaseComObject(Interlocked.Exchange(ref _target, null));
        ReleaseComObject(Interlocked.Exchange(ref _device, null));
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [ComImport]
    [Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionDevice
    {
        [PreserveSig]
        int Commit();

        [PreserveSig]
        int WaitForCommitCompletion();

        [PreserveSig]
        int GetFrameStatistics(nint statistics);

        [PreserveSig]
        int CreateTargetForHwnd(
            nint hwnd,
            [MarshalAs(UnmanagedType.Bool)] bool topmost,
            out IDCompositionTarget target);

        [PreserveSig]
        int CreateVisual(out IDCompositionVisual visual);
    }

    [ComImport]
    [Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionTarget
    {
        [PreserveSig]
        int SetRoot([MarshalAs(UnmanagedType.Interface)] IDCompositionVisual? visual);
    }

    [ComImport]
    [Guid("4D93059D-097B-4651-9A60-F0F25116E2F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionVisual
    {
    }
}
