// This source is compiled by both the legacy CLR4 taskpane compiler and the
// nullable-enabled WebUi project. Keep the shared file C# 4 compatible while
// containing the WebUi-only nullable analysis at this source boundary. The
// CLR4 compiler rejects the modern CS86xx warning identifiers, so this narrow
// source-scoped pragma is deliberately unnumbered rather than project-global.
#pragma warning disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SwSearch.SolidWorksAddin
{
    internal interface ITaskpaneShellResultsTarget
    {
        Control View { get; }
        event EventHandler HostReady;
        event EventHandler<ShellResultsFailureEventArgs> HostFailure;
        ShellPopulationTicket Replace(long generation, IEnumerable<string> paths);
        ShellPopulationTicket Append(long generation, IEnumerable<string> paths);
        void CancelBefore(long generation);
        void BeginShutdown(Action<ShellShutdownResult> completed);
    }

    /// <summary>
    /// Hosts Explorer DefView over IResultsFolder. The Shell owns selection,
    /// icons, verbs and drag/drop; this control only supplies original file
    /// system IShellItem instances returned by the Broker.
    /// </summary>
    internal sealed class TaskpaneShellResultsHost : Control, ITaskpaneShellResultsTarget
    {
        private const uint ExplorerOptions = 0x1u | 0x40u | 0x80u;
        private const uint NoDropTarget = 0x200u;
        private const uint FwfNoColumnHeader = 0x00800000u;
        // Explorer's DetailsView header performs its own local Shell sort.
        // That sort cannot represent the app's required three-state Broker
        // cycle (ascending -> descending -> relevance/no sort), and it can
        // silently reorder a partially populated result folder.  Hide the
        // Shell-owned column header and let the aligned WinForms header above
        // this view own all ordering decisions instead. The rows themselves
        // are configured from ShellResultColumnContract below, so the custom
        // header never labels a column which the real IShellItem view does not
        // actually render.
        private const uint FolderFlags = 0x1u | 0x40u | 0x10000u | 0x200000u | FwfNoColumnHeader;
        private const int DetailsView = 4;
        private const int WorkerReadyTimeoutMilliseconds = 2000;
        private const int WorkerStopDispatchWaitMilliseconds = 250;
        private const int MarshalReleaseDispatchWaitMilliseconds = 2000;
        private const uint CoWaitDispatchCalls = 0x8u;
        private const uint CoWaitDispatchWindowMessages = 0x10u;
        private const uint ColumnInfoMaskWidth = 0x1u;

        private IExplorerBrowser _browser;
        private object _currentView;
        private IResultsFolder _resultsFolder;
        private ShellResultsPopulationWorker _worker;
        private ExplorerBrowserEventSink _eventSink;
        private uint _adviseCookie;
        private bool _advised;
        private bool _oleInitialized;
        private bool _destroying;
        private bool _baseDisposeCompleted;
        private bool _shutdownCompleted;
        private long _latestGeneration;
        private int _ownedResourceCount;
        private int _destroyCallCount;
        private int _uiThreadId;
        private int _finalizeThreadId;
        private int _finalizeInProgress;
        private int[] _columnWidths = ShellResultColumnContract.CreateDefaultWidths();
        private int[] _appliedColumnWidths;
        private bool _columnsInstalled;
        private ShellRect _lastShellRect;
        private bool _hasLastShellRect;
        private bool _workerDisposeCompleted;
        private bool _unadviseCompleted;
        private bool _unsiteCompleted;
        private bool _destroyCompleted;
        private bool _resultsFolderReleaseCompleted;
        private bool _currentViewReleaseCompleted;
        private bool _browserReleaseCompleted;
        private bool _oleUninitializeCompleted;
        private RegisteredWaitHandle _deferredExitWait;
        private readonly ManualResetEvent _shutdownCompletedEvent = new ManualResetEvent(false);
        private readonly object _shutdownGate = new object();
        private readonly List<Action<ShellShutdownResult>> _shutdownCallbacks =
            new List<Action<ShellShutdownResult>>();

        // Isolation-harness hook only. Production leaves it null.
        internal static Action<string> WorkerOperationHookForTesting;
        internal static Action BeforeWorkerThreadStartForTesting;
        internal static Func<int> WorkerCoInitializeForTesting;
        internal static Func<string, Exception> FinalizeFailureInjectorForTesting;
        internal static Action<string> FinalizeOperationObserverForTesting;
        internal static Func<Action, bool> PostToUiForTesting;
        internal static Func<Action, bool> MarshalReleasePostForTesting;
        internal static Action<IntPtr> MarshalReleaseCoreObserverForTesting;

        internal TaskpaneShellResultsHost()
        {
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
            Dock = DockStyle.Fill;
            BackColor = System.Drawing.SystemColors.Window;
            TabStop = true;
        }

        public event EventHandler HostReady;
        public event EventHandler<ShellResultsFailureEventArgs> HostFailure;

        public Control View { get { return this; } }
        internal bool IsShellReady { get { return _worker != null && _worker.IsRunning; } }
        internal bool TryGetFocusedPath(out string path)
        {
            path = null;
            return _currentView != null && !_destroying
                && FolderViewSelection.TryGetFocusedPath(_currentView, out path);
        }
        internal bool TryGetFocusedIndex(out int index)
        {
            index = -1;
            return _currentView != null && !_destroying
                && FolderViewSelection.TryGetFocusedIndex(_currentView, out index);
        }
        internal long LatestGeneration { get { return Interlocked.Read(ref _latestGeneration); } }
        internal int OwnedResourceCountForTesting { get { return _ownedResourceCount; } }
        internal int DestroyCallCountForTesting { get { return _destroyCallCount; } }
        internal int UiThreadIdForTesting { get { return _uiThreadId; } }
        internal int FinalizeThreadIdForTesting { get { return _finalizeThreadId; } }
        internal bool ShutdownCompletedForTesting { get { lock (_shutdownGate) return _shutdownCompleted; } }
        internal WaitHandle ShutdownCompletedWaitHandle { get { return _shutdownCompletedEvent; } }

        public ShellPopulationTicket Replace(long generation, IEnumerable<string> paths)
        {
            Interlocked.Exchange(ref _latestGeneration, generation);
            ShellResultsPopulationWorker worker = _worker;
            if (worker == null || !worker.IsRunning)
                return ShellPopulationTicket.Completed(generation, false, 0, 0);
            return worker.Replace(generation, SnapshotPaths(paths));
        }

        public ShellPopulationTicket Append(long generation, IEnumerable<string> paths)
        {
            if (generation != Interlocked.Read(ref _latestGeneration))
                return ShellPopulationTicket.Completed(generation, false, 0, 0);
            ShellResultsPopulationWorker worker = _worker;
            if (worker == null || !worker.IsRunning)
                return ShellPopulationTicket.Completed(generation, false, 0, 0);
            return worker.Append(generation, SnapshotPaths(paths));
        }

        public void CancelBefore(long generation)
        {
            Interlocked.Exchange(ref _latestGeneration, generation);
            ShellResultsPopulationWorker worker = _worker;
            if (worker != null) worker.CancelBefore(generation);
        }

        internal void ConfigureColumns(int[] widths)
        {
            int[] normalized = ShellResultColumnContract.NormalizeWidths(widths);
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { ConfigureColumns(normalized); });
                return;
            }

            if (WidthsEqual(_columnWidths, normalized) &&
                (_currentView == null || WidthsEqual(_appliedColumnWidths, normalized)))
                return;

            _columnWidths = normalized;
            try
            {
                ApplyColumnLayout();
            }
            catch (Exception exception)
            {
                ReportFailure("results-column-layout", exception);
                if (_currentView != null) BeginDestroyShellView();
            }
        }

        internal int[] ReadConfiguredColumnWidthsForTesting()
        {
            if (InvokeRequired)
                throw new InvalidOperationException("Column layout evidence must be read on the Shell owner STA.");
            if (_currentView == null || _destroying)
                throw new InvalidOperationException("The ExplorerBrowser view is not available.");

            IColumnManager manager = _currentView as IColumnManager;
            if (manager == null)
                throw new InvalidCastException("The ExplorerBrowser view does not expose IColumnManager.");

            uint count;
            ThrowIfFailed(manager.GetColumnCount(2u, out count), "IColumnManager.GetColumnCount");
            if (count != ShellResultColumnContract.Columns.Length)
                throw new InvalidOperationException("The ExplorerBrowser visible-column count does not match the strict contract.");

            PropertyKey[] keys = new PropertyKey[count];
            ThrowIfFailed(manager.GetColumns(2u, keys, count), "IColumnManager.GetColumns");
            int[] widths = new int[keys.Length];
            for (int index = 0; index < keys.Length; index++)
            {
                PropertyKey expected = ShellResultColumnContract.Columns[index].PropertyKey;
                if (keys[index].FormatId != expected.FormatId || keys[index].PropertyId != expected.PropertyId)
                    throw new InvalidOperationException("The ExplorerBrowser visible-column order does not match the strict contract.");

                PropertyKey key = keys[index];
                ColumnInfo info = new ColumnInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(ColumnInfo)),
                    Mask = ColumnInfoMaskWidth,
                    Name = String.Empty,
                };
                ThrowIfFailed(manager.GetColumnInfo(ref key, ref info), "IColumnManager.GetColumnInfo");
                widths[index] = checked((int)info.Width);
            }
            return widths;
        }

        public void BeginShutdown(Action<ShellShutdownResult> completed)
        {
            bool invokeNow = false;
            lock (_shutdownGate)
            {
                if (_shutdownCompleted) invokeNow = completed != null;
                else if (completed != null && !_shutdownCallbacks.Contains(completed))
                    _shutdownCallbacks.Add(completed);
            }
            if (invokeNow)
            {
                InvokeShutdownCallback(completed, ShellShutdownResult.Success());
                return;
            }
            BeginDestroyShellView();
        }

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
            if (!DesignMode) InitializeShellView();
        }

        protected override void OnResize(EventArgs args)
        {
            base.OnResize(args);
            IExplorerBrowser browser = _browser;
            if (browser == null || !IsHandleCreated || _destroying) return;
            ShellRect rect = ClientShellRect();
            if (_hasLastShellRect && RectEquals(_lastShellRect, rect)) return;
            int hr = browser.SetRect(IntPtr.Zero, rect);
            if (hr < 0) ReportFailure("explorer-browser-resize", Marshal.GetExceptionForHR(hr));
            else
            {
                _lastShellRect = rect;
                _hasLastShellRect = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                CompleteBaseDispose(false);
                return;
            }
            BeginShutdown(delegate(ShellShutdownResult result)
            {
                if (result.Succeeded) CompleteBaseDispose(true);
            });
        }

        private void InitializeShellView()
        {
            if (_browser != null || _destroying) return;
            try
            {
                int ole = ShellResultsNative.OleInitialize(IntPtr.Zero);
                if (ole < 0) Marshal.ThrowExceptionForHR(ole);
                _oleInitialized = true;

                Type browserType = Type.GetTypeFromCLSID(ShellResultsNative.ClsidExplorerBrowser, true);
                _browser = (IExplorerBrowser)Activator.CreateInstance(browserType);
                _ownedResourceCount++;

                IObjectWithSite objectWithSite = _browser as IObjectWithSite;
                if (objectWithSite != null) objectWithSite.SetSite(null);

                ShellRect rect = ClientShellRect();
                FolderSettings settings = new FolderSettings { ViewMode = DetailsView, Flags = FolderFlags };
                ThrowIfFailed(_browser.Initialize(Handle, ref rect, ref settings), "ExplorerBrowser.Initialize");
                _lastShellRect = rect;
                _hasLastShellRect = true;
                ThrowIfFailed(_browser.SetOptions(ExplorerOptions), "ExplorerBrowser.SetOptions");
                ThrowIfFailed(_browser.SetEmptyText("没有匹配的 SOLIDWORKS 文件"), "ExplorerBrowser.SetEmptyText");

                _eventSink = new ExplorerBrowserEventSink(
                    EventNavigationPending,
                    EventViewCreated,
                    EventNavigationComplete,
                    EventNavigationFailed);
                ThrowIfFailed(_browser.Advise(_eventSink, out _adviseCookie), "ExplorerBrowser.Advise");
                _advised = true;
                ThrowIfFailed(_browser.FillFromObject(null, NoDropTarget), "ExplorerBrowser.FillFromObject");

                Guid folderViewId = typeof(IFolderView).GUID;
                IntPtr viewPointer;
                ThrowIfFailed(_browser.GetCurrentView(ref folderViewId, out viewPointer), "ExplorerBrowser.GetCurrentView");
                try { _currentView = Marshal.GetObjectForIUnknown(viewPointer); }
                finally { if (viewPointer != IntPtr.Zero) Marshal.Release(viewPointer); }
                _ownedResourceCount++;
                _columnsInstalled = false;
                _appliedColumnWidths = null;
                ApplyColumnLayout();

                IFolderView folderView = (IFolderView)_currentView;
                Guid resultsId = typeof(IResultsFolder).GUID;
                IntPtr resultsPointer;
                ThrowIfFailed(folderView.GetFolder(ref resultsId, out resultsPointer), "IFolderView.GetFolder");
                try { _resultsFolder = (IResultsFolder)Marshal.GetObjectForIUnknown(resultsPointer); }
                finally { if (resultsPointer != IntPtr.Zero) Marshal.Release(resultsPointer); }
                _ownedResourceCount++;

                IntPtr stream;
                Guid iid = typeof(IResultsFolder).GUID;
                ThrowIfFailed(ShellResultsNative.CoMarshalInterThreadInterfaceInStream(
                    ref iid, _resultsFolder, out stream), "CoMarshalInterThreadInterfaceInStream");
                MarshaledInterfaceStream streamLease = new MarshaledInterfaceStream(
                    stream, ReleaseMarshaledStreamOnUi);
                try
                {
                    _worker = new ShellResultsPopulationWorker(
                        streamLease,
                        ReadLatestGeneration,
                        ReportWorkerFailure,
                        null,
                        WorkerOperationHookForTesting,
                        BeforeWorkerThreadStartForTesting,
                        WorkerCoInitializeForTesting);
                    streamLease = null; // worker owns the marshal stream from here
                    _worker.Start();
                }
                finally
                {
                    if (streamLease != null) streamLease.ReleaseUnconsumed();
                }
                BeginWorkerReadyHandshake(_worker);
            }
            catch (Exception exception)
            {
                ReportFailure("explorer-browser-initialize", exception);
                BeginDestroyShellView();
            }
        }

        private void ApplyColumnLayout()
        {
            if (_currentView == null || _destroying) return;

            IColumnManager manager = _currentView as IColumnManager;
            if (manager == null)
                throw new InvalidCastException("The ExplorerBrowser view does not expose IColumnManager.");

            ShellResultColumnDefinition[] columns = ShellResultColumnContract.Columns;
            PropertyKey[] keys = new PropertyKey[columns.Length];
            for (int index = 0; index < columns.Length; index++)
                keys[index] = columns[index].PropertyKey;

            if (!_columnsInstalled)
            {
                ThrowIfFailed(manager.SetColumns(keys, (uint)keys.Length), "IColumnManager.SetColumns");
                _columnsInstalled = true;
            }
            for (int index = 0; index < columns.Length; index++)
            {
                if (_appliedColumnWidths != null &&
                    index < _appliedColumnWidths.Length &&
                    _appliedColumnWidths[index] == _columnWidths[index])
                    continue;

                PropertyKey key = keys[index];
                ColumnInfo info = new ColumnInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(ColumnInfo)),
                    Mask = ColumnInfoMaskWidth,
                    Width = (uint)_columnWidths[index],
                    Name = String.Empty,
                };
                ThrowIfFailed(manager.SetColumnInfo(ref key, ref info), "IColumnManager.SetColumnInfo");
            }
            _appliedColumnWidths = (int[])_columnWidths.Clone();
        }

        private void BeginWorkerReadyHandshake(ShellResultsPopulationWorker worker)
        {
            Task.Factory.StartNew(delegate
            {
                int signaled = WaitHandle.WaitAny(
                    new[] { worker.ReadyWaitHandle, worker.ExitedWaitHandle },
                    WorkerReadyTimeoutMilliseconds);
                bool posted = PostToUi(delegate
                {
                    if (!Object.ReferenceEquals(_worker, worker) || _destroying) return;
                    if (signaled == 0 && worker.IsRunning)
                    {
                        EventHandler ready = HostReady;
                        if (ready != null) ready(this, EventArgs.Empty);
                        return;
                    }
                    ReportFailure("results-worker-ready",
                        new InvalidOperationException("Shell results worker did not complete its COM handoff."));
                    BeginDestroyShellView();
                });
                if (!posted)
                {
                    worker.RequestStop();
                    AddinDiagnostics.WriteFailure("results-worker-ready-dispatch",
                        new InvalidOperationException("The Shell owner HWND rejected the worker-ready dispatch."));
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        private bool BeginDestroyShellView()
        {
            if (_destroying)
            {
                ShellResultsPopulationWorker active = _worker;
                if (active == null || active.HasExited)
                {
                    FinalizeShellView(active);
                    return ShutdownCompletedForTesting;
                }
                return false;
            }
            _destroying = true;
            ShellResultsPopulationWorker worker = _worker;
            if (worker != null)
            {
                worker.RequestStop();
                int wait = WaitWithComDispatch(
                    new[] { worker.ExitedWaitHandle }, WorkerStopDispatchWaitMilliseconds);
                if (wait != 0 || !worker.HasExited)
                {
                    ReportFailure("results-worker-stop-deferred",
                        new TimeoutException("Shell results worker is still releasing its COM proxy; teardown was deferred."));
                    RegisterDeferredTeardown(worker);
                    return false;
                }
            }
            FinalizeShellView(worker);
            return true;
        }

        private void RegisterDeferredTeardown(ShellResultsPopulationWorker worker)
        {
            if (_deferredExitWait != null) return;
            _deferredExitWait = ThreadPool.RegisterWaitForSingleObject(
                worker.ExitedWaitHandle,
                delegate
                {
                    bool posted = PostToUi(delegate
                    {
                        if (!Object.ReferenceEquals(_worker, worker) || !worker.HasExited) return;
                        RegisteredWaitHandle registration = _deferredExitWait;
                        _deferredExitWait = null;
                        if (registration != null) registration.Unregister(null);
                        FinalizeShellView(worker);
                    });
                    if (!posted)
                    {
                        ReportFailure("results-ui-dispatch-unavailable",
                            new InvalidOperationException(
                                "The Shell owner HWND was unavailable; teardown remains recoverable for a later UI-thread retry."));
                    }
                },
                null,
                Timeout.Infinite,
                true);
        }

        private void FinalizeShellView(ShellResultsPopulationWorker worker)
        {
            if (worker != null && !worker.HasExited) return;
            if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                Exception wrongThread = new InvalidOperationException(
                    "Shell COM teardown must run on the original owner STA.");
                ReportFailure("results-finalize-wrong-thread", wrongThread);
                _destroying = false;
                CompleteShutdownAttempt(ShellShutdownResult.Failure(
                    "results-finalize-wrong-thread", wrongThread));
                return;
            }
            if (Interlocked.CompareExchange(ref _finalizeInProgress, 1, 0) != 0) return;
            _finalizeThreadId = Thread.CurrentThread.ManagedThreadId;
            ShellShutdownResult result = null;
            try
            {
                RegisteredWaitHandle registration = _deferredExitWait;
                _deferredExitWait = null;
                if (registration != null)
                {
                    try { registration.Unregister(null); }
                    catch (Exception exception) { ReportFailure("results-exit-wait-unregister", exception); }
                }

                if (!_workerDisposeCompleted)
                {
                    if (!TryFinalizeStage("results-worker-dispose", "worker-dispose", delegate
                    {
                        if (worker != null) worker.DisposeAfterExit();
                    }, out result)) return;
                    _workerDisposeCompleted = true;
                    if (Object.ReferenceEquals(_worker, worker)) _worker = null;
                }

                IExplorerBrowser browser = _browser;
                if (!_unadviseCompleted)
                {
                    if (!TryFinalizeStage("explorer-browser-unadvise", "unadvise", delegate
                    {
                        if (browser != null && _advised)
                        {
                            int hr = browser.Unadvise(_adviseCookie);
                            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        }
                    }, out result)) return;
                    _unadviseCompleted = true;
                    _advised = false;
                    _eventSink = null;
                }

                if (!_unsiteCompleted)
                {
                    if (!TryFinalizeStage("explorer-browser-unsite", "unsite", delegate
                    {
                        IObjectWithSite objectWithSite = browser as IObjectWithSite;
                        if (objectWithSite != null) objectWithSite.SetSite(null);
                    }, out result)) return;
                    _unsiteCompleted = true;
                }

                if (!_destroyCompleted)
                {
                    if (!TryFinalizeStage("explorer-browser-destroy", "destroy", delegate
                    {
                        if (browser != null)
                        {
                            _destroyCallCount++;
                            int hr = browser.Destroy();
                            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        }
                    }, out result)) return;
                    _destroyCompleted = true;
                }

                if (!_resultsFolderReleaseCompleted)
                {
                    if (!TryFinalizeStage("results-folder-release", "release-results-folder", delegate
                    {
                        ReleaseOwnedComCritical(ref _resultsFolder);
                    }, out result)) return;
                    _resultsFolderReleaseCompleted = true;
                }

                if (!_currentViewReleaseCompleted)
                {
                    if (!TryFinalizeStage("results-view-release", "release-current-view", delegate
                    {
                        ReleaseOwnedComCritical(ref _currentView);
                    }, out result)) return;
                    _currentViewReleaseCompleted = true;
                    _columnsInstalled = false;
                    _appliedColumnWidths = null;
                }

                if (!_browserReleaseCompleted)
                {
                    if (!TryFinalizeStage("explorer-browser-release", "release-browser", delegate
                    {
                        ReleaseOwnedComCritical(ref _browser);
                    }, out result)) return;
                    _browserReleaseCompleted = true;
                    _hasLastShellRect = false;
                }

                if (!_oleUninitializeCompleted)
                {
                    if (!TryFinalizeStage("results-ole-uninitialize", "ole-uninitialize", delegate
                    {
                        if (_oleInitialized) ShellResultsNative.OleUninitialize();
                    }, out result)) return;
                    _oleInitialized = false;
                    _oleUninitializeCompleted = true;
                }

                result = ShellShutdownResult.Success();
            }
            catch (Exception exception)
            {
                ReportFailure("results-finalize-unexpected", exception);
                result = ShellShutdownResult.Failure("results-finalize-unexpected", exception);
            }
            finally
            {
                _destroying = false;
                Interlocked.Exchange(ref _finalizeInProgress, 0);
                CompleteShutdownAttempt(result ?? ShellShutdownResult.Failure(
                    "results-finalize-incomplete",
                    new InvalidOperationException("Shell teardown did not reach a terminal result.")));
            }
        }

        private bool TryFinalizeStage(
            string diagnosticStage,
            string operationStage,
            Action operation,
            out ShellShutdownResult failure)
        {
            try
            {
                Func<string, Exception> injector = FinalizeFailureInjectorForTesting;
                Exception injected = injector == null ? null : injector(operationStage);
                if (injected != null) throw injected;
                operation();
                Action<string> observer = FinalizeOperationObserverForTesting;
                if (observer != null)
                {
                    try { observer(operationStage); }
                    catch (Exception exception) { ReportFailure("results-finalize-observer", exception); }
                }
                failure = null;
                return true;
            }
            catch (Exception exception)
            {
                ReportFailure(diagnosticStage, exception);
                failure = ShellShutdownResult.Failure(diagnosticStage, exception);
                return false;
            }
        }

        private void CompleteShutdownAttempt(ShellShutdownResult result)
        {
            Action<ShellShutdownResult>[] callbacks;
            lock (_shutdownGate)
            {
                if (_shutdownCompleted && !result.Succeeded) return;
                if (result.Succeeded) _shutdownCompleted = true;
                callbacks = _shutdownCallbacks.ToArray();
                _shutdownCallbacks.Clear();
            }
            if (result.Succeeded) _shutdownCompletedEvent.Set();
            foreach (Action<ShellShutdownResult> callback in callbacks)
                InvokeShutdownCallback(callback, result);
        }

        private void InvokeShutdownCallback(Action<ShellShutdownResult> callback, ShellShutdownResult result)
        {
            try { callback(result); }
            catch (Exception exception) { ReportFailure("results-shutdown-callback", exception); }
        }

        private void CompleteBaseDispose(bool disposing)
        {
            if (_baseDisposeCompleted) return;
            _baseDisposeCompleted = true;
            base.Dispose(disposing);
        }

        private bool ReleaseMarshaledStreamOnUi(MarshaledInterfaceStream owner, IntPtr stream)
        {
            if (stream == IntPtr.Zero) return true;
            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
            {
                ReleaseMarshaledStreamCore(stream);
                return true;
            }

            if (!IsHandleCreated || IsDisposed)
                throw new InvalidOperationException("The Shell owner STA was unavailable for marshal-stream release.");
            TaskCompletionSource<Exception> completion = new TaskCompletionSource<Exception>();
            Action releaseOnOwnerSta = delegate
            {
                Exception failure = null;
                try { ReleaseMarshaledStreamCore(stream); }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    // The accepted UI callback owns this raw stream until this
                    // exact completion transition. No disposable signal is
                    // captured, so a late callback cannot Set a closed handle.
                    owner.CompletePendingRelease(stream, failure);
                    completion.TrySetResult(failure);
                }
            };

            bool accepted;
            try
            {
                Func<Action, bool> testPost = MarshalReleasePostForTesting;
                if (testPost != null) accepted = testPost(releaseOnOwnerSta);
                else
                {
                    BeginInvoke(releaseOnOwnerSta);
                    accepted = true;
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The Shell owner STA rejected marshal-stream release dispatch.", exception);
            }
            if (!accepted)
                throw new InvalidOperationException("The Shell owner STA rejected marshal-stream release dispatch.");

            if (!completion.Task.Wait(MarshalReleaseDispatchWaitMilliseconds)) return false;
            Exception completedFailure = completion.Task.Result;
            if (completedFailure != null) throw completedFailure;
            return true;
        }

        private static void ReleaseMarshaledStreamCore(IntPtr stream)
        {
            int hr = ShellResultsNative.CoReleaseMarshalData(stream);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            Marshal.Release(stream);
            Action<IntPtr> observer = MarshalReleaseCoreObserverForTesting;
            if (observer != null)
            {
                try { observer(stream); }
                catch (Exception exception) { AddinDiagnostics.WriteFailure("marshal-release-observer", exception); }
            }
        }

        private bool PostToUi(Action action)
        {
            try
            {
                Func<Action, bool> testPost = PostToUiForTesting;
                if (testPost != null) return testPost(action);
                if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
                {
                    action();
                    return true;
                }
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(action);
                    return true;
                }
                return false;
            }
            catch (InvalidOperationException) { return false; }
        }

        internal static int WaitWithComDispatch(WaitHandle[] waits, int timeoutMilliseconds)
        {
            if (waits == null || waits.Length == 0) return -1;
            IntPtr[] handles = new IntPtr[waits.Length];
            for (int index = 0; index < waits.Length; index++)
                handles[index] = waits[index].SafeWaitHandle.DangerousGetHandle();
            int signaled;
            int hr = ShellResultsNative.CoWaitForMultipleHandles(
                CoWaitDispatchCalls | CoWaitDispatchWindowMessages,
                unchecked((uint)Math.Max(0, timeoutMilliseconds)),
                handles.Length,
                handles,
                out signaled);
            if (hr == 0) return signaled;
            if (hr == unchecked((int)0x80010115)) return -1; // RPC_S_CALLPENDING / timeout
            Marshal.ThrowExceptionForHR(hr);
            return -1;
        }

        private long ReadLatestGeneration() { return Interlocked.Read(ref _latestGeneration); }

        private void ReportWorkerFailure(string stage, Exception exception)
        {
            if (!PostToUi(delegate { ReportFailure(stage, exception); }))
                AddinDiagnostics.WriteFailure(stage, exception);
        }

        private void ReportFailure(string stage, Exception exception)
        {
            AddinDiagnostics.WriteFailure(stage, exception);
            EventHandler<ShellResultsFailureEventArgs> handler = HostFailure;
            if (handler == null) return;
            try { handler(this, new ShellResultsFailureEventArgs(stage, exception)); }
            catch (Exception callbackFailure)
            {
                AddinDiagnostics.WriteFailure("results-host-failure-callback", callbackFailure);
            }
        }

        private ShellRect ClientShellRect()
        {
            return new ShellRect
            {
                Left = 0,
                Top = 0,
                Right = Math.Max(0, ClientSize.Width),
                Bottom = Math.Max(0, ClientSize.Height)
            };
        }

        private static bool WidthsEqual(int[] left, int[] right)
        {
            if (Object.ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index]) return false;
            return true;
        }

        private static bool RectEquals(ShellRect left, ShellRect right)
        {
            return left.Left == right.Left && left.Top == right.Top &&
                left.Right == right.Right && left.Bottom == right.Bottom;
        }

        private static string[] SnapshotPaths(IEnumerable<string> paths)
        {
            if (paths == null) return new string[0];
            List<string> values = new List<string>();
            foreach (string path in paths)
                if (!String.IsNullOrWhiteSpace(path)) values.Add(path);
            return values.ToArray();
        }

        private void ReleaseOwnedComCritical<T>(ref T value) where T : class
        {
            T owned = value;
            if (owned == null) return;
            if (Marshal.IsComObject(owned)) Marshal.ReleaseComObject(owned);
            value = null;
            _ownedResourceCount--;
        }

        private static void ThrowIfFailed(int hr, string operation)
        {
            if (hr >= 0) return;
            Exception error = Marshal.GetExceptionForHR(hr) ?? new COMException(operation + " failed.", hr);
            throw new InvalidOperationException(operation + " failed.", error);
        }

        private int EventNavigationPending(IntPtr childPidl) { return 0; }
        private int EventViewCreated(object shellView) { return 0; }
        private int EventNavigationComplete(IntPtr childPidl) { return 0; }
        private int EventNavigationFailed(IntPtr childPidl) { return 0; }
    }

    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
    public interface IExplorerBrowserEvents
    {
        [PreserveSig] int OnNavigationPending(IntPtr childPidl);
        [PreserveSig] int OnViewCreated([MarshalAs(UnmanagedType.Interface)] object shellView);
        [PreserveSig] int OnNavigationComplete(IntPtr childPidl);
        [PreserveSig] int OnNavigationFailed(IntPtr childPidl);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ExplorerBrowserEventSink : IExplorerBrowserEvents
    {
        private readonly Func<IntPtr, int> _pending;
        private readonly Func<object, int> _created;
        private readonly Func<IntPtr, int> _complete;
        private readonly Func<IntPtr, int> _failed;

        internal ExplorerBrowserEventSink(
            Func<IntPtr, int> pending,
            Func<object, int> created,
            Func<IntPtr, int> complete,
            Func<IntPtr, int> failed)
        {
            _pending = pending;
            _created = created;
            _complete = complete;
            _failed = failed;
        }

        public int OnNavigationPending(IntPtr childPidl) { return _pending(childPidl); }
        public int OnViewCreated(object shellView) { return _created(shellView); }
        public int OnNavigationComplete(IntPtr childPidl) { return _complete(childPidl); }
        public int OnNavigationFailed(IntPtr childPidl) { return _failed(childPidl); }
    }

    internal sealed class ShellShutdownResult
    {
        private ShellShutdownResult(bool succeeded, string stage, Exception exception)
        {
            Succeeded = succeeded;
            Stage = stage ?? String.Empty;
            Exception = exception;
        }

        internal bool Succeeded { get; private set; }
        internal string Stage { get; private set; }
        internal Exception Exception { get; private set; }

        internal static ShellShutdownResult Success()
        {
            return new ShellShutdownResult(true, String.Empty, null);
        }

        internal static ShellShutdownResult Failure(string stage, Exception exception)
        {
            return new ShellShutdownResult(false, stage, exception ??
                new InvalidOperationException("Shell shutdown stage failed."));
        }
    }

    internal sealed class ShellResultsFailureEventArgs : EventArgs
    {
        internal ShellResultsFailureEventArgs(string stage, Exception exception)
        {
            Stage = stage;
            Exception = exception;
        }
        internal string Stage { get; private set; }
        internal Exception Exception { get; private set; }
    }

    internal sealed class ShellPopulationResult
    {
        internal ShellPopulationResult(bool applied, int added, int skipped)
        {
            Applied = applied;
            Added = added;
            Skipped = skipped;
        }
        internal bool Applied { get; private set; }
        internal int Added { get; private set; }
        internal int Skipped { get; private set; }
    }

    internal sealed class ShellPopulationTicket : IDisposable
    {
        private readonly TaskCompletionSource<ShellPopulationResult> _completion =
            new TaskCompletionSource<ShellPopulationResult>();

        internal ShellPopulationTicket(long generation) { Generation = generation; }
        internal long Generation { get; private set; }
        internal bool IsCompleted { get { return _completion.Task.IsCompleted; } }
        internal bool Applied { get { return ResultOrDefault().Applied; } }
        internal int Added { get { return ResultOrDefault().Added; } }
        internal int Skipped { get { return ResultOrDefault().Skipped; } }

        internal static ShellPopulationTicket Completed(long generation, bool applied, int added, int skipped)
        {
            ShellPopulationTicket ticket = new ShellPopulationTicket(generation);
            ticket.Complete(applied, added, skipped);
            return ticket;
        }

        internal void Complete(bool applied, int added, int skipped)
        {
            _completion.TrySetResult(new ShellPopulationResult(applied, added, skipped));
        }

        // Exposes the already-completed source so modern callers can await the
        // worker without parking a thread-pool thread on Wait().
        internal Task Completion { get { return _completion.Task; } }

        internal void Wait(CancellationToken cancellationToken)
        {
            if (_completion.Task.IsCompleted) return;
            WaitHandle completed = ((IAsyncResult)_completion.Task).AsyncWaitHandle;
            int signaled = WaitHandle.WaitAny(new[] { completed, cancellationToken.WaitHandle });
            if (signaled == 1) cancellationToken.ThrowIfCancellationRequested();
            _completion.Task.Wait();
        }

        public void Dispose()
        {
            // Consumers own no producer signal. Early disposal/cancellation is
            // therefore safe while the worker is still completing the command.
        }

        private ShellPopulationResult ResultOrDefault()
        {
            return _completion.Task.IsCompleted
                ? _completion.Task.Result
                : new ShellPopulationResult(false, 0, 0);
        }
    }

    internal sealed class MarshaledInterfaceStream
    {
        private const int StateOwned = 0;
        private const int StateReleasing = 1;
        private const int StatePendingDispatch = 2;
        private const int StateReleased = 3;
        private const int StateConsumed = 4;

        private readonly object _gate = new object();
        private IntPtr _stream;
        private readonly Func<MarshaledInterfaceStream, IntPtr, bool> _release;
        private int _state;
        private static int _outstanding;

        internal MarshaledInterfaceStream(
            IntPtr stream,
            Func<MarshaledInterfaceStream, IntPtr, bool> release)
        {
            if (stream == IntPtr.Zero) throw new ArgumentException("A marshaled interface stream is required.");
            if (release == null) throw new ArgumentNullException("release");
            _stream = stream;
            _release = release;
            Interlocked.Increment(ref _outstanding);
        }

        internal static int OutstandingForTesting { get { return Volatile.Read(ref _outstanding); } }
        internal bool IsReleased
        {
            get { lock (_gate) return _state == StateReleased || _state == StateConsumed; }
        }
        internal bool IsPendingDispatch
        {
            get { lock (_gate) return _state == StatePendingDispatch || _state == StateReleasing; }
        }

        internal IntPtr Consume()
        {
            lock (_gate)
            {
                if (_state != StateOwned) return IntPtr.Zero;
                IntPtr stream = _stream;
                _stream = IntPtr.Zero;
                _state = StateConsumed;
                Interlocked.Decrement(ref _outstanding);
                return stream;
            }
        }

        internal bool ReleaseUnconsumed()
        {
            IntPtr stream;
            lock (_gate)
            {
                if (_state == StateReleased || _state == StateConsumed) return true;
                if (_state == StateReleasing || _state == StatePendingDispatch) return false;
                _state = StateReleasing;
                stream = _stream;
            }
            try
            {
                bool releasedSynchronously = _release(this, stream);
                lock (_gate)
                {
                    if (_state == StateReleased) return true;
                    if (releasedSynchronously) MarkReleasedUnderLock();
                    else if (_state == StateReleasing) _state = StatePendingDispatch;
                    return _state == StateReleased;
                }
            }
            catch
            {
                lock (_gate)
                {
                    if (_state == StateReleasing || _state == StatePendingDispatch)
                        _state = StateOwned;
                }
                throw;
            }
        }

        internal void CompletePendingRelease(IntPtr stream, Exception failure)
        {
            lock (_gate)
            {
                if (_state == StateReleased || _state == StateConsumed) return;
                if (_stream != stream) return;
                if (failure == null) MarkReleasedUnderLock();
                else _state = StateOwned;
            }
        }

        private void MarkReleasedUnderLock()
        {
            if (_state == StateReleased || _state == StateConsumed) return;
            _stream = IntPtr.Zero;
            _state = StateReleased;
            Interlocked.Decrement(ref _outstanding);
        }
    }

    internal sealed class ShellResultsPopulationWorker
    {
        private const int StateCreated = 0;
        private const int StateStarting = 1;
        private const int StateRunning = 2;
        private const int StateStopping = 3;
        private const int StateDead = 4;
        private const int MaximumQueuedCommands = 3;

        private readonly MarshaledInterfaceStream _marshaledResultsStream;
        private readonly Func<long> _latestGeneration;
        private readonly Action<string, Exception> _failure;
        private readonly Func<IResultsFolder> _unmarshalForTesting;
        private readonly Action<string> _operationHook;
        private readonly Action _beforeThreadStart;
        private readonly Func<int> _coInitialize;
        private readonly BlockingCollection<ShellPopulationCommand> _commands =
            new BlockingCollection<ShellPopulationCommand>(
                new ConcurrentQueue<ShellPopulationCommand>(), MaximumQueuedCommands);
        private readonly Thread _thread;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly ManualResetEvent _exited = new ManualResetEvent(false);
        private int _state;
        private int _waitHandlesDisposed;

        internal ShellResultsPopulationWorker(
            IntPtr marshaledResultsStream,
            Func<long> latestGeneration,
            Action<string, Exception> failure)
            : this(null, latestGeneration, failure, null, null, null, null)
        {
            if (marshaledResultsStream != IntPtr.Zero)
                throw new ArgumentException("Raw marshal streams require an explicit owner release policy.");
        }

        internal ShellResultsPopulationWorker(
            IntPtr marshaledResultsStream,
            Func<long> latestGeneration,
            Action<string, Exception> failure,
            Func<IResultsFolder> unmarshalForTesting,
            Action<string> operationHook)
            : this(null, latestGeneration, failure, unmarshalForTesting, operationHook, null, null)
        {
            if (marshaledResultsStream != IntPtr.Zero)
                throw new ArgumentException("Raw marshal streams require an explicit owner release policy.");
        }

        internal ShellResultsPopulationWorker(
            MarshaledInterfaceStream marshaledResultsStream,
            Func<long> latestGeneration,
            Action<string, Exception> failure,
            Func<IResultsFolder> unmarshalForTesting,
            Action<string> operationHook,
            Action beforeThreadStart,
            Func<int> coInitialize)
        {
            if (marshaledResultsStream == null && unmarshalForTesting == null)
                throw new ArgumentException("A marshaled IResultsFolder stream is required.");
            if (latestGeneration == null) throw new ArgumentNullException("latestGeneration");
            _marshaledResultsStream = marshaledResultsStream;
            _latestGeneration = latestGeneration;
            _failure = failure ?? delegate { };
            _unmarshalForTesting = unmarshalForTesting;
            _operationHook = operationHook;
            _beforeThreadStart = beforeThreadStart;
            _coInitialize = coInitialize;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SwSearch Shell Results Population"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        internal WaitHandle ReadyWaitHandle { get { return _ready; } }
        internal WaitHandle ExitedWaitHandle { get { return _exited; } }
        internal bool IsRunning { get { return Volatile.Read(ref _state) == StateRunning; } }
        internal bool HasExited { get { return _exited.WaitOne(0); } }
        internal int PendingCommandCountForTesting { get { return _commands.Count; } }
        internal bool IsAcceptingCommandsForTesting { get { return !_commands.IsAddingCompleted; } }

        internal void Start()
        {
            if (Interlocked.CompareExchange(ref _state, StateStarting, StateCreated) != StateCreated) return;
            try
            {
                if (_beforeThreadStart != null) _beforeThreadStart();
                _thread.Start();
            }
            catch (Exception exception)
            {
                try { ReportFailureNoThrow("results-worker-start", exception); }
                finally { CompleteFailedStart(); }
                throw;
            }
        }

        internal ShellPopulationTicket Replace(long generation, string[] paths)
        {
            CancelBefore(generation);
            return Queue(new ShellPopulationCommand(generation, true, paths));
        }

        internal ShellPopulationTicket Append(long generation, string[] paths)
        {
            return Queue(new ShellPopulationCommand(generation, false, paths));
        }

        internal void CancelBefore(long generation)
        {
            ShellPopulationCommand pending;
            while (_commands.TryTake(out pending))
                pending.Ticket.Complete(false, 0, pending.Paths.Length);
        }

        internal void RequestStop()
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _state);
                if (observed == StateDead || observed == StateStopping) break;
            }
            while (Interlocked.CompareExchange(ref _state, StateStopping, observed) != observed);
            ShellPopulationCommand pending;
            while (_commands.TryTake(out pending))
                pending.Ticket.Complete(false, 0, pending.Paths.Length);
            try { _commands.CompleteAdding(); }
            catch (InvalidOperationException) { }
        }

        internal void DisposeAfterExit()
        {
            if (!HasExited) throw new InvalidOperationException("Worker wait handles cannot be released before exit.");
            if (_marshaledResultsStream != null && !_marshaledResultsStream.ReleaseUnconsumed())
                throw new InvalidOperationException(
                    "The marshal-stream release is still owned by an accepted UI dispatch.");
            if (Interlocked.Exchange(ref _waitHandlesDisposed, 1) != 0) return;
            _ready.Close();
            _exited.Close();
            _commands.Dispose();
        }

        private ShellPopulationTicket Queue(ShellPopulationCommand command)
        {
            if (!IsRunning)
            {
                command.Ticket.Complete(false, 0, command.Paths.Length);
                return command.Ticket;
            }
            try
            {
                if (!_commands.TryAdd(command))
                    command.Ticket.Complete(false, 0, command.Paths.Length);
            }
            catch (InvalidOperationException)
            {
                command.Ticket.Complete(false, 0, command.Paths.Length);
            }
            return command.Ticket;
        }

        private void Run()
        {
            IResultsFolder folder = null;
            int initialized = unchecked((int)0x80004005);
            try
            {
                initialized = _coInitialize == null
                    ? ShellResultsNative.CoInitializeEx(IntPtr.Zero, 0x0u)
                    : _coInitialize();
                if (initialized < 0) Marshal.ThrowExceptionForHR(initialized);
                folder = _unmarshalForTesting == null ? UnmarshalResultsFolder() : _unmarshalForTesting();
                if (folder == null) throw new InvalidOperationException("IResultsFolder unmarshal returned null.");
                if (Interlocked.CompareExchange(ref _state, StateRunning, StateStarting) != StateStarting)
                    return;
                _ready.Set();
                foreach (ShellPopulationCommand command in _commands.GetConsumingEnumerable())
                    Apply(folder, command);
            }
            catch (Exception exception)
            {
                ReportFailureNoThrow("results-worker", exception);
            }
            finally
            {
                try
                {
                    if (_marshaledResultsStream != null)
                    {
                        try { _marshaledResultsStream.ReleaseUnconsumed(); }
                        catch (Exception exception) { ReportFailureNoThrow("results-worker-release-stream", exception); }
                    }
                }
                finally
                {
                    try { CloseQueueAndTickets(); }
                    finally
                    {
                        try
                        {
                            if (folder != null && Marshal.IsComObject(folder)) Marshal.ReleaseComObject(folder);
                        }
                        catch (Exception exception) { ReportFailureNoThrow("results-worker-release-proxy", exception); }
                        finally
                        {
                            try
                            {
                                if (_coInitialize == null && initialized >= 0) ShellResultsNative.CoUninitialize();
                            }
                            finally { _exited.Set(); }
                        }
                    }
                }
            }
        }

        private void CompleteFailedStart()
        {
            try
            {
                if (_marshaledResultsStream != null) _marshaledResultsStream.ReleaseUnconsumed();
            }
            catch (Exception exception)
            {
                ReportFailureNoThrow("results-worker-start-release-stream", exception);
            }
            finally
            {
                try { CloseQueueAndTickets(); }
                finally { _exited.Set(); }
            }
        }

        private void CloseQueueAndTickets()
        {
            Interlocked.Exchange(ref _state, StateDead);
            try { _commands.CompleteAdding(); }
            catch (InvalidOperationException) { }
            ShellPopulationCommand pending;
            while (_commands.TryTake(out pending))
                pending.Ticket.Complete(false, 0, pending.Paths.Length);
        }

        private void ReportFailureNoThrow(string stage, Exception exception)
        {
            try { _failure(stage, exception); }
            catch (Exception callbackFailure)
            {
                try { AddinDiagnostics.WriteFailure(stage + "-callback", callbackFailure); }
                catch { }
            }
        }

        private IResultsFolder UnmarshalResultsFolder()
        {
            Guid iid = typeof(IResultsFolder).GUID;
            IntPtr folderPointer;
            IntPtr stream = _marshaledResultsStream == null
                ? IntPtr.Zero
                : _marshaledResultsStream.Consume();
            if (stream == IntPtr.Zero) throw new InvalidOperationException("The marshal stream was already released.");
            int hr = ShellResultsNative.CoGetInterfaceAndReleaseStream(
                stream, ref iid, out folderPointer);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try { return (IResultsFolder)Marshal.GetObjectForIUnknown(folderPointer); }
            finally { if (folderPointer != IntPtr.Zero) Marshal.Release(folderPointer); }
        }

        private void Apply(IResultsFolder folder, ShellPopulationCommand command)
        {
            int added = 0;
            int skipped = 0;
            bool applied = false;
            try
            {
                if (!IsRunning || command.Generation != _latestGeneration()) return;
                InvokeHook("before-remove-all");
                if (!IsRunning || command.Generation != _latestGeneration()) return;
                if (command.Replace) Marshal.ThrowExceptionForHR(folder.RemoveAll());
                applied = true;
                foreach (string path in command.Paths)
                {
                    if (!IsRunning || command.Generation != _latestGeneration()) { skipped++; continue; }
                    IShellItem item = null;
                    try
                    {
                        if (!ShellPathPolicy.IsAllowedSolidWorksPath(path)) { skipped++; continue; }

                        // Fast path: parse to an absolute PIDL and hand the raw
                        // IDList to the results folder. Falls back to the full
                        // IShellItem bind when parsing or AddIDList is rejected.
                        IntPtr pidl = IntPtr.Zero;
                        IntPtr addedChild = IntPtr.Zero;
                        try
                        {
                            uint parsedAttributes;
                            if (ShellResultsNative.SHParseDisplayName(
                                    path, IntPtr.Zero, out pidl, 0, out parsedAttributes) >= 0
                                && pidl != IntPtr.Zero)
                            {
                                InvokeHook("before-add-item");
                                if (!IsRunning || command.Generation != _latestGeneration()) { skipped++; continue; }
                                if (folder.AddIDList(pidl, out addedChild) >= 0)
                                {
                                    added++;
                                    continue;
                                }
                            }
                        }
                        finally
                        {
                            if (addedChild != IntPtr.Zero) Marshal.FreeCoTaskMem(addedChild);
                            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                        }

                        Guid iid = typeof(IShellItem).GUID;
                        int hr = ShellResultsNative.SHCreateItemFromParsingName(path, null, ref iid, out item);
                        if (hr < 0 || item == null) { skipped++; continue; }
                        InvokeHook("before-add-item");
                        if (!IsRunning || command.Generation != _latestGeneration()) { skipped++; continue; }
                        hr = folder.AddItem(item);
                        if (hr < 0) { skipped++; continue; }
                        added++;
                    }
                    catch (Exception exception)
                    {
                        skipped++;
                        ReportFailureNoThrow("results-add-item", exception);
                    }
                    finally
                    {
                        if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                    }
                }
            }
            catch (Exception exception)
            {
                ReportFailureNoThrow(command.Replace ? "results-replace" : "results-append", exception);
            }
            finally
            {
                command.Ticket.Complete(
                    applied && IsRunning && command.Generation == _latestGeneration(), added, skipped);
            }
        }

        private void InvokeHook(string stage)
        {
            Action<string> hook = _operationHook;
            if (hook != null) hook(stage);
        }
    }

    internal sealed class ShellPopulationCommand
    {
        internal ShellPopulationCommand(long generation, bool replace, string[] paths)
        {
            Generation = generation;
            Replace = replace;
            Paths = paths ?? new string[0];
            Ticket = new ShellPopulationTicket(generation);
        }
        internal long Generation { get; private set; }
        internal bool Replace { get; private set; }
        internal string[] Paths { get; private set; }
        internal ShellPopulationTicket Ticket { get; private set; }
    }

    /// <summary>
    /// One truthful column in the real Shell DetailsView.  The same ordered
    /// contract is projected by the desktop facade for the clickable native
    /// header; query-only fields are intentionally absent because an original
    /// file-system IShellItem cannot expose a per-query matched-value cell.
    /// </summary>
    internal sealed class ShellResultColumnDefinition
    {
        internal ShellResultColumnDefinition(
            string id,
            string label,
            PropertyKey propertyKey,
            int minimumWidth,
            int weight)
        {
            Id = id;
            Label = label;
            PropertyKey = propertyKey;
            MinimumWidth = minimumWidth;
            Weight = weight;
        }

        internal string Id { get; private set; }
        internal string Label { get; private set; }
        internal PropertyKey PropertyKey { get; private set; }
        internal int MinimumWidth { get; private set; }
        internal int Weight { get; private set; }
    }

    internal static class ShellResultColumnContract
    {
        // PKEY_ItemNameDisplay, PKEY_ItemTypeText and
        // PKEY_ItemFolderPathDisplay, in the exact order painted by the
        // companion WinForms header.
        internal static readonly ShellResultColumnDefinition[] Columns =
        {
            new ShellResultColumnDefinition(
                "fileName",
                "文件名",
                new PropertyKey(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10u),
                180,
                42),
            new ShellResultColumnDefinition(
                "kind",
                "文件类型",
                new PropertyKey(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 4u),
                120,
                18),
            new ShellResultColumnDefinition(
                "directory",
                "所在文件夹",
                new PropertyKey(new Guid("E3E0584C-B788-4A5A-BB20-7F5A44C9ACDD"), 6u),
                220,
                40),
        };

        internal static int[] CreateDefaultWidths()
        {
            return new[] { 420, 180, 620 };
        }

        internal static int[] NormalizeWidths(int[] widths)
        {
            if (widths == null || widths.Length != Columns.Length)
                throw new ArgumentException("The Shell result width contract is incomplete.", "widths");

            int[] normalized = new int[widths.Length];
            for (int index = 0; index < widths.Length; index++)
                normalized[index] = Math.Max(Columns[index].MinimumWidth, widths[index]);
            return normalized;
        }
    }

    internal static class ShellPathPolicy
    {
        private const uint SigDnFileSystemPath = 0x80058000u; // SIGDN_FILESYSPATH

        internal static bool IsAllowedSolidWorksPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathRooted(path)) return false;
            string extension = Path.GetExtension(path);
            return String.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadFileSystemPath(IShellItem item)
        {
            if (item == null) return null;
            IntPtr value;
            int hr = item.GetDisplayName(SigDnFileSystemPath, out value);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try { return Marshal.PtrToStringUni(value); }
            finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellRect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FolderSettings { internal int ViewMode; internal uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        internal PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        internal Guid FormatId;
        internal uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ColumnInfo
    {
        internal uint Size;
        internal uint Mask;
        internal uint State;
        internal uint Width;
        internal uint DefaultWidth;
        internal uint IdealWidth;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string Name;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6")]
    internal interface IExplorerBrowser
    {
        [PreserveSig] int Initialize(IntPtr parent, ref ShellRect rect, ref FolderSettings settings);
        [PreserveSig] int Destroy();
        [PreserveSig] int SetRect(IntPtr deferredWindowPosition, ShellRect rect);
        [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string propertyBag);
        [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string emptyText);
        [PreserveSig] int SetFolderSettings(ref FolderSettings settings);
        [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] IExplorerBrowserEvents events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int BrowseToIDList(IntPtr childPidl, uint flags);
        [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object target, uint flags);
        [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object source, uint flags);
        [PreserveSig] int RemoveAll();
        [PreserveSig] int GetCurrentView(ref Guid interfaceId, out IntPtr view);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    internal interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid interfaceId, out IntPtr folder);
        [PreserveSig] int Item(int iItem, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D8EC27BB-3F3B-4042-B10A-4ACFD924D453")]
    internal interface IColumnManager
    {
        [PreserveSig] int SetColumnInfo(ref PropertyKey propertyKey, ref ColumnInfo columnInfo);
        [PreserveSig] int GetColumnInfo(ref PropertyKey propertyKey, ref ColumnInfo columnInfo);
        [PreserveSig] int GetColumnCount(uint flags, out uint count);
        [PreserveSig] int GetColumns(
            uint flags,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] PropertyKey[] columns,
            uint count);
        [PreserveSig] int SetColumns(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] PropertyKey[] columns,
            uint count);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("96E5AE6D-6AE1-4B1C-900C-C6480EAA8828")]
    internal interface IResultsFolder
    {
        [PreserveSig] int AddItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int AddIDList(IntPtr absolutePidl, out IntPtr addedChildPidl);
        [PreserveSig] int RemoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int RemoveIDList(IntPtr absolutePidl);
        [PreserveSig] int RemoveAll();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint displayName, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    internal interface IShellFolder
    {
        void _VtblGap0();
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out ShellStrRet pName);
    }

    [StructLayout(LayoutKind.Explicit, Size = 264)]
    internal struct ShellStrRet
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal IntPtr Pointer;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
    internal interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object site);
        void GetSite(ref Guid interfaceId, out IntPtr site);
    }

    internal static class ShellResultsNative
    {
        internal static readonly Guid ClsidExplorerBrowser = new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");

        // PIDL-only parsing skips full IShellItem binding (property store,
        // column handlers). Paired with IResultsFolder.AddIDList it keeps the
        // per-item cost of large result population to a pure parse + add.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        internal static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            IntPtr bindContext,
            out IntPtr pidl,
            uint sfgaoIn,
            out uint sfgaoOut);

        [DllImport("ole32.dll")]
        internal static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")]
        internal static extern void OleUninitialize();
        [DllImport("ole32.dll")]
        internal static extern int CoInitializeEx(IntPtr reserved, uint apartmentModel);
        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        internal static extern int CoMarshalInterThreadInterfaceInStream(
            ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] object value, out IntPtr stream);
        [DllImport("ole32.dll")]
        internal static extern int CoGetInterfaceAndReleaseStream(
            IntPtr stream, ref Guid interfaceId, out IntPtr value);
        [DllImport("ole32.dll")]
        internal static extern int CoReleaseMarshalData(IntPtr stream);
        [DllImport("ole32.dll")]
        internal static extern int CoWaitForMultipleHandles(
            uint flags,
            uint timeoutMilliseconds,
            int handleCount,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] IntPtr[] handles,
            out int index);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        internal static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            [MarshalAs(UnmanagedType.Interface)] object bindContext,
            ref Guid interfaceId,
            out IShellItem item);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        internal static extern int SHCreateItemWithParent(
            IntPtr pidlParent,
            [MarshalAs(UnmanagedType.Interface)] IShellFolder psfParent,
            IntPtr pidlChild,
            ref Guid riid,
            out IShellItem ppv);
    }

    internal static class FolderViewSelection
    {
        private static readonly Guid IidIShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid IidIShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        // Single cheap COM call for change detection. Callers that only need to
        // know whether focus moved must not pay for the full item resolution in
        // TryGetFocusedPath on every poll tick.
        internal static bool TryGetFocusedIndex(object shellView, out int index)
        {
            index = -1;
            try
            {
                var folderView = shellView as IFolderView;
                if (folderView == null) return false;

                int focused;
                if (folderView.GetFocusedItem(out focused) < 0 || focused < 0) return false;

                index = focused;
                return true;
            }
            catch { return false; }
        }

        internal static bool TryGetFocusedPath(object shellView, out string path)
        {
            path = null;
            try
            {
                var folderView = shellView as IFolderView;
                if (folderView == null) return false;

                int focused;
                if (folderView.GetFocusedItem(out focused) < 0 || focused < 0) return false;

                int itemCount;
                if (folderView.ItemCount(2u /* SVGIO_ALLVIEW */, out itemCount) < 0
                    || focused >= itemCount) return false;

                IntPtr pidl;
                if (folderView.Item(focused, out pidl) < 0 || pidl == IntPtr.Zero) return false;
                try
                {
                    var folderIid = IidIShellFolder;
                    IntPtr folderPtr;
                    if (folderView.GetFolder(ref folderIid, out folderPtr) < 0 || folderPtr == IntPtr.Zero)
                        return false;
                    IShellFolder parentFolder = null;
                    try
                    {
                        parentFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                        var itemIid = IidIShellItem;
                        IShellItem item;
                        if (ShellResultsNative.SHCreateItemWithParent(
                                IntPtr.Zero, parentFolder, pidl, ref itemIid, out item) < 0 || item == null)
                            return false;
                        try
                        {
                            path = ShellPathPolicy.ReadFileSystemPath(item);
                        }
                        finally
                        {
                            if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                        }
                    }
                    finally
                    {
                        if (parentFolder != null && Marshal.IsComObject(parentFolder))
                            Marshal.ReleaseComObject(parentFolder);
                        Marshal.Release(folderPtr);
                    }
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
                return !string.IsNullOrWhiteSpace(path);
            }
            catch { return false; }
        }
    }
}

#pragma warning restore
