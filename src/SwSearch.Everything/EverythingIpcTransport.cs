using System.Runtime.InteropServices;
using SwSearch.Contracts;

namespace SwSearch.Everything;

public sealed class EverythingIpcTransport : IEverythingQueryTransport
{
    private const uint WmCopyData = 0x004A;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int ErrorTimeout = 1460;
    private readonly TimeSpan _queryTimeout;
    private readonly EverythingIpcLifecycle _lifecycle = new();
    private readonly ReplyWindow _replyWindow;
    private readonly TaskCompletionSource<bool> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeStarted;
    private int _replySequence = 0x53570000;

    internal Func<Task>? AfterQueryGateAcquiredForTesting { get; set; }

    public EverythingIpcTransport(TimeSpan? queryTimeout = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Everything IPC 仅支持 Windows。");
        }

        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(10);
        if (_queryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(queryTimeout));
        }

        _replyWindow = new ReplyWindow();
    }

    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
        new EverythingRuntimeProbe().ProbeAsync(cancellationToken);

    public async ValueTask<EverythingQueryPage> QueryPageAsync(
        string search,
        uint offset,
        uint maximumResults,
        CancellationToken cancellationToken)
    {
        _lifecycle.ThrowIfDisposed(this);
        if (maximumResults == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        await using var lease = await _lifecycle.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (AfterQueryGateAcquiredForTesting is not null)
        {
            await AfterQueryGateAcquiredForTesting().ConfigureAwait(false);
        }

        nint replyHandle;
        using (var initializationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            initializationTimeout.CancelAfter(_queryTimeout);
            try
            {
                replyHandle = await _replyWindow.GetHandleAsync(initializationTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EverythingQueryException(
                    "EVERYTHING_REPLY_WINDOW_TIMEOUT",
                    "Everything IPC 回复窗口初始化超时。");
            }
        }

        var everythingWindow = EverythingRuntimeProbe.FindWindow(
            "EVERYTHING_TASKBAR_NOTIFICATION",
            null);
        if (everythingWindow == nint.Zero)
        {
            throw new EverythingQueryException(
                "EVERYTHING_IPC_NOT_FOUND",
                "未检测到 Everything 1.4 IPC 窗口。");
        }

        var replyId = unchecked((uint)Interlocked.Increment(ref _replySequence));
        var responseTask = _replyWindow.ExpectAsync(replyId);
        var query = EverythingIpcProtocol.BuildQuery(
            replyHandle,
            replyId,
            search,
            offset,
            maximumResults);

        var queryPointer = Marshal.AllocHGlobal(query.Length);
        try
        {
            Marshal.Copy(query, 0, queryPointer, query.Length);
            var copyData = new CopyDataStruct
            {
                Data = EverythingIpcProtocol.CopyDataQuery2W,
                ByteCount = query.Length,
                DataPointer = queryPointer,
            };

            var sendTimeout = checked((uint)Math.Clamp(_queryTimeout.TotalMilliseconds, 1, 2_000));
            var sent = SendMessageTimeout(
                everythingWindow,
                WmCopyData,
                replyHandle,
                ref copyData,
                SmtoBlock | SmtoAbortIfHung,
                sendTimeout,
                out var accepted);
            if (sent == nint.Zero || accepted == 0)
            {
                _replyWindow.Cancel(replyId);
                if (EverythingRuntimeProbe.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null) == nint.Zero)
                {
                    throw new EverythingQueryException(
                        "EVERYTHING_IPC_NOT_FOUND",
                        "Everything IPC 窗口在发送查询时消失。");
                }

                if (sent == nint.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new EverythingQueryException(
                        error == ErrorTimeout ? "EVERYTHING_QUERY_SEND_TIMEOUT" : "EVERYTHING_QUERY_SEND_FAILED",
                        $"Everything did not accept Query2W within the send deadline (Win32={error}).");
                }

                throw new EverythingQueryException(
                    "EVERYTHING_QUERY_REJECTED",
                    $"Everything rejected Query2W (Win32={Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(queryPointer);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_queryTimeout);
        try
        {
            var response = await responseTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            return EverythingIpcProtocol.ParseResult(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _replyWindow.Cancel(replyId);
            var code = EverythingRuntimeProbe.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null) == nint.Zero
                ? "EVERYTHING_IPC_NOT_FOUND"
                : "EVERYTHING_QUERY_TIMEOUT";
            throw new EverythingQueryException(code, "Everything 查询等待结果超时。");
        }
        finally
        {
            _replyWindow.Cancel(replyId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return new ValueTask(_disposeCompletion.Task);
        }

        var drained = _lifecycle.DisposeAsync();
        _ = CompleteDisposeAsync(drained);
        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task CompleteDisposeAsync(ValueTask drained)
    {
        try
        {
            await drained.ConfigureAwait(false);
            _replyWindow.Dispose();
            _disposeCompletion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint Data;
        public int ByteCount;
        public nint DataPointer;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        ref CopyDataStruct lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    private sealed class ReplyWindow : IDisposable
    {
        private const uint WmClose = 0x0010;
        private const uint WmQuit = 0x0012;
        private static readonly nint MessageOnlyWindow = new(-3);
        private readonly object _sync = new();
        private readonly string _className = $"SwSearch.Everything.{Guid.NewGuid():N}";
        private readonly Thread _thread;
        private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WindowProcedure _windowProcedure;
        private uint _pendingId;
        private TaskCompletionSource<byte[]>? _pending;
        private uint _threadId;

        internal ReplyWindow()
        {
            _windowProcedure = WindowProc;
            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "SwSearch Everything IPC reply window",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private nint Handle => _ready.Task.IsCompletedSuccessfully ? _ready.Task.Result : nint.Zero;

        internal Task<nint> GetHandleAsync(CancellationToken cancellationToken) =>
            _ready.Task.WaitAsync(cancellationToken);

        internal Task<byte[]> ExpectAsync(uint replyId)
        {
            lock (_sync)
            {
                if (_pending is not null)
                {
                    throw new InvalidOperationException("同一 IPC 回复窗口只能有一个活动查询。");
                }

                _pendingId = replyId;
                _pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }

        internal void Cancel(uint replyId)
        {
            lock (_sync)
            {
                if (_pendingId != replyId)
                {
                    return;
                }

                _pending?.TrySetCanceled();
                _pending = null;
                _pendingId = 0;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _pending?.TrySetException(new ObjectDisposedException(nameof(ReplyWindow)));
                _pending = null;
                _pendingId = 0;
            }

            if (Handle != nint.Zero)
            {
                _ = PostMessage(Handle, WmClose, nint.Zero, nint.Zero);
            }
            else if (_threadId != 0)
            {
                _ = PostThreadMessage(_threadId, WmQuit, nint.Zero, nint.Zero);
            }

            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void RunMessageLoop()
        {
            _threadId = GetCurrentThreadId();
            var instance = GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Instance = instance,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                ClassName = _className,
            };

            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                _ready.TrySetException(new EverythingQueryException(
                    "EVERYTHING_REPLY_WINDOW_FAILED",
                    $"注册 Everything IPC 回复窗口失败（Win32={Marshal.GetLastWin32Error()}）。"));
                return;
            }

            var window = CreateWindowEx(
                0,
                _className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                MessageOnlyWindow,
                nint.Zero,
                instance,
                nint.Zero);
            if (window == nint.Zero)
            {
                _ = UnregisterClass(_className, instance);
                _ready.TrySetException(new EverythingQueryException(
                    "EVERYTHING_REPLY_WINDOW_FAILED",
                    $"创建 Everything IPC 回复窗口失败（Win32={Marshal.GetLastWin32Error()}）。"));
                return;
            }

            _ready.TrySetResult(window);
            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }

            _ = UnregisterClass(_className, instance);
        }

        private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == WmCopyData)
            {
                var copyData = Marshal.PtrToStructure<CopyDataStruct>(lParam);
                if (copyData.ByteCount is < EverythingIpcProtocol.ListHeaderSize or > 268_435_456 ||
                    copyData.DataPointer == nint.Zero)
                {
                    return nint.Zero;
                }

                TaskCompletionSource<byte[]>? completion = null;
                lock (_sync)
                {
                    if (copyData.Data == _pendingId)
                    {
                        completion = _pending;
                        _pending = null;
                        _pendingId = 0;
                    }
                }

                if (completion is not null)
                {
                    var bytes = new byte[copyData.ByteCount];
                    Marshal.Copy(copyData.DataPointer, bytes, 0, bytes.Length);
                    completion.TrySetResult(bytes);
                    return new nint(1);
                }

                return nint.Zero;
            }

            if (message == WmClose)
            {
                _ = DestroyWindow(window);
                PostQuitMessage(0);
                return nint.Zero;
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public nint WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public string? MenuName;
            public string ClassName;
            public nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public nint Window;
            public uint Value;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public Point Point;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string className, nint instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(ref Message message);

        [DllImport("user32.dll")]
        private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);
    }
}

internal sealed class EverythingIpcLifecycle
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _active;
    private TaskCompletionSource<bool>? _drained;
    private bool _disposed;

    internal void ThrowIfDisposed(object owner)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, owner);
        }
    }

    internal async ValueTask<EverythingIpcLifecycleLease> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active++;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DecrementActive();
            throw;
        }

        return new EverythingIpcLifecycleLease(this);
    }

    internal void Release()
    {
        _gate.Release();
        DecrementActive();
    }

    internal ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_sync)
        {
            _disposed = true;
            if (_active != 0)
            {
                drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _drained = drained;
            }
        }

        if (drained is null)
        {
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }

        return AwaitDrainAsync(drained);
    }

    private async ValueTask AwaitDrainAsync(TaskCompletionSource<bool> drained)
    {
        await drained.Task.ConfigureAwait(false);
        _gate.Dispose();
    }

    private void DecrementActive()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_sync)
        {
            if (--_active == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult(true);
    }
}

internal sealed class EverythingIpcLifecycleLease : IAsyncDisposable
{
    private readonly EverythingIpcLifecycle _lifecycle;
    private int _released;

    internal EverythingIpcLifecycleLease(EverythingIpcLifecycle lifecycle)
    {
        _lifecycle = lifecycle;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _lifecycle.Release();
        }

        return ValueTask.CompletedTask;
    }
}
