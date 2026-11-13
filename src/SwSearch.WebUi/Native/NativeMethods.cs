using System.Runtime.InteropServices;

namespace SwSearch.WebUi.Native;

internal static class NativeMethods
{
    internal const int WmMouseMove = 0x0200;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmLButtonDoubleClick = 0x0203;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmRButtonDoubleClick = 0x0206;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMButtonUp = 0x0208;
    internal const int WmMButtonDoubleClick = 0x0209;
    internal const int WmMouseWheel = 0x020A;
    internal const int WmXButtonDown = 0x020B;
    internal const int WmXButtonUp = 0x020C;
    internal const int WmXButtonDoubleClick = 0x020D;
    internal const int WmMouseHWheel = 0x020E;
    internal const int WmCaptureChanged = 0x0215;
    internal const int WmMouseLeave = 0x02A3;
    internal const int WmSetCursor = 0x0020;

    internal const uint TmeLeave = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint hWnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TrackMouseEventData eventTrack);

    [DllImport("user32.dll")]
    internal static extern nint SetCursor(nint cursor);

    [DllImport("dcomp.dll", ExactSpelling = true)]
    internal static extern int DCompositionCreateDevice(
        nint renderingDevice,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object dcompositionDevice);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventData
    {
        internal int Size;
        internal uint Flags;
        internal nint TrackWindow;
        internal uint HoverTime;
    }
}
