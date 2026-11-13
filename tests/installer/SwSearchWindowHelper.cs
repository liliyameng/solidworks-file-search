using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Test-only hidden top-level Win32 window used to exercise WiX CloseApplication.
// It never loads or calls any SwSearch/SOLIDWORKS component.
internal static class SwSearchWindowHelper
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private static bool _ignoreClose;
    private static string _eventPath = string.Empty;
    private static WndProc _windowProcedure = WindowProcedure;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    private static IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WM_CLOSE)
        {
            try
            {
                File.AppendAllText(_eventPath, DateTimeOffset.Now.ToString("o") + " WM_CLOSE\r\n", new UTF8Encoding(false));
            }
            catch
            {
                // Evidence failure is detected by the caller; window behavior stays deterministic.
            }

            if (_ignoreClose)
            {
                return IntPtr.Zero;
            }

            DestroyWindow(window);
            return IntPtr.Zero;
        }

        if (message == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private static string ReadArgument(string[] arguments, string name)
    {
        for (int index = 0; index + 1 < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        throw new ArgumentException("Missing argument: " + name);
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            string mode = ReadArgument(arguments, "--mode");
            string runId = ReadArgument(arguments, "--run-id");
            string nonce = ReadArgument(arguments, "--nonce");
            string readyPath = Path.GetFullPath(ReadArgument(arguments, "--ready"));
            _eventPath = Path.GetFullPath(ReadArgument(arguments, "--events"));
            if (mode != "respond" && mode != "ignore")
            {
                throw new ArgumentException("Mode must be respond or ignore.");
            }
            _ignoreClose = mode == "ignore";

            string className = "SwMsiValidation_" + Guid.NewGuid().ToString("N");
            IntPtr instance = GetModuleHandle(null);
            WNDCLASSEX windowClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                lpfnWndProc = _windowProcedure,
                hInstance = instance,
                lpszClassName = className,
                lpszMenuName = null
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                return 11;
            }

            IntPtr window = CreateWindowEx(
                WS_EX_TOOLWINDOW,
                className,
                "SwSearch MSI CloseApplication validation",
                WS_OVERLAPPED,
                0,
                0,
                1,
                1,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                return 12;
            }

            int pid = Process.GetCurrentProcess().Id;
            int sessionId = Process.GetCurrentProcess().SessionId;
            string json = "{\"pid\":" + pid
                + ",\"sessionId\":" + sessionId
                + ",\"hwnd\":" + window.ToInt64()
                + ",\"mode\":\"" + EscapeJson(mode) + "\""
                + ",\"runId\":\"" + EscapeJson(runId) + "\""
                + ",\"nonce\":\"" + EscapeJson(nonce) + "\"}";
            Directory.CreateDirectory(Path.GetDirectoryName(readyPath));
            File.WriteAllText(readyPath, json, new UTF8Encoding(false));

            MSG message;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            return 0;
        }
        catch
        {
            return 10;
        }
    }
}
