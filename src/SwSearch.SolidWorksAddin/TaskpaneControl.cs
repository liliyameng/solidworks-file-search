using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace SwSearch.SolidWorksAddin
{
    /// <summary>
    /// SOLIDWORKS owns this HWND. WebView2 renders only the compact Library
    /// search toolbar. ExplorerBrowser owns the results, Shell verbs and OLE
    /// drag/drop for the original files.
    /// </summary>
    internal sealed class TaskpaneControl : UserControl
    {
        private const int InitialPageSize = 200;
        private const int PopulationPageSize = 500;
        private const int ShutdownOwnerRetrySliceMilliseconds = 100;
        private static readonly TimeSpan LibrarySettingsRefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly int[] SupportedKinds = new[] { 0, 1 };

        private readonly TaskpaneCompositionWebViewHost _webHost;
        private readonly ITaskpaneShellResultsTarget _shellResults;
        private readonly Label _statusLabel;
        private readonly LiveSearchCoordinator _searchCoordinator;
        private readonly LiveSearchScheduler _searchScheduler;
        private readonly TaskpaneSearchRequestFactory _requestFactory = new TaskpaneSearchRequestFactory();
        private readonly ISldWorks _swApplication;
        private readonly bool _librarySettingsInjected;
        private TaskpaneLibrarySettings _librarySettings;
        private DateTime _librarySettingsLoadedAtUtc;
        private readonly Func<LiveSearchRequest, CancellationToken, BrokerResponse> _search;
        private readonly BrokerRevisionTracker _revisionTracker = new BrokerRevisionTracker();
        private readonly System.Windows.Forms.Timer _monitorTimer = new System.Windows.Forms.Timer();
        private readonly object _populationGate = new object();
        private readonly object _shutdownGate = new object();
        private readonly List<Action> _shutdownCallbacks = new List<Action>();
        private readonly ManualResetEvent _shutdownCompletedEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _shutdownAttemptEvent = new ManualResetEvent(false);

        // Isolation-harness hook only. Production leaves it null.
        internal static Func<WaitHandle[], int, int> ShutdownDispatchWaitForTesting;

        private CancellationTokenSource _populationCancellation = new CancellationTokenSource();
        private Task _populationTask = Task.FromResult(0);
        private LiveSearchRequest _lastAppliedRequest;
        private BrokerStateDto _lastState;
        private long _resultSetGeneration;
        private long _queryRevision;
        private long _loadedCount;
        private long? _totalResultCount;
        private bool _hasMoreResults;
        private bool _connectionFailed;
        private bool _explicitAppExitMarked;
        private bool _monitorRunning;
        private bool _disposing;
        private bool _shutdownStarted;
        private bool _webViewShutdownCompleted;
        private bool _shellShutdownCompleted;
        private bool _shutdownCompleted;
        private bool _parentShutdownInProgress;
        private bool _baseDisposeCompleted;
        private bool _shutdownSucceeded;

        // The taskpane is deliberately fixed to the SOLIDWORKS Design Library.
        private string _searchText = String.Empty;
        private int _searchMode = 3;
        private int _scopeMode = TaskpaneSearchScope.AllMachine;
        private string _scopePath = String.Empty;
        private string _status = "正在连接搜索服务…";

        internal TaskpaneControl(ISldWorks swApplication)
            : this(swApplication, null, null)
        {
        }

        internal TaskpaneControl(
            ISldWorks swApplication,
            Func<LiveSearchRequest, CancellationToken, BrokerResponse> search)
            : this(swApplication, search, null)
        {
        }

        internal TaskpaneControl(
            ISldWorks swApplication,
            Func<LiveSearchRequest, CancellationToken, BrokerResponse> search,
            TaskpaneLibrarySettings librarySettings)
            : this(swApplication, search, librarySettings, null)
        {
        }

        internal TaskpaneControl(
            ISldWorks swApplication,
            Func<LiveSearchRequest, CancellationToken, BrokerResponse> search,
            TaskpaneLibrarySettings librarySettings,
            ITaskpaneShellResultsTarget shellResults)
        {
            _swApplication = swApplication;
            _librarySettingsInjected = librarySettings != null;
            _librarySettings = librarySettings ?? TaskpaneLibrarySettings.Load(swApplication);
            _librarySettingsLoadedAtUtc = DateTime.UtcNow;
            _search = search ?? delegate(LiveSearchRequest request, CancellationToken cancellationToken)
            {
                Stopwatch latency = Stopwatch.StartNew();
                try
                {
                    BrokerResponse response = BrokerProtocol.Search(request, cancellationToken);
                    latency.Stop();
                    int resultCount = response == null || response.Hits == null
                        ? 0
                        : response.Hits.Count;
                    int libraryRejectedCount = request == null || request.LibraryRoots == null ||
                                               request.LibraryRoots.Length == 0 || resultCount == 0
                        ? 0
                        : resultCount - TaskpaneLibrarySettings.CountInsideRoots(
                            response.Hits, request.LibraryRoots);
                    AddinDiagnostics.WriteSearchEvent(
                        request,
                        resultCount,
                        libraryRejectedCount,
                        latency.ElapsedMilliseconds,
                        null);
                    return response;
                }
                catch (Exception exception)
                {
                    latency.Stop();
                    AddinDiagnostics.WriteSearchEvent(
                        request, 0, 0, latency.ElapsedMilliseconds, exception);
                    throw;
                }
            };

            _searchCoordinator = new LiveSearchCoordinator(_search);
            _searchCoordinator.Completed += SearchCompleted;
            _searchScheduler = new LiveSearchScheduler(SubmitScheduledSearch, 40);

            _webHost = new TaskpaneCompositionWebViewHost { Dock = DockStyle.Fill };
            _webHost.WebReady += WebHostReady;
            _webHost.CommandReceived += WebCommandReceived;
            _webHost.HostFailure += WebHostFailure;

            _shellResults = shellResults ?? new TaskpaneShellResultsHost();
            _shellResults.View.Dock = DockStyle.Fill;
            _shellResults.HostReady += ShellResultsReady;
            _shellResults.HostFailure += ShellResultsFailure;

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(7, 0, 5, 0),
                BackColor = System.Drawing.SystemColors.Control,
                ForeColor = System.Drawing.SystemColors.ControlText,
                Text = _status
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.Controls.Add(_webHost, 0, 0);
            layout.Controls.Add(_shellResults.View, 0, 1);
            layout.Controls.Add(_statusLabel, 0, 2);

            Dock = DockStyle.Fill;
            BackColor = System.Drawing.SystemColors.Window;
            Controls.Add(layout);

            _monitorTimer.Interval = 3000;
            _monitorTimer.Tick += MonitorTimerTick;
        }

        internal void BeginConnect()
        {
            SafeOnUi(delegate
            {
                _monitorTimer.Start();
                BeginNewResultSet();
                ScheduleSearch(true, false);
            });
        }

        internal void ApplyFolderScopePath(string ignoredPath)
        {
            // Folder scope is intentionally unavailable in the taskpane.
            _searchMode = 3;
            _scopeMode = TaskpaneSearchScope.AllMachine;
            _scopePath = String.Empty;
            PublishState(null);
        }

        internal bool WaitForSearchIdle(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            if (!_searchCoordinator.WaitForIdle(timeout)) return false;
            Task population;
            lock (_populationGate) population = _populationTask;
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return population.IsCompleted;
            try { return population.Wait(remaining); }
            catch (AggregateException aggregate)
            {
                return aggregate.Flatten().InnerExceptions.All(delegate(Exception error)
                {
                    return error is OperationCanceledException;
                });
            }
        }

        internal int ActiveSearchRequestCount { get { return _searchCoordinator.ActiveRequestCount; } }
        internal int CurrentScopeForTesting { get { return _scopeMode; } }
        internal int SearchDebounceMillisecondsForTesting { get { return _searchScheduler.DelayMilliseconds; } }
        internal string SearchTextForTesting { get { return _searchText; } }
        internal int SearchModeForTesting { get { return _searchMode; } }
        internal string ScopePathForTesting { get { return _scopePath; } }
        internal long QueryRevisionForTesting { get { return _queryRevision; } }
        internal long ResultSetGenerationForTesting { get { return _resultSetGeneration; } }
        internal long LoadedCountForTesting { get { return _loadedCount; } }
        internal long? TotalResultCountForTesting { get { return _totalResultCount; } }
        internal string StatusForTesting { get { return _status; } }
        internal ITaskpaneShellResultsTarget ShellResultsForTesting { get { return _shellResults; } }
        internal TaskpaneCompositionWebViewHost WebHostForTesting { get { return _webHost; } }
        internal bool ParentDisposeCompletedForTesting
        {
            get { lock (_shutdownGate) return _baseDisposeCompleted; }
        }
        internal bool ShutdownCompletedForTesting
        {
            get { lock (_shutdownGate) return _shutdownCompleted; }
        }
        internal bool ShutdownSucceededForTesting
        {
            get { lock (_shutdownGate) return _shutdownSucceeded; }
        }
        internal bool WebViewShutdownCompletedForTesting
        {
            get { lock (_shutdownGate) return _webViewShutdownCompleted; }
        }

        internal bool ReceiveWebMessageForTesting(string json, out string error)
        {
            TaskpaneWebCommand command;
            if (!TaskpaneWebBridgePolicy.TryParse(json, out command, out error)) return false;
            WebCommandReceived(this, new TaskpaneWebCommandEventArgs(command));
            return true;
        }

        internal void SetSearchTextForTesting(string text)
        {
            ApplySearchShape(text, false);
        }

        internal void SetSearchModeForTesting(int ignoredMode)
        {
            _searchMode = 3;
            _scopeMode = TaskpaneSearchScope.AllMachine;
            _scopePath = String.Empty;
            PublishState(null);
        }

        internal void ApplySearchResultForTesting(
            int generation,
            LiveSearchRequest request,
            BrokerResponse response)
        {
            ApplySearchCompletion(new LiveSearchCompletion(generation, request, response, null));
        }

        internal void ApplyMonitorStateForTesting(BrokerStateDto state) { UpdateState(state, false); }
        internal void ApplyRecoveredMonitorStateForTesting(BrokerStateDto state) { UpdateState(state, true); }
        internal void ApplyMonitorFailureForTesting(Exception error) { ApplyMonitorFailure(error); }

        private void WebHostReady(object sender, EventArgs args)
        {
            PublishState(null);
            BeginNewResultSet();
            ScheduleSearch(true, false);
        }

        private void ShellResultsReady(object sender, EventArgs args)
        {
            // The first Broker result may have arrived while ExplorerBrowser was
            // initializing. A new generation safely republishes it from source.
            BeginNewResultSet();
            ScheduleSearch(true, false);
        }

        private void WebHostFailure(object sender, TaskpaneWebHostFailureEventArgs args)
        {
            RecordFailure(args.Stage, args.Exception);
        }

        private void ShellResultsFailure(object sender, ShellResultsFailureEventArgs args)
        {
            _status = "文件结果视图暂不可用";
            _statusLabel.Text = _status;
            RecordFailure(args.Stage, args.Exception);
            PublishState(null);
        }

        private void WebCommandReceived(object sender, TaskpaneWebCommandEventArgs args)
        {
            try
            {
                TaskpaneWebCommand command = args.Command;
                if (command == null) return;
                if (command.Type == "ready")
                {
                    PublishState(command.RequestId);
                    return;
                }
                if (command.Type == "search")
                {
                    ApplySearchShape(command.Text, command.Immediate);
                    return;
                }
                if (command.Type == "retry")
                {
                    BrokerProtocol.RetryConnection();
                    _status = "正在重新连接后台更新…";
                    PublishState(command.RequestId);
                    BeginNewResultSet();
                    ScheduleSearch(true, true);
                }
            }
            catch (Exception exception)
            {
                RecordFailure("taskpane-bridge", exception);
                _webHost.PostError(args.Command == null ? null : args.Command.RequestId,
                    "操作未能完成，请重试。");
            }
        }

        private void ApplySearchShape(string text, bool immediate)
        {
            string normalized = text ?? String.Empty;
            bool changed = !String.Equals(_searchText, normalized, StringComparison.Ordinal);
            _searchText = normalized;
            _searchMode = 3;
            _scopeMode = TaskpaneSearchScope.AllMachine;
            _scopePath = String.Empty;
            if (changed) BeginNewResultSet();
            ScheduleSearch(immediate, true);
        }

        private void ScheduleSearch(bool immediate, bool isInteractive)
        {
            _searchCoordinator.CancelCurrent();
            if (immediate) _searchScheduler.SubmitImmediately(isInteractive);
            else _searchScheduler.InputChanged(isInteractive);
        }

        private void SubmitScheduledSearch(bool isInteractive)
        {
            SafeOnUi(delegate { StartSearch(isInteractive); });
        }

        private void StartSearch(bool isInteractive)
        {
            if (_disposing || IsDisposed || Disposing) return;
            _searchMode = 3;
            _scopeMode = TaskpaneSearchScope.AllMachine;
            _scopePath = String.Empty;

            // A SOLIDWORKS session can change Design Library locations after
            // the taskpane was created. Refresh once for this new logical
            // result set, then freeze the returned roots in LiveSearchRequest
            // so all subsequent pages retain one consistent boundary.
            DateTime nowUtc = DateTime.UtcNow;
            if (!_librarySettingsInjected &&
                nowUtc - _librarySettingsLoadedAtUtc >= LibrarySettingsRefreshInterval)
            {
                _librarySettings = TaskpaneLibrarySettings.Load(_swApplication);
                _librarySettingsLoadedAtUtc = nowUtc;
            }

            LiveSearchRequest request;
            string error;
            if (!_requestFactory.TryCreate(
                (_searchText ?? String.Empty).Trim(),
                3,
                CurrentKinds(),
                TaskpaneSearchScope.AllMachine,
                String.Empty,
                true,
                ActiveLibraryRoots(),
                out request,
                out error))
            {
                BeginNewResultSet();
                _status = String.IsNullOrWhiteSpace(error) ? "未配置 SOLIDWORKS 设计库路径" : error;
                PublishState(null);
                return;
            }

            _status = "文件库 · 正在搜索…";
            PublishState(null);
            _searchCoordinator.Submit(request.WithPage(0, InitialPageSize, isInteractive));
        }

        private void SearchCompleted(LiveSearchCompletion completion)
        {
            SafeOnUi(delegate { ApplySearchCompletion(completion); });
        }

        private void ApplySearchCompletion(LiveSearchCompletion completion)
        {
            if (_disposing || !_searchCoordinator.IsCurrent(completion.Generation)) return;
            if (completion.Error != null)
            {
                if (completion.Error is BrokerAppExitMarkedException)
                {
                    PublishExplicitAppExitState(null);
                    return;
                }
                _explicitAppExitMarked = false;
                _connectionFailed = true;
                _status = completion.Error is BrokerProtocolMismatchException
                    ? "搜索服务版本不匹配：" + completion.Error.Message
                    : "后台更新暂不可用";
                PublishState(null);
                return;
            }
            BrokerResponse response = completion.Response;
            if (response == null) return;

            string[] roots = completion.Request.LibraryRoots ?? new string[0];
            List<SearchHitDto> hits;
            if (!TryAcceptLibraryPage(response.Hits, roots, out hits))
            {
                BeginNewResultSet();
                _status = roots.Length == 0
                    ? "未配置 SOLIDWORKS 设计库路径"
                    : "搜索服务返回了设计库外结果，已拒绝显示";
                PublishState(null);
                return;
            }

            CancellationTokenSource cancellation = RotatePopulation(false);
            long generation = _resultSetGeneration;
            LiveSearchRequest accepted = completion.Request.WithPage(0, InitialPageSize)
                .WithPagingSnapshot(response.IndexRevision, response.PageFingerprint);
            _lastAppliedRequest = accepted;
            _explicitAppExitMarked = false;
            _connectionFailed = false;
            _loadedCount = hits.Count;
            _totalResultCount = response.TotalCount.HasValue
                ? Math.Max(0, response.TotalCount.Value)
                : hits.Count;
            _hasMoreResults = response.HasMore == true || _loadedCount < _totalResultCount.Value;
            UpdateState(response.State, false);

            ShellPopulationTicket first = _shellResults.Replace(
                generation,
                hits.Select(delegate(SearchHitDto hit) { return hit.FullPath; }));
            _status = ResultCountStatus();
            PublishState(null);

            Task population = Task.Factory.StartNew(
                delegate { PopulateRemaining(generation, accepted, response, first, cancellation); },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            lock (_populationGate) _populationTask = population;
        }

        private void PopulateRemaining(
            long generation,
            LiveSearchRequest accepted,
            BrokerResponse firstResponse,
            ShellPopulationTicket firstTicket,
            CancellationTokenSource cancellation)
        {
            try
            {
                using (firstTicket) firstTicket.Wait(cancellation.Token);
                int offset = firstResponse.Hits == null ? 0 : firstResponse.Hits.Count;
                bool hasMore = firstResponse.HasMore == true ||
                    (firstResponse.TotalCount.HasValue && offset < firstResponse.TotalCount.Value);
                while (hasMore)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (generation != Interlocked.Read(ref _resultSetGeneration)) return;

                    LiveSearchRequest pageRequest = accepted.WithPage(offset, PopulationPageSize, false);
                    BrokerResponse page = _search(pageRequest, cancellation.Token);
                    if (page == null || page.PageReset ||
                        page.IndexRevision != accepted.ExpectedRevision ||
                        !String.Equals(page.PageFingerprint, accepted.PageFingerprint, StringComparison.Ordinal))
                    {
                        RequestResultSetRefresh(generation);
                        return;
                    }
                    List<SearchHitDto> hits;
                    if (!TryAcceptLibraryPage(page.Hits, accepted.LibraryRoots, out hits))
                    {
                        ReportPopulationFailure(generation,
                            new InvalidDataException("Broker returned a result outside the Design Library roots."));
                        return;
                    }
                    if (hits.Count == 0)
                    {
                        hasMore = false;
                        break;
                    }

                    using (ShellPopulationTicket ticket = _shellResults.Append(
                        generation,
                        hits.Select(delegate(SearchHitDto hit) { return hit.FullPath; })))
                    {
                        ticket.Wait(cancellation.Token);
                        if (!ticket.Applied) return;
                    }
                    offset += hits.Count;
                    hasMore = page.HasMore == true ||
                        (page.TotalCount.HasValue && offset < page.TotalCount.Value);
                    ReportPopulationProgress(generation, offset, page.TotalCount, hasMore);
                }
                ReportPopulationProgress(generation, offset, firstResponse.TotalCount, false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ReportPopulationFailure(generation, exception);
            }
        }

        private void ReportPopulationProgress(long generation, long loaded, long? total, bool hasMore)
        {
            SafeOnUi(delegate
            {
                if (generation != _resultSetGeneration) return;
                _loadedCount = loaded;
                if (total.HasValue) _totalResultCount = Math.Max(0, total.Value);
                _hasMoreResults = hasMore;
                _status = ResultCountStatus();
                PublishState(null);
            });
        }

        private void ReportPopulationFailure(long generation, Exception exception)
        {
            RecordFailure("taskpane-shell-population", exception);
            if (exception is BrokerAppExitMarkedException)
            {
                SafeOnUi(delegate { PublishExplicitAppExitState(null); });
                return;
            }
            SafeOnUi(delegate
            {
                if (generation != _resultSetGeneration) return;
                _explicitAppExitMarked = false;
                _status = "后台更新暂不可用";
                PublishState(null);
            });
        }

        private void RequestResultSetRefresh(long generation)
        {
            SafeOnUi(delegate
            {
                if (generation != _resultSetGeneration) return;
                BeginNewResultSet();
                ScheduleSearch(false, false);
            });
        }

        private bool TryAcceptLibraryPage(
            IEnumerable<SearchHitDto> source,
            string[] roots,
            out List<SearchHitDto> accepted)
        {
            accepted = new List<SearchHitDto>();
            if (roots == null || roots.Length == 0) return false;
            List<SearchHitDto> input = source == null ? new List<SearchHitDto>() : source.ToList();
            accepted = TaskpaneLibrarySettings.FilterToRoots(input, roots);
            return accepted.Count == input.Count;
        }

        private void BeginNewResultSet()
        {
            unchecked { ++_queryRevision; }
            RotatePopulation(true);
            _lastAppliedRequest = null;
            _loadedCount = 0;
            _totalResultCount = null;
            _hasMoreResults = false;
            DisposeTicketWhenComplete(_shellResults.Replace(_resultSetGeneration, new string[0]));
        }

        private CancellationTokenSource RotatePopulation(bool advanceGeneration)
        {
            CancellationTokenSource previous;
            CancellationTokenSource current = new CancellationTokenSource();
            lock (_populationGate)
            {
                previous = _populationCancellation;
                _populationCancellation = current;
                if (advanceGeneration) unchecked { ++_resultSetGeneration; }
                _shellResults.CancelBefore(_resultSetGeneration);
            }
            previous.Cancel();
            previous.Dispose();
            return current;
        }

        private static void DisposeTicketWhenComplete(ShellPopulationTicket ticket)
        {
            if (ticket == null) return;
            Task.Factory.StartNew(delegate
            {
                try { ticket.Wait(CancellationToken.None); }
                finally { ticket.Dispose(); }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        private async void MonitorTimerTick(object sender, EventArgs args)
        {
            if (_monitorRunning) return;
            _monitorRunning = true;
            try
            {
                BrokerResponse response = await Task.Run(new Func<BrokerResponse>(BrokerProtocol.GetState));
                if (_disposing || IsDisposed || Disposing) return;
                UpdateState(response == null ? null : response.State, true);
            }
            catch (Exception exception)
            {
                if (_disposing || IsDisposed || Disposing) return;
                ApplyMonitorFailure(exception);
            }
            finally
            {
                _monitorRunning = false;
            }
        }

        private void ApplyMonitorFailure(Exception exception)
        {
            if (exception is BrokerAppExitMarkedException)
            {
                PublishExplicitAppExitState(null);
                return;
            }
            _explicitAppExitMarked = false;
            _connectionFailed = true;
            _status = exception is BrokerProtocolMismatchException
                ? "搜索服务版本不匹配：" + exception.Message
                : "后台更新暂不可用";
            PublishState(null);
        }

        private void UpdateState(BrokerStateDto state, bool requeryOnRevisionChange)
        {
            if (state == null) return;
            _lastState = state;
            bool recovered = _connectionFailed;
            _explicitAppExitMarked = false;
            _connectionFailed = false;
            bool changed = _revisionTracker.Observe(state.Epoch, state.Revision, state.Phase);
            // State-only monitor ticks never touch ExplorerBrowser or its result
            // folder. A committed revision schedules one new query generation.
            PublishState(null);
            if (requeryOnRevisionChange && (recovered || changed))
            {
                BeginNewResultSet();
                ScheduleSearch(false, false);
            }
        }

        private void PublishExplicitAppExitState(string requestId)
        {
            bool changed = !_explicitAppExitMarked;
            _explicitAppExitMarked = true;
            _connectionFailed = true;
            _status = "桌面搜索已从托盘退出。请启动 SOLIDWORKS 文件检索后再试。";
            if (changed || !String.IsNullOrEmpty(requestId))
            {
                PublishState(requestId);
            }
        }

        private string ResultCountStatus()
        {
            if (ActiveLibraryRoots().Length == 0) return "文件库 · 未配置 SOLIDWORKS 设计库路径";
            long count = _totalResultCount.HasValue ? _totalResultCount.Value : _loadedCount;
            return "文件库 · 共 " + count.ToString(CultureInfo.InvariantCulture) + " 个文件";
        }

        private void PublishState(string requestId)
        {
            _statusLabel.Text = _status ?? String.Empty;
            try
            {
                _webHost.PostMessage("state", requestId, new
                {
                    query = _searchText,
                    mode = "library",
                    scope = new
                    {
                        folder = false,
                        kind = "library",
                        rootPath = String.Empty,
                        roots = ActiveLibraryRoots(),
                        summary = "全部设计库"
                    },
                    busy = _searchCoordinator.ActiveRequestCount != 0,
                    canRetry = _connectionFailed && !_explicitAppExitMarked,
                    resultSummary = _totalResultCount.HasValue
                        ? _totalResultCount.Value.ToString(CultureInfo.InvariantCulture) + " 个结果"
                        : "",
                    rootPath = String.Empty,
                    status = _status,
                    hasMore = _hasMoreResults,
                    totalResultCount = _totalResultCount.HasValue ? _totalResultCount.Value : _loadedCount,
                    loading = _searchCoordinator.ActiveRequestCount != 0,
                    revision = _lastState == null ? 0 : _lastState.Revision,
                    queryRevision = _queryRevision
                });
            }
            catch (Exception exception)
            {
                RecordFailure("taskpane-post-state", exception);
            }
        }

        private int[] CurrentKinds()
        {
            return (int[])SupportedKinds.Clone();
        }

        private string[] ActiveLibraryRoots() { return _librarySettings.Roots; }

        private void SafeOnUi(Action action)
        {
            if (action == null || _disposing || IsDisposed || Disposing) return;
            if (!InvokeRequired)
            {
                action();
                return;
            }
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private static void RecordFailure(string stage, Exception exception)
        {
            AddinDiagnostics.WriteFailure(stage, exception);
        }

        internal void BeginShutdown(Action completed)
        {
            bool start = false;
            bool invokeNow = false;
            lock (_shutdownGate)
            {
                if (_shutdownCompleted) invokeNow = completed != null;
                else
                {
                    if (completed != null) _shutdownCallbacks.Add(completed);
                    if (!_shutdownStarted)
                    {
                        _shutdownStarted = true;
                        start = true;
                    }
                }
            }
            if (invokeNow)
            {
                InvokeShutdownCallback(completed);
                return;
            }
            if (!start) return;

            _disposing = true;
            try
            {
                _monitorTimer.Stop();
                _monitorTimer.Tick -= MonitorTimerTick;
                _webHost.WebReady -= WebHostReady;
                _webHost.CommandReceived -= WebCommandReceived;
                _webHost.HostFailure -= WebHostFailure;
                _shellResults.HostReady -= ShellResultsReady;
                _shellResults.HostFailure -= ShellResultsFailure;
                _searchCoordinator.Completed -= SearchCompleted;
                _searchScheduler.Dispose();
                _searchCoordinator.CancelCurrent();
                RotatePopulation(true);
            }
            catch (Exception exception)
            {
                RecordFailure("taskpane-shutdown-managed", exception);
            }

            // The parent HWND remains alive until the Shell worker has released
            // its cross-apartment IResultsFolder proxy and the host has completed
            // Unadvise -> Unsite -> Destroy -> COM release on this UI STA. The
            // same parent also remains alive while a WebView2 controller create
            // is in flight; its local lease must close before parent completion.
            try { _webHost.BeginShutdown(WebViewShutdownCompleted); }
            catch (Exception exception)
            {
                RecordFailure("taskpane-webview-shutdown-start", exception);
                _shutdownAttemptEvent.Set();
            }
            try { _shellResults.BeginShutdown(ShellShutdownAttemptCompleted); }
            catch (Exception exception)
            {
                RecordFailure("taskpane-shell-shutdown-start", exception);
                _shutdownAttemptEvent.Set();
            }
        }

        internal bool ShutdownAndWaitOnUi(int timeoutMilliseconds)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            int boundedTimeout = Math.Max(0, timeoutMilliseconds);
            BeginShutdown(null);
            while (true)
            {
                if (_shutdownCompletedEvent.WaitOne(0)) return ShutdownSucceededForTesting;
                if (deadline.ElapsedMilliseconds >= boundedTimeout) return false;

                // Consume a prior explicit failure before making this bounded
                // owner-STA retry. The deferred worker-exit post is only a wakeup:
                // correctness does not depend on that WinForms message arriving.
                _shutdownAttemptEvent.Reset();
                try { _webHost.BeginShutdown(WebViewShutdownCompleted); }
                catch (Exception exception)
                {
                    RecordFailure("taskpane-webview-shutdown-retry", exception);
                    return false;
                }
                try { _shellResults.BeginShutdown(ShellShutdownAttemptCompleted); }
                catch (Exception exception)
                {
                    RecordFailure("taskpane-shell-shutdown-retry", exception);
                    return false;
                }

                if (_shutdownCompletedEvent.WaitOne(0)) return ShutdownSucceededForTesting;
                if (_shutdownAttemptEvent.WaitOne(0))
                {
                    _shutdownAttemptEvent.Reset();
                    return false;
                }

                int remaining = boundedTimeout - (int)Math.Min(
                    Int32.MaxValue, deadline.ElapsedMilliseconds);
                if (remaining <= 0) return false;
                int dispatchSlice = Math.Min(remaining, ShutdownOwnerRetrySliceMilliseconds);
                int result;
                try
                {
                    WaitHandle[] shutdownWaits =
                        new WaitHandle[] { _shutdownCompletedEvent, _shutdownAttemptEvent };
                    Func<WaitHandle[], int, int> testWait = ShutdownDispatchWaitForTesting;
                    result = testWait == null
                        ? TaskpaneShellResultsHost.WaitWithComDispatch(shutdownWaits, dispatchSlice)
                        : testWait(shutdownWaits, dispatchSlice);
                }
                catch (Exception exception)
                {
                    RecordFailure("taskpane-shutdown-dispatch-wait", exception);
                    return false;
                }
                // Success publishes both events. CoWait may therefore report
                // the attempt index even though the completed terminal is
                // already visible; the monotonic terminal always wins.
                if (_shutdownCompletedEvent.WaitOne(0)) return ShutdownSucceededForTesting;
                if (result == 0) return false;
                if (result == 1)
                {
                    _shutdownAttemptEvent.Reset();
                    return false;
                }
            }
        }

        private void ShellShutdownAttemptCompleted(ShellShutdownResult result)
        {
            if (result == null || !result.Succeeded)
            {
                RecordFailure(
                    result == null || String.IsNullOrEmpty(result.Stage)
                        ? "taskpane-shell-shutdown"
                        : result.Stage,
                    result == null ? new InvalidOperationException("Shell shutdown returned no result.") : result.Exception);
                _shutdownAttemptEvent.Set();
                return;
            }
            lock (_shutdownGate) _shellShutdownCompleted = true;
            TryCompleteParentShutdownOnUi();
        }

        private void WebViewShutdownCompleted()
        {
            lock (_shutdownGate) _webViewShutdownCompleted = true;
            TryCompleteParentShutdownOnUi();
        }

        private void TryCompleteParentShutdownOnUi()
        {
            lock (_shutdownGate)
            {
                if (_shutdownCompleted || !_shellShutdownCompleted ||
                    !_webViewShutdownCompleted || _parentShutdownInProgress)
                    return;
                _parentShutdownInProgress = true;
            }
            CompleteParentShutdownOnUi();
        }

        private void CompleteParentShutdownOnUi()
        {
            bool baseDisposed = false;
            try
            {
                try { _searchCoordinator.Dispose(); }
                catch (Exception exception) { RecordFailure("taskpane-shutdown-search", exception); }
                try { _monitorTimer.Dispose(); }
                catch (Exception exception) { RecordFailure("taskpane-shutdown-timer", exception); }

                lock (_shutdownGate)
                {
                    if (_shutdownCompleted) return;
                }

                // Calling the base only after the Shell host completion handshake is
                // what keeps the SOLIDWORKS-owned parent HWND valid during teardown.
                base.Dispose(true);
                baseDisposed = true;
            }
            catch (Exception exception)
            {
                RecordFailure("taskpane-shutdown-finalize", exception);
            }
            finally
            {
                // A base Control teardown failure is not a successful terminal:
                // leave the completion event unsignaled so Disconnect returns
                // false at its bounded deadline and a later owner-STA retry can
                // safely attempt the parent release again.
                if (baseDisposed) SignalShutdownTerminal(true);
                else
                {
                    lock (_shutdownGate) _parentShutdownInProgress = false;
                    _shutdownAttemptEvent.Set();
                }
            }
        }

        private void SignalShutdownTerminal(bool succeeded)
        {
            Action[] callbacks;
            lock (_shutdownGate)
            {
                if (_shutdownCompleted) return;
                _baseDisposeCompleted = succeeded;
                _shutdownSucceeded = succeeded;
                _shutdownCompleted = true;
                callbacks = _shutdownCallbacks.ToArray();
                _shutdownCallbacks.Clear();
            }
            _shutdownCompletedEvent.Set();
            _shutdownAttemptEvent.Set();
            foreach (Action callback in callbacks) InvokeShutdownCallback(callback);
        }

        private static void InvokeShutdownCallback(Action callback)
        {
            try { callback(); }
            catch (Exception exception) { RecordFailure("taskpane-shutdown-callback", exception); }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(false);
                return;
            }
            BeginShutdown(null);
        }
    }
}
