using System.ComponentModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SwSearch.WebUi;
using SwSearch.WebUi.Native;
using SwSearch.WebUi.Security;
using Forms = System.Windows.Forms;

namespace SwSearch.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _nativeColumnApplyTimer;
    private readonly DispatcherTimer _nativeColumnPersistenceTimer;
    private readonly SettingsPersistenceCoordinator _settingsSaver;
    private CompositionWebViewHost? _webView;
    private readonly DispatcherTimer _webViewIdleTeardownTimer;
    private readonly DesktopWebBridge _bridge;
    private NativeShellResultsHost _nativeShellResults;
    private readonly Forms.Panel _nativeResultsPanel;
    private readonly Forms.Panel _nativeResultsViewport;
    private readonly NativeStatusPanel _nativeStatusPanel;
    private readonly Forms.Label _nativeStatusText;
    private readonly Forms.Label _nativeProgressText;
    private readonly Forms.Label _nativeResultCountText;
    private readonly Forms.Button _nativeShellStatusRetryButton;
    private readonly NativeShellResultsColumnHeader _nativeColumnHeader;
    private readonly Forms.Panel _nativeShellFailurePanel;
    private readonly Forms.Label _nativeShellFailureMessage;
    private readonly Forms.Panel _matchInfoPanel;
    private readonly Forms.Label _matchPropertyLabel;
    private readonly Forms.Label _matchValueLabel;
    private readonly DispatcherTimer? _selectionPollTimer;
    private readonly DispatcherTimer _nativeShellDebounceTimer;
    private readonly Dictionary<string, (string? MatchedProperty, string? MatchedValue)> _pathMatchMap
        = new(StringComparer.OrdinalIgnoreCase);
    private int _pathMatchMapVersion;
    private int _selectionLastFocusedIndex = -1;
    private int _selectionLastMapVersion = -1;
    private (string? MatchedProperty, string? MatchedValue)? _defaultMatchCache;
    private int _defaultMatchVersion = -1;
    private string? _lastFocusedPath;
    private string? _lastMatchPropertyText;
    private string? _lastMatchValueText;
    private string? _lastMatchProperty;
    private string? _lastMatchValue;
    private CancellationTokenSource? _nativeShellPagingCancellation;
    private readonly CancellationTokenSource _nativeShellLifetime = new();
    private long _nativeShellGeneration;
    private int _nativeShellRefreshQueued;
    // The foreground view raises ordinary collection events for each item in
    // a ReplaceAll.  Explorer must never turn those individual UI changes into
    // a new all-pages Broker crawl: a snapshot is requested only after the
    // final Reset that marks one complete result page as committed.
    private string? _nativeShellRequestedSnapshotKey;
    private int _nativeShellPageResetCount;
    private int _nativeShellPageResetRetryScheduled;
    private int _nativeShellUnavailableRetryScheduled;
    private string? _nativeShellAvailabilityNotice;
    private Task<NativeShellShutdownResult>? _nativeShellShutdownTask;
    private bool _nativeShellShutdownStarted;
    private Window? _preferencesWindow;
    private CompositionWebViewHost? _preferencesWebView;
    private bool _preferencesWindowClosing;
    private bool _statusRefreshRunning;
    private bool _nativeResultsHostAttached;
    private bool _nativeShellRetryInProgress;
    private int _loadedInitializationStarted;
    private bool _closed;
    private bool _closeFlushStarted;
    private bool _closeFlushCompleted;
    private int[]? _lastAppliedNativeColumnWidths;
    private readonly TrayLifetimeState _trayLifetime = new();

    internal event Action? HiddenToTray;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        _settingsSaver = viewModel.SettingsPersistence;
        _bridge = new DesktopWebBridge(viewModel, UserPreferencesRepository.CreateFailSoft(
            () => new UserPreferencesRepository(languageResolver: () => viewModel.UiLanguageEffective)));
        _webViewIdleTeardownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(5),
        };
        _webViewIdleTeardownTimer.Tick += (_, _) =>
        {
            _webViewIdleTeardownTimer.Stop();
            TearDownToolbarWebViewForLongHide();
        };
        EnsureToolbarWebView(initialize: false);
        _nativeShellResults = CreateNativeShellResultsHost();
        _nativeResultsPanel = CreateNativeResultsPanel(
            out _nativeColumnHeader,
            out _nativeResultsViewport,
            out _nativeShellFailurePanel,
            out _nativeShellFailureMessage,
            out _matchInfoPanel,
            out _matchPropertyLabel,
            out _matchValueLabel);
        ViewModel.UiLanguageChanged += ViewModelOnUiLanguageChanged;
        ApplyUiLanguage();
        _nativeColumnApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _nativeColumnApplyTimer.Tick += (_, _) =>
        {
            _nativeColumnApplyTimer.Stop();
            ConfigureNativeShellColumns();
        };
        _nativeColumnPersistenceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _nativeColumnPersistenceTimer.Tick += (_, _) =>
        {
            _nativeColumnPersistenceTimer.Stop();
            ViewModel.SaveNativeShellColumnRatios(_nativeColumnHeader.SnapshotRatios());
        };
        _nativeColumnHeader.ColumnWidthsChanged += (_, _) => ScheduleNativeShellColumnConfiguration();
        _nativeColumnHeader.ColumnResizeCompleted += (_, _) =>
        {
            ScheduleNativeShellColumnConfiguration();
            _nativeColumnPersistenceTimer.Stop();
            _nativeColumnPersistenceTimer.Start();
        };
        ConfigureNativeShellColumns();
        _nativeStatusPanel = new NativeStatusPanel();
        _nativeStatusText = _nativeStatusPanel.StatusLabel;
        // Trailing debounce for native Shell repopulation. Continuous typing
        // commits a new result page every few keystrokes; restarting this quiet
        // window keeps the Explorer view from being cleared and refilled once
        // per intermediate query.
        _nativeShellDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _nativeShellDebounceTimer.Tick += NativeShellDebounceTimerOnTick;
        _nativeProgressText = _nativeStatusPanel.ProgressLabel;
        _nativeResultCountText = _nativeStatusPanel.ResultCountLabel;
        _nativeShellStatusRetryButton = _nativeStatusPanel.RetryButton;
        _nativeShellStatusRetryButton.Click += (_, _) => RetryNativeResultsHost();
        NativeStatusContainer.Child = _nativeStatusPanel;
        _bridge.StateReady += BridgeOnStateReady;
        _bridge.PreferencesStateReady += BridgeOnPreferencesStateReady;
        _bridge.FileDragArmed += BridgeOnFileDragArmed;
        ViewModel.Results.CollectionChanged += ViewModelOnResultsCollectionChanged;
        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _statusTimer.Tick += async (_, _) => await RefreshRuntimeStatusAsync();
        _selectionPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _selectionPollTimer.Tick += SelectionPollTimerOnTick;
        _selectionPollTimer.Start();
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        Loaded += MainWindow_OnLoaded;
        StateChanged += MainWindow_OnStateChanged;
    }

    private NativeShellResultsHost CreateNativeShellResultsHost()
    {
        var host = new NativeShellResultsHost();
        host.Ready += NativeShellResultsOnReady;
        host.Failure += NativeShellResultsOnFailure;
        return host;
    }

    internal MainViewModel ViewModel => (MainViewModel)DataContext;

    internal bool CanApplyBackgroundSettings => !_closed;

    /// <summary>
    /// Creates the desktop result area from Windows Shell controls only.  The
    /// WebView above this panel is deliberately a small command toolbar; it
    /// never paints a duplicate HTML result table over the ExplorerBrowser.
    /// </summary>
    private Forms.Panel CreateNativeResultsPanel(
        out NativeShellResultsColumnHeader columnHeader,
        out Forms.Panel resultsViewport,
        out Forms.Panel failurePanel,
        out Forms.Label failureMessage,
       out Forms.Panel matchInfoPanel,
        out Forms.Label matchPropertyLabel,
        out Forms.Label matchValueLabel)
   {
        var panel = new Forms.Panel
        {
            Dock = Forms.DockStyle.Fill,
            BackColor = System.Drawing.SystemColors.Window,
            Margin = Forms.Padding.Empty,
            Padding = Forms.Padding.Empty,
        };

        // The real ExplorerBrowser keeps its Shell-owned header hidden so it
        // cannot locally reorder only the loaded page. This clickable native
        // header and the Shell rows below both consume the same three standard
        // property columns and the same pixel widths.
        columnHeader = new NativeShellResultsColumnHeader();
        columnHeader.ColumnInvoked += (_, eventArgs) => ApplyNativeSortCycle(eventArgs.ColumnId);

        resultsViewport = new Forms.Panel
        {
            Dock = Forms.DockStyle.Fill,
            BackColor = System.Drawing.SystemColors.Window,
        };
        var view = _nativeShellResults.View;
        view.Dock = Forms.DockStyle.Fill;
        resultsViewport.Controls.Add(view);

        matchInfoPanel = new Forms.Panel
        {
            Dock = Forms.DockStyle.Top,
            Height = 32,
            BackColor = System.Drawing.Color.FromArgb(240, 245, 248),
        };
        matchPropertyLabel = new Forms.Label
        {
            AutoSize = true,
            Location = new System.Drawing.Point(10, 7),
            Text = "命中属性：文件名",
            ForeColor = System.Drawing.SystemColors.ControlText,
        };
        matchValueLabel = new Forms.Label
        {
            AutoSize = true,
            Location = new System.Drawing.Point(10, 7),
            Text = "命中值：—",
            ForeColor = System.Drawing.SystemColors.ControlText,
        };
        matchInfoPanel.Controls.Add(matchPropertyLabel);
        matchInfoPanel.Controls.Add(matchValueLabel);

        // A failed ExplorerBrowser initialization must not leave the main
        // window as a silent blank rectangle.  Keep the failure presentation
        // entirely native, above the Shell child HWND, and let users retry the
        // connection without pretending an HTML result table still exists.
        failurePanel = new Forms.Panel
        {
            Dock = Forms.DockStyle.Fill,
            BackColor = System.Drawing.SystemColors.Window,
            Visible = true,
        };
        failureMessage = new Forms.Label
        {
            AutoSize = false,
            Dock = Forms.DockStyle.Top,
            Height = 56,
            Padding = new Forms.Padding(18, 14, 18, 4),
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            ForeColor = System.Drawing.SystemColors.ControlText,
            Text = "正在初始化原生文件视图…",
        };
        var retry = new Forms.Button
        {
            AutoSize = true,
            Location = new System.Drawing.Point(18, 64),
            Text = "重试",
            AccessibleName = "重试原生文件视图",
        };
        retry.Click += (_, _) => RetryNativeResultsHost();
        failurePanel.Controls.Add(retry);
        failurePanel.Controls.Add(failureMessage);
        resultsViewport.Controls.Add(failurePanel);
        panel.Controls.Add(resultsViewport);
        panel.Controls.Add(matchInfoPanel);
        panel.Controls.Add(columnHeader);
        return panel;
    }

    private void ApplyNativeSortCycle(string column)
    {
        var direction = NativeShellSortCycle.NextDirection(
            ViewModel.CurrentSortColumn,
            ViewModel.CurrentSortDescending,
            column);
        _bridge.ApplyNativeSort(column, direction);
        UpdateNativeSortHeader(column, direction);
    }

    private void UpdateNativeSortHeader(string? appliedColumn = null, string? appliedDirection = null)
    {
        var column = appliedColumn ?? ViewModel.CurrentSortColumn;
        var direction = appliedColumn is null
            ? (ViewModel.CurrentSortColumn is null ? null : ViewModel.CurrentSortDescending ? "desc" : "asc")
            : appliedDirection;
        _nativeColumnHeader.SetSortState(column, direction);
    }

    private void ScheduleNativeShellColumnConfiguration()
    {
        if (_closed)
        {
            return;
        }

        _nativeColumnApplyTimer.Stop();
        _nativeColumnApplyTimer.Start();
    }

    private void ConfigureNativeShellColumns(bool force = false)
    {
        if (_closed)
        {
            return;
        }

        var widths = _nativeColumnHeader.SnapshotWidths();
        if (!force && _lastAppliedNativeColumnWidths?.SequenceEqual(widths) == true)
        {
            return;
        }

        _lastAppliedNativeColumnWidths = widths;
        _nativeShellResults.ConfigureColumns(widths);
    }

    /// <summary>
    /// ExplorerBrowser creates and pumps native COM windows from OnHandleCreated.
    /// Parenting it while WPF is still raising Loaded can re-enter a suspended
    /// dispatcher layout pass and leave every HWND host at its startup bounds.
    /// Attach only after Loaded has yielded to an idle layout turn.
    /// </summary>
    private void AttachNativeResultsHostAfterLayout()
    {
        if (_closed || _nativeResultsHostAttached)
        {
            return;
        }

        if (NativeResultsContainer.Child is null)
        {
            NativeResultsContainer.Child = _nativeResultsPanel;
        }
        else if (!ReferenceEquals(NativeResultsContainer.Child, _nativeResultsPanel))
        {
            throw new InvalidOperationException("The native result host is already parented by another view.");
        }
        _nativeResultsHostAttached = true;
        _nativeShellFailurePanel.BringToFront();
    }

    private void NativeShellResultsOnReady(object? sender, EventArgs e)
    {
        void ApplyReady()
        {
            _nativeShellFailurePanel.Visible = false;
            _matchInfoPanel.Visible = true;
            _nativeShellStatusRetryButton.Visible = false;
            QueueNativeShellSnapshot(force: true);
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyReady();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)ApplyReady);
        }
    }

    private void NativeShellResultsOnFailure(object? sender, NativeShellResultsFailureEventArgs e)
    {
        ShowNativeShellFailure(
            "native-shell-results-" + e.Stage,
            e.Exception ?? new InvalidOperationException("Native Shell result host failed."));
    }

    /// <summary>
    /// A session or population failure is not a background-only diagnostic:
    /// without a visible native error surface the Explorer area can remain
    /// blank while the user has no way to request another coherent snapshot.
    /// This path intentionally does not retry automatically; the retry button
    /// is bounded by an explicit user action.
    /// </summary>
    private void ShowNativeShellFailure(string stage, Exception exception)
    {
        AppStartupDiagnostics.Record(stage, exception);

        void ShowFailure()
        {
            if (_closed)
            {
                return;
            }

            _nativeShellFailureMessage.Text =
                "原生文件视图暂不可用。请点击“重试”；若仍失败，请关闭后重新打开软件。";
            _nativeShellFailurePanel.Visible = true;
            _nativeShellFailurePanel.BringToFront();
            _nativeStatusText.Text = "原生文件视图初始化失败。";
            _nativeShellStatusRetryButton.Visible = true;
            _matchInfoPanel.Visible = false;
        }

        if (Dispatcher.CheckAccess())
        {
            ShowFailure();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)ShowFailure);
        }
    }

    private void RetryNativeShellView()
    {
        if (_closed || _nativeShellShutdownStarted)
        {
            return;
        }

        ViewModel.RetryConnectionCommand.Execute(null);
        if (_nativeShellResults.IsReady)
        {
            _nativeShellFailurePanel.Visible = false;
            _matchInfoPanel.Visible = true;
            QueueNativeShellSnapshot(force: true);
            return;
        }

        if (_nativeShellRetryInProgress)
        {
            return;
        }

        _nativeShellRetryInProgress = true;
        _nativeShellFailureMessage.Text = "正在重新初始化原生文件视图…";
        _nativeShellFailurePanel.Visible = true;
        _nativeShellFailurePanel.BringToFront();
        _nativeShellPagingCancellation?.Cancel();

        var previous = _nativeShellResults;
        previous.Ready -= NativeShellResultsOnReady;
        previous.Failure -= NativeShellResultsOnFailure;
        previous.CancelBefore(long.MaxValue);
        previous.BeginShutdown(result =>
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
            {
                if (_closed)
                {
                    return;
                }

                if (!result.Succeeded)
                {
                    _nativeShellRetryInProgress = false;
                    _nativeShellFailureMessage.Text =
                        "原生文件视图未能安全关闭，请再次点击“重试”。";
                    _nativeShellFailurePanel.Visible = true;
                    _nativeShellFailurePanel.BringToFront();
                    return;
                }

                _nativeResultsViewport.Controls.Remove(previous.View);
                previous.Dispose();

                var replacement = CreateNativeShellResultsHost();
                _nativeShellResults = replacement;
                _lastAppliedNativeColumnWidths = null;
                ConfigureNativeShellColumns(force: true);
                var view = replacement.View;
                view.Dock = Forms.DockStyle.Fill;
                _nativeResultsViewport.Controls.Add(view);
                view.SendToBack();
                _nativeShellFailurePanel.BringToFront();
                _nativeShellRequestedSnapshotKey = null;
                _nativeShellRetryInProgress = false;
            }));
        });
    }

    private void RetryNativeResultsHost()
    {
        if (_closed || _nativeShellRetryInProgress)
        {
            return;
        }

        if (_nativeResultsHostAttached)
        {
            RetryNativeShellView();
            return;
        }

        _nativeStatusText.Text = "正在重新挂载原生文件视图…";
        _nativeShellStatusRetryButton.Visible = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
        {
            try
            {
                AttachNativeResultsHostAfterLayout();
            }
            catch (Exception exception)
            {
                ShowNativeShellFailure("native-shell-results-host-reattach", exception);
            }
        }));
    }

    private void ViewModelOnResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        UpdateNativeSortHeader();
        QueueNativeShellSnapshot();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StatusText) or
            nameof(MainViewModel.ResultSummary) or
            nameof(MainViewModel.IndexProgress))
        {
            RefreshNativeStatus();
        }

    }

    /// <summary>
    /// The visible web toolbar query is mirrored into an independent immutable
    /// Broker paging session.  It is intentionally not based on
    /// RequestResultWindow/ResultBaseOffset: those APIs own the interactive
    /// Web query and impose its window limit, whereas Shell receives every
    /// Broker page until the result set is exhausted.
    /// </summary>
    private void QueueNativeShellSnapshot(bool force = false)
    {
        if (_closed || _nativeShellShutdownStarted || !_nativeShellResults.IsReady)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => QueueNativeShellSnapshot(force)));
            return;
        }

        // A hidden or minimized window must not drive Explorer repopulation.
        // The restore path re-queues once so a requery committed while hidden
        // is not lost.
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        if (force)
        {
            // Retry paths already waited a bounded delay; dispatch immediately
            // while preserving the single-flight marker.
            if (Interlocked.Exchange(ref _nativeShellRefreshQueued, 1) != 0)
            {
                return;
            }
            _nativeShellDebounceTimer.Stop();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                Interlocked.Exchange(ref _nativeShellRefreshQueued, 0);
                if (!_closed && !_nativeShellShutdownStarted && _nativeShellResults.IsReady)
                {
                    StartNativeShellSnapshot(force: true);
                }
            }));
            return;
        }

        // Trailing debounce: every new committed result page restarts the
        // quiet window, so only the stable final query triggers the all-pages
        // Shell crawl.
        _nativeShellDebounceTimer.Stop();
        _nativeShellDebounceTimer.Start();
    }

    private void NativeShellDebounceTimerOnTick(object? sender, EventArgs e)
    {
        _nativeShellDebounceTimer.Stop();
        if (_closed || _nativeShellShutdownStarted || !_nativeShellResults.IsReady)
        {
            return;
        }
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }
        StartNativeShellSnapshot(force: false);
    }

    private void StartNativeShellSnapshot(bool force)
    {
        var snapshotKey = CreateNativeShellSnapshotKey();
        if (!force && string.Equals(snapshotKey, _nativeShellRequestedSnapshotKey, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(snapshotKey, _nativeShellRequestedSnapshotKey, StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _nativeShellPageResetCount, 0);
            Interlocked.Exchange(ref _nativeShellUnavailableRetryScheduled, 0);
        }
        _nativeShellRequestedSnapshotKey = snapshotKey;
        var generation = Interlocked.Increment(ref _nativeShellGeneration);
        _pathMatchMap.Clear();
        _pathMatchMapVersion++;
        _selectionLastFocusedIndex = -1;
        _selectionLastMapVersion = -1;
        _lastFocusedPath = null;
        SetMatchInfoText(null, null);
        _nativeShellResults.CancelBefore(generation);
        _nativeShellPagingCancellation?.Cancel();
        _nativeShellPagingCancellation?.Dispose();
        _nativeShellPagingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_nativeShellLifetime.Token);

        NativeShellSearchSession session;
        try
        {
            session = ViewModel.CreateNativeShellSearchSession(generation);
        }
        catch (Exception exception)
        {
            ShowNativeShellFailure("native-shell-session", exception);
            return;
        }

        _ = PopulateNativeShellAsync(session, generation, _nativeShellPagingCancellation.Token);
    }

    private async Task PopulateNativeShellAsync(
        NativeShellSearchSession session,
        long generation,
        CancellationToken cancellationToken)
    {
        var replace = true;
        var populationWatch = System.Diagnostics.Stopwatch.StartNew();
        var firstPageApplied = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var page = await session.ReadNextPageAsync(cancellationToken).ConfigureAwait(false);
                if (generation != Interlocked.Read(ref _nativeShellGeneration))
                {
                    return;
                }

                if (page.Reset)
                {
                    // The Broker revision moved between pages.  Do not mix two
                    // generations in the Shell. A moving metadata index can
                    // cause several such revisions in succession, so restart
                    // only through the bounded, delayed path below rather
                    // than looping from page zero on every callback.
                    ScheduleNativeShellPageResetRetry();
                    return;
                }

                if (page.TemporarilyUnavailable)
                {
                    // Keep the last coherent Explorer view rather than
                    // replacing it with a transient empty Broker snapshot.
                    // A single low-frequency retry prevents an initial blank
                    // view from staying permanent without producing a busy
                    // reconnect loop.
                    SetNativeShellAvailabilityNotice("搜索服务暂时不可用，正在稍后重试。");
                    ScheduleNativeShellUnavailableRetry();
                    return;
                }

                Interlocked.Exchange(ref _nativeShellUnavailableRetryScheduled, 0);
                SetNativeShellAvailabilityNotice(null);

                using var ticket = replace
                    ? _nativeShellResults.Replace(generation, page.Paths)
                    : _nativeShellResults.Append(generation, page.Paths);
                await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!ticket.Applied || generation != Interlocked.Read(ref _nativeShellGeneration))
                {
                    return;
                }
                if (!firstPageApplied)
                {
                    firstPageApplied = true;
                    SearchPerformanceDiagnostics.RecordNativeShellPhase(
                        generation, "native-first-page", populationWatch.Elapsed.TotalMilliseconds, ticket.Added);
                }

                if (page.MatchEntries.Count != 0)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (generation == Interlocked.Read(ref _nativeShellGeneration))
                        {
                            foreach (var entry in page.MatchEntries)
                            {
                                if (!_pathMatchMap.ContainsKey(entry.Path))
                                {
                                    _pathMatchMap[entry.Path] = (entry.MatchedProperty, entry.MatchedValue);
                                }
                            }
                            _pathMatchMapVersion++;
                            _lastFocusedPath = null;
                            RefreshSelectionMatchInfo();
                        }
                    }, DispatcherPriority.Background);
                }

                replace = false;
                if (!page.HasMore)
                {
                    Interlocked.Exchange(ref _nativeShellPageResetCount, 0);
                    SearchPerformanceDiagnostics.RecordNativeShellPhase(
                        generation, "native-complete", populationWatch.Elapsed.TotalMilliseconds, ticket.Added);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer foreground query replaced this Shell generation.
        }
        catch (Exception exception)
        {
            // A stale/cancelled generation must not cover a newer coherent
            // Explorer view with an error.  For the active generation, do
            // not silently leave an empty result area: show the same native
            // failure surface used for Shell initialization and offer one
            // explicit retry rather than introducing a background loop.
            if (!cancellationToken.IsCancellationRequested &&
                generation == Interlocked.Read(ref _nativeShellGeneration))
            {
                ShowNativeShellFailure("native-shell-population", exception);
            }
        }
        finally
        {
            session.Dispose();
        }
    }

    private string CreateNativeShellSnapshotKey() => string.Join('\u001f',
        // Do not include the foreground result-set identity or Broker page
        // revision here.  Background indexing changes those values while the
        // user has not changed their query; treating them as a new Shell query
        // restarts every all-pages crawl from offset zero.
        ViewModel.SearchText.Trim(),
        ViewModel.SelectedMode.Value,
        ViewModel.CurrentScope.Kind,
        ViewModel.CurrentScope.RootPath ?? string.Empty,
        ViewModel.CurrentSortColumn ?? string.Empty,
        ViewModel.CurrentSortDescending);

    private void ScheduleNativeShellPageResetRetry()
    {
        var resetCount = Interlocked.Increment(ref _nativeShellPageResetCount);
        if (resetCount > 3 || Interlocked.Exchange(ref _nativeShellPageResetRetryScheduled, 1) != 0)
        {
            if (resetCount > 3)
            {
                AppStartupDiagnostics.Record(
                    "native-shell-page-reset",
                    new InvalidOperationException("The Broker index changed repeatedly while native Shell results were being populated."));
            }
            return;
        }

        _ = RetryNativeShellAfterDelayAsync(
            TimeSpan.FromMilliseconds(250 * Math.Pow(2, resetCount - 1)),
            clearPageResetSchedule: true);
    }

    private void ScheduleNativeShellUnavailableRetry()
    {
        if (Interlocked.Exchange(ref _nativeShellUnavailableRetryScheduled, 1) != 0)
        {
            return;
        }

        _ = RetryNativeShellAfterDelayAsync(TimeSpan.FromSeconds(2), clearPageResetSchedule: false);
    }

    private async Task RetryNativeShellAfterDelayAsync(TimeSpan delay, bool clearPageResetSchedule)
    {
        try
        {
            await Task.Delay(delay, _nativeShellLifetime.Token).ConfigureAwait(false);
            if (_closed || _nativeShellShutdownStarted)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => QueueNativeShellSnapshot(force: true), DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (_nativeShellLifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (clearPageResetSchedule)
            {
                Interlocked.Exchange(ref _nativeShellPageResetRetryScheduled, 0);
            }
            else
            {
                // The retry is deliberately one-shot for this stable query.
                // A new committed foreground result page resets this marker.
            }
        }
    }

    private void SelectionPollTimerOnTick(object? sender, EventArgs e)
    {
        if (_closed || !_nativeShellResults.IsReady)
        {
            return;
        }

        RefreshSelectionMatchInfo();
    }

    private void RefreshSelectionMatchInfo()
    {
        if (_matchInfoPanel is null || _matchPropertyLabel is null || _matchValueLabel is null)
        {
            return;
        }

        // Selection overlay takes priority: when a row is focused, display its
        // match info.  Otherwise fall back to a representative default from the
        // current result set so the overlay never stays blank.
        if (_nativeShellResults.TryGetFocusedIndex(out var focusedIndex))
        {
            // Cheap change detection first: one GetFocusedItem call. The full
            // item resolution below only runs when the focused index or the
            // result map version actually moved.
            if (focusedIndex == _selectionLastFocusedIndex
                && _selectionLastMapVersion == _pathMatchMapVersion
                && _lastFocusedPath is not null)
            {
                return;
            }
            _selectionLastFocusedIndex = focusedIndex;
            _selectionLastMapVersion = _pathMatchMapVersion;
            if (_nativeShellResults.TryGetFocusedPath(out var focusedPath)
                && !string.IsNullOrEmpty(focusedPath))
            {
                // Remember the resolved focus even when the map lacks it, so
                // an unmapped focus row does not repeat the full resolution
                // chain on every poll tick.
                _lastFocusedPath = focusedPath;
                if (_pathMatchMap.TryGetValue(focusedPath, out var match))
                {
                    SetMatchInfoText(match.MatchedProperty, match.MatchedValue);
                    return;
                }
            }
        }
        else
        {
            _selectionLastFocusedIndex = -1;
        }

        // No focused selection — show the representative default, cached per
        // result map version instead of rescanning the whole map each tick.
        if (_pathMatchMap.Count == 0)
        {
            SetMatchInfoText(null, null);
            return;
        }
        if (_defaultMatchVersion != _pathMatchMapVersion)
        {
            _defaultMatchCache = GetDefaultMatchFromMap(_pathMatchMap);
            _defaultMatchVersion = _pathMatchMapVersion;
        }
        var defaultMatch = _defaultMatchCache;
        if (defaultMatch is null)
        {
            SetMatchInfoText(null, null);
            return;
        }

        SetMatchInfoText(defaultMatch.Value.MatchedProperty, defaultMatch.Value.MatchedValue);
    }

    private void SetMatchInfoText(string? matchedProperty, string? matchedValue)
    {
        _lastMatchProperty = matchedProperty;
        _lastMatchValue = matchedValue;
        var language = ViewModel.UiLanguageEffective;
        var propertyText = AppUiText.F(
            language,
            "native.matchPropertyPrefix",
            string.IsNullOrEmpty(matchedProperty) ? AppUiText.Get(language, "native.matchFallback") : matchedProperty);
        var valueText = AppUiText.F(
            language,
            "native.matchValuePrefix",
            string.IsNullOrEmpty(matchedValue) ? AppUiText.Get(language, "native.matchDash") : matchedValue);
        if (!string.Equals(propertyText, _lastMatchPropertyText, StringComparison.Ordinal))
        {
            _matchPropertyLabel.Text = propertyText;
            _lastMatchPropertyText = propertyText;
            // The value label position only depends on the property label
            // width, so it is recomputed exclusively when that text changed.
            _matchValueLabel.Location = new System.Drawing.Point(
                _matchPropertyLabel.Right + 20,
                _matchValueLabel.Location.Y);
        }
        if (!string.Equals(valueText, _lastMatchValueText, StringComparison.Ordinal))
        {
            _matchValueLabel.Text = valueText;
            _lastMatchValueText = valueText;
        }
    }

    /// <summary>Re-localizes native surfaces (Shell column header, match strip) for the current language.</summary>
    private void ApplyUiLanguage()
    {
        _nativeColumnHeader.ApplyLanguage(ViewModel.UiLanguageEffective);
        // Drop the deduplicated texts so the strip re-renders in the new
        // language with whatever match is currently selected.
        _lastMatchPropertyText = null;
        _lastMatchValueText = null;
        SetMatchInfoText(_lastMatchProperty, _lastMatchValue);
    }

    private void ViewModelOnUiLanguageChanged()
    {
        if (_closed)
        {
            return;
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)ApplyUiLanguage);
    }

    internal static (string? MatchedProperty, string? MatchedValue)? GetDefaultMatchFromMap(
        Dictionary<string, (string? MatchedProperty, string? MatchedValue)> map)
    {
        if (map.Count == 0) return null;
        // Prefer the first entry that has a matched property name.
        foreach (var kv in map)
        {
            if (!string.IsNullOrEmpty(kv.Value.MatchedProperty))
                return kv.Value;
        }
        // All entries are filename-only hits; return the first.
        return map.First().Value;
    }

    private void RefreshNativeStatus()
    {
        if (_closed)
        {
            return;
        }

        SetTextIfChanged(_nativeStatusText, _nativeShellAvailabilityNotice ?? ViewModel.StatusText);
        SetTextIfChanged(_nativeProgressText, ViewModel.IndexProgress.Label);
        SetTextIfChanged(_nativeResultCountText, ViewModel.ResultSummary);
    }

    private static void SetTextIfChanged(Forms.Label label, string? value)
    {
        value ??= string.Empty;
        if (!string.Equals(label.Text, value, StringComparison.Ordinal))
        {
            label.Text = value;
        }
    }

    private void SetNativeShellAvailabilityNotice(string? notice)
    {
        void Apply()
        {
            _nativeShellAvailabilityNotice = notice;
            RefreshNativeStatus();
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Apply);
        }
    }

    /// <summary>
    /// Stops the cross-apartment Shell worker while the owner HWND still
    /// exists. A timeout is not permission to dispose the ExplorerBrowser:
    /// the caller keeps the window alive and allows a later Exit retry to
    /// await the same teardown task instead.
    /// </summary>
    private async Task<bool> ShutdownNativeShellAsync()
    {
        if (!_nativeShellShutdownStarted)
        {
            _nativeShellShutdownStarted = true;
            _nativeShellPagingCancellation?.Cancel();
            _nativeShellResults.CancelBefore(long.MaxValue);
            _nativeShellShutdownTask = _nativeShellResults.ShutdownAsync();
        }

        try
        {
            var result = await _nativeShellShutdownTask!.WaitAsync(TimeSpan.FromSeconds(8));
            if (!result.Succeeded)
            {
                AppStartupDiagnostics.Record("native-shell-shutdown-" + result.Stage,
                    result.Exception ?? new InvalidOperationException("Native Shell shutdown did not complete."));
                _nativeStatusText.Text = "原生文件视图尚未关闭；请稍后再次退出。";
                return false;
            }
            return true;
        }
        catch (TimeoutException exception)
        {
            AppStartupDiagnostics.Record("native-shell-shutdown-timeout", exception);
            _nativeStatusText.Text = "正在关闭原生文件视图；请稍后再次退出。";
            return false;
        }
        catch (Exception exception)
        {
            AppStartupDiagnostics.Record("native-shell-shutdown", exception);
            _nativeStatusText.Text = "原生文件视图关闭失败；请稍后再次退出。";
            return false;
        }
    }

    internal void ApplyLoadedSettings(AppSettings settings)
    {
        if (_closed)
        {
            return;
        }
        _nativeColumnHeader.SetRatios(settings.NormalizedNativeShellColumnRatios);
        ScheduleNativeShellColumnConfiguration();
        ViewModel.ApplyLoadedSettings(settings);
        if (_nativeResultsPanel is NativeStatusPanel nativeStatusPanel)
        {
            nativeStatusPanel.ApplyLanguage(ViewModel.UiLanguageEffective);
        }
        _bridge.PublishState();
    }

    public void ApplyFolderScope(string path)
    {
        ViewModel.ApplyFolderScope(path);
        RestoreFromTray();
        if (_webView is { IsReady: true })
        {
            _webView.PostMessage("focusSearch", new { selectAll = true });
        }
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _loadedInitializationStarted, 1) != 0)
        {
            return;
        }

        var startTask = ViewModel.StartAsync();
        var initialStatusTask = RefreshRuntimeStatusAsync();
        _statusTimer.Start();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        try
        {
            AttachNativeResultsHostAfterLayout();
        }
        catch (Exception exception)
        {
            ShowNativeShellFailure("native-shell-results-host-attach", exception);
        }
        try
        {
            if (_webView is not null)
            {
                await _webView.InitializeAsync();
            }
        }
        catch (Exception)
        {
            // CompositionWebViewHost owns the native, readable runtime failure surface.
        }
        try
        {
            await startTask;
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            AppStartupDiagnostics.Record("main-start", exception);
            // Search startup is optional to the native/WebView shell.  Keeping the
            // window alive lets Retry and the readable runtime surface remain usable.
        }
        await initialStatusTask;
        RefreshNativeStatus();
        QueueNativeShellSnapshot();
        ViewModel.WakeBrokerOnce();
    }

    /// <summary>
    /// Recreates the toolbar WebView after a long-hide teardown. The page
    /// hydrates its query/mode/scope from the bridge state published on the
    /// ready handshake, so nothing the user typed is lost.
    /// </summary>
    private void EnsureToolbarWebView(bool initialize)
    {
        if (_closed || _webView is not null)
        {
            return;
        }

        _webView = new CompositionWebViewHost(new WebViewHostOptions
        {
            View = WebUiView.MainToolbar,
            Language = ViewModel.UiLanguageEffective,
            RuntimeConfiguration = WebViewRuntimeConfiguration.Evergreen,
        });
        WebToolbarContainer.Child = _webView;
        _webView.MessageReceived += WebViewOnMessageReceived;
        _webView.MessageRejected += WebViewOnMessageRejected;
        _webView.RuntimeUnavailable += WebViewOnRuntimeUnavailable;
        if (initialize)
        {
            // The constructor path defers initialization to the Loaded event;
            // only the post-teardown recreation starts it here.
            _ = InitializeToolbarWebViewAsync(_webView);
        }
    }

    private static async Task InitializeToolbarWebViewAsync(CompositionWebViewHost webView)
    {
        try
        {
            await webView.InitializeAsync();
        }
        catch (Exception)
        {
            // CompositionWebViewHost owns the native, readable runtime failure
            // surface; keep the window alive for Retry either way.
        }
    }

    /// <summary>
    /// Destroys the toolbar WebView while the window stays hidden in the tray
    /// past the idle threshold. The resident tray state then carries no
    /// renderer tree at all; restore recreates it on demand.
    /// </summary>
    private void TearDownToolbarWebViewForLongHide()
    {
        if (_closed || _webView is null)
        {
            return;
        }

        var webView = _webView;
        _webView = null;
        WebToolbarContainer.Child = null;
        webView.MessageReceived -= WebViewOnMessageReceived;
        webView.MessageRejected -= WebViewOnMessageRejected;
        webView.RuntimeUnavailable -= WebViewOnRuntimeUnavailable;
        try
        {
            webView.Dispose();
        }
        catch (Exception exception)
        {
            AppStartupDiagnostics.Record("toolbar-webview-teardown", exception);
        }
        ScheduleWorkingSetTrim();
    }

    private void BridgeOnStateReady(object state)
    {
        if (!_closed && _webView is { IsReady: true })
        {
            _webView.PostMessage("state", state);
        }
    }

    private void BridgeOnPreferencesStateReady(object preferences)
    {
        // The preferences window is a distinct, short-lived WebView with no
        // result surface. It receives only the small { preferences } update;
        // the full bridge state (which can carry the bounded result viewport)
        // is never serialized for it.
        if (!_closed && _preferencesWebView is { IsReady: true } target)
        {
            target.PostMessage("state", new { preferences });
        }
    }

    private void BridgeOnFileDragArmed(FileDragArmRequest request)
    {
        if (_closed)
        {
            return;
        }

        // WebMessageReceived is a WebView2 callback. Post only the arm state to
        // the next input turn; CompositionWebViewHost starts OLE later from its
        // raw mouse path once the same held gesture crosses DragSize.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
        {
            if (_closed)
            {
                return;
            }

            _webView?.QueueShellFileDragArm(request.GestureId, request.Path);
        }));
    }

    private void WebViewOnMessageReceived(object? sender, ValidatedWebMessageEventArgs e)
    {
        void Dispatch()
        {
            if (string.Equals(e.Message.Type, "preferences.open", StringComparison.Ordinal))
            {
                OpenPreferencesWindow();
                return;
            }

            _bridge.HandleMessage(e.Message.Type, e.Message.Payload);
            if (string.Equals(e.Message.Type, "ready", StringComparison.Ordinal) &&
                _webView is { IsReady: true })
            {
                _webView.PostMessage("focusSearch", new { selectAll = false });
            }
        }
        if (Dispatcher.CheckAccess())
        {
            Dispatch();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)Dispatch);
        }
    }

    /// <summary>
    /// Opens settings in a separate composition WebView.  Keeping it outside
    /// the toolbar prevents the old modal from clipping the query field and
    /// keeps the Shell result host independent from preference rendering.
    /// </summary>
    private async void OpenPreferencesWindow()
    {
        if (_closed)
        {
            return;
        }

        if (_preferencesWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        // A previous instance is already in its synchronous WPF close path.
        // Its Closed callback will release the host before the user can open a
        // fresh settings window, avoiding two preferences WebViews at once.
        if (_preferencesWindow is not null)
        {
            return;
        }

        var preferencesWebView = new CompositionWebViewHost(new WebViewHostOptions
        {
            View = WebUiView.Preferences,
            Language = ViewModel.UiLanguageEffective,
            RuntimeConfiguration = WebViewRuntimeConfiguration.Evergreen,
        });
        var formsHost = new System.Windows.Forms.Integration.WindowsFormsHost
        {
            Child = preferencesWebView,
        };
        var window = new Window
        {
            Title = AppUiText.Get(ViewModel.UiLanguageEffective, "window.settingsTitle"),
            Owner = this,
            Width = 760,
            Height = 620,
            MinWidth = 620,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            Content = formsHost,
        };

        _preferencesWindow = window;
        _preferencesWebView = preferencesWebView;
        preferencesWebView.MessageReceived += PreferencesWebViewOnMessageReceived;
        preferencesWebView.MessageRejected += PreferencesWebViewOnMessageRejected;
        preferencesWebView.RuntimeUnavailable += PreferencesWebViewOnRuntimeUnavailable;
        window.Closing += PreferencesWindowOnClosing;
        window.Closed += PreferencesWindowOnClosed;
        window.Show();

        try
        {
            await preferencesWebView.InitializeAsync();
        }
        catch (Exception exception)
        {
            // CompositionWebViewHost paints its own readable runtime fault.
            // Preserve the independent settings window so the fault remains
            // actionable rather than tearing down the caller's main window.
            AppStartupDiagnostics.Record("preferences-webview", exception);
        }
    }

    private void PreferencesWebViewOnMessageReceived(object? sender, ValidatedWebMessageEventArgs e)
    {
        void Dispatch()
        {
            if (_closed || !ReferenceEquals(sender, _preferencesWebView))
            {
                return;
            }

            _bridge.HandleMessage(e.Message.Type, e.Message.Payload);

            // The standalone page may emit preferences.open before its own
            // ready event.  Reopen once the host is ready so the same bridge
            // operation always publishes a state payload to this window.
            if (string.Equals(e.Message.Type, "ready", StringComparison.Ordinal))
            {
                _bridge.OpenPreferences();
            }
            else if (string.Equals(e.Message.Type, "preferences.close", StringComparison.Ordinal))
            {
                ClosePreferencesWindow(notifyBridge: false);
            }
        }

        if (Dispatcher.CheckAccess())
        {
            Dispatch();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)Dispatch);
        }
    }

    private void PreferencesWebViewOnMessageRejected(object? sender, WebMessageRejectedEventArgs e)
    {
        AppWebUiDiagnostics.RecordRejection(e);
    }

    private void PreferencesWebViewOnRuntimeUnavailable(object? sender, WebViewRuntimeUnavailableEventArgs e)
    {
        AppWebUiDiagnostics.RecordFault("preferences", e);
        ViewModel.RecordSettingsUiFailure(e.Exception ?? new InvalidOperationException(e.Message));
    }

    private void PreferencesWindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (!_preferencesWindowClosing)
        {
            CloseBridgePreferences();
        }

        _preferencesWindowClosing = true;
    }

    private void PreferencesWindowOnClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _preferencesWindow))
        {
            return;
        }

        var preferences = _preferencesWebView;
        _preferencesWindow = null;
        _preferencesWebView = null;
        _preferencesWindowClosing = false;
        if (preferences is not null)
        {
            preferences.MessageReceived -= PreferencesWebViewOnMessageReceived;
            preferences.MessageRejected -= PreferencesWebViewOnMessageRejected;
            preferences.RuntimeUnavailable -= PreferencesWebViewOnRuntimeUnavailable;
            preferences.Dispose();
        }
    }

    private void ClosePreferencesWindow(bool notifyBridge)
    {
        var window = _preferencesWindow;
        if (window is null)
        {
            if (notifyBridge)
            {
                CloseBridgePreferences();
            }
            return;
        }

        if (notifyBridge)
        {
            CloseBridgePreferences();
        }

        _preferencesWindowClosing = true;
        window.Close();
    }

    private void CloseBridgePreferences()
    {
        using var document = JsonDocument.Parse("{}");
        _bridge.HandleMessage("preferences.close", document.RootElement);
    }

    private void WebViewOnRuntimeUnavailable(object? sender, WebViewRuntimeUnavailableEventArgs e)
    {
        AppWebUiDiagnostics.RecordFault("toolbar", e);
        ViewModel.RecordSettingsUiFailure(e.Exception ?? new InvalidOperationException(e.Message));
    }

    private void WebViewOnMessageRejected(object? sender, WebMessageRejectedEventArgs e)
    {
        AppWebUiDiagnostics.RecordRejection(e);
                ViewModel.ReportFileActionFailure(AppUiText.F(ViewModel.UiLanguageEffective, "ui.requestRejected", e.Reason));
    }

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_trayLifetime.OnWindowClosing() == TrayCloseDisposition.Hide)
        {
            e.Cancel = true;
            SuspendInputAndHide();
            HiddenToTray?.Invoke();
            return;
        }
        if (_closeFlushCompleted)
        {
            return;
        }
        e.Cancel = true;
        if (_closeFlushStarted)
        {
            return;
        }
        _closeFlushStarted = true;
        var closeReady = false;
        try
        {
            // The ExplorerBrowser owns an MTA Shell proxy.  Its explicit
            // BeginShutdown/ShutdownAsync handshake must complete while this
            // Window HWND is still alive; tray hiding deliberately does not
            // reach this path.
            if (!await ShutdownNativeShellAsync())
            {
                return;
            }
            ClosePreferencesWindow(notifyBridge: true);
            await _bridge.FlushColumnPersistenceAsync(TimeSpan.FromSeconds(3));
            _nativeColumnPersistenceTimer.Stop();
            var finalSettings = ViewModel.UpdateNativeShellColumnRatiosInMemory(
                _nativeColumnHeader.SnapshotRatios());
            var settingsShutdown = await _settingsSaver.FlushLatestAndStopAsync(
                finalSettings,
                TimeSpan.FromSeconds(3));
            if (settingsShutdown.Persistence.Error is not null)
            {
                await ViewModel.ReportSettingsPersistenceFailureAsync(
                    settingsShutdown.Persistence.Error);
            }
            else if (!settingsShutdown.Persistence.Saved)
            {
                await ViewModel.ReportSettingsPersistenceFailureAsync(
                    new InvalidOperationException("Latest native column ratios were not saved before close."));
            }
            if (!settingsShutdown.WorkerStopped)
            {
                await ViewModel.ReportSettingsPersistenceFailureAsync(
                    new TimeoutException("Settings persistence worker did not stop before close."));
            }
            closeReady = true;
        }
        catch (Exception exception)
        {
            await ViewModel.ReportSettingsPersistenceFailureAsync(exception);
            closeReady = true;
        }
        if (!closeReady)
        {
            // Keep the owner HWND and the ExplorerBrowser alive.  A later
            // user Exit retries the same BeginShutdown completion task;
            // disposing now would reintroduce the cross-apartment crash.
            _closeFlushStarted = false;
            return;
        }

        await ViewModel.DrainSettingsDiagnosticsAsync(TimeSpan.FromSeconds(1));
        _closeFlushCompleted = true;
        Close();
    }

    internal void RestoreFromTray()
    {
        if (_closed) return;
        _trayLifetime.Restore();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        _webViewIdleTeardownTimer.Stop();
        EnsureToolbarWebView(initialize: true);
        _webView?.ResumeFromHiddenWindow();
        _webView?.ResumeInput();
        _statusTimer.Start();
        _selectionPollTimer?.Start();
        ViewModel.ResumeBrokerMonitoring();
        ViewModel.WakeBrokerOnce();
        _ = RefreshRuntimeStatusAsync();
        // A requery may have committed while hidden and queueing was skipped;
        // the snapshot-key dedupe makes this a no-op for unchanged queries.
        QueueNativeShellSnapshot();
        Activate();
        Topmost = true;
        Topmost = false;
        if (_webView is { IsReady: true }) _webView.PostMessage("focusSearch", new { selectAll = false });
    }

    internal void HideToTray()
    {
        if (!_closed)
        {
            // A hidden window drives neither Explorer repopulation nor the
            // Broker monitor cadence; RestoreFromTray resumes both.
            ViewModel.PauseBrokerMonitoring();
            _nativeShellDebounceTimer.Stop();
            _trayLifetime.Hide();
            SuspendInputAndHide();
            // Tray residency is the long-lived state of this app: release the
            // WebView2 renderer memory and trim our own working set. The
            // restore path resumes both before the window is shown again.
            _webView?.SuspendForHiddenWindow();
            _webViewIdleTeardownTimer.Stop();
            _webViewIdleTeardownTimer.Start();
            ScheduleWorkingSetTrim();
        }
    }

    /// <summary>
    /// Best-effort working-set trim of THIS process only after hiding to tray.
    /// Restore repages through soft faults, which stays imperceptible while
    /// the window is not visible; no other process is ever touched.
    /// </summary>
    private void ScheduleWorkingSetTrim()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
        {
            if (_closed)
            {
                return;
            }
            try
            {
                // (IntPtr)(-1) is the current-process pseudo-handle; (-1, -1)
                // asks the memory manager to trim the working set now.
                _ = SetProcessWorkingSetSize((IntPtr)(-1), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch
            {
                // Trimming is optional and must never affect hiding.
            }
        }));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            _webView?.SuspendInput();
            _statusTimer.Stop();
            _selectionPollTimer?.Stop();
        }
        else if (IsVisible)
        {
            _webViewIdleTeardownTimer.Stop();
            EnsureToolbarWebView(initialize: true);
            _webView?.ResumeFromHiddenWindow();
            _webView?.ResumeInput();
            ViewModel.WakeBrokerOnce();
            _statusTimer.Start();
            _selectionPollTimer?.Start();
            _ = RefreshRuntimeStatusAsync();
        }
    }

    private void SuspendInputAndHide()
    {
        try
        {
            _webView?.SuspendInput();
            _statusTimer.Stop();
            _selectionPollTimer?.Stop();
        }
        finally
        {
            Hide();
        }
    }

    internal void RefreshCacheFromTray() => ViewModel.RefreshIndexCommand.Execute(null);

    internal void RequestProcessExit()
    {
        if (_closed) return;
        _trayLifetime.RequestExit();
        Close();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        ClosePreferencesWindow(notifyBridge: false);
        _statusTimer.Stop();
        _nativeColumnApplyTimer.Stop();
        _nativeColumnPersistenceTimer.Stop();
        _selectionPollTimer?.Stop();
        StateChanged -= MainWindow_OnStateChanged;
        ViewModel.Results.CollectionChanged -= ViewModelOnResultsCollectionChanged;
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _bridge.StateReady -= BridgeOnStateReady;
        _bridge.FileDragArmed -= BridgeOnFileDragArmed;
        _bridge.Dispose();
        if (_webView is not null)
        {
            _webView.MessageReceived -= WebViewOnMessageReceived;
            _webView.MessageRejected -= WebViewOnMessageRejected;
            _webView.RuntimeUnavailable -= WebViewOnRuntimeUnavailable;
            _webView.Dispose();
        }
        _nativeShellResults.Ready -= NativeShellResultsOnReady;
        _nativeShellResults.Failure -= NativeShellResultsOnFailure;
        _nativeShellPagingCancellation?.Cancel();
        _nativeShellPagingCancellation?.Dispose();
        _nativeShellLifetime.Cancel();
        _nativeShellLifetime.Dispose();
        _nativeShellResults.Dispose();
        ViewModel.Dispose();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            if (_webView is { IsReady: true })
            {
                _webView.PostMessage("focusSearch", new { selectAll = true });
            }
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            OpenPreferencesWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel.SearchText = string.Empty;
            if (_webView is { IsReady: true })
            {
                _webView.PostMessage("focusSearch", new { selectAll = false });
            }
            e.Handled = true;
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        if (_statusRefreshRunning)
        {
            return;
        }
        _statusRefreshRunning = true;
        try
        {
            await ViewModel.RefreshRuntimeStatusAsync();
            RefreshNativeStatus();
        }
        finally
        {
            _statusRefreshRunning = false;
        }
    }
}
