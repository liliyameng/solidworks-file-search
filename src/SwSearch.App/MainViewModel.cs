using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using SwSearch.Contracts;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SwSearch.App;

public sealed class SearchResultItem(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;
    public string FullPath => Hit.FullPath;
    public string FileName => Hit.FileName;
    public string DirectoryPath => Path.GetDirectoryName(Hit.FullPath) ?? string.Empty;
    public string KindLabel => Hit.Kind switch
    {
        SolidWorksDocumentKind.Part => "零件",
        SolidWorksDocumentKind.Assembly => "装配体",
        _ => "未知"
    };
    public string MatchedPropertyLabel => Hit.MatchedProperty ?? "文件名";
    public string MatchedValueLabel => Hit.MatchedValue ?? "—";
    public string SourceLabel => Hit.OriginModelPath is null
        ? (Hit.MatchedProperty is null ? "文件" : "当前文档")
        : "关联模型 · " + Path.GetFileName(Hit.OriginModelPath) +
          (string.IsNullOrWhiteSpace(Hit.OriginConfiguration) ? string.Empty : " · " + Hit.OriginConfiguration);
}

public sealed class SearchResultsCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AppendRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        var changed = false;
        foreach (var item in items) { Items.Add(item); changed = true; }
        if (!changed) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void TrimStart(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, Items.Count);
        for (var index = 0; index < count; index++) Items.RemoveAt(0);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ColumnLayoutItem : ObservableObject
{
    private string _displayName;
    private double _width;
    private int _order;
    private bool _visible;

    public ColumnLayoutItem(ResultColumnLayout layout)
    {
        Id = layout.Id;
        _displayName = layout.DisplayName;
        _width = layout.Width;
        _order = layout.Order;
        _visible = layout.Visible;
    }

    public string Id { get; }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public double Width { get => _width; set => SetProperty(ref _width, value); }
    public int Order { get => _order; set => SetProperty(ref _order, value); }
    public bool Visible { get => _visible; set => SetProperty(ref _visible, value); }
    public ResultColumnLayout ToLayout() => new(Id, DisplayName, Width, Order, Visible);
}

