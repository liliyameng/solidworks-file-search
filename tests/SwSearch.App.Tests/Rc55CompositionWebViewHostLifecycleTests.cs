using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;
using SwSearch.WebUi;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc55CompositionWebViewHostLifecycleTests
{
    private static readonly nint HostHandle = (nint)0x5155;
    private static readonly nint ForeignHandle = (nint)0x9191;

    [TestMethod]
    public void ButtonDown_SendFailure_ReleasesCaptureAcquiredByHost()
    {
        var native = new FakeCaptureNative();
        var gate = new CompositionMouseInputGate(native);

        Assert.ThrowsException<InvalidOperationException>(() =>
            gate.ForwardMouseInput(
                HostHandle,
                buttonDown: true,
                buttonUp: false,
                () =>
                {
                    native.Events.Add("send");
                    throw new InvalidOperationException("injected");
                }));

        Assert.AreEqual(nint.Zero, native.Capture);
        CollectionAssert.AreEqual(
            new[] { "set:5155", "send", "get", "release" },
            native.Events);
    }

    [TestMethod]
    public void ButtonUp_ReleasesOwnCaptureBeforeSendEvenWhenSendFails()
    {
        var native = new FakeCaptureNative { Capture = HostHandle };
        var gate = new CompositionMouseInputGate(native);

        Assert.ThrowsException<InvalidOperationException>(() =>
            gate.ForwardMouseInput(
                HostHandle,
                buttonDown: false,
                buttonUp: true,
                () =>
                {
                    native.Events.Add("send");
                    throw new InvalidOperationException("injected");
                }));

        Assert.AreEqual(nint.Zero, native.Capture);
        CollectionAssert.AreEqual(
            new[] { "get", "release", "send" },
            native.Events);
    }

    [TestMethod]
    public void ForeignCapture_IsNeverReleasedByUpSuspendOrCancel()
    {
        var native = new FakeCaptureNative { Capture = ForeignHandle };
        var gate = new CompositionMouseInputGate(native)
        {
            TrackingMouse = true,
        };

        gate.ForwardMouseInput(
            HostHandle,
            buttonDown: false,
            buttonUp: true,
            () => native.Events.Add("send"));
        gate.Suspend(HostHandle);
        gate.Cancel(HostHandle);

        Assert.AreEqual(ForeignHandle, native.Capture);
        Assert.AreEqual(0, native.ReleaseCalls);
        Assert.IsFalse(gate.DesiredVisible);
        Assert.IsFalse(gate.TrackingMouse);
    }

    [TestMethod]
    public void SuspendResumeCancelAndShutdownPath_AreIdempotent()
    {
        var native = new FakeCaptureNative { Capture = HostHandle };
        var gate = new CompositionMouseInputGate(native)
        {
            TrackingMouse = true,
        };

        gate.Suspend(HostHandle);
        gate.Suspend(HostHandle);
        Assert.IsFalse(gate.DesiredVisible);
        Assert.IsFalse(gate.TrackingMouse);
        Assert.AreEqual(1, native.ReleaseCalls);

        gate.Resume();
        gate.Resume();
        Assert.IsTrue(gate.DesiredVisible);

        native.Capture = HostHandle;
        gate.TrackingMouse = true;
        gate.Cancel(HostHandle);
        gate.Cancel(HostHandle);
        Assert.IsFalse(gate.TrackingMouse);
        Assert.AreEqual(2, native.ReleaseCalls);

        gate.Suspend(HostHandle);
        gate.Suspend(HostHandle);
        Assert.AreEqual(2, native.ReleaseCalls);
    }

    [TestMethod]
    public void NonButtonInput_IncludingWheel_DoesNotCaptureOrRelease()
    {
        var native = new FakeCaptureNative();
        var gate = new CompositionMouseInputGate(native);
        var sends = 0;

        gate.ForwardMouseInput(
            HostHandle,
            buttonDown: false,
            buttonUp: false,
            () => sends++);

        Assert.AreEqual(1, sends);
        Assert.AreEqual(0, native.SetCalls);
        Assert.AreEqual(0, native.ReleaseCalls);
        Assert.AreEqual(0, native.GetCalls);
    }

    [TestMethod]
    public void Suspend_ControllerVisibilityFailures_LeaveInputClosedAndAreSwallowed()
    {
        foreach (var failure in new Exception[]
                 {
                     new COMException("injected"),
                     new InvalidOperationException("injected"),
                 })
        {
            var native = new FakeCaptureNative { Capture = HostHandle };
            var gate = new CompositionMouseInputGate(native)
            {
                TrackingMouse = true,
            };

            gate.Resume();
            gate.Suspend(HostHandle, () => throw failure);

            Assert.IsFalse(gate.DesiredVisible);
            Assert.IsFalse(gate.TrackingMouse);
            Assert.AreEqual(nint.Zero, native.Capture);
            Assert.AreEqual(1, native.ReleaseCalls);
        }
    }

    [TestMethod]
    public void Resume_ControllerFailure_DoesNotCommitForwardingState()
    {
        foreach (var failure in new Exception[]
                 {
                     new COMException("injected"),
                     new InvalidOperationException("injected"),
                 })
        {
            var gate = new CompositionMouseInputGate(new FakeCaptureNative());
            gate.Suspend(HostHandle);

            var resumed = gate.TryResume(() => throw failure);

            Assert.IsFalse(resumed);
            Assert.IsFalse(gate.DesiredVisible);
            Assert.IsFalse(gate.TrackingMouse);
        }
    }

    [TestMethod]
    public void Resume_CommitsForwardingOnlyAfterControllerAndBoundsSucceed()
    {
        var gate = new CompositionMouseInputGate(new FakeCaptureNative());
        gate.Suspend(HostHandle);
        var callbackObservedForwarding = true;

        var resumed = gate.TryResume(() =>
        {
            callbackObservedForwarding = gate.DesiredVisible;
        });

        Assert.IsTrue(resumed);
        Assert.IsFalse(
            callbackObservedForwarding,
            "Mouse forwarding must remain disabled while controller visibility and bounds are applied.");
        Assert.IsTrue(gate.DesiredVisible);
    }

    [TestMethod]
    public void ProductionCallGraph_CoversHideMinimizeInitializationCancelAndDispose()
    {
        var host = Read("src", "SwSearch.WebUi", "CompositionWebViewHost.cs");
        var window = Read("src", "SwSearch.App", "MainWindow.xaml.cs");
        var app = Read("src", "SwSearch.App", "App.xaml.cs");

        StringAssert.Contains(host, "controller.IsVisible = false;");
        StringAssert.Contains(host, "if (message.Msg == WmCancelMode)");
        StringAssert.Contains(host, "protected override void OnHandleDestroyed");
        StringAssert.Contains(host, "_inputGate.ForwardMouseInput(");
        StringAssert.Contains(
            SliceMethod(
                host,
                "protected override void Dispose(bool disposing)",
                "private void ConfigureController("),
            "SuspendInput();");
        Assert.AreEqual(
            0,
            Count(
                SliceMethod(
                    host,
                    "if (message.Msg == NativeMethods.WmMouseHWheel)",
                    "else if (message.Msg == NativeMethods.WmMouseWheel)"),
                "SetCapture"),
            "WM_MOUSEHWHEEL must remain forwarding-only.");
        Assert.AreEqual(
            0,
            Count(host, "WS_EX_TRANSPARENT"),
            "The fix must not replace capture balancing with a click-through window style.");

        Assert.AreEqual(
            2,
            Count(window, "SuspendInputAndHide();"),
            "Both close-to-tray and explicit hide-to-tray must share the safe hide gate.");
        StringAssert.Contains(window, "StateChanged += MainWindow_OnStateChanged;");
        StringAssert.Contains(window, "if (WindowState == WindowState.Minimized)");
        // RC90's tray teardown made the toolbar WebView nullable; the safe
        // hide gate suspends through the null-conditional form.
        StringAssert.Contains(window, "_webView?.SuspendInput();");
        StringAssert.Contains(
            app,
            "Dispatcher.BeginInvoke(window.HideToTray",
            "Startup background hiding must flow through the same safe hide path.");

        var initialize = SliceMethod(
            host,
            "public async Task InitializeAsync()",
            "public void PostMessage<T>(");
        Assert.AreEqual(
            3,
            Count(initialize, "if (!CanContinueInitialization(ownerHandle))"),
            "Initialization must recheck lifetime after the gate and both WebView2 awaits.");
        StringAssert.Contains(initialize, "ClosePendingController(pendingController);");
        StringAssert.Contains(initialize, "_initializeGate.Release();");
        Assert.AreEqual(0, Count(host, "_initializeGate.Dispose()"));

        var suspendAndHide = SliceMethod(
            window,
            "private void SuspendInputAndHide()",
            "internal void RefreshCacheFromTray()");
        StringAssert.Contains(suspendAndHide, "try");
        StringAssert.Contains(suspendAndHide, "finally");
        Assert.IsTrue(
            suspendAndHide.IndexOf("_webView?.SuspendInput();", StringComparison.Ordinal) <
            suspendAndHide.IndexOf("Hide();", StringComparison.Ordinal),
            "Hide must run from finally after suspension is attempted.");

        var restore = SliceMethod(window, "internal void RestoreFromTray()", "internal void HideToTray()");
        Assert.IsTrue(
            restore.IndexOf("Show();", StringComparison.Ordinal) <
            restore.IndexOf("_webView?.ResumeInput();", StringComparison.Ordinal),
            "The HWND must be shown before the composition controller resumes.");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        return source[start..end];
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0;
             (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. segments]));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class FakeCaptureNative : ICompositionMouseCaptureNative
    {
        internal List<string> Events { get; } = [];

        internal nint Capture { get; set; }

        internal int GetCalls { get; private set; }

        internal int SetCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        public nint GetCapture()
        {
            GetCalls++;
            Events.Add("get");
            return Capture;
        }

        public nint SetCapture(nint handle)
        {
            SetCalls++;
            Events.Add($"set:{handle.ToInt64():X}");
            var previous = Capture;
            Capture = handle;
            return previous;
        }

        public bool ReleaseCapture()
        {
            ReleaseCalls++;
            Events.Add("release");
            Capture = 0;
            return true;
        }
    }
}
