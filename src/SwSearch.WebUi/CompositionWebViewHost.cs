using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using SwSearch.WebUi.Native;
using SwSearch.WebUi.Security;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SwSearch.App.Tests")]

namespace SwSearch.WebUi;

/// <summary>
/// HWND-backed WinForms host for a WebView2 composition controller. This is not a
/// windowed WebView2 control: DirectComposition owns the visual and native window
/// messages are forwarded explicitly to the composition controller.
/// </summary>
public sealed class CompositionWebViewHost : Control
{
    private const int WmCancelMode = 0x001F;
    private readonly WebViewHostOptions _options;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly CompositionMouseInputGate _inputGate;
    private CoreWebView2Environment? _environment;
    private CoreWebView2CompositionController? _controller;
    private DirectCompositionSurface? _compositionSurface;
    private Label? _faultLabel;
    private nint _inputHandle;
    private bool _initializationAttempted;
    private bool _resumeRequested = true;
    private NativeFileDragGesture? _nativeFileDragGesture;
    private PendingShellFileDrag? _pendingShellFileDrag;
    private long _shellFileDragValidationGeneration;
    private bool _shellFileDragActive;
    private bool _disposed;

    public CompositionWebViewHost(WebViewHostOptions options)
        : this(options, Win32CompositionMouseCaptureNative.Instance)
    {
    }

