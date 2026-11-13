using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace SwSearch.SolidWorksAddin
{
    [ComVisible(true)]
    [Guid("F3A7CC42-C6E5-4CF8-96D6-2D1F1AA1B61D")]
    [ProgId("SwSearch.SolidWorksAddin")]
    // SetAddinCallbackInfo2 marshals the callback object as IDispatch.
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class SwAddin : ISwAddin
    {
        private const int DisconnectShutdownTimeoutMilliseconds = 5000;
        private ISldWorks _swApplication;
        private ITaskpaneView _taskpaneView;
        private TaskpaneControl _control;
        private int _disconnecting;

        public bool ConnectToSW(object thisSw, int cookie)
        {
            string stage = "query-interface";
            try
            {
                _swApplication = (ISldWorks)thisSw;
                stage = "set-callback";
                if (!_swApplication.SetAddinCallbackInfo2(0L, this, cookie))
                {
                    throw new InvalidOperationException("SOLIDWORKS rejected the add-in callback registration.");
                }

                stage = "resolve-assets";
                string assets = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "TaskpaneAssets");
                string[] icons = new string[]
                {
                    Path.Combine(assets, "SwSearch-20.png"),
                    Path.Combine(assets, "SwSearch-32.png"),
                    Path.Combine(assets, "SwSearch-40.png"),
                    Path.Combine(assets, "SwSearch-64.png"),
                    Path.Combine(assets, "SwSearch-96.png"),
                    Path.Combine(assets, "SwSearch-128.png")
                };
                try
                {
                    stage = "create-taskpane-v3";
                    _taskpaneView = (ITaskpaneView)_swApplication.CreateTaskpaneView3(icons, "SOLIDWORKS 文件检索");
                }
                catch (Exception exception)
                {
                    AddinDiagnostics.WriteFailure(stage, exception);
                    stage = "create-taskpane-v2";
                    _taskpaneView = (ITaskpaneView)_swApplication.CreateTaskpaneView2(icons[1], "SOLIDWORKS 文件检索");
                }

                stage = "create-control";
                _control = new TaskpaneControl(_swApplication);
                _control.CreateControl();
                stage = "display-taskpane";
                bool displayed = _taskpaneView.DisplayWindowFromHandlex64(_control.Handle.ToInt64());
                if (!displayed)
                {
                    throw new InvalidOperationException("SOLIDWORKS did not accept the Taskpane window handle.");
                }
                stage = "connect-search";
                _control.BeginConnect();
                return true;
            }
            catch (Exception exception)
            {
                AddinDiagnostics.WriteFailure(stage, exception);
                DisconnectFromSW();
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            if (Interlocked.Exchange(ref _disconnecting, 1) != 0) return false;
            try
            {
                TaskpaneControl control = _control;
                ITaskpaneView taskpane = _taskpaneView;
                bool completed = DisconnectTaskpaneOnUi(
                    control,
                    delegate { return DeleteTaskpaneAfterControl(taskpane); },
                    DisconnectShutdownTimeoutMilliseconds);
                if (!completed) return false;

                _control = null;
                _taskpaneView = null;
                _swApplication = null; // borrowed/shared RCW: never release it
                return true;
            }
            catch (Exception exception)
            {
                AddinDiagnostics.WriteFailure("disconnect", exception);
                return false;
            }
            finally { Interlocked.Exchange(ref _disconnecting, 0); }
        }

        internal static bool DisconnectTaskpaneOnUi(
            TaskpaneControl control,
            Func<bool> deleteTaskpane,
            int timeoutMilliseconds)
        {
            if (deleteTaskpane == null) throw new ArgumentNullException("deleteTaskpane");
            try
            {
                if (control != null)
                {
                    // Dispose starts the ordered Shell shutdown. The COM/window
                    // dispatch wait below does not return success until the worker
                    // proxy, ExplorerBrowser and parent HWND are all finalized.
                    control.Dispose();
                    if (!control.ShutdownAndWaitOnUi(timeoutMilliseconds)) return false;
                }
                return deleteTaskpane();
            }
            catch (Exception exception)
            {
                AddinDiagnostics.WriteFailure("disconnect-taskpane-terminal", exception);
                return false;
            }
        }

        private static bool DeleteTaskpaneAfterControl(ITaskpaneView taskpane)
        {
            if (taskpane == null) return true;
            try
            {
                taskpane.DeleteView();
                ReleaseOwnedComObject(taskpane, "release-taskpane");
                return true;
            }
            catch (InvalidComObjectException exception)
            {
                AddinDiagnostics.WriteFailure("delete-taskpane-detached", exception);
                return false;
            }
            catch (COMException exception)
            {
                AddinDiagnostics.WriteFailure("delete-taskpane-com", exception);
                return false;
            }
            catch (Exception exception)
            {
                AddinDiagnostics.WriteFailure("delete-taskpane", exception);
                return false;
            }
        }

        private static void ReleaseOwnedComObject(object item, string stage)
        {
            if (item != null && Marshal.IsComObject(item))
            {
                try { Marshal.FinalReleaseComObject(item); }
                catch (Exception exception) { AddinDiagnostics.WriteFailure(stage, exception); }
            }
        }
    }
}
