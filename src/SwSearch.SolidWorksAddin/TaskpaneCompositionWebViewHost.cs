using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SwSearch.WebUi;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace SwSearch.SolidWorksAddin
{
    internal static class WebViewRuntimeConfiguration
    {
        internal const string VirtualHostName = "swsearch.local";
        internal const string TaskpaneUri = "https://swsearch.local/taskpane.html";

        // RC18 deliberately uses the system Evergreen Runtime. A future fixed
        // runtime switch has exactly one configuration point here.
        internal static string BrowserExecutableFolder { get { return null; } }

        internal static string UserDataFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SwSearch",
                    "WebView2");
            }
        }
    }

    internal sealed class TaskpaneCompositionWebViewHost : UserControl
    {
        private readonly Label _nativeStatus;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly TaskpaneWebViewInitializationLifetime _initializationLifetime =
            new TaskpaneWebViewInitializationLifetime();
        private readonly object _shutdownGate = new object();
        private readonly List<Action> _shutdownCallbacks = new List<Action>();
        private readonly List<TaskpaneWebViewInitializationLease> _pendingInitializationReleases =
            new List<TaskpaneWebViewInitializationLease>();
        private bool _initializationStarted;
        private int _initializationInFlightCount;
        private bool _shutdownStarted;
        private bool _shutdownResourcesReleased;
        private bool _shutdownCompleted;
        private bool _baseDisposeCompleted;
        private bool _webReady;
        private bool _trackingMouse;
        private CoreWebView2Environment _environment;
        private CoreWebView2CompositionController _compositionController;
        private CoreWebView2 _core;
        private IDCompositionDevice _compositionDevice;
        private IDCompositionTarget _compositionTarget;
        private IDCompositionVisual _rootVisual;
        private TaskpaneWebViewInitializationLease _publishedInitializationLease;
        private TaskpaneWebViewInitializationLease _testInitializationLease;

        // Isolation-harness hook only. Production leaves it null.
        internal static Func<IntPtr, string, Task<TaskpaneWebViewInitializationLease>>
            InitializationFactoryForTesting;

        private string _uiLanguage;

        internal TaskpaneCompositionWebViewHost()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = SystemColors.Window;
            TabStop = true;
            _nativeStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(18),
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                Text = "正在加载搜索界面…"
            };
            Controls.Add(_nativeStatus);
        }

        /// <summary>
        /// Effective UI language for the first page paint. Set before the host
        /// initializes; the setter also re-localizes the native loading label.
        /// Named distinctly from the SwSearch.WebUi.UiLanguage type so both can
        /// be referenced in the same file without qualification.
        /// </summary>
        internal string EffectiveUiLanguage
        {
            get { return _uiLanguage; }
            set
            {
                _uiLanguage = value;
                if (!string.IsNullOrEmpty(value))
                {
                    _nativeStatus.Text = TaskpaneUserText.T(value, "status.loadingUi");
                }
            }
        }

        internal event EventHandler WebReady;
        internal event EventHandler<TaskpaneWebCommandEventArgs> CommandReceived;
        internal event EventHandler<TaskpaneWebHostFailureEventArgs> HostFailure;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (DesignMode) return;
            lock (_shutdownGate)
            {
                if (_initializationStarted || _shutdownStarted) return;
            }
            // A transient failure while releasing the prior HWND generation
            // retains its lease. The replacement HWND gets one immediate
            // owner-STA retry before a new controller may be created.
            if (!ReleasePublishedResources()) return;
            int epoch;
            lock (_shutdownGate)
            {
                if (_initializationStarted || _shutdownStarted) return;
                _initializationStarted = true;
                _initializationInFlightCount++;
                epoch = _initializationLifetime.BeginEpoch();
            }
            try
            {
                BeginInvoke(new Action(delegate { InitializeCompositionAsync(epoch); }));
            }
            catch (Exception exception)
            {
                CompleteInitializationAttempt();
                if (_initializationLifetime.IsCurrent(epoch))
                    ShowFailure("webview-start", exception);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            // A composition controller is bound to one exact HWND lifetime.
            // Invalidate and release the old generation before allowing a new
            // OnHandleCreated generation to initialize the replacement HWND.
            _initializationLifetime.Invalidate();
            bool released = ReleasePublishedResources();
            lock (_shutdownGate)
            {
                if (!_shutdownStarted) _initializationStarted = false;
                else if (!_shutdownCompleted && !released) _shutdownResourcesReleased = false;
            }
            base.OnHandleDestroyed(e);
        }

        private async void InitializeCompositionAsync(int epoch)
        {
            TaskpaneWebViewInitializationLease initialization = null;
            try
            {
                await Task.Yield();
                if (!_initializationLifetime.IsCurrent(epoch)) return;
                string assetRoot = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(typeof(TaskpaneCompositionWebViewHost).Assembly.Location),
                    "webui"));
                Func<IntPtr, string, Task<TaskpaneWebViewInitializationLease>> testFactory =
                    InitializationFactoryForTesting;
                if (testFactory == null)
                {
                    bool assetsAvailable = await Task.Factory.StartNew(
                        delegate { return File.Exists(Path.Combine(assetRoot, "taskpane.html")); });
                    if (!_initializationLifetime.IsCurrent(epoch)) return;
                    if (!assetsAvailable)
                    {
                        ShowFailure(
                            "webview-assets",
                            new FileNotFoundException("Packaged taskpane web assets are missing."));
                        return;
                    }
                }

                if (!IsHandleCreated) return;
                IntPtr ownerHandle = Handle;
                if (testFactory != null)
                {
                    initialization = await testFactory(ownerHandle, assetRoot);
                    if (initialization == null)
                        throw new InvalidOperationException("The WebView initialization test factory returned no lease.");
                    if (!IsCurrentHandle(epoch, ownerHandle)) return;
                    initialization.ConfigureForTesting(ownerHandle);
                }
                else
                {
                    initialization = new TaskpaneWebViewInitializationLease();
                    initialization.Environment = await CreateEnvironmentAsync(
                        WebViewRuntimeConfiguration.BrowserExecutableFolder,
                        WebViewRuntimeConfiguration.UserDataFolder);
                    if (!IsCurrentHandle(epoch, ownerHandle)) return;
                    initialization.Controller = await initialization.Environment
                        .CreateCoreWebView2CompositionControllerAsync(ownerHandle);
                    if (!IsCurrentHandle(epoch, ownerHandle)) return;

                    CreateCompositionVisualTree(initialization, ownerHandle);
                    if (!IsCurrentHandle(epoch, ownerHandle)) return;
                    initialization.Controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                    initialization.Controller.ShouldDetectMonitorScaleChanges = true;
                    initialization.Controller.RootVisualTarget = initialization.RootVisual;
                    initialization.Controller.IsVisible = true;
                    initialization.Controller.DefaultBackgroundColor = Color.White;
                    UpdateControllerScaleAndBounds(initialization.Controller, ownerHandle);
                    ThrowIfFailed(initialization.CompositionDevice.Commit());

                    initialization.Core = initialization.Controller.CoreWebView2;
                    ConfigureLockedDownWebView(initialization.Core, assetRoot, epoch);
                }
                if (!_initializationLifetime.TryPublish(
                    epoch,
                    initialization,
                    PublishInitialization,
                    ReleaseOrRetainInitialization))
                {
                    initialization = null;
                    return;
                }
                initialization = null; // host owns the published lease from here
                _nativeStatus.Visible = false;
                CoreWebView2 core = _core;
                TaskpaneWebViewInitializationLease testInitialization = _testInitializationLease;
                if (IsCurrentHandle(epoch, ownerHandle))
                {
                    if (testInitialization != null)
                        testInitialization.NavigateForTesting(ownerHandle);
                    else if (core != null)
                        core.Navigate(string.IsNullOrEmpty(_uiLanguage)
                            ? WebViewRuntimeConfiguration.TaskpaneUri
                            : WebViewRuntimeConfiguration.TaskpaneUri + "?lang=" +
                              Uri.EscapeDataString(_uiLanguage));
                }
            }
            catch (Exception exception)
            {
                if (_initializationLifetime.IsCurrent(epoch))
                    ShowFailure("webview-init", exception);
            }
            finally
            {
                if (initialization != null) ReleaseOrRetainInitialization(initialization);
                CompleteInitializationAttempt();
            }
        }

        private bool IsCurrentHandle(int epoch, IntPtr ownerHandle)
        {
            return _initializationLifetime.IsCurrent(epoch) &&
                IsHandleCreated && Handle == ownerHandle;
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(
        string browserExecutableFolder,
        string userDataFolder)
    {
        // This host compiles with the C# 5 compiler (csc.exe), so the retry
        // is structured as capture-then-second-attempt instead of an await
        // inside a catch clause.
        CoreWebView2Environment environment = null;
        Exception firstAttemptError = null;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder,
                userDataFolder,
                new CoreWebView2EnvironmentOptions
                {
                    // Must stay byte-identical to SwSearch.WebUi
                    // CompositionWebViewHost.SharedBrowserArguments: both
                    // hosts share one user data folder and therefore one
                    // browser process, whose launch arguments may never
                    // differ between environments.
                    AdditionalBrowserArguments =
                        "--disk-cache-size=1048576 --media-cache-size=1048576"
                });
        }
        catch (Exception exception)
        {
            firstAttemptError = exception;
        }

        if (environment != null)
        {
            return environment;
        }

        try
        {
            // The browser process for this user data folder may already be
            // running with different arguments — for example the desktop App
            // from before an MSI upgrade, which the installer must not
            // force-close. Join the existing browser process with its own
            // arguments instead of failing the taskpane.
            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder,
                userDataFolder,
                null);
        }
        catch
        {
            // A genuinely missing Evergreen runtime fails both attempts
            // identically; surface the original error to ShowFailure.
            throw firstAttemptError;
        }
    }

    private static void CreateCompositionVisualTree(
            TaskpaneWebViewInitializationLease initialization,
            IntPtr ownerHandle)
        {
            Guid deviceId = typeof(IDCompositionDevice).GUID;
            ThrowIfFailed(NativeMethods.DCompositionCreateDevice(
                IntPtr.Zero,
                ref deviceId,
                out initialization.CompositionDevice));
            ThrowIfFailed(initialization.CompositionDevice.CreateTargetForHwnd(
                ownerHandle, true, out initialization.CompositionTarget));
            ThrowIfFailed(initialization.CompositionDevice.CreateVisual(out initialization.RootVisual));
            ThrowIfFailed(initialization.CompositionTarget.SetRoot(initialization.RootVisual));
            ThrowIfFailed(initialization.CompositionDevice.Commit());
        }

        private void PublishInitialization(TaskpaneWebViewInitializationLease initialization)
        {
            _publishedInitializationLease = initialization;
            if (initialization.IsForTesting)
            {
                _testInitializationLease = initialization;
                return;
            }
            _environment = initialization.Environment;
            _compositionController = initialization.Controller;
            _core = initialization.Core;
            _compositionDevice = initialization.CompositionDevice;
            _compositionTarget = initialization.CompositionTarget;
            _rootVisual = initialization.RootVisual;
        }

        private void ConfigureLockedDownWebView(CoreWebView2 core, string assetRoot, int epoch)
        {
            CoreWebView2Settings settings = core.Settings;
            settings.IsScriptEnabled = true;
            settings.IsWebMessageEnabled = true;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsSwipeNavigationEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                WebViewRuntimeConfiguration.VirtualHostName,
                assetRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
            {
                try
                {
                    if (!_initializationLifetime.IsCurrent(epoch) ||
                        !IsAllowedTopLevelUri(args.Uri))
                        args.Cancel = true;
                }
                catch (Exception exception)
                {
                    try { args.Cancel = true; } catch { }
                    if (_initializationLifetime.IsCurrent(epoch))
                        RaiseFailure("webview-navigation", exception);
                }
            };
            core.FrameNavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
            {
                try
                {
                    if (!_initializationLifetime.IsCurrent(epoch) ||
                        !IsAllowedLocalOrigin(args.Uri))
                        args.Cancel = true;
                }
                catch (Exception exception)
                {
                    try { args.Cancel = true; } catch { }
                    if (_initializationLifetime.IsCurrent(epoch))
                        RaiseFailure("webview-frame-navigation", exception);
                }
            };
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args)
            {
                try { args.Handled = true; }
                catch (Exception exception)
                {
                    if (_initializationLifetime.IsCurrent(epoch))
                        RaiseFailure("webview-new-window", exception);
                }
            };
            core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs args)
            {
                try { args.Cancel = true; args.Handled = true; }
                catch (Exception exception)
                {
                    try { args.Cancel = true; args.Handled = true; } catch { }
                    if (_initializationLifetime.IsCurrent(epoch))
                        RaiseFailure("webview-download", exception);
                }
            };
            core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs args)
            {
                try
                {
                    args.State = CoreWebView2PermissionState.Deny;
                    args.Handled = true;
                    args.SavesInProfile = false;
                }
                catch (Exception exception)
                {
                    try
                    {
                        args.State = CoreWebView2PermissionState.Deny;
                        args.Handled = true;
                    }
                    catch { }
                    if (_initializationLifetime.IsCurrent(epoch))
                        RaiseFailure("webview-permission", exception);
                }
            };
            core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args)
            {
                SafeEvent(epoch, "webview-message", delegate { ReceiveWebMessage(args); });
            };
            core.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs args)
            {
                SafeEvent(epoch, "webview-process", delegate
                {
                    ShowFailure("webview-process", new InvalidOperationException("WebView2 browser process stopped."));
                });
            };
            core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                SafeEvent(epoch, "webview-navigation", delegate
                {
                    if (!args.IsSuccess)
                    {
                        ShowFailure("webview-navigation", new InvalidOperationException("Local taskpane page failed to load."));
                        return;
                    }
                    _webReady = true;
                    EventHandler ready = WebReady;
                    if (ready != null) ready(this, EventArgs.Empty);
                });
            };
        }

        private void ReceiveWebMessage(CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!IsAllowedLocalOrigin(args.Source)) return;
            TaskpaneWebCommand command;
            string error;
            if (!TaskpaneWebBridgePolicy.TryParse(
                    args.WebMessageAsJson,
                    String.IsNullOrEmpty(_uiLanguage) ? UiLanguage.Chinese : _uiLanguage,
                    out command,
                    out error))
            {
                PostError(null, error);
                return;
            }
            EventHandler<TaskpaneWebCommandEventArgs> handler = CommandReceived;
            if (handler != null) handler(this, new TaskpaneWebCommandEventArgs(command));
        }

        internal void PostMessage(string type, string requestId, object payload)
        {
            if (String.IsNullOrEmpty(type) || type.Length > 32) return;
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { PostMessage(type, requestId, payload); })); }
                catch (InvalidOperationException) { }
                return;
            }
            if (!_webReady || _core == null) return;
            try
            {
                Dictionary<string, object> envelope = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "type", type },
                    { "payload", payload ?? new object() }
                };
                if (!String.IsNullOrEmpty(requestId)) envelope["requestId"] = requestId;
                _core.PostWebMessageAsJson(_serializer.Serialize(envelope));
            }
            catch (Exception exception)
            {
                RaiseFailure("webview-post", exception);
            }
        }

        internal void PostError(string requestId, string message)
        {
            PostMessage("error", requestId, new { message = String.IsNullOrWhiteSpace(message) ? "操作未完成。" : message });
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SafeEvent("webview-bounds", UpdateControllerScaleAndBounds);
        }

        protected override void WndProc(ref Message message)
        {
            try
            {
                if (message.Msg == NativeMethods.WmSetFocus && _compositionController != null)
                    _compositionController.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                if (message.Msg == NativeMethods.WmDpiChanged)
                    UpdateControllerScaleAndBounds();
                if (message.Msg == NativeMethods.WmSetCursor && _compositionController != null &&
                    _compositionController.Cursor != IntPtr.Zero)
                    NativeMethods.SetCursor(_compositionController.Cursor);
                if (TryForwardMouse(ref message)) return;
            }
            catch (Exception exception)
            {
                RaiseFailure("webview-input", exception);
            }
            base.WndProc(ref message);
        }

        private bool TryForwardMouse(ref Message message)
        {
            CoreWebView2CompositionController controller = _compositionController;
            if (controller == null) return false;

            CoreWebView2MouseEventKind kind;
            bool wheelCoordinates = false;
            bool buttonDown = false;
            bool buttonUp = false;
            switch (message.Msg)
            {
                case NativeMethods.WmMouseMove: kind = CoreWebView2MouseEventKind.Move; break;
                case NativeMethods.WmLeftButtonDown: kind = CoreWebView2MouseEventKind.LeftButtonDown; buttonDown = true; break;
                case NativeMethods.WmLeftButtonUp: kind = CoreWebView2MouseEventKind.LeftButtonUp; buttonUp = true; break;
                case NativeMethods.WmLeftButtonDoubleClick: kind = CoreWebView2MouseEventKind.LeftButtonDoubleClick; buttonDown = true; break;
                case NativeMethods.WmRightButtonDown: kind = CoreWebView2MouseEventKind.RightButtonDown; buttonDown = true; break;
                case NativeMethods.WmRightButtonUp: kind = CoreWebView2MouseEventKind.RightButtonUp; buttonUp = true; break;
                case NativeMethods.WmRightButtonDoubleClick: kind = CoreWebView2MouseEventKind.RightButtonDoubleClick; buttonDown = true; break;
                case NativeMethods.WmMiddleButtonDown: kind = CoreWebView2MouseEventKind.MiddleButtonDown; buttonDown = true; break;
                case NativeMethods.WmMiddleButtonUp: kind = CoreWebView2MouseEventKind.MiddleButtonUp; buttonUp = true; break;
                case NativeMethods.WmMiddleButtonDoubleClick: kind = CoreWebView2MouseEventKind.MiddleButtonDoubleClick; buttonDown = true; break;
                case NativeMethods.WmXButtonDown: kind = CoreWebView2MouseEventKind.XButtonDown; buttonDown = true; break;
                case NativeMethods.WmXButtonUp: kind = CoreWebView2MouseEventKind.XButtonUp; buttonUp = true; break;
                case NativeMethods.WmXButtonDoubleClick: kind = CoreWebView2MouseEventKind.XButtonDoubleClick; buttonDown = true; break;
                case NativeMethods.WmMouseWheel: kind = CoreWebView2MouseEventKind.Wheel; wheelCoordinates = true; break;
                case NativeMethods.WmMouseHWheel: kind = CoreWebView2MouseEventKind.HorizontalWheel; wheelCoordinates = true; break;
                case NativeMethods.WmMouseLeave:
                    kind = CoreWebView2MouseEventKind.Leave;
                    _trackingMouse = false;
                    controller.SendMouseInput(kind, CoreWebView2MouseEventVirtualKeys.None, 0, Point.Empty);
                    message.Result = IntPtr.Zero;
                    return true;
                default:
                    return false;
            }

            if (buttonDown)
            {
                Focus();
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                NativeMethods.SetCapture(Handle);
            }
            if (buttonUp && NativeMethods.GetCapture() == Handle) NativeMethods.ReleaseCapture();

            Point point = TaskpaneMouseInput.DecodeSignedPoint(message.LParam);
            if (wheelCoordinates) point = TaskpaneMouseInput.ScreenPointToClient(Handle, point);
            CoreWebView2MouseEventVirtualKeys keys = TaskpaneMouseInput.DecodeVirtualKeys(message.WParam);
            uint mouseData = 0;
            if (message.Msg == NativeMethods.WmMouseWheel || message.Msg == NativeMethods.WmMouseHWheel)
                mouseData = TaskpaneMouseInput.DecodeWheelData(message.WParam);
            else if (message.Msg == NativeMethods.WmXButtonDown || message.Msg == NativeMethods.WmXButtonUp ||
                     message.Msg == NativeMethods.WmXButtonDoubleClick)
                mouseData = (uint)TaskpaneMouseInput.DecodeUnsignedHighWord(message.WParam);

            controller.SendMouseInput(kind, keys, mouseData, point);
            if (message.Msg == NativeMethods.WmMouseMove && !_trackingMouse)
            {
                NativeMethods.TrackMouseEventForLeave(Handle);
                _trackingMouse = true;
            }
            message.Result = IntPtr.Zero;
            return true;
        }

        private void UpdateControllerScaleAndBounds()
        {
            CoreWebView2CompositionController controller = _compositionController;
            if (controller == null || !IsHandleCreated) return;
            UpdateControllerScaleAndBounds(controller, Handle);
        }

        private static void UpdateControllerScaleAndBounds(
            CoreWebView2CompositionController controller,
            IntPtr ownerHandle)
        {
            if (controller == null || ownerHandle == IntPtr.Zero) return;
            NativeMethods.Rect bounds;
            if (!NativeMethods.GetClientRect(ownerHandle, out bounds)) return;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            controller.RasterizationScale = NativeMethods.DpiForWindow(ownerHandle) / 96.0;
            controller.Bounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }

        private static bool IsAllowedTopLevelUri(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   String.Equals(uri.Host, WebViewRuntimeConfiguration.VirtualHostName, StringComparison.OrdinalIgnoreCase) &&
                   uri.IsDefaultPort &&
                   String.Equals(uri.AbsolutePath, "/taskpane.html", StringComparison.Ordinal);
        }

        private static bool IsAllowedLocalOrigin(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   String.Equals(uri.Host, WebViewRuntimeConfiguration.VirtualHostName, StringComparison.OrdinalIgnoreCase) &&
                   uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo);
        }

        private void SafeEvent(string stage, Action action)
        {
            try { action(); }
            catch (Exception exception) { RaiseFailure(stage, exception); }
        }

        private void SafeEvent(int epoch, string stage, Action action)
        {
            if (!_initializationLifetime.IsCurrent(epoch)) return;
            SafeEvent(stage, action);
        }

        private void ShowFailure(string stage, Exception exception)
        {
            try
            {
                _webReady = false;
                if (_compositionController != null) _compositionController.IsVisible = false;
                _nativeStatus.Text =
                    "无法加载搜索界面\r\n\r\n" +
                    "需要系统安装 Microsoft Edge WebView2 Runtime。\r\n" +
                    "请联系管理员完成离线安装后重新启动 SOLIDWORKS。";
                _nativeStatus.Visible = true;
                _nativeStatus.BringToFront();
            }
            catch { }
            RaiseFailure(stage, exception);
        }

        private void RaiseFailure(string stage, Exception exception)
        {
            try
            {
                EventHandler<TaskpaneWebHostFailureEventArgs> handler = HostFailure;
                if (handler != null) handler(this, new TaskpaneWebHostFailureEventArgs(stage, exception));
                else AddinDiagnostics.WriteFailure(stage, exception);
            }
            catch { }
        }

        private static void ThrowIfFailed(int hresult)
        {
            if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
        }

        internal bool InitializationInFlightForTesting
        {
            get { lock (_shutdownGate) return _initializationInFlightCount != 0; }
        }

        internal bool ShutdownCompletedForTesting
        {
            get { lock (_shutdownGate) return _shutdownCompleted; }
        }

        internal void RecreateHandleForTesting()
        {
            RecreateHandle();
        }

        internal void BeginShutdown(Action completed)
        {
            bool invokeNow = false;
            bool invalidate = false;
            bool releaseResources = false;
            lock (_shutdownGate)
            {
                if (_shutdownCompleted) invokeNow = completed != null;
                else
                {
                    if (completed != null && !_shutdownCallbacks.Contains(completed))
                        _shutdownCallbacks.Add(completed);
                    if (!_shutdownStarted)
                    {
                        _shutdownStarted = true;
                        invalidate = true;
                    }
                    releaseResources = !_shutdownResourcesReleased;
                }
            }
            if (invokeNow)
            {
                InvokeShutdownCallback(completed);
                return;
            }

            if (releaseResources)
            {
                // Invalidate first: either an in-flight TryPublish won the same
                // epoch gate and is now visible below, or its local lease must
                // reject and self-clean before this host can become terminal.
                if (invalidate) _initializationLifetime.Invalidate();
                bool released = ReleasePublishedResources();
                lock (_shutdownGate) _shutdownResourcesReleased = released;
            }
            TrySignalShutdownTerminal();
        }

        private void CompleteInitializationAttempt()
        {
            lock (_shutdownGate)
            {
                if (_initializationInFlightCount > 0) _initializationInFlightCount--;
            }
            TrySignalShutdownTerminal();
        }

        private void TrySignalShutdownTerminal()
        {
            Action[] callbacks = null;
            lock (_shutdownGate)
            {
                if (_shutdownCompleted || !_shutdownStarted ||
                    !_shutdownResourcesReleased || _initializationInFlightCount != 0)
                    return;
                _shutdownCompleted = true;
                callbacks = _shutdownCallbacks.ToArray();
                _shutdownCallbacks.Clear();
            }
            foreach (Action callback in callbacks) InvokeShutdownCallback(callback);
        }

        private static void InvokeShutdownCallback(Action callback)
        {
            if (callback == null) return;
            try { callback(); }
            catch (Exception exception) { AddinDiagnostics.WriteFailure("webview-shutdown-callback", exception); }
        }

        private void ReleaseOrRetainInitialization(
            TaskpaneWebViewInitializationLease initialization)
        {
            if (initialization == null) return;
            Exception failure;
            if (initialization.TryRelease(out failure)) return;
            lock (_shutdownGate)
            {
                if (!_pendingInitializationReleases.Contains(initialization))
                    _pendingInitializationReleases.Add(initialization);
                if (_shutdownStarted) _shutdownResourcesReleased = false;
            }
            ShowFailure(
                "webview-release-initialization",
                failure ?? new InvalidOperationException("WebView initialization release did not complete."));
        }

        private bool ReleasePublishedResources()
        {
            _webReady = false;
            bool released = true;
            TaskpaneWebViewInitializationLease published = _publishedInitializationLease;
            if (published != null)
            {
                Exception failure;
                if (published.TryRelease(out failure))
                {
                    if (Object.ReferenceEquals(_publishedInitializationLease, published))
                    {
                        _publishedInitializationLease = null;
                        _testInitializationLease = null;
                        _core = null;
                        _compositionController = null;
                        _environment = null;
                        _rootVisual = null;
                        _compositionTarget = null;
                        _compositionDevice = null;
                    }
                }
                else
                {
                    released = false;
                    ShowFailure(
                        "webview-release-published",
                        failure ?? new InvalidOperationException("Published WebView release did not complete."));
                }
            }

            TaskpaneWebViewInitializationLease[] pending;
            lock (_shutdownGate) pending = _pendingInitializationReleases.ToArray();
            foreach (TaskpaneWebViewInitializationLease initialization in pending)
            {
                Exception failure;
                if (initialization.TryRelease(out failure))
                {
                    lock (_shutdownGate) _pendingInitializationReleases.Remove(initialization);
                }
                else
                {
                    released = false;
                    ShowFailure(
                        "webview-release-retry",
                        failure ?? new InvalidOperationException("Retained WebView release retry did not complete."));
                }
            }

            lock (_shutdownGate)
            {
                return released &&
                    _publishedInitializationLease == null &&
                    _pendingInitializationReleases.Count == 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                CompleteBaseDispose(false);
                return;
            }
            BeginShutdown(delegate { CompleteBaseDispose(true); });
        }

        private void CompleteBaseDispose(bool disposing)
        {
            if (_baseDisposeCompleted) return;
            _baseDisposeCompleted = true;
            base.Dispose(disposing);
        }

    }

    internal sealed class TaskpaneWebViewInitializationLifetime
    {
        private readonly object _gate = new object();
        private int _epoch = 1;
        private bool _invalidated;

        internal int Capture()
        {
            lock (_gate) return _epoch;
        }

        internal int BeginEpoch()
        {
            lock (_gate)
            {
                unchecked { _epoch++; }
                _invalidated = false;
                return _epoch;
            }
        }

        internal bool IsCurrent(int epoch)
        {
            lock (_gate) return !_invalidated && epoch == _epoch;
        }

        internal void Invalidate()
        {
            lock (_gate)
            {
                if (_invalidated) return;
                _invalidated = true;
                unchecked { _epoch++; }
            }
        }

        internal bool TryPublish<T>(
            int epoch,
            T candidate,
            Action<T> publish,
            Action<T> reject)
        {
            if (publish == null) throw new ArgumentNullException("publish");
            if (reject == null) throw new ArgumentNullException("reject");
            bool accepted = false;
            try
            {
                lock (_gate)
                {
                    if (!_invalidated && epoch == _epoch)
                    {
                        publish(candidate);
                        accepted = true;
                    }
                }
            }
            catch
            {
                reject(candidate);
                throw;
            }
            if (!accepted) reject(candidate);
            return accepted;
        }
    }

    internal sealed class TaskpaneWebViewInitializationLease : IDisposable
    {
        internal CoreWebView2Environment Environment;
        internal CoreWebView2CompositionController Controller;
        internal CoreWebView2 Core;
        internal IDCompositionDevice CompositionDevice;
        internal IDCompositionTarget CompositionTarget;
        internal IDCompositionVisual RootVisual;
        private Action<IntPtr> _configureForTesting;
        private Action<IntPtr> _navigateForTesting;
        private Action _closeControllerForTesting;
        private Action _releaseCompositionForTesting;
        private readonly object _releaseGate = new object();
        private bool _isForTesting;
        private bool _controllerClosed;
        private bool _releaseInProgress;
        private bool _released;

        internal bool IsForTesting { get { return _isForTesting; } }

        internal static TaskpaneWebViewInitializationLease CreateForTesting(
            Action<IntPtr> configure,
            Action<IntPtr> navigate,
            Action closeController,
            Action releaseComposition)
        {
            if (closeController == null) throw new ArgumentNullException("closeController");
            if (releaseComposition == null) throw new ArgumentNullException("releaseComposition");
            return new TaskpaneWebViewInitializationLease
            {
                _isForTesting = true,
                _configureForTesting = configure,
                _navigateForTesting = navigate,
                _closeControllerForTesting = closeController,
                _releaseCompositionForTesting = releaseComposition
            };
        }

        internal void ConfigureForTesting(IntPtr ownerHandle)
        {
            Action<IntPtr> configure = _configureForTesting;
            if (configure != null) configure(ownerHandle);
        }

        internal void NavigateForTesting(IntPtr ownerHandle)
        {
            Action<IntPtr> navigate = _navigateForTesting;
            if (navigate != null) navigate(ownerHandle);
        }

        public void Dispose()
        {
            Exception failure;
            if (!TryRelease(out failure))
                throw new InvalidOperationException(
                    "WebView initialization resources could not be released.",
                    failure);
        }

        internal bool TryRelease(out Exception failure)
        {
            lock (_releaseGate)
            {
                failure = null;
                if (_released) return true;
                if (_releaseInProgress)
                {
                    failure = new InvalidOperationException(
                        "WebView release is already in progress; owner-STA reentry was deferred.");
                    return false;
                }
                _releaseInProgress = true;
                try
                {
                    if (!_controllerClosed)
                    {
                        CoreWebView2CompositionController controller = Controller;
                        if (controller != null)
                        {
                            controller.RootVisualTarget = null;
                            controller.Close();
                        }
                        Action closeController = _closeControllerForTesting;
                        if (closeController != null) closeController();
                        Controller = null;
                        Core = null;
                        Environment = null;
                        _closeControllerForTesting = null;
                        _controllerClosed = true;
                    }

                    Exception releaseFailure;
                    if (!TryReleaseComObject(ref RootVisual, out releaseFailure) ||
                        !TryReleaseComObject(ref CompositionTarget, out releaseFailure) ||
                        !TryReleaseComObject(ref CompositionDevice, out releaseFailure))
                    {
                        failure = releaseFailure;
                        return false;
                    }

                    Action releaseComposition = _releaseCompositionForTesting;
                    if (releaseComposition != null) releaseComposition();
                    _configureForTesting = null;
                    _navigateForTesting = null;
                    _releaseCompositionForTesting = null;
                    _released = true;
                    return true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    return false;
                }
                finally
                {
                    _releaseInProgress = false;
                }
            }
        }

        private static bool TryReleaseComObject<T>(ref T value, out Exception failure)
            where T : class
        {
            failure = null;
            T current = value;
            if (current == null) return true;
            if (!Marshal.IsComObject(current))
            {
                value = null;
                return true;
            }
            try
            {
                Marshal.FinalReleaseComObject(current);
                value = null;
                return true;
            }
            catch (Exception exception)
            {
                failure = exception;
                return false;
            }
        }
    }

    internal static class TaskpaneMouseInput
    {
        internal static int DecodeSignedHighWord(IntPtr value)
        {
            return unchecked((short)((value.ToInt64() >> 16) & 0xffff));
        }

        internal static int DecodeUnsignedHighWord(IntPtr value)
        {
            return unchecked((ushort)((value.ToInt64() >> 16) & 0xffff));
        }

        internal static uint DecodeWheelData(IntPtr wParam)
        {
            return unchecked((uint)DecodeSignedHighWord(wParam));
        }

        internal static Point DecodeSignedPoint(IntPtr lParam)
        {
            long value = lParam.ToInt64();
            return new Point(
                unchecked((short)(value & 0xffff)),
                unchecked((short)((value >> 16) & 0xffff)));
        }

        internal static Point ScreenPointToClient(IntPtr hwnd, Point screen)
        {
            NativeMethods.Point point = new NativeMethods.Point { X = screen.X, Y = screen.Y };
            return NativeMethods.ScreenToClient(hwnd, ref point) ? new Point(point.X, point.Y) : screen;
        }

        internal static CoreWebView2MouseEventVirtualKeys DecodeVirtualKeys(IntPtr wParam)
        {
            int lowWord = unchecked((ushort)(wParam.ToInt64() & 0xffff));
            return (CoreWebView2MouseEventVirtualKeys)(lowWord & 0x7f);
        }
    }

    [ComImport]
    [Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionDevice
    {
        [PreserveSig] int Commit();
        [PreserveSig] int WaitForCommitCompletion();
        [PreserveSig] int GetFrameStatistics(IntPtr statistics);
        [PreserveSig] int CreateTargetForHwnd(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.Bool)] bool topmost,
            out IDCompositionTarget target);
        [PreserveSig] int CreateVisual(out IDCompositionVisual visual);
    }

    [ComImport]
    [Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionTarget
    {
        [PreserveSig] int SetRoot([MarshalAs(UnmanagedType.Interface)] object visual);
    }

    [ComImport]
    [Guid("4D93059D-097B-4651-9A60-F0F25116E2F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionVisual
    {
    }

    internal static class NativeMethods
    {
        internal const int WmSetFocus = 0x0007;
        internal const int WmKillFocus = 0x0008;
        internal const int WmSetCursor = 0x0020;
        internal const int WmMouseMove = 0x0200;
        internal const int WmLeftButtonDown = 0x0201;
        internal const int WmLeftButtonUp = 0x0202;
        internal const int WmLeftButtonDoubleClick = 0x0203;
        internal const int WmRightButtonDown = 0x0204;
        internal const int WmRightButtonUp = 0x0205;
        internal const int WmRightButtonDoubleClick = 0x0206;
        internal const int WmMiddleButtonDown = 0x0207;
        internal const int WmMiddleButtonUp = 0x0208;
        internal const int WmMiddleButtonDoubleClick = 0x0209;
        internal const int WmMouseWheel = 0x020A;
        internal const int WmXButtonDown = 0x020B;
        internal const int WmXButtonUp = 0x020C;
        internal const int WmXButtonDoubleClick = 0x020D;
        internal const int WmMouseHWheel = 0x020E;
        internal const int WmMouseLeave = 0x02A3;
        internal const int WmDpiChanged = 0x02E0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrackMouseEvent
        {
            internal int Size;
            internal int Flags;
            internal IntPtr HwndTrack;
            internal int HoverTime;
        }

        [DllImport("dcomp.dll", PreserveSig = true)]
        internal static extern int DCompositionCreateDevice(
            IntPtr renderingDevice,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IDCompositionDevice device);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "TrackMouseEvent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TrackMouseEventNative(ref TrackMouseEvent track);

        internal static double DpiForWindow(IntPtr hwnd)
        {
            try
            {
                uint value = GetDpiForWindow(hwnd);
                return value == 0 ? 96 : value;
            }
            catch (EntryPointNotFoundException) { return 96; }
        }

        internal static void TrackMouseEventForLeave(IntPtr hwnd)
        {
            TrackMouseEvent track = new TrackMouseEvent
            {
                Size = Marshal.SizeOf(typeof(TrackMouseEvent)),
                Flags = 0x00000002,
                HwndTrack = hwnd,
                HoverTime = 0
            };
            TrackMouseEventNative(ref track);
        }
    }
}