    internal CompositionWebViewHost(
        WebViewHostOptions options,
        ICompositionMouseCaptureNative captureNative)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _inputGate = new CompositionMouseInputGate(captureNative);
        MessageRouter = new WebMessageRouter();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        BackColor = Color.White;
        TabStop = true;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "SwSearch Web 界面";
    }

    [Browsable(false)]
    public WebMessageRouter MessageRouter { get; }

    [Browsable(false)]
    public bool IsReady { get; private set; }

    [Browsable(false)]
    public string? BrowserVersionString
    {
        get
        {
            CoreWebView2Environment? environment;
            lock (_lifecycleGate)
            {
                environment = _environment;
            }

            return environment?.BrowserVersionString;
        }
    }

    public event EventHandler? Ready;

    public event EventHandler<WebViewRuntimeUnavailableEventArgs>? RuntimeUnavailable;

    public event EventHandler<ValidatedWebMessageEventArgs>? MessageReceived;

    public event EventHandler<WebMessageRejectedEventArgs>? MessageRejected;

    public event EventHandler<ExternalLinkRequestedEventArgs>? ExternalLinkRequested;

    public static bool TryValidateMessage(
        WebMessageRouter router,
        string webMessageAsJson,
        out ValidatedWebMessage? message,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(router);
        return router.TryValidate(webMessageAsJson, out message, out error);
    }

    /// <summary>Initializes the Evergreen (or explicitly configured Fixed Version) runtime once.</summary>
    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (!IsHandleCreated)
        {
            _ = Handle;
        }

        var ownerHandle = Handle;
        CoreWebView2Environment? pendingEnvironment = null;
        CoreWebView2CompositionController? pendingController = null;
        DirectCompositionSurface? pendingSurface = null;
        await _initializeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!CanContinueInitialization(ownerHandle))
            {
                return;
            }

            lock (_lifecycleGate)
            {
                if (_disposed || _controller is not null || _initializationAttempted)
                {
                    return;
                }

                _initializationAttempted = true;
            }

            var assetFolder = _options.ResolveAssetFolder();
            ValidateAssetFolder(assetFolder);

            var runtime = _options.RuntimeConfiguration;
            pendingEnvironment = await TryCreateEnvironmentAsync(
                runtime,
                _options.ResolveUserDataFolder()).ConfigureAwait(true);
            if (!CanContinueInitialization(ownerHandle))
            {
                return;
            }

            pendingController = await pendingEnvironment
                .CreateCoreWebView2CompositionControllerAsync(ownerHandle)
                .ConfigureAwait(true);
            if (!CanContinueInitialization(ownerHandle))
            {
                return;
            }

            // The toolbar/preferences pages have no media-heavy content, so the
            // Low memory target lets the runtime shed renderer backing memory
            // it would otherwise keep for fast future rendering.
            pendingController.CoreWebView2.MemoryUsageTargetLevel =
                CoreWebView2MemoryUsageTargetLevel.Low;

            pendingSurface = DirectCompositionSurface.Create(ownerHandle);
            ConfigureController(pendingController, pendingSurface);
            ConfigureSecurity(
                pendingController.CoreWebView2,
                pendingEnvironment,
                assetFolder);
            pendingController.CoreWebView2.Navigate(
                (string.IsNullOrEmpty(_options.Language)
                    ? WebViewOrigin.EntryPoint(_options.View)
                    : WebViewOrigin.EntryPoint(_options.View, _options.Language!)).AbsoluteUri);

            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                _environment = pendingEnvironment;
                _controller = pendingController;
                _compositionSurface = pendingSurface;
                pendingEnvironment = null;
                pendingController = null;
                pendingSurface = null;
            }

            if (_resumeRequested)
            {
                ResumeInput();
            }
        }
        catch (Exception exception)
        {
            _inputGate.Suspend(CurrentHandle);
            if (!IsLifecycleDisposed)
            {
                ShowRuntimeFault(exception);
            }
        }
        finally
        {
            ClosePendingController(pendingController);
            pendingSurface?.Dispose();
            pendingEnvironment = null;
            _initializeGate.Release();
        }
    }

    public void PostMessage<T>(string type, T payload, string? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Message type is required.", nameof(type));
        }

        void PostCore()
        {
            ThrowIfDisposed();
            var controller = CurrentController;
            if (controller?.CoreWebView2 is null)
            {
                throw new InvalidOperationException("WebView2 is not initialized.");
            }

            var envelope = new HostMessageEnvelope<T>(type, payload, requestId);
            controller.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(envelope));
        }

        if (InvokeRequired)
        {
            BeginInvoke(PostCore);
        }
        else
        {
            PostCore();
        }
    }

    /// <summary>
    /// Defers a trusted desktop bridge arm until the current WebView callback
    /// has unwound. The host still requires its own active left-button gesture
    /// and the system DragSize threshold, so a replayed page message can never
    /// initiate an OLE operation by itself.
    /// </summary>
    public void QueueShellFileDragArm(string gestureId, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(gestureId) || IsLifecycleDisposed || !IsHandleCreated)
        {
            return;
        }

        void QueueCore()
        {
            if (IsLifecycleDisposed || !IsHandleCreated ||
                _shellFileDragActive ||
                _nativeFileDragGesture is not { } gesture ||
                !string.Equals(gesture.GestureId, gestureId, StringComparison.Ordinal) ||
                !ShellFileDrop.IsSupportedSolidWorksDocument(fullPath))
            {
                return;
            }

            var pending = new PendingShellFileDrag(
                gestureId,
                fullPath,
                Interlocked.Increment(ref _shellFileDragValidationGeneration),
                IsPathValidated: false);
            _pendingShellFileDrag = pending;
            _ = ValidateShellFileDragAsync(pending);
        }

        // Never start or even arm synchronously in WebMessageReceived. A
        // posted WinForms turn avoids re-entering WebView2 with an OLE modal
        // loop while retaining the user-held native gesture.
        try
        {
            BeginInvoke((Action)QueueCore);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void SuspendInput()
    {
        _resumeRequested = false;
        CancelNativeFileDragGesture();
        var controller = CurrentController;
        _inputGate.Suspend(
            CurrentHandle,
            controller is null ? null : () => controller.IsVisible = false);
    }

    /// <summary>
    /// Releases WebView2 renderer memory while the owner window stays hidden
    /// in the tray. Must run after <see cref="SuspendInput"/> made the
    /// controller invisible; TrySuspendAsync simply returns false for a still
    /// visible controller, so a fast hide/show cycle can never suspend a live
    /// surface. Resume happens through <see cref="ResumeFromHiddenWindow"/>.
    /// </summary>
    public void SuspendForHiddenWindow()
    {
        void SuspendCore()
        {
            var core = CurrentController?.CoreWebView2;
            if (core is null || core.IsSuspended)
            {
                return;
            }
            _ = TrySuspendCoreAsync(core);
        }

        try
        {
            BeginInvoke((Action)SuspendCore);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static async Task TrySuspendCoreAsync(CoreWebView2 core)
    {
        try
        {
            await core.TrySuspendAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // The host is tearing down or the surface became visible again.
        }
    }

    /// <summary>Counterpart of <see cref="SuspendForHiddenWindow"/>; a no-op when the WebView is not suspended.</summary>
    public void ResumeFromHiddenWindow()
    {
        void ResumeCore()
        {
            var core = CurrentController?.CoreWebView2;
            if (core is null || !core.IsSuspended)
            {
                return;
            }
            core.Resume();
        }

        try
        {
            BeginInvoke((Action)ResumeCore);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void ResumeInput()
    {
        if (IsLifecycleDisposed)
        {
            return;
        }

        _resumeRequested = true;
        var (controller, compositionSurface) = CurrentControllerResources;
        if (controller is null || compositionSurface is null || !IsHandleCreated)
        {
            return;
        }

        var resumed = false;
        try
        {
            resumed = _inputGate.TryResume(() =>
            {
                controller.IsVisible = true;
                ApplyControllerBounds(controller, compositionSurface);
            });
        }
        finally
        {
            if (!resumed)
            {
                _resumeRequested = false;
                _inputGate.Suspend(
                    CurrentHandle,
                    () => controller.IsVisible = false);
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _inputHandle = Handle;
        if (!DesignMode)
        {
            BeginInvoke(async () => await InitializeAsync().ConfigureAwait(true));
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateControllerBounds();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateControllerBounds();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmCancelMode)
        {
            _inputGate.Cancel(CurrentHandle);
            CancelNativeFileDragGesture();
        }
        else if (message.Msg == NativeMethods.WmCaptureChanged)
        {
            _inputGate.Cancel(CurrentHandle);
            CancelNativeFileDragGesture();
        }
        else if (message.Msg == NativeMethods.WmLButtonUp)
        {
            // Covers an up that arrives after input has been suspended or the
            // controller is unavailable, where TryForwardMouseMessage will not
            // otherwise see it.
            CancelNativeFileDragGesture();
        }

        // OLE owns mouse dispatch for the short native Shell drag loop. Do not
        // feed those messages back into the browser as a text/HTML drag.
        if (_shellFileDragActive && IsMouseInputMessage(message.Msg))
        {
            base.WndProc(ref message);
            return;
        }

        var controller = CurrentController;
        if (controller is not null && _inputGate.DesiredVisible)
        {
            if (TryForwardMouseMessage(ref message))
            {
                return;
            }

            if (message.Msg == NativeMethods.WmSetCursor && controller.Cursor != 0)
            {
                _ = NativeMethods.SetCursor(controller.Cursor);
                message.Result = (nint)1;
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CancelNativeFileDragGesture();
        _inputGate.Cancel(CurrentHandle);
        _inputHandle = 0;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelNativeFileDragGesture();
            SuspendInput();
            CoreWebView2CompositionController? controller = null;
            DirectCompositionSurface? compositionSurface = null;
            lock (_lifecycleGate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    controller = _controller;
                    compositionSurface = _compositionSurface;
                    _environment = null;
                    _controller = null;
                    _compositionSurface = null;
                }
            }

            if (controller is not null || compositionSurface is not null)
            {
                IsReady = false;
                ClosePendingController(controller);
                compositionSurface?.Dispose();
            }
            else if (IsLifecycleDisposed)
            {
                IsReady = false;
            }
        }

        base.Dispose(disposing);
    }

    private void ConfigureController(
        CoreWebView2CompositionController controller,
        DirectCompositionSurface compositionSurface)
    {
        controller.RootVisualTarget = compositionSurface.RootVisual;
        controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
        controller.ShouldDetectMonitorScaleChanges = true;
        controller.DefaultBackgroundColor = Color.White;
        controller.AllowExternalDrop = false;
        controller.IsVisible = false;
        controller.CursorChanged += (_, _) => Invalidate();
        compositionSurface.Commit();
    }

    private void ConfigureSecurity(
        CoreWebView2 webView,
        CoreWebView2Environment environment,
        string assetFolder)
    {
        var settings = webView.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;

        webView.SetVirtualHostNameToFolderMapping(
            WebViewOrigin.HostName,
            assetFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        webView.NavigationStarting += (sender, args) =>
            args.Cancel = !TryParseLocalUri(args.Uri, out Uri? _);
        webView.FrameNavigationStarting += (_, args) => args.Cancel = true;
        webView.NewWindowRequested += (_, args) => args.Handled = true;
        webView.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
        };
        webView.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            args.Handled = true;
        };
        webView.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        webView.ServerCertificateErrorDetected += (_, args) =>
            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        webView.WebResourceRequested += (sender, args) =>
        {
            if (TryParseLocalUri(args.Request.Uri, out Uri? _))
            {
                return;
            }

            args.Response = environment.CreateWebResourceResponse(
                new MemoryStream(),
                403,
                "Forbidden",
                "Content-Type: text/plain\r\nCache-Control: no-store");
        };
        webView.WebMessageReceived += WebViewOnWebMessageReceived;
        webView.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess || IsReady || IsLifecycleDisposed)
            {
                return;
            }

            IsReady = true;
            Ready?.Invoke(this, EventArgs.Empty);
        };
    }

    private void WebViewOnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!TryParseLocalUri(args.Source, out _))
        {
            MessageRejected?.Invoke(this, new WebMessageRejectedEventArgs("Message source is not local."));
            return;
        }

        if (!TryValidateMessage(MessageRouter, args.WebMessageAsJson, out var message, out var error) || message is null)
        {
            var audit = WebMessageRejectionAudit.FromJson(args.WebMessageAsJson);
            MessageRejected?.Invoke(this, new WebMessageRejectedEventArgs(
                error ?? "Message was rejected.", audit.MessageType, audit.Action, audit.SchemaVersion));
            return;
        }

        if (message.Type == "openExternal")
        {
            var uri = new Uri(message.Payload.GetProperty("uri").GetString()!, UriKind.Absolute);
            if (!_options.ExternalLinkHosts.Contains(uri.Host))
            {
                MessageRejected?.Invoke(this, new WebMessageRejectedEventArgs("External host is not allowed."));
                return;
            }

            ExternalLinkRequested?.Invoke(this, new ExternalLinkRequestedEventArgs(uri));
            return;
        }

        MessageReceived?.Invoke(this, new ValidatedWebMessageEventArgs(message));
    }

    private bool TryForwardMouseMessage(ref Message message)
    {
        var controller = CurrentController;
        if (controller is null || !_inputGate.DesiredVisible)
        {
            return false;
        }

        CoreWebView2MouseEventKind kind;
        var mouseData = 0u;
        Point point;
        CoreWebView2MouseEventVirtualKeys keys;

        if (message.Msg == NativeMethods.WmMouseHWheel)
        {
            var horizontal = HorizontalWheelMessage.Decode(message.WParam, message.LParam);
            keys = horizontal.VirtualKeys;
            mouseData = horizontal.WebViewMouseData;
            point = ScreenToClient(horizontal.ScreenPoint);
            kind = CoreWebView2MouseEventKind.HorizontalWheel;
        }
        else if (message.Msg == NativeMethods.WmMouseWheel)
        {
            keys = DecodeKeys(message.WParam);
            mouseData = DecodeSignedWheelData(message.WParam);
            point = ScreenToClient(DecodePoint(message.LParam));
            kind = CoreWebView2MouseEventKind.Wheel;
        }
        else if (message.Msg == NativeMethods.WmMouseLeave)
        {
            _inputGate.TrackingMouse = false;
            controller.SendMouseInput(CoreWebView2MouseEventKind.Leave, 0, 0, Point.Empty);
            message.Result = 0;
            return true;
        }
        else if (!TryMapClientMouseMessage(message.Msg, out kind))
        {
            return false;
        }
        else
        {
            keys = DecodeKeys(message.WParam);
            point = DecodePoint(message.LParam);
            if (message.Msg is NativeMethods.WmXButtonDown or NativeMethods.WmXButtonUp or NativeMethods.WmXButtonDoubleClick)
            {
                mouseData = unchecked((uint)((message.WParam.ToInt64() >> 16) & 0xFFFF));
            }
        }

        if (kind == CoreWebView2MouseEventKind.Move && !_inputGate.TrackingMouse)
        {
            var track = new NativeMethods.TrackMouseEventData
            {
                Size = Marshal.SizeOf<NativeMethods.TrackMouseEventData>(),
                Flags = NativeMethods.TmeLeave,
                TrackWindow = Handle,
            };
            _inputGate.TrackingMouse = NativeMethods.TrackMouseEvent(ref track);
        }

        if (IsButtonDown(kind))
        {
            Focus();
            controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }

        _inputGate.ForwardMouseInput(
            Handle,
            IsButtonDown(kind),
            IsButtonUp(kind),
            () => controller.SendMouseInput(kind, keys, mouseData, point));

        if (kind == CoreWebView2MouseEventKind.LeftButtonDown)
        {
            BeginNativeFileDragGesture(point);
        }
        else if (kind == CoreWebView2MouseEventKind.Move)
        {
            UpdateNativeFileDragGesture(point);
        }
        else if (kind == CoreWebView2MouseEventKind.LeftButtonUp)
        {
            CancelNativeFileDragGesture();
        }

        message.Result = 0;
        return true;
    }

    private void UpdateControllerBounds()
    {
        var (controller, compositionSurface) = CurrentControllerResources;
        if (controller is null ||
            compositionSurface is null ||
            !_inputGate.DesiredVisible ||
            ClientSize.Width <= 0 ||
            ClientSize.Height <= 0)
        {
            return;
        }

        var boundsApplied = false;
        try
        {
            boundsApplied = _inputGate.TryResume(
                () => ApplyControllerBounds(controller, compositionSurface));
        }
        finally
        {
            if (!boundsApplied)
            {
                _resumeRequested = false;
                _inputGate.Suspend(
                    CurrentHandle,
                    () => controller.IsVisible = false);
            }
        }
    }

    private void ApplyControllerBounds(
        CoreWebView2CompositionController controller,
        DirectCompositionSurface compositionSurface)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            throw new InvalidOperationException(
                "The composition controller cannot resume without positive client bounds.");
        }

        // ClientSize and ScreenToClient are in the same physical coordinate space for
        // a per-monitor-aware HWND. Raw-pixel bounds preserve negative monitor origins
        // and avoid a second application of the DPI scale.
        controller.Bounds = new Rectangle(Point.Empty, ClientSize);
        compositionSurface.Commit();
    }

    private void BeginNativeFileDragGesture(Point point)
    {
        CancelNativeFileDragGesture();
        var gesture = new NativeFileDragGesture(Guid.NewGuid().ToString("N"), point, point);
        _nativeFileDragGesture = gesture;

        try
        {
            PostMessage("fileDrag.gesture", new { gestureId = gesture.GestureId });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            CancelNativeFileDragGesture();
        }
    }

    private void UpdateNativeFileDragGesture(Point point)
    {
        if (_nativeFileDragGesture is not { } gesture)
        {
            return;
        }

        _nativeFileDragGesture = gesture with { LastPoint = point };
        TryStartPendingShellFileDrag();
    }

    private async Task ValidateShellFileDragAsync(PendingShellFileDrag pending)
    {
        // A network/cloud File.Exists can block. Run it away from the UI and
        // only return to this HWND if the exact held gesture remains current.
        var exists = await Task.Run(() => ShellFileDrop.Exists(pending.FullPath)).ConfigureAwait(false);
        if (IsLifecycleDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsLifecycleDisposed ||
                    _pendingShellFileDrag is not { } current ||
                    current.ValidationGeneration != pending.ValidationGeneration ||
                    _nativeFileDragGesture is not { } gesture ||
                    !string.Equals(gesture.GestureId, pending.GestureId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!exists)
                {
                    CancelNativeFileDragGesture();
                    return;
                }

                _pendingShellFileDrag = current with { IsPathValidated = true };
                TryStartPendingShellFileDrag();
            }));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void TryStartPendingShellFileDrag()
    {
        if (_shellFileDragActive ||
            _nativeFileDragGesture is not { } gesture ||
            _pendingShellFileDrag is not { IsPathValidated: true } pending ||
            !string.Equals(gesture.GestureId, pending.GestureId, StringComparison.Ordinal) ||
            !HasExceededDragThreshold(gesture.StartPoint, gesture.LastPoint) ||
            !ShellFileDrop.TryCreateDataObject(pending.FullPath, out var dataObject) ||
            dataObject is null)
        {
            return;
        }

        // Lock/clear the arm before entering OLE. A nested loop may process
        // arbitrary window messages; none may replay this one-file gesture.
        _pendingShellFileDrag = null;
        _nativeFileDragGesture = null;
        StartNativeShellFileDrag(dataObject, gesture.LastPoint);
    }

    private void StartNativeShellFileDrag(DataObject dataObject, Point finalPoint)
    {
        _shellFileDragActive = true;
        _inputGate.Cancel(CurrentHandle);
        try
        {
            // DataFormats.FileDrop maps to CF_HDROP. No text-only fallback is
            // attached to this data object, so Shell targets receive the file.
            _ = DoDragDrop(dataObject, DragDropEffects.Copy);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException or ExternalException or Win32Exception)
        {
            // A target can reject or a shell drag can be cancelled; both leave
            // the native source safely closed without re-entering WebView2.
        }
        finally
        {
            _shellFileDragActive = false;
            CompleteWebPointerAfterShellFileDrag(finalPoint);
        }
    }

    private void CompleteWebPointerAfterShellFileDrag(Point point)
    {
        var controller = CurrentController;
        if (controller is not null && _inputGate.DesiredVisible)
        {
            try
            {
                controller.SendMouseInput(
                    CoreWebView2MouseEventKind.LeftButtonUp,
                    0,
                    0,
                    point);
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException)
            {
                // The native source has already completed. A renderer teardown
                // must not leak capture or revive a stale pending arm.
            }
        }
    }

    private void CancelNativeFileDragGesture()
    {
        _nativeFileDragGesture = null;
        _pendingShellFileDrag = null;
        Interlocked.Increment(ref _shellFileDragValidationGeneration);
    }

    private static bool HasExceededDragThreshold(Point start, Point current)
    {
        var dragSize = SystemInformation.DragSize;
        var rectangle = new Rectangle(
            start.X - dragSize.Width / 2,
            start.Y - dragSize.Height / 2,
            dragSize.Width,
            dragSize.Height);
        return !rectangle.Contains(current);
    }

    private sealed record NativeFileDragGesture(string GestureId, Point StartPoint, Point LastPoint);

    private sealed record PendingShellFileDrag(
        string GestureId,
        string FullPath,
        long ValidationGeneration,
        bool IsPathValidated);

    private bool CanContinueInitialization(nint ownerHandle) =>
        !IsLifecycleDisposed &&
        IsHandleCreated &&
        _inputHandle == ownerHandle;

    private static void ClosePendingController(
        CoreWebView2CompositionController? controller)
    {
        if (controller is null)
        {
            return;
        }

        try
        {
            controller.RootVisualTarget = null;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
        }

        try
        {
            controller.Close();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
        }
    }

    private void ShowRuntimeFault(Exception exception)
    {
        if (IsLifecycleDisposed)
        {
            return;
        }

        var message = exception is WebView2RuntimeNotFoundException
            ? "无法加载界面：此计算机缺少 Microsoft Edge WebView2 Runtime。\r\n请安装系统级 Evergreen WebView2 Runtime 后重试。"
            : $"无法加载 Web 界面。\r\n{exception.Message}";

        _faultLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Padding(24),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = message,
            AccessibleRole = AccessibleRole.StaticText,
            AccessibleName = "WebView2 Runtime 不可用",
        };
        Controls.Add(_faultLabel);
        _faultLabel.BringToFront();
        RuntimeUnavailable?.Invoke(this, new WebViewRuntimeUnavailableEventArgs(message, exception));
    }

    private static void ValidateAssetFolder(string assetFolder)
    {
        if (!Directory.Exists(assetFolder))
        {
            throw new DirectoryNotFoundException($"Web UI asset folder was not found: {assetFolder}");
        }

        foreach (var file in new[] { "index.html", "main.html", "maintoolbar.html", "preferences.html", "property.html", "taskpane.html" })
        {
            if (!File.Exists(Path.Combine(assetFolder, file)))
            {
                throw new FileNotFoundException($"Required Web UI asset was not found: {file}");
            }
        }
    }

    /// <summary>
    /// Environment creation arguments that every host sharing
    /// <c>%LOCALAPPDATA%\SwSearch\WebView2</c> must pass identically: one user
    /// data folder owns one browser process, and a second environment whose
    /// browser arguments differ fails with a COMException. The SOLIDWORKS
    /// taskpane host carries the same literal.
    /// </summary>
    internal const string SharedBrowserArguments =
        "--disk-cache-size=1048576 --media-cache-size=1048576";

    private static async Task<CoreWebView2Environment> TryCreateEnvironmentAsync(
        WebViewRuntimeConfiguration runtime,
        string userDataFolder)
    {
        try
        {
            return await CoreWebView2Environment.CreateAsync(
                NullIfWhiteSpace(runtime.BrowserExecutableFolder),
                userDataFolder,
                new CoreWebView2EnvironmentOptions
                {
                    // Our pages are static toolbars; a bounded browser disk
                    // cache keeps the per-user profile folder from growing
                    // without bound across months of residency.
                    AdditionalBrowserArguments = SharedBrowserArguments,
                }).ConfigureAwait(true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw;
        }
        catch (Exception)
        {
            // A browser process for this user data folder that is already
            // running with different arguments — most often the add-in DLL
            // from before an MSI upgrade, still loaded inside SOLIDWORKS,
            // which the installer must not force-close — rejects our
            // bounded-cache argument set. Join the existing browser process
            // with its own arguments instead of leaving the UI without a web
            // surface; the cache bound is a session-local loss only.
            return await CoreWebView2Environment.CreateAsync(
                NullIfWhiteSpace(runtime.BrowserExecutableFolder),
                userDataFolder,
                options: null).ConfigureAwait(true);
        }
    }

    private static bool TryParseLocalUri(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && WebViewOrigin.IsLocalResource(uri);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static CoreWebView2MouseEventVirtualKeys DecodeKeys(nint wParam) =>
        (CoreWebView2MouseEventVirtualKeys)(unchecked((ulong)wParam.ToInt64()) & 0xFFFF);

    private static uint DecodeSignedWheelData(nint wParam)
    {
        var packed = unchecked((ulong)wParam.ToInt64());
        var delta = unchecked((short)((packed >> 16) & 0xFFFF));
        return unchecked((uint)(int)delta);
    }

    private static Point DecodePoint(nint lParam)
    {
        var packed = unchecked((ulong)lParam.ToInt64());
        return new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
    }

    private Point ScreenToClient(Point screenPoint)
    {
        var native = new NativeMethods.NativePoint(screenPoint.X, screenPoint.Y);
        if (!NativeMethods.ScreenToClient(Handle, ref native))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new Point(native.X, native.Y);
    }

    private static bool TryMapClientMouseMessage(int message, out CoreWebView2MouseEventKind kind)
    {
        kind = message switch
        {
            NativeMethods.WmMouseMove => CoreWebView2MouseEventKind.Move,
            NativeMethods.WmLButtonDown => CoreWebView2MouseEventKind.LeftButtonDown,
            NativeMethods.WmLButtonUp => CoreWebView2MouseEventKind.LeftButtonUp,
            NativeMethods.WmLButtonDoubleClick => CoreWebView2MouseEventKind.LeftButtonDoubleClick,
            NativeMethods.WmRButtonDown => CoreWebView2MouseEventKind.RightButtonDown,
            NativeMethods.WmRButtonUp => CoreWebView2MouseEventKind.RightButtonUp,
            NativeMethods.WmRButtonDoubleClick => CoreWebView2MouseEventKind.RightButtonDoubleClick,
            NativeMethods.WmMButtonDown => CoreWebView2MouseEventKind.MiddleButtonDown,
            NativeMethods.WmMButtonUp => CoreWebView2MouseEventKind.MiddleButtonUp,
            NativeMethods.WmMButtonDoubleClick => CoreWebView2MouseEventKind.MiddleButtonDoubleClick,
            NativeMethods.WmXButtonDown => CoreWebView2MouseEventKind.XButtonDown,
            NativeMethods.WmXButtonUp => CoreWebView2MouseEventKind.XButtonUp,
            NativeMethods.WmXButtonDoubleClick => CoreWebView2MouseEventKind.XButtonDoubleClick,
            _ => 0,
        };
        return kind != 0;
    }

    private static bool IsMouseInputMessage(int message) =>
        message is NativeMethods.WmMouseMove or
            NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp or NativeMethods.WmLButtonDoubleClick or
            NativeMethods.WmRButtonDown or NativeMethods.WmRButtonUp or NativeMethods.WmRButtonDoubleClick or
            NativeMethods.WmMButtonDown or NativeMethods.WmMButtonUp or NativeMethods.WmMButtonDoubleClick or
            NativeMethods.WmXButtonDown or NativeMethods.WmXButtonUp or NativeMethods.WmXButtonDoubleClick or
            NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel or NativeMethods.WmMouseLeave or
            NativeMethods.WmSetCursor;

    private static bool IsButtonDown(CoreWebView2MouseEventKind kind) =>
        kind is CoreWebView2MouseEventKind.LeftButtonDown or
            CoreWebView2MouseEventKind.RightButtonDown or
            CoreWebView2MouseEventKind.MiddleButtonDown or
            CoreWebView2MouseEventKind.XButtonDown;

    private static bool IsButtonUp(CoreWebView2MouseEventKind kind) =>
        kind is CoreWebView2MouseEventKind.LeftButtonUp or
            CoreWebView2MouseEventKind.RightButtonUp or
            CoreWebView2MouseEventKind.MiddleButtonUp or
            CoreWebView2MouseEventKind.XButtonUp;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsLifecycleDisposed, this);
    }

    private bool IsLifecycleDisposed
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _disposed;
            }
        }
    }

    private CoreWebView2CompositionController? CurrentController
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _controller;
            }
        }
    }

    private (
        CoreWebView2CompositionController? Controller,
        DirectCompositionSurface? CompositionSurface) CurrentControllerResources
    {
        get
        {
            lock (_lifecycleGate)
            {
                return (_controller, _compositionSurface);
            }
        }
    }

    private nint CurrentHandle => IsHandleCreated ? Handle : _inputHandle;

    private sealed record HostMessageEnvelope<T>(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("payload")] T Payload,
        [property: JsonPropertyName("requestId")] string? RequestId);
}