public sealed record SearchModeOption(SearchMode Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record CompositeIndexProgress(
    double? FilePercent,
    double? CachedCoveragePercent,
    double? CurrentMetadataPercent,
    string Label,
    bool IsMetadataServiceStalled,
    string FileState,
    string MetadataState,
    string StatusKind,
    string Tooltip)
{
    private const string WaitingProgressText = "文件 --% · 可读取缓存 --% · 属性处理 --%";

    public static CompositeIndexProgress Waiting { get; } = new(
        null,
        null,
        null,
        WaitingProgressText,
        false,
        RuntimeServiceState.Unavailable.ToString(),
        RuntimeServiceState.Unavailable.ToString(),
        "waiting",
        WaitingProgressText);
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    internal const int SearchPageSize = 120;
    internal const int ContinuationPageSize = 200;
    // Metadata commits can advance the index revision many times in a short
    // burst. Keep their visual refresh outside the input path: one quiet
    // refresh is enough to replace the current viewport with the latest page.
    private static readonly TimeSpan RevisionRefreshDebounceDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan RevisionRefreshMinimumInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RevisionRefreshInputQuietDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(66, 72, 80));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(166, 91, 0));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(180, 34, 39));
    private static readonly IReadOnlyCollection<SolidWorksDocumentKind> SupportedKinds =
        [SolidWorksDocumentKind.Part, SolidWorksDocumentKind.Assembly];
    private readonly ISearchClient _searchClient;
    private readonly IFileActions _fileActions;
    private readonly MetadataProgressEpochTracker _metadataProgressEpochTracker = new();
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _statePollInterval;
    private readonly object _brokerMonitorGate;
    private bool _brokerMonitorPaused;
    private TaskCompletionSource<bool>? _brokerMonitorResumeSignal;
    private int _brokerWakeRequested;
    private readonly IAppSettingsStore? _settingsStore;
    private readonly SettingsPersistenceCoordinator _settingsPersistence;
    private readonly ISettingsPersistenceDiagnosticSink _settingsDiagnostics;
    private readonly CancellationTokenSource _lifetime = new();
    private AppSettings _settings;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _countCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _revisionCancellation;
    private string _searchText = string.Empty;
    private SearchModeOption _selectedMode;
    private SearchResultItem? _selectedResult;
    private bool _isBusy;
    private bool _hasMoreResults;
    private bool _isRetryVisible;
    private string _statusText = "正在连接搜索服务…";
    private Brush _statusBrush = WarningBrush;
    private string _resultSummary = "正在读取文件";
    private string _runtimeSummary = "索引状态读取中";
    private CompositeIndexProgress _indexProgress = CompositeIndexProgress.Waiting;
    private DiscoveryPreferenceOption _selectedDiscoveryOption;
    private string _networkRootsText;
    private bool _isBackgroundPaused;
    private SearchScope _scope = SearchScope.AllMachine;
    private bool _disposed;
    private int _started;
    private long _searchVersion;
    private int _searchTextCompositionDepth;
    private bool _compositionSearchPending;
    private bool _applyingLoadedSettings;
    private long _userStateGeneration;
    private string? _lastEpoch;
    private long _lastRevision = -1;
    private long _lastInteractiveSearchTimestamp;
    private long _lastRevisionRefreshTimestamp;
    private long? _pageRevision;
    private string? _pageFingerprint;
    private int _nextOffset;
    private int _resultBaseOffset;
    private long _totalResultCount = -1;
    private long _resultSetId;
    private string? _sortColumn;
    private bool _sortDescending;
    private BrokerRuntimePhase? _lastPhase;
    private int _hasCommittedFileIndex;

    public MainViewModel(
        ISearchClient searchClient,
        IFileActions fileActions,
        TimeSpan? debounceDelay = null,
        IAppSettingsStore? settingsStore = null,
        AppSettings? initialSettings = null,
        TimeSpan? statePollInterval = null)
        : this(
            searchClient,
            fileActions,
            debounceDelay,
            settingsStore,
            initialSettings,
            statePollInterval,
            new FileSettingsPersistenceDiagnosticSink())
    {
    }

    internal MainViewModel(
        ISearchClient searchClient,
        IFileActions fileActions,
        TimeSpan? debounceDelay,
        IAppSettingsStore? settingsStore,
        AppSettings? initialSettings,
        TimeSpan? statePollInterval,
        ISettingsPersistenceDiagnosticSink settingsDiagnostics)
    {
        _searchClient = searchClient;
        _fileActions = fileActions;
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(80);
        _statePollInterval = statePollInterval ?? TimeSpan.FromSeconds(20);
        _brokerMonitorGate = new object();
        _settingsStore = settingsStore;
        _settingsPersistence = new SettingsPersistenceCoordinator(PrepareSettingsSnapshot);
        _settingsDiagnostics = settingsDiagnostics;
        _settings = initialSettings ?? AppSettings.Default;
        SearchModes =
        [
            new(SearchMode.All, "全部"),
            new(SearchMode.FileName, "文件名"),
            new(SearchMode.Property, "属性"),
        ];
        _selectedMode = SearchModes[0];
        DiscoveryOptions =
        [
            new(DiscoveryPreference.Automatic, "自动（推荐）"),
            new(DiscoveryPreference.EverythingPreferred, "Everything 优先（可选加速）"),
            new(DiscoveryPreference.BuiltInOnly, "仅内置扫描"),
        ];
        _selectedDiscoveryOption = DiscoveryOptions.First(option => option.Value == DiscoveryPreference.Automatic);
        _networkRootsText = string.Join(Environment.NewLine, _settings.NetworkRoots);
        _isBackgroundPaused = false;
        ColumnLayouts = new ObservableCollection<ColumnLayoutItem>(
            _settings.NormalizedColumns.Select(static column => new ColumnLayoutItem(column)));
        ObserveColumnEditors();
        SearchNowCommand = new RelayCommand(() => _ = ScheduleSearchAsync(immediate: true));
        LoadMoreCommand = new RelayCommand(RequestLoadMore);
        RetryConnectionCommand = new RelayCommand(RetryConnection);
        RefreshIndexCommand = new RelayCommand(() => _ = RefreshIndexAsync());
        TogglePauseCommand = new RelayCommand(TogglePause);
        RestoreAllMachineCommand = new RelayCommand(RestoreAllMachine);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    public SearchResultsCollection<SearchResultItem> Results { get; } = [];
    public ObservableCollection<ColumnLayoutItem> ColumnLayouts { get; }
    public RelayCommand SearchNowCommand { get; }
    public RelayCommand LoadMoreCommand { get; }
    public RelayCommand RetryConnectionCommand { get; }
    public RelayCommand RefreshIndexCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand RestoreAllMachineCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public IReadOnlyList<SearchModeOption> SearchModes { get; }
    public IReadOnlyList<DiscoveryPreferenceOption> DiscoveryOptions { get; }
    internal SettingsPersistenceCoordinator SettingsPersistence => _settingsPersistence;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                if (Volatile.Read(ref _searchTextCompositionDepth) != 0)
                {
                    _compositionSearchPending = true;
                }
                else
                {
                    _ = ScheduleSearchAsync(immediate: false);
                }
            }
        }
    }

    internal void ApplySearchTextFromPage(string value)
    {
        if (!SetProperty(ref _searchText, value)) return;
        _ = ScheduleSearchAsync(immediate: true);
    }

    public void BeginSearchTextComposition()
    {
        if (Interlocked.Increment(ref _searchTextCompositionDepth) != 1)
        {
            return;
        }

        _compositionSearchPending = false;
        Interlocked.Increment(ref _searchVersion);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _countCancellation?.Cancel();
        _countCancellation?.Dispose();
        _searchCancellation = null;
    }

    public void CompleteSearchTextComposition()
    {
        if (Interlocked.Decrement(ref _searchTextCompositionDepth) > 0)
        {
            return;
        }

        Interlocked.Exchange(ref _searchTextCompositionDepth, 0);
        if (_compositionSearchPending)
        {
            _compositionSearchPending = false;
            _ = ScheduleSearchAsync(immediate: false);
        }
    }

    public SearchModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                RaisePropertyChanged(nameof(IsAllMode));
                RaisePropertyChanged(nameof(IsFileNameMode));
                RaisePropertyChanged(nameof(IsPropertyMode));
                _ = ScheduleSearchAsync(immediate: false);
            }
        }
    }

    public SearchResultItem? SelectedResult { get => _selectedResult; set => SetProperty(ref _selectedResult, value); }
    public bool IsAllMode { get => SelectedMode.Value == SearchMode.All; set { if (value) SelectedMode = SearchModes[0]; } }
    public bool IsFileNameMode { get => SelectedMode.Value == SearchMode.FileName; set { if (value) SelectedMode = SearchModes[1]; } }
    public bool IsPropertyMode { get => SelectedMode.Value == SearchMode.Property; set { if (value) SelectedMode = SearchModes[2]; } }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaisePropertyChanged(nameof(BusyVisibility)); } }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool HasMoreResults
    {
        get => _hasMoreResults;
        private set
        {
            if (SetProperty(ref _hasMoreResults, value))
            {
                RaisePropertyChanged(nameof(LoadMoreVisibility));
            }
        }
    }
    public Visibility LoadMoreVisibility => HasMoreResults ? Visibility.Visible : Visibility.Collapsed;
    public int ResultBaseOffset => _resultBaseOffset;
    public int LoadedResultCount => _nextOffset;
    public long TotalResultCount
    {
        get => _totalResultCount;
        private set => SetProperty(ref _totalResultCount, value);
    }
    internal long? CurrentPageRevision => _pageRevision;
    internal long CurrentResultSetId => _resultSetId;
    internal string? CurrentSortColumn => _sortColumn;
    internal bool CurrentSortDescending => _sortDescending;
    public bool IsRetryVisible { get => _isRetryVisible; private set { if (SetProperty(ref _isRetryVisible, value)) RaisePropertyChanged(nameof(RetryVisibility)); } }
    public Visibility RetryVisibility => IsRetryVisible ? Visibility.Visible : Visibility.Collapsed;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public string RuntimeSummary { get => _runtimeSummary; private set => SetProperty(ref _runtimeSummary, value); }
    public CompositeIndexProgress IndexProgress
    {
        get => _indexProgress;
        private set => SetProperty(ref _indexProgress, value);
    }
    public DiscoveryPreferenceOption SelectedDiscoveryOption
    {
        get => _selectedDiscoveryOption;
        set
        {
            if (SetProperty(ref _selectedDiscoveryOption, value)) MarkUserStateChanged();
        }
    }
    public string NetworkRootsText
    {
        get => _networkRootsText;
        set
        {
            if (SetProperty(ref _networkRootsText, value)) MarkUserStateChanged();
        }
    }
    public bool IsBackgroundPaused
    {
        get => _isBackgroundPaused;
        private set
        {
            if (SetProperty(ref _isBackgroundPaused, value))
            {
                RaisePropertyChanged(nameof(PauseButtonText));
            }
        }
    }
    public string PauseButtonText => IsBackgroundPaused ? "恢复后台处理" : "暂停后台处理";
    public SearchScope CurrentScope => _scope;
    internal long UserStateGeneration => Volatile.Read(ref _userStateGeneration);
    public bool IsFolderScope => _scope.Kind == SearchScopeKind.Folder;
    public Visibility ScopeBannerVisibility => IsFolderScope ? Visibility.Visible : Visibility.Collapsed;
    public string ScopeSummary => IsFolderScope
        ? $"当前文件夹及全部子文件夹：{_scope.RootPath}"
        : "全机范围";

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _ = ScheduleSearchAsync(immediate: true);
        if (_searchClient is IBrokerStateClient stateClient)
        {
            _ = MonitorBrokerAsync(stateClient, _lifetime.Token);
        }
        return Task.CompletedTask;
    }

    internal void ApplyLoadedSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
        {
            return;
        }

        _applyingLoadedSettings = true;
        try
        {
            _settings = settings with
            {
                DiscoveryPreference = DiscoveryPreference.Automatic,
                BackgroundPaused = false,
            };
            SelectedDiscoveryOption = DiscoveryOptions.First(option => option.Value == DiscoveryPreference.Automatic);
            NetworkRootsText = string.Join(Environment.NewLine, settings.NetworkRoots);
            IsBackgroundPaused = false;
            ReplaceColumnEditor(settings.NormalizedColumns);
            if (_searchClient is IRuntimeControlClient runtime)
            {
                runtime.SetBackgroundPaused(false);
            }
        }
        finally
        {
            _applyingLoadedSettings = false;
        }
    }

    public void ApplyFolderScope(string path)
    {
        try
        {
            _scope = SearchScope.Folder(path);
            MarkUserStateChanged();
            RaiseScopeChanged();
            StatusText = "当前文件夹及全部子文件夹。";
            StatusBrush = NormalBrush;
            _ = ScheduleSearchAsync(true);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            StatusText = $"文件夹范围无效：{exception.Message}";
            StatusBrush = ErrorBrush;
        }
    }

    public void RestoreAllMachine()
    {
        _scope = SearchScope.AllMachine;
        MarkUserStateChanged();
        RaiseScopeChanged();
        StatusText = "已恢复全机范围。";
        StatusBrush = NormalBrush;
        _ = ScheduleSearchAsync(true);
    }

    public bool Open(SearchResultItem result) => RunFileAction(() => _fileActions.Open(result.FullPath), "已请求打开文件");
    public bool Reveal(SearchResultItem result) => RunFileAction(() => _fileActions.Reveal(result.FullPath), "已在资源管理器中定位");
    public bool CopyPath(SearchResultItem result) => RunFileAction(() => _fileActions.CopyPath(result.FullPath), "完整路径已复制");
    public bool ViewProperties(SearchResultItem result) => RunFileAction(() => _fileActions.ViewProperties(result.FullPath), "已启动属性查看器");

    public void ReportFileActionFailure(string message)
    {
        StatusText = $"操作失败：{message}";
        StatusBrush = ErrorBrush;
    }

    public void SaveSettingsFromEditor()
    {
        var snapshot = CreateSettingsSnapshotFromEditor();
        _settings = snapshot;
        ReplaceColumnEditor(snapshot.NormalizedColumns);
        NetworkRootsText = string.Join(Environment.NewLine, snapshot.NetworkRoots);
        var generation = UserStateGeneration;
        StatusText = "设置已应用，正在后台保存…";
        StatusBrush = NormalBrush;
        _ = PersistSettingsInBackgroundAsync(snapshot, generation);
    }

    internal AppSettings CreateSettingsSnapshotFromEditor()
    {
        MarkUserStateChanged();
        var roots = JsonAppSettingsStore.NormalizeNetworkRoots(
            NetworkRootsText.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var columns = ResultColumnDefaults.Normalize(ColumnLayouts.Select(static item => item.ToLayout()));
        return new AppSettings(
            DiscoveryPreference.Automatic,
            roots,
            false,
            columns,
            _settings.PropertyCardSource,
            _settings.PropertyCardPath,
            3,
            _settings.NormalizedPropertyDisplayOrder,
            _settings.NormalizedNativeShellColumnRatios);
    }

    private ValueTask<IPreparedAppSettingsSave> PrepareSettingsSnapshot(
        AppSettings settings,
        CancellationToken cancellationToken) =>
        _settingsStore?.PrepareSaveAsync(settings, cancellationToken) ??
        ValueTask.FromResult<IPreparedAppSettingsSave>(new DeferredAppSettingsSave(static () => { }));

    internal Task<SettingsPersistenceResult> PersistSettingsSnapshotAsync(
        AppSettings settings,
        CancellationToken cancellationToken) =>
        _settingsPersistence.QueueAsync(settings, cancellationToken);

    internal void ReportSettingsPersistenceFailure(Exception exception)
    {
        if (_disposed) return;
        StatusText = $"状态已生效，但设置未保存（{exception.GetType().Name}）。";
        StatusBrush = WarningBrush;
        _settingsDiagnostics.TryWrite("SETTINGS_PERSISTENCE_FAILED", exception.GetType().Name);
    }

    internal void RecordSettingsUiFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _settingsDiagnostics.TryWrite("SETTINGS_UI_FAILED", exception.GetType().Name);
    }

    internal async Task ReportSettingsPersistenceFailureAsync(
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        if (!_disposed)
        {
            StatusText = $"状态已生效，但设置未保存（{exception.GetType().Name}）。";
            StatusBrush = WarningBrush;
        }
        _settingsDiagnostics.TryWrite("SETTINGS_PERSISTENCE_FAILED", exception.GetType().Name);
        await Task.CompletedTask;
    }

    internal Task ObserveClosePersistenceResultAsync(
        SettingsPersistenceResult result,
        CancellationToken cancellationToken = default) =>
        result.Error is null
            ? Task.CompletedTask
            : ReportSettingsPersistenceFailureAsync(result.Error, cancellationToken);

    internal Task<bool> DrainSettingsDiagnosticsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _settingsDiagnostics.StopAsync(timeout, cancellationToken);

    internal void ApplyPersistedSettings(AppSettings settings)
    {
        _settings = settings;
        ReplaceColumnEditor(settings.NormalizedColumns);
        NetworkRootsText = string.Join(Environment.NewLine, settings.NetworkRoots);
        StatusText = "设置已保存；网络根目录将在 Broker 下次启动时生效。";
        StatusBrush = NormalBrush;
    }

    public void SaveColumnLayout(IEnumerable<ResultColumnLayout> layouts)
    {
        var snapshot = UpdateColumnLayoutInMemory(layouts);
        _ = PersistSettingsInBackgroundAsync(snapshot, UserStateGeneration);
    }

    internal AppSettings UpdateColumnLayoutInMemory(IEnumerable<ResultColumnLayout> layouts)
    {
        MarkUserStateChanged();
        var normalized = ResultColumnDefaults.Normalize(layouts);
        _settings = _settings with { Columns = normalized };
        ReplaceColumnEditor(normalized);
        return _settings;
    }

    internal void SaveNativeShellColumnRatios(IEnumerable<double> ratios)
    {
        var snapshot = UpdateNativeShellColumnRatiosInMemory(ratios);
        _ = PersistSettingsInBackgroundAsync(snapshot, UserStateGeneration);
    }

    internal AppSettings UpdateNativeShellColumnRatiosInMemory(IEnumerable<double> ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        MarkUserStateChanged();
        _settings = _settings with
        {
            NativeShellColumnRatios = NativeShellColumnRatioDefaults.Normalize(ratios),
        };
        return _settings;
    }

    internal AppSettings UpdatePropertyDisplayOrderInMemory(IEnumerable<string?>? propertyNames)
    {
        _settings = _settings with
        {
            PropertyDisplayOrder = PropertyDisplayOrderDefaults.Normalize(propertyNames),
        };
        return _settings;
    }

    public void RestoreDefaultColumns()
    {
        _ = RestoreDefaultColumnsInMemory();
    }

    internal AppSettings RestoreDefaultColumnsInMemory() =>
        UpdateColumnLayoutInMemory(ResultColumnDefaults.All);

    public async Task RefreshRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_searchClient is not IRuntimeControlClient runtime)
        {
            return;
        }
        try
        {
            var progress = await runtime.GetProgressAsync(cancellationToken);
            var rawProjection = CreateIndexProgress(
                progress,
                Volatile.Read(ref _hasCommittedFileIndex) != 0);
            var projected = _metadataProgressEpochTracker.Stabilize(
                progress.MetadataProgressEpoch,
                progress.MetadataProgressEpochSession,
                rawProjection);
            RuntimeSummary = CreateRuntimeSummary(progress, projected);
            IndexProgress = projected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The connection monitor owns the visible failure state.
        }
    }

    internal static CompositeIndexProgress CreateIndexProgress(
        RuntimeProgressSnapshot progress,
        bool hasCommittedFileIndex = false)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var legacy = progress.Metadata;
        var discovery = progress.Discovery;
        var metadata = progress.MetadataService;

        var fileState = discovery?.State ??
                        (progress.IsPaused ? RuntimeServiceState.Paused : RuntimeServiceState.Running);
        var indexed = Math.Max(0, discovery?.IndexedFileCount ?? legacy.DiscoveredFileCount);
        var estimatedTotal = discovery?.EstimatedTotalCount;
        var baselineTotal = discovery?.BaselineFileCount;
        var committedFileCount = Math.Max(0, legacy.DiscoveredFileCount);
        // A provider's running estimate is useful live progress even before a
        // durable snapshot commits.  Keep that distinct from completion: an
        // idle stale/failed cache must never be promoted to 100% merely because
        // its historical row count equals an old estimate.
        var fileIndexCompletionKnown = discovery is null ||
            !discovery.HasPersistedDiscoveryState ||
            discovery.HasCommittedCompleteSnapshot;
        var liveDiscoveryInProgress = fileState is RuntimeServiceState.Running or
            RuntimeServiceState.Paused or RuntimeServiceState.Stalled;
        double? filePercent;
        if (discovery?.HasCommittedCompleteSnapshot == true)
        {
            // Committed complete snapshot exists (generation > 0, last-success
            // timestamp, source_state=ready). It is the durable completion proof,
            // so a non-live provider always renders 100 even when its transient
            // counters reset or its estimate differs from the committed store.
            // While a new uncommitted scan is live, use the provider estimate when
            // it is available (or the committed baseline otherwise) and cap at
            // 99.99; a live scan must never claim a fake 100.
            var isLiveScan = fileState is RuntimeServiceState.Running
                or RuntimeServiceState.Paused
                or RuntimeServiceState.Stalled;
            if (isLiveScan)
            {
                if (indexed == 0)
                {
                    // When the provider restarts with reset counters while a
                    // committed store exists, hold at the live-scan cap instead
                    // of regressing to 0%.  A live uncommitted scan can never
                    // show a fragile 100%.
                    filePercent = 99.99d;
                }
                else
                {
                    var denominator = estimatedTotal is > 0
                        ? estimatedTotal.Value
                        : baselineTotal is > 0
                            ? baselineTotal.Value
                            : Math.Max(committedFileCount, indexed);
                    filePercent = denominator > 0
                        ? Math.Min(99.99d, Percentage(indexed, denominator))
                        : null;
                }
            }
            else
            {
                // Non-live (Idle/Unavailable): the committed snapshot is authoritative.
                // IndexedFileCount may be 0 during a reconnect or after the provider
                // resets, but the durable baseline proves completion.
                filePercent = 100d;
            }
        }
        else if (liveDiscoveryInProgress &&
                 discovery?.HasPersistedDiscoveryState == true &&
                 indexed == 0 &&
                 (committedFileCount > 0 || baselineTotal is > 0))
        {
            // A persisted partial snapshot still has usable rows. Everything
            // resets its transient counter before the next atomic snapshot is
            // committed; keep that restart transition at the live-scan cap
            // instead of regressing the visible file progress to 0.00%.
            filePercent = 99.99d;
        }
        else if (estimatedTotal is > 0 && (fileIndexCompletionKnown || liveDiscoveryInProgress))
        {
            // Provider estimate or the fixed committed baseline.  An
            // uncommitted live scan is capped below 100 even when it climbs
            // past the baseline, because only a durable complete snapshot can
            // prove completion.
            filePercent = fileIndexCompletionKnown
                ? Percentage(indexed, estimatedTotal.Value)
                : Math.Min(99.99d, Percentage(indexed, estimatedTotal.Value));
        }
        else if (liveDiscoveryInProgress && !fileIndexCompletionKnown && baselineTotal is > 0)
        {
            // No provider estimate and no runtime baseline (an older broker):
            // keep the committed snapshot as the fixed denominator so the bar
            // advances monotonically and caps at 99.99 instead of regressing
            // or showing "--%" mid-scan.
            filePercent = Math.Min(99.99d, Percentage(indexed, baselineTotal.Value));
        }
        else if (fileIndexCompletionKnown && fileState == RuntimeServiceState.Idle && indexed > 0)
        {
            filePercent = 100d;
        }
        else if (fileIndexCompletionKnown && hasCommittedFileIndex && committedFileCount > 0)
        {
            filePercent = 100d;
        }
        else
        {
            filePercent = null;
        }

        var metadataState = metadata?.State ??
                            (progress.IsPaused ? RuntimeServiceState.Paused : RuntimeServiceState.Running);
        var completed = Math.Max(0, metadata?.CompletedCount ?? legacy.CompletedCount);
        var partial = Math.Max(0, metadata?.PartialCount ?? legacy.PartialCount);
        var pending = Math.Max(0, metadata?.PendingCount ?? legacy.PendingCount);
        var failed = Math.Max(0, metadata?.FailedCount ?? legacy.FailedCount);
        var skipped = Math.Max(0, legacy.SkippedCount);
        var terminalCount = SaturatingSum(completed, partial, failed, skipped);
        var metadataDiscovered = Math.Max(0, legacy.DiscoveredFileCount);
        var cachedReadable = legacy.CachedReadableFileCount is { } cached
            ? Math.Max(0, cached)
            : (long?)null;
        double? cachedCoveragePercent = metadataDiscovered > 0 && cachedReadable is { } readable
            ? Percentage(readable, metadataDiscovered)
            : null;
        double? currentMetadataPercent = metadataDiscovered > 0
            ? Percentage(terminalCount, metadataDiscovered)
            : null;

        var metadataCode = metadata?.Code ?? string.Empty;
        var recovering = metadataState == RuntimeServiceState.Stalled ||
                         IsMetadataRecoveryCode(metadataCode);
        var unavailable = metadataState == RuntimeServiceState.Unavailable;
        var stalled = recovering || unavailable;
        var storage = ProjectStorageStatus(progress);
        var (_, statusKind) = storage.Message is not null
            ? ($" · {storage.Message}", storage.StatusKind)
            : (metadataState, recovering, pending) switch
            {
                (_, true, _) => (" · 属性处理超时，正在自动恢复", "metadata-recovery"),
                (RuntimeServiceState.Unavailable, _, _) => (" · 属性宿主无法启动", "metadata-unavailable"),
                (RuntimeServiceState.Running, _, _) => (" · 属性更新中", "active"),
                (RuntimeServiceState.Paused, _, _) => (" · 属性已暂停", "paused"),
                (RuntimeServiceState.Idle, _, > 0) => (" · 属性等待更新", "pending"),
                (RuntimeServiceState.Idle, _, _) when currentMetadataPercent is >= 100d && skipped > 0 =>
                    (" · 属性处理已完成，部分云端文件仅按文件名索引", "complete-with-skips"),
                (RuntimeServiceState.Idle, _, _) when currentMetadataPercent is >= 100d =>
                    (" · 属性已完成", "complete"),
                (_, _, > 0) => (" · 属性等待更新", "pending"),
                _ => (string.Empty, "normal"),
            };
        var label = CreateVisibleProgressText(
            filePercent,
            cachedCoveragePercent,
            currentMetadataPercent);
        return new CompositeIndexProgress(
            filePercent,
            cachedCoveragePercent,
            currentMetadataPercent,
            label,
            stalled || storage.IsFailure,
            fileState.ToString(),
            metadataState.ToString(),
            statusKind,
            label);
    }

    private sealed class MetadataProgressEpochTracker
    {
        private long _observedEpoch = -1;
        private string? _observedSession;
        private double? _visibleCurrentMetadataPercent;
        private CompositeIndexProgress? _lastProjection;

        public CompositeIndexProgress Stabilize(
            long metadataProgressEpoch,
            string? metadataProgressEpochSession,
            CompositeIndexProgress projection)
        {
            // Older brokers and first-start snapshots have no explicit work
            // epoch. Preserve their raw, intentionally non-monotonic semantics;
            // only a committed batch boundary opts into this protection.
            if (metadataProgressEpoch <= 0 ||
                string.IsNullOrWhiteSpace(metadataProgressEpochSession))
            {
                _observedEpoch = -1;
                _observedSession = null;
                _visibleCurrentMetadataPercent = null;
                _lastProjection = null;
                return projection;
            }

            if (!string.Equals(
                    _observedSession,
                    metadataProgressEpochSession,
                    StringComparison.Ordinal))
            {
                // The broker process changed. Epoch numbers restart per
                // broker, so this is a fresh trusted baseline rather than an
                // out-of-order update from the previous process.
                _observedSession = metadataProgressEpochSession;
                _observedEpoch = metadataProgressEpoch;
                _visibleCurrentMetadataPercent = projection.CurrentMetadataPercent;
                _lastProjection = projection;
                return projection;
            }

            if (metadataProgressEpoch < _observedEpoch)
            {
                // A delayed response must not become a new work-set boundary
                // (or overwrite a newer high-water mark) merely because its
                // version differs. Keep the last coherent projection instead.
                return _lastProjection ?? projection;
            }

            if (metadataProgressEpoch > _observedEpoch)
            {
                _observedEpoch = metadataProgressEpoch;
                _visibleCurrentMetadataPercent = projection.CurrentMetadataPercent;
                _lastProjection = projection;
                return projection;
            }

            var visible = projection.CurrentMetadataPercent;
            if (_visibleCurrentMetadataPercent is { } previous &&
                visible is { } current &&
                current < previous)
            {
                visible = previous;
            }

            if (visible is { } advanced &&
                (_visibleCurrentMetadataPercent is null || advanced > _visibleCurrentMetadataPercent.Value))
            {
                _visibleCurrentMetadataPercent = advanced;
            }

            if (visible == projection.CurrentMetadataPercent)
            {
                _lastProjection = projection;
                return projection;
            }

            var label = CreateVisibleProgressText(
                projection.FilePercent,
                projection.CachedCoveragePercent,
                visible);
            var stabilized = projection with
            {
                CurrentMetadataPercent = visible,
                Label = label,
                Tooltip = label,
            };
            _lastProjection = stabilized;
            return stabilized;
        }
    }

    internal static string CreateRuntimeSummary(
        RuntimeProgressSnapshot progress,
        CompositeIndexProgress? projected = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        projected ??= CreateIndexProgress(progress);
        var storageMessage = ProjectStorageStatus(progress).Message;
        var metadata = progress.Metadata;
        var operation = projected.StatusKind switch
        {
            "storage-maintenance" or "storage-blocked" => storageMessage ?? "属性更新中",
            "pending" => "属性等待更新",
            "metadata-recovery" => "属性处理超时，正在自动恢复",
            "metadata-unavailable" => "属性宿主无法启动",
            "paused" => "已暂停",
            "complete-with-skips" => "属性处理已完成，部分云端文件仅按文件名索引",
            "complete" => "已完成",
            _ => progress.IsPaused ? "已暂停" : "运行中",
        };
        var skipped = metadata.SkippedCount > 0 ? $" / 已跳过 {metadata.SkippedCount:N0}" : string.Empty;
        return $"{DescribeDiscoverySource(progress.DiscoverySource)} · 文件 {metadata.DiscoveredFileCount:N0} · " +
               $"属性完成 {metadata.CompletedCount:N0} / 部分 {metadata.PartialCount:N0} / " +
               $"待处理 {metadata.PendingCount:N0} / 失败 {metadata.FailedCount:N0}{skipped} · {operation}";
    }

    private static bool IsMetadataRecoveryCode(string code) =>
        code is "METADATA_WORKER_RECOVERING" or
            "METADATA_FILE_TIMEOUT" or
            "METADATA_TIMEOUT_DEFERRED";

    private static StorageStatusProjection ProjectStorageStatus(RuntimeProgressSnapshot progress)
    {
        var reason = progress.StoragePauseReason?.Trim();
        var phase = progress.StorageMaintenancePhase?.Trim();
        var recoveryFailed = string.Equals(phase, "recovery-failed", StringComparison.OrdinalIgnoreCase) ||
                             IsStorageRecoveryFailureCode(reason);
        var maintenanceActive = !string.IsNullOrWhiteSpace(phase) &&
                                !string.Equals(phase, "idle", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(phase, "recovery-failed", StringComparison.OrdinalIgnoreCase);

        if (recoveryFailed)
        {
            return new StorageStatusProjection(
                "索引维护暂时失败，后台将自动重试",
                "storage-maintenance",
                IsFailure: false);
        }
        if (string.Equals(reason, "WAL_PRESSURE", StringComparison.OrdinalIgnoreCase))
        {
            return new StorageStatusProjection(
                "正在整理索引缓存",
                "storage-maintenance",
                IsFailure: false);
        }
        if (maintenanceActive)
        {
            return new StorageStatusProjection(
                "正在整理索引空间，属性更新将自动继续",
                "storage-maintenance",
                IsFailure: false);
        }
        if (progress.MetadataWritesPaused && IsStorageCapacityCode(reason))
        {
            return new StorageStatusProjection(
                "索引空间不足，属性更新已暂停",
                "storage-blocked",
                IsFailure: true);
        }
        if (progress.MetadataWritesPaused)
        {
            return new StorageStatusProjection(
                "属性更新已暂停",
                "storage-blocked",
                IsFailure: true);
        }
        return new StorageStatusProjection(null, "normal", IsFailure: false);
    }

    private static bool IsStorageCapacityCode(string? code) =>
        code is not null &&
        (code.Equals("DATABASE_PHYSICAL_SAFETY_LIMIT", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("DATABASE_HARD_LIMIT", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("DATABASE_LOGICAL_LIMIT", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("LOGICAL_DATABASE_LIMIT", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("LOGICAL_STORAGE_BLOCKED", StringComparison.OrdinalIgnoreCase));

    private static bool IsStorageRecoveryFailureCode(string? code) =>
        code is not null &&
        (code.Equals("DATABASE_RECOVERY_FAILED", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("STORAGE_RECOVERY_FAILED", StringComparison.OrdinalIgnoreCase));

    private static string CreateVisibleProgressText(
        double? filePercent,
        double? cachedCoveragePercent,
        double? currentMetadataPercent) =>
        $"文件 {FormatPercent(filePercent)} · " +
        $"可读取缓存 {FormatPercent(cachedCoveragePercent)} · " +
        $"属性处理 {FormatPercent(currentMetadataPercent)}";

    private static string DescribeDiscoverySource(string? source)
    {
        var value = source?.Trim() ?? string.Empty;
        if (value.Contains("EVERYTHING", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("MFT", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return "快速索引";
        }
        if (value.Contains("BUILTIN", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("DIRECTORY", StringComparison.OrdinalIgnoreCase))
        {
            return "内置扫描";
        }
        return value.Any(static character => character is >= '\u4e00' and <= '\u9fff')
            ? value
            : "文件索引";
    }

    private static long SaturatingSum(params long[] values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            var nonNegative = Math.Max(0, value);
            if (long.MaxValue - total < nonNegative)
            {
                return long.MaxValue;
            }
            total += nonNegative;
        }
        return total;
    }

    private readonly record struct StorageStatusProjection(
        string? Message,
        string StatusKind,
        bool IsFailure);

    private static double Percentage(long value, long total) =>
        Math.Clamp(Math.Round(value * 100d / total, 2, MidpointRounding.AwayFromZero), 0d, 100d);

    private static string FormatPercent(double? value) => value switch
    {
        null => "--%",
        >= 100d => "100.00%",
        var percent => $"{percent:0.00}%",
    };

    public void RequestLoadMore()
    {
        if (_disposed || IsBusy || !HasMoreResults)
        {
            return;
        }

        RequestResultWindow(_nextOffset, ContinuationPageSize);
    }

    internal void RequestResultWindow(int offset, int limit)
    {
        if (_disposed || offset < 0 || limit is < 1 or > 500)
        {
            return;
        }
        if (offset == _resultBaseOffset && Results.Count != 0)
        {
            return;
        }
        _ = ScheduleSearchAsync(immediate: true, windowRequest: true, requestedOffset: offset, requestedLimit: limit);
    }

    internal void ApplySort(string column, bool descending)
    {
        if (string.Equals(_sortColumn, column, StringComparison.Ordinal) && _sortDescending == descending)
        {
            return;
        }
        _sortColumn = column;
        _sortDescending = descending;
        _ = ScheduleSearchAsync(immediate: true);
    }

    /// <summary>
    /// Clears an explicit column sort. A null sort is intentionally distinct
    /// from ascending/descending: the index then applies its stable default
    /// relevance ordering for the current query.
    /// </summary>
    internal void ClearSort()
    {
        if (_sortColumn is null)
        {
            return;
        }

        _sortColumn = null;
        _sortDescending = false;
        _ = ScheduleSearchAsync(immediate: true);
    }

    /// <summary>
    /// Captures the current query as an immutable, read-only Broker session for
    /// the desktop Explorer surface.  This intentionally does not share the
    /// interactive search cancellation source or mutate <see cref="Results"/>:
    /// the native Shell can page every result while the foreground search keeps
    /// responding to typing immediately.
    /// </summary>
    internal NativeShellSearchSession CreateNativeShellSearchSession(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var snapshot = new SearchQuery(
            SearchText.Trim(),
            SelectedMode.Value,
            Offset: 0,
            Limit: NativeShellSearchSession.PageSize,
            Kinds: SupportedKinds.ToArray(),
            Scope: _scope,
            SortColumn: _sortColumn,
            SortDescending: _sortDescending,
            IsInteractive: false);

        return new NativeShellSearchSession(_searchClient, snapshot, generation, _lifetime.Token);
    }

    private async Task ScheduleSearchAsync(
        bool immediate,
        bool windowRequest = false,
        int? requestedOffset = null,
        int? requestedLimit = null,
        bool preserveExistingResults = false,
        bool isInteractive = true)
    {
        if (_disposed)
        {
            return;
        }
        if (isInteractive)
        {
            Interlocked.Exchange(ref _lastInteractiveSearchTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        var offset = windowRequest || preserveExistingResults
            ? requestedOffset ?? (preserveExistingResults ? _resultBaseOffset : 0)
            : 0;
        var pageSize = windowRequest || preserveExistingResults
            ? Math.Clamp(
                requestedLimit ?? (preserveExistingResults && Results.Count > 0
                    ? Results.Count
                    : ContinuationPageSize),
                1,
                500)
            : SearchPageSize;
        var version = Interlocked.Increment(ref _searchVersion);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _searchCancellation.Token;
        if (!windowRequest && !preserveExistingResults)
        {
            Interlocked.Increment(ref _resultSetId);
            _countCancellation?.Cancel();
            _countCancellation?.Dispose();
            _countCancellation = null;
            if (Results.Count == 0)
            {
                ResultSummary = "正在搜索…";
            }
        }
        try
        {
            if (!immediate)
            {
                await Task.Delay(_debounceDelay, token);
            }
            await SearchAsync(
                token,
                version,
                offset,
                pageSize,
                windowRequest,
                preserveExistingResults,
                isInteractive);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task SearchAsync(
        CancellationToken cancellationToken,
        long version,
        int offset,
        int pageSize,
        bool windowRequest,
        bool preserveExistingResults,
        bool isInteractive)
    {
        var performanceClock = System.Diagnostics.Stopwatch.StartNew();
        var cacheTemporarilyUnavailable = false;
        SearchPerformanceDiagnostics.RecordPhase(version, "query-start", 0, 0);
        var text = SearchText.Trim();
        var kinds = SupportedKinds;

        if (!preserveExistingResults)
        {
            IsBusy = true;
            StatusText = ScopeStatus(windowRequest ? "正在读取文件…" : "正在搜索…");
            StatusBrush = NormalBrush;
        }
        try
        {
            var result = await _searchClient.SearchAsync(
                new SearchQuery(
                    text,
                    SelectedMode.Value,
                    Offset: offset,
                    Limit: pageSize,
                    Kinds: kinds,
                    Scope: _scope,
                    ExpectedRevision: windowRequest && !preserveExistingResults ? _pageRevision : null,
                    PageFingerprint: windowRequest && !preserveExistingResults ? _pageFingerprint : null,
                    SortColumn: _sortColumn,
                    SortDescending: _sortDescending,
                    IsInteractive: isInteractive),
                cancellationToken);
            SearchPerformanceDiagnostics.RecordPhase(
                version, "query-complete", performanceClock.Elapsed.TotalMilliseconds, result.Hits.Count);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            if (result.Availability == SearchResultAvailability.CacheTemporarilyUnavailable)
            {
                cacheTemporarilyUnavailable = true;
                StatusText = ScopeStatus(NormalizeCapabilityMessage(result.Capability));
                StatusBrush = WarningBrush;
                IsRetryVisible = true;
                _ = RetryUnavailableCacheAsync(version, offset, pageSize);
                return;
            }

            // A reconnecting Broker is not an authority for an empty page. The
            // local reader can briefly see an empty transitional snapshot while
            // the Broker finishes migration/recovery; keep the already-rendered
            // cache until a non-transient result arrives.
            if (Results.Count > 0 &&
                result.Hits.Count == 0 &&
                IsTransientBrokerCapability(result.Capability.Code))
            {
                StatusText = ScopeStatus(NormalizeCapabilityMessage(result.Capability));
                StatusBrush = WarningBrush;
                IsRetryVisible = true;
                return;
            }

            if (!windowRequest && !preserveExistingResults)
            {
                TotalResultCount = -1;
            }

            var responseHits = result.Hits.Where(static hit =>
                hit.Kind is SolidWorksDocumentKind.Part or SolidWorksDocumentKind.Assembly).ToArray();
            var selectedPath = SelectedResult?.FullPath;
            var replacement = responseHits.Select(static hit => new SearchResultItem(hit)).ToArray();
            var resultsChanged = !preserveExistingResults ||
                                  _resultBaseOffset != offset ||
                                  !Results.Select(static item => item.Hit).SequenceEqual(responseHits);
            if (resultsChanged)
            {
                if (preserveExistingResults || windowRequest && result.PageReset)
                {
                    Interlocked.Increment(ref _resultSetId);
                }
                Results.ReplaceAll(replacement);
                _resultBaseOffset = offset;
                _nextOffset = offset + result.Hits.Count;
                RaisePropertyChanged(nameof(ResultBaseOffset));
                RaisePropertyChanged(nameof(LoadedResultCount));
                SelectedResult = selectedPath is null
                    ? null
                    : Results.FirstOrDefault(item =>
                        item.FullPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            }
            _pageRevision = result.IndexRevision ?? result.State?.Revision;
            _pageFingerprint = result.PageFingerprint ?? SearchPagingFingerprint.Create(new SearchQuery(
                text, SelectedMode.Value, Kinds: kinds, Scope: _scope,
                SortColumn: _sortColumn, SortDescending: _sortDescending));
            if (resultsChanged)
            {
                SearchPerformanceDiagnostics.RecordPhase(
                    version, "results-applied", performanceClock.Elapsed.TotalMilliseconds, Results.Count);
            }
            HasMoreResults = result.Hits.Count > 0 && (result.HasMore ?? (result.Hits.Count == pageSize));
            if (!windowRequest && !HasMoreResults && (!preserveExistingResults || offset == 0))
            {
                TotalResultCount = responseHits.Length;
            }
            ResultSummary = TotalResultCount >= 0 ? $"共 {TotalResultCount:N0} 个文件" : "正在统计文件数…";
            IsRetryVisible = IsBrokerFailureCapability(result.Capability.Code);
            if (result.State is { } state)
            {
                ObserveBrokerState(state, requeryOnRevisionChange: true);
            }
            else
            {
                StatusText = ScopeStatus(result.Capability.State == CapabilityState.Available && Results.Count == 0
                    ? IsFolderScope ? "范围内暂无结果。" : "未找到匹配项。"
                    : NormalizeCapabilityMessage(result.Capability));
                StatusBrush = result.Capability.State == CapabilityState.Available ? NormalBrush : WarningBrush;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (version == Volatile.Read(ref _searchVersion))
            {
                if (!windowRequest && !preserveExistingResults)
                {
                    HasMoreResults = false;
                }
                if (!preserveExistingResults)
                {
                    ResultSummary = Results.Count == 0 ? "共 0 个文件" : $"共 {Results.Count:N0} 个文件";
                    StatusText = Results.Count == 0 ? "文件列表暂不可用，请重试。" : string.Empty;
                    StatusBrush = ErrorBrush;
                    IsRetryVisible = true;
                }
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _searchVersion))
            {
                if (!preserveExistingResults)
                {
                    IsBusy = false;
                }
                if (!windowRequest &&
                    !preserveExistingResults &&
                    isInteractive &&
                    !cacheTemporarilyUnavailable &&
                    TotalResultCount < 0 &&
                    _searchClient is ISearchCountClient countClient)
                {
                    StartBackgroundCount(countClient, CurrentResultSetId, text, kinds);
                }
            }
        }
    }

    private void StartBackgroundCount(
        ISearchCountClient countClient,
        long resultSetId,
        string text,
        IReadOnlyCollection<SolidWorksDocumentKind> kinds)
    {
        _countCancellation?.Cancel();
        _countCancellation?.Dispose();
        _countCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = RefreshTotalCountAsync(countClient, resultSetId, new SearchQuery(
            text, SelectedMode.Value, Offset: 0, Limit: 1, Kinds: kinds, Scope: _scope,
            SortColumn: _sortColumn, SortDescending: _sortDescending, IsInteractive: false),
            _countCancellation.Token);
    }

    private async Task RefreshTotalCountAsync(
        ISearchCountClient countClient,
        long resultSetId,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            // Counting a broad one-character property query can be much more
            // expensive than the first viewport. Only start after input has
            // stayed unchanged; a new query cancels and interrupts the command.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
            var count = await countClient.CountAsync(query, cancellationToken);
            if (!_disposed && resultSetId == CurrentResultSetId)
            {
                TotalResultCount = count;
                ResultSummary = $"共 {count:N0} 个文件";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Exact counting is informational and never blocks the first page.
        }
    }

    private async Task MonitorBrokerAsync(IBrokerStateClient stateClient, CancellationToken cancellationToken)
    {
        // Idle backoff: the monitor only needs to notice Broker-driven
        // revisions quickly while something is actually changing. A quiet
        // Broker stretches the poll interval; any revision/phase move or
        // connection error resets it to the base cadence. The cap scales with
        // the configured base so short test intervals stay effectively
        // constant while the production cadence (20 s) tops out at 60 s.
        var delay = _statePollInterval;
        var backoffCapTicks = Math.Min(
            TimeSpan.FromSeconds(60).Ticks,
            _statePollInterval.Ticks * 3);
        long lastRevision = -1;
        BrokerRuntimePhase? lastPhase = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitForBrokerMonitoringResumeAsync(cancellationToken);
            var stateChanged = false;
            try
            {
                // Passive probe: the real BrokerSearchClient must never be
                // launched by the monitor — a stopped Broker is the designed
                // idle state, and only explicit user actions cold-start it.
                // Test doubles keep the recovering interface behavior.
                BrokerStateSnapshot? state;
                if (stateClient is BrokerSearchClient passiveCapable)
                {
                    state = await passiveCapable.TryGetStatePassiveAsync(cancellationToken);
                }
                else
                {
                    state = await stateClient.GetStateAsync(cancellationToken);
                }
                if (state is null)
                {
                    // Quiet state: no error surface, no retry button, and the
                    // idle backoff cadence applies until someone wakes it.
                    stateChanged = false;
                }
                else
                {
                    stateChanged = state.Revision != lastRevision || state.Phase != lastPhase;
                    lastRevision = state.Revision;
                    lastPhase = state.Phase;
                    ObserveBrokerState(state, requeryOnRevisionChange: true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (BrokerProtocolMismatchException exception)
            {
                StatusText = $"搜索服务版本不匹配：{exception.Message}";
                StatusBrush = ErrorBrush;
                IsRetryVisible = true;
                stateChanged = true;
            }
            catch (BrokerConnectionException exception)
            {
                // Keep the already rendered local cache visible and report the
                // actual failing phase. A response timeout must never be
                // flattened into a pipe-connect failure.
                StatusText = ScopeStatus($"已显示本地缓存；{exception.Message}");
                StatusBrush = exception.Retryable ? WarningBrush : ErrorBrush;
                IsRetryVisible = true;
                stateChanged = true;
            }
            catch (Exception)
            {
                // Local SQLite search remains authoritative. Broker monitoring
                // must not overwrite a valid result page or contend with input.
                StatusBrush = WarningBrush;
                IsRetryVisible = true;
                stateChanged = true;
            }

            delay = stateChanged
                ? _statePollInterval
                : TimeSpan.FromTicks(Math.Min(
                    Math.Min(delay.Ticks + delay.Ticks, backoffCapTicks),
                    Math.Max(backoffCapTicks, _statePollInterval.Ticks)));
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Stops Broker state polling while the main window is hidden to tray.
    /// The paused monitor parks on a resume signal instead of polling, so a
    /// hidden window performs no background pipe traffic.
    /// </summary>
    public void PauseBrokerMonitoring()
    {
        lock (_brokerMonitorGate)
        {
            if (!_brokerMonitorPaused)
            {
                _brokerMonitorPaused = true;
                _brokerMonitorResumeSignal ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            // The next visible session may request one fresh wake again.
            Interlocked.Exchange(ref _brokerWakeRequested, 0);
        }
    }

    /// <summary>
    /// One bounded active state round when the main window becomes visible.
    /// Unlike the passive monitor this may cold-start the Broker (an explicit
    /// product-surface use), restoring index freshness on wake. Restricted to
    /// the real client so test doubles are untouched.
    /// </summary>
    public void WakeBrokerOnce()
    {
        if (_disposed || _searchClient is not BrokerSearchClient brokerClient)
        {
            return;
        }
        if (Interlocked.Exchange(ref _brokerWakeRequested, 1) != 0)
        {
            return;
        }
        _ = WakeBrokerCoreAsync(brokerClient);
    }

    private async Task WakeBrokerCoreAsync(BrokerSearchClient brokerClient)
    {
        try
        {
            _ = await brokerClient.GetStateAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // Bounded best effort; the passive monitor owns steady-state.
        }
    }

    /// <summary>Resumes polling; the next poll runs immediately after resume.</summary>
    public void ResumeBrokerMonitoring()
    {
        lock (_brokerMonitorGate)
        {
            if (!_brokerMonitorPaused)
            {
                return;
            }
            _brokerMonitorPaused = false;
            _brokerMonitorResumeSignal?.TrySetResult(true);
        }
    }

    private async Task WaitForBrokerMonitoringResumeAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_brokerMonitorGate)
        {
            wait = _brokerMonitorPaused ? _brokerMonitorResumeSignal?.Task : null;
        }
        if (wait is null)
        {
            return;
        }
        try
        {
            await wait.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ObserveBrokerState(BrokerStateSnapshot state, bool requeryOnRevisionChange)
    {
        var recoveredConnection = IsRetryVisible;
        var changed = _lastEpoch is not null &&
                      (!_lastEpoch.Equals(state.Epoch, StringComparison.Ordinal) || _lastRevision != state.Revision);
        var becameQueryable = _lastPhase == BrokerRuntimePhase.Starting && state.Phase != BrokerRuntimePhase.Starting;
        _lastEpoch = state.Epoch;
        _lastRevision = state.Revision;
        _lastPhase = state.Phase;
        if (state.CacheAvailable && state.FileCount > 0)
        {
            Interlocked.Exchange(ref _hasCommittedFileIndex, 1);
        }
        IsRetryVisible = false;
        StatusBrush = state.Capability.State == CapabilityState.Unavailable ? WarningBrush : NormalBrush;
        StatusText = ScopeStatus(state switch
        {
            { CacheAvailable: true, BackgroundUpdating: true } => $"共 {state.FileCount:N0} 个文件",
            { CacheAvailable: false } => "首次建立索引 · 可继续输入，发现文件后会自动更新",
            { Phase: BrokerRuntimePhase.Ready, LastUpdatedUtc: { } updated } =>
                $"索引就绪 · {state.FileCount:N0} 个文件 · 更新于 {updated.ToLocalTime():HH:mm:ss}",
            { Phase: BrokerRuntimePhase.Ready } => $"索引就绪 · {state.FileCount:N0} 个文件",
            _ => NormalizeCapabilityMessage(state.Capability),
        });
        if (requeryOnRevisionChange && (recoveredConnection || changed || becameQueryable))
        {
            ScheduleRevisionRefresh();
        }
    }

    private void ScheduleRevisionRefresh()
    {
        _revisionCancellation?.Cancel();
        _revisionCancellation?.Dispose();
        _revisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _revisionCancellation.Token;
        _ = RefreshAfterRevisionDelayAsync(token);
    }

    private async Task RefreshAfterRevisionDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Capture the interactive query generation before waiting. If the
            // user types while we are coalescing metadata commits, their new
            // query is authoritative and this low-priority refresh must not
            // cancel or replace it.
            var queryVersion = Volatile.Read(ref _searchVersion);
            await Task.Delay(RevisionRefreshDebounceDelay, cancellationToken);
            await WaitForInteractiveQueryToBecomeQuietAsync(cancellationToken);
            await WaitForRevisionRefreshSlotAsync(cancellationToken);
            if (queryVersion != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            if (IsBusy)
            {
                // Do not cancel an interactive read just because metadata has
                // committed. Re-arm one coalesced refresh so the newest
                // revision is still reflected once that read becomes idle.
                await Task.Delay(RevisionRefreshDebounceDelay, cancellationToken);
                if (queryVersion == Volatile.Read(ref _searchVersion) &&
                    IsCurrentRevisionRefresh(cancellationToken))
                {
                    ScheduleRevisionRefresh();
                }
                return;
            }

            if (queryVersion == Volatile.Read(ref _searchVersion))
            {
                Interlocked.Exchange(ref _lastRevisionRefreshTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                await ScheduleSearchAsync(
                    immediate: true,
                    requestedOffset: _resultBaseOffset,
                    requestedLimit: Results.Count > 0 ? Results.Count : SearchPageSize,
                    preserveExistingResults: true,
                    isInteractive: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForInteractiveQueryToBecomeQuietAsync(CancellationToken cancellationToken)
    {
        var timestamp = Volatile.Read(ref _lastInteractiveSearchTimestamp);
        if (timestamp == 0)
        {
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timestamp);
        var remaining = RevisionRefreshInputQuietDelay - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private bool IsCurrentRevisionRefresh(CancellationToken cancellationToken) =>
        _revisionCancellation is { IsCancellationRequested: false } source &&
        source.Token == cancellationToken;

    private async Task WaitForRevisionRefreshSlotAsync(CancellationToken cancellationToken)
    {
        var timestamp = Volatile.Read(ref _lastRevisionRefreshTimestamp);
        if (timestamp == 0)
        {
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timestamp);
        var remaining = RevisionRefreshMinimumInterval - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private async Task RetryUnavailableCacheAsync(long version, int offset, int pageSize)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
            if (_disposed || version != Volatile.Read(ref _searchVersion) || IsBusy)
            {
                return;
            }

            await ScheduleSearchAsync(
                immediate: true,
                requestedOffset: offset,
                requestedLimit: pageSize,
                preserveExistingResults: true,
                isInteractive: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void RetryConnection()
    {
        if (_searchClient is IBrokerStateClient stateClient)
        {
            stateClient.RetryConnection();
        }
        IsRetryVisible = false;
        StatusText = "正在重试连接搜索服务…";
        StatusBrush = WarningBrush;
        var preserveCurrentWindow = Results.Count > 0;
        _ = ScheduleSearchAsync(
            immediate: true,
            requestedOffset: preserveCurrentWindow ? _resultBaseOffset : null,
            requestedLimit: preserveCurrentWindow ? Results.Count : null,
            preserveExistingResults: preserveCurrentWindow);
    }

    private async Task RefreshIndexAsync()
    {
        if (_searchClient is not IRefreshableSearchClient refreshable)
        {
            return;
        }
        StatusText = Results.Count == 0 ? "正在准备文件列表…" : $"共 {Results.Count:N0} 个文件";
        StatusBrush = NormalBrush;
        try
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var capability = await refreshable.RefreshAsync(_refreshCancellation.Token);
            StatusText = NormalizeCapabilityMessage(capability);
            StatusBrush = capability.State == CapabilityState.Available ? NormalBrush : WarningBrush;
            await ScheduleSearchAsync(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "刷新已取消。";
        }
        catch (Exception)
        {
            StatusText = "刷新失败，已保留当前索引，请稍后重试。";
            StatusBrush = ErrorBrush;
            IsRetryVisible = true;
        }
    }

    private void TogglePause()
    {
        if (_searchClient is not IRuntimeControlClient runtime)
        {
            return;
        }
        // Compatibility-only API for older in-process callers. RC18 exposes no
        // pause control and always resumes the low-resource background pipeline.
        runtime.SetBackgroundPaused(false);
        IsBackgroundPaused = false;
        _settings = _settings with { BackgroundPaused = false };
    }

    private async Task PersistSettingsInBackgroundAsync(
        AppSettings snapshot,
        long userStateGeneration)
    {
        SettingsPersistenceResult result;
        try
        {
            result = await _settingsPersistence.QueueAsync(snapshot, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (_disposed || result.Superseded || UserStateGeneration != userStateGeneration) return;
        if (result.Error is null) return;
        ReportSettingsPersistenceFailure(result.Error);
    }

    private void ReplaceColumnEditor(IEnumerable<ResultColumnLayout> layouts)
    {
        foreach (var item in ColumnLayouts)
        {
            item.PropertyChanged -= ColumnEditorOnPropertyChanged;
        }
        ColumnLayouts.Clear();
        foreach (var layout in ResultColumnDefaults.Normalize(layouts))
        {
            var item = new ColumnLayoutItem(layout);
            item.PropertyChanged += ColumnEditorOnPropertyChanged;
            ColumnLayouts.Add(item);
        }
    }

    private void ObserveColumnEditors()
    {
        foreach (var item in ColumnLayouts)
        {
            item.PropertyChanged += ColumnEditorOnPropertyChanged;
        }
    }

    private void ColumnEditorOnPropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkUserStateChanged();

    private void MarkUserStateChanged()
    {
        if (!_applyingLoadedSettings)
        {
            Interlocked.Increment(ref _userStateGeneration);
        }
    }

    private void RaiseScopeChanged()
    {
        RaisePropertyChanged(nameof(CurrentScope));
        RaisePropertyChanged(nameof(IsFolderScope));
        RaisePropertyChanged(nameof(ScopeBannerVisibility));
        RaisePropertyChanged(nameof(ScopeSummary));
    }

    private string ScopeStatus(string status) =>
        IsFolderScope ? $"当前文件夹范围 · {status}" : status;

    internal static string NormalizeCapabilityMessage(RuntimeCapability capability) => capability.Code switch
    {
        "INDEX_READY" or "INDEX_CACHE_READY" => "索引服务正常。",
        "INDEX_EMPTY" or "INDEX_INITIALIZING" => "首次建立索引 · 可继续输入",
        "INDEX_CACHE_STALE" => "文件列表正在更新",
        "DISCOVERY_PARTIAL" => "部分文件夹暂时无法读取，已保留原有索引，其余文件夹已更新。",
        "DISCOVERY_SNAPSHOT_INCOMPLETE" => "部分搜索位置当前不可用，已保留已有索引。",
        "DISCOVERY_QUERY_FAILED" or "DISCOVERY_LOOP_FAILED" => "文件索引本轮未完成，已有索引仍可搜索。",
        "BUILTIN_DISCOVERY_KNOWN_ROOT_OFFLINE" => "部分已索引磁盘当前不可用，已保留已有索引。",
        "BUILTIN_DISCOVERY_NO_ONLINE_ROOT" => "当前没有可访问的搜索位置，已有索引仍可搜索。",
        "BUILTIN_DISCOVERY_PROBE_FAILED" => "暂时无法检查搜索位置，已有索引仍可搜索。",
        "METADATA_INITIALIZING" => "属性索引正在初始化，已有缓存仍可搜索。",
        "METADATA_WORKER_UNAVAILABLE" or "METADATA_WORKER_RECOVERING" =>
            "属性索引暂时不可用，文件名与已有属性仍可搜索。",
        "BROKER_RECONNECTING" => "搜索服务正在重连；保留最近结果。",
        "BROKER_DRAINING" => "旧版搜索服务正在退出；保留本地缓存并自动重连。",
        "BROKER_CONNECT_TIMEOUT" => "搜索服务管道暂不可用；保留本地缓存并自动重连。",
        "BROKER_RESPONSE_TIMEOUT" => "搜索服务已连接但未及时响应；保留本地缓存并自动重连。",
        "BROKER_STARTUP_FAILED" => "搜索服务启动失败；请查看本地诊断记录。",
        "LOCAL_INDEX_UNAVAILABLE" => "本地缓存正在迁移或暂时被占用；已保留当前结果，后台将自动重试。",
        _ => ContainsChinese(capability.ChineseMessage)
            ? capability.ChineseMessage
            : capability.State == CapabilityState.Unavailable
                ? "索引服务暂时不可用，已有索引仍可搜索。"
                : "索引状态正在更新，已有索引仍可搜索。",
    };

    private static bool ContainsChinese(string? text) =>
        text?.Any(static character => character is >= '\u4e00' and <= '\u9fff') == true;

    private static bool IsBrokerFailureCapability(string? code) =>
        code?.StartsWith("BROKER_", StringComparison.Ordinal) == true;

    private static bool IsTransientBrokerCapability(string? code) =>
        code is "BROKER_RECONNECTING" or
            "BROKER_DRAINING" or
            "BROKER_CONNECT_TIMEOUT" or
            "BROKER_RESPONSE_TIMEOUT" or
            "BROKER_DISCONNECTED";

    private bool RunFileAction(Action action, string successMessage)
    {
        try
        {
            action();
            StatusText = successMessage;
            StatusBrush = NormalBrush;
            return true;
        }
        catch (Exception exception)
        {
            ReportFileActionFailure(exception.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResumeBrokerMonitoring();
        _lifetime.Cancel();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _countCancellation?.Cancel();
        _countCancellation?.Dispose();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _revisionCancellation?.Cancel();
        _revisionCancellation?.Dispose();
        _settingsPersistence.Dispose();
        _settingsDiagnostics.Dispose();
        if (_searchClient is IDisposable searchClientLifetime)
        {
            searchClientLifetime.Dispose();
        }
        foreach (var item in ColumnLayouts)
        {
            item.PropertyChanged -= ColumnEditorOnPropertyChanged;
        }
        _lifetime.Dispose();
    }
}