internal interface ICompositionMouseCaptureNative
{
    nint GetCapture();

    nint SetCapture(nint handle);

    bool ReleaseCapture();
}

internal sealed class CompositionMouseInputGate
{
    private readonly ICompositionMouseCaptureNative _native;

    internal CompositionMouseInputGate(ICompositionMouseCaptureNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    internal bool DesiredVisible { get; private set; }

    internal bool TrackingMouse { get; set; }

    internal void ForwardMouseInput(
        nint hostHandle,
        bool buttonDown,
        bool buttonUp,
        Action sendInput)
    {
        ArgumentNullException.ThrowIfNull(sendInput);
        if (buttonUp)
        {
            ReleaseOwnCapture(hostHandle);
        }

        if (buttonDown)
        {
            _ = _native.SetCapture(hostHandle);
        }

        try
        {
            sendInput();
        }
        catch
        {
            if (buttonDown)
            {
                ReleaseOwnCapture(hostHandle);
            }

            throw;
        }
    }

    internal void Suspend(nint hostHandle, Action? hideController = null)
    {
        DesiredVisible = false;
        TrackingMouse = false;
        ReleaseOwnCapture(hostHandle);

        if (hideController is null)
        {
            return;
        }

        try
        {
            hideController();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // The input gate is already closed and capture is already balanced.
        }
    }

    internal void Resume()
    {
        DesiredVisible = true;
    }

    internal bool TryResume(Action showControllerAndApplyBounds)
    {
        ArgumentNullException.ThrowIfNull(showControllerAndApplyBounds);
        DesiredVisible = false;
        try
        {
            showControllerAndApplyBounds();
            Resume();
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            TrackingMouse = false;
            return false;
        }
    }

    internal void Cancel(nint hostHandle)
    {
        TrackingMouse = false;
        ReleaseOwnCapture(hostHandle);
    }

    internal bool ReleaseOwnCapture(nint hostHandle)
    {
        if (hostHandle == 0 || _native.GetCapture() != hostHandle)
        {
            return false;
        }

        return _native.ReleaseCapture();
    }
}

internal sealed class Win32CompositionMouseCaptureNative : ICompositionMouseCaptureNative
{
    internal static Win32CompositionMouseCaptureNative Instance { get; } = new();

    private Win32CompositionMouseCaptureNative()
    {
    }

    public nint GetCapture() => GetCaptureNative();

    public nint SetCapture(nint handle) => NativeMethods.SetCapture(handle);

    public bool ReleaseCapture() => NativeMethods.ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "GetCapture")]
    private static extern nint GetCaptureNative();
}
