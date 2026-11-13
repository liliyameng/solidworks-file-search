using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using SwSearch.Contracts;
using SwSearch.WebUi.Security;

namespace SwSearch.App;

/// <summary>
/// The deliberately small command surface between the packaged web UI and the desktop view model.
/// Paths supplied by the page are never trusted: file actions must resolve to an item in the current
/// result page before they can reach <see cref="MainViewModel"/>.
/// </summary>
internal sealed class DesktopWebBridge : IDisposable
{
    internal const int ColumnPersistenceDebounceMilliseconds = 450;

    private static readonly IReadOnlySet<string> AllowedCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "ready",
        "search",
        "mode",
        "loadMore",
        "open",
        "reveal",
        "copy",
        "viewProperties",
        "preferences.open",
        "preferences.save",
        "preferences.close",
        "columnWidth",
        "retry",
        "contextAction",
        "openResult",
        "fileDrag.arm",
        "sort",
        "restoreScope",
        "performance.firstPaint",
    };

    private readonly MainViewModel _viewModel;
    private readonly IUserPreferencesRepository _preferencesRepository;
    private readonly System.Windows.Threading.DispatcherTimer _publishTimer;
    private CancellationTokenSource? _columnPersistenceCancellation;
    private CancellationTokenSource? _preferencesOperationCancellation;
    private AppSettings? _pendingColumnSettings;
    private long _preferencesGeneration;
    private bool _preferencesOpen;
    private bool _pageReady;
    private bool _disposed;
    private PendingSearchPerformance? _pendingSearchPerformance;
    private CompletedSearchPerformance? _completedSearchPerformance;
    private bool _searchPerformanceReady;
    private string? _publishedResultKey;
    private QueryUiState? _queryUi;
    private long _nextQueryUiId;
    private bool _modeSearchPending;
    internal long PublishedResultDtoCount { get; private set; }

    public DesktopWebBridge(
        MainViewModel viewModel,
        IUserPreferencesRepository preferencesRepository)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _preferencesRepository = preferencesRepository ??
            throw new ArgumentNullException(nameof(preferencesRepository));
        _publishTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(8),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => PublishScheduledState(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _publishTimer.Stop();
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.Results.CollectionChanged += ResultsOnCollectionChanged;
    }

    public event Action<object>? StateReady;

    /// <summary>
    /// Preferences-only updates for the standalone preferences WebView. The
    /// full bridge state (which can carry the bounded result viewport) is never
    /// addressed to that window, so it must not be serialized for it.
    /// </summary>
    public event Action<object>? PreferencesStateReady;

    /// <summary>
    /// Raised only after a page-supplied arm request has been bound to the
    /// current broker-owned result window. It only arms a host gesture; the
    /// composition host independently requires the same held native gesture
    /// and the Windows drag threshold before it can start OLE.
    /// </summary>
    internal event Action<FileDragArmRequest>? FileDragArmed;

    internal static bool IsAllowedCommand(string? command) =>
        command is not null && AllowedCommands.Contains(command);

    public void HandleMessage(string command, JsonElement payload)
    {
        if (_disposed || !IsAllowedCommand(command))
        {
            return;
        }

        try
        {
            switch (command)
            {
                case "ready":
                    _pageReady = true;
                    _publishedResultKey = null;
                    PublishState();
                    break;
                case "search":
                    if (TryReadString(payload, "query", 512, out var query))
                    {
                        var startsNewQuery = !string.Equals(_viewModel.SearchText, query, StringComparison.Ordinal) ||
                                             _modeSearchPending;
                        if (TryReadInt(payload, "clientSequence", out var clientSequence) &&
                            clientSequence >= 0 &&
                            TryReadInt(payload, "inputDelayMs", out var inputDelayMilliseconds) &&
                            inputDelayMilliseconds >= 0)
                        {
                            if (startsNewQuery)
                            {
                                _pendingSearchPerformance = new PendingSearchPerformance(
                                    clientSequence, inputDelayMilliseconds, System.Diagnostics.Stopwatch.GetTimestamp());
                                _completedSearchPerformance = null;
                                _searchPerformanceReady = false;
                                _queryUi = new QueryUiState(
                                    Interlocked.Increment(ref _nextQueryUiId),
                                    clientSequence,
                                    _viewModel.IsBusy ? QueryUiPhase.Running : QueryUiPhase.Debouncing);
                            }
                        }
                        _modeSearchPending = false;
                        _viewModel.ApplySearchTextFromPage(query);
                        SchedulePublish();
                    }
                    break;
                case "mode":
                    ApplyMode(payload);
                    break;
                case "loadMore":
                    if (TryReadInt(payload, "offset", out var offset) &&
                        TryReadInt(payload, "limit", out var limit))
                    {
                        _viewModel.RequestResultWindow(offset, limit);
                    }
                    break;
                case "open":
                    RejectUnboundResultAction();
                    break;
                case "openResult":
                    RejectUnboundResultAction();
                    break;
                case "fileDrag.arm":
                    ArmShellFileDrag(payload);
                    break;
                case "reveal":
                    RejectUnboundResultAction();
                    break;
                case "copy":
                    RejectUnboundResultAction();
                    break;
                case "viewProperties":
                    RejectUnboundResultAction();
                    break;
                case "preferences.open":
                    OpenPreferences();
                    break;
                case "preferences.save":
                    if (TryCreatePreferencesSaveRequest(payload, out var request))
                    {
                        SavePreferences(request);
                    }
                    break;
                case "preferences.close":
                    ClosePreferences();
                    break;
                case "columnWidth":
                    ApplyColumnWidth(payload);
                    break;
                case "retry":
                    _viewModel.RetryConnectionCommand.Execute(null);
                    break;
                case "contextAction":
                    ApplyContextAction(payload);
                    break;
                case "sort":
                    ApplySort(payload);
                    break;
                case "restoreScope":
                    _viewModel.RestoreAllMachine();
                    break;
                case "performance.firstPaint":
                    RecordFirstPaint(payload);
                    break;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or JsonException)
        {
            // Invalid page messages are rejected without crossing into the UI exception pipeline.
        }
    }

    public void PublishState()
    {
        if (!_pageReady || _disposed)
        {
            return;
        }

        if (_searchPerformanceReady && _pendingSearchPerformance is { } readyTiming)
        {
            _completedSearchPerformance = new CompletedSearchPerformance(
                readyTiming.ClientSequence,
                readyTiming.InputDelayMilliseconds,
                Math.Max(0, (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(readyTiming.StartTimestamp).TotalMilliseconds)));
            _pendingSearchPerformance = null;
            _searchPerformanceReady = false;
        }

        var resultKey = string.Join('\u001f',
            _viewModel.CurrentResultSetId,
            _viewModel.SearchText,
            ModeName(_viewModel.SelectedMode.Value),
            _viewModel.CurrentScope.Kind.ToString(),
            _viewModel.CurrentScope.RootPath ?? string.Empty,
            _viewModel.CurrentSortColumn ?? string.Empty,
            _viewModel.CurrentSortDescending,
            _viewModel.ResultBaseOffset,
            _viewModel.Results.Count);
        var resultCount = _viewModel.Results.Count;
        var sameResultSet = string.Equals(resultKey, _publishedResultKey, StringComparison.Ordinal);
        // The desktop bridge publishes one bounded viewport window. Property,
        // timer and Broker state changes carry no result DTOs at all.
        var resultBatch = sameResultSet
            ? Array.Empty<SearchResultItem>()
            : _viewModel.Results.Take(500).ToArray();
        var resultsMode = sameResultSet ? "none" : "replace";
        var clientResultCount = _viewModel.TotalResultCount >= 0
            ? _viewModel.TotalResultCount
            : _viewModel.ResultBaseOffset + resultCount;

        var payload = new
        {
            query = _viewModel.SearchText,
            scope = _viewModel.IsFolderScope
                ? new { folder = true, kind = "folder", rootPath = _viewModel.CurrentScope.RootPath }
                : new { folder = false, kind = "all", rootPath = (string?)null },
            version = typeof(DesktopWebBridge).Assembly.GetName().Version?.ToString(3) ?? "1.2.49",
            mode = ModeName(_viewModel.SelectedMode.Value),
            busy = _viewModel.IsBusy,
            canRetry = _viewModel.IsRetryVisible,
            hasMore = _viewModel.HasMoreResults,
            resultBaseOffset = _viewModel.ResultBaseOffset,
            loadedResultCount = _viewModel.ResultBaseOffset + resultCount,
            totalResultCount = clientResultCount,
            resultSummary = _viewModel.ResultSummary,
            status = _viewModel.StatusText,
            runtime = _viewModel.RuntimeSummary,
            indexProgress = new
            {
                filePercent = _viewModel.IndexProgress.FilePercent,
                cachedCoveragePercent = _viewModel.IndexProgress.CachedCoveragePercent,
                currentMetadataPercent = _viewModel.IndexProgress.CurrentMetadataPercent,
            },
            sort = _viewModel.CurrentSortColumn is null ? null : new
            {
                column = _viewModel.CurrentSortColumn,
                direction = _viewModel.CurrentSortDescending ? "desc" : "asc",
            },
            columns = CreateFixedColumns(),
            resultsMode,
            resultSetId = _viewModel.CurrentResultSetId,
            resultRevision = _viewModel.CurrentPageRevision,
            resultStart = _viewModel.ResultBaseOffset,
            results = resultBatch.Select(static item => new
            {
                fullPath = item.FullPath,
                fileName = item.FileName,
                kind = item.KindLabel,
                matchedProperty = item.MatchedPropertyLabel,
                matchedValue = item.MatchedValueLabel,
                source = item.SourceLabel,
                directory = item.DirectoryPath,
            }).ToArray(),
            performance = _completedSearchPerformance is { } timing ? new
            {
                clientSequence = timing.ClientSequence,
                inputDelayMs = timing.InputDelayMilliseconds,
                hostMs = timing.HostMilliseconds,
            } : null,
            queryProgress = _queryUi is { } queryUi ? new
            {
                queryUiId = queryUi.QueryUiId,
                clientSequence = queryUi.ClientSequence,
                phase = QueryUiPhaseName(queryUi.Phase),
            } : null,
        };
        _publishedResultKey = resultKey;
        PublishedResultDtoCount += resultBatch.Length;
        StateReady?.Invoke(payload);
    }

    public void RequestFocus() => StateReady?.Invoke(new { focusSearch = true });

    internal void OpenPreferences()
    {
        if (_disposed)
        {
            return;
        }
        _preferencesOpen = true;
        var operation = BeginPreferencesOperation();
        PublishPreferences(new
        {
            isOpen = true,
            busy = true,
            error = (string?)null,
            status = "正在读取偏好设置…",
        });
        _ = LoadPreferencesAsync(operation.Generation, operation.Token, "偏好设置已加载。");
    }

    private void ClosePreferences()
    {
        if (_disposed)
        {
            return;
        }
        _preferencesOpen = false;
        Interlocked.Increment(ref _preferencesGeneration);
        _preferencesOperationCancellation?.Cancel();
        _preferencesOperationCancellation?.Dispose();
        _preferencesOperationCancellation = null;
        PublishPreferences(new
        {
            isOpen = false,
            busy = false,
            error = (string?)null,
            status = string.Empty,
        });
    }

    private void SavePreferences(UserPreferencesSaveRequest request)
    {
        if (_disposed || !_preferencesOpen)
        {
            return;
        }
        var operation = BeginPreferencesOperation();
        PublishPreferences(new
        {
            isOpen = true,
            busy = true,
            error = (string?)null,
            status = "正在保存偏好设置…",
        });
        _ = SavePreferencesAsync(request, operation.Generation, operation.Token);
    }

    private async Task SavePreferencesAsync(
        UserPreferencesSaveRequest request,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var saveResult = await _preferencesRepository.SaveAsync(request, cancellationToken);
            if (!IsCurrentPreferencesOperation(generation, cancellationToken))
            {
                return;
            }
            if (saveResult.PreferencesSaved)
            {
                var persisted = _viewModel.UpdatePropertyDisplayOrderInMemory(
                    saveResult.NormalizedRequest.PropertyDisplayOrder);
                _viewModel.ApplyPersistedSettings(persisted);
            }
            var snapshot = await _preferencesRepository.LoadAsync(cancellationToken);
            if (!IsCurrentPreferencesOperation(generation, cancellationToken))
            {
                return;
            }
            var failedIntegrations = saveResult.Integrations
                .Where(static result => !result.Succeeded)
                .ToArray();
            var error = !saveResult.PreferencesSaved
                ? saveResult.PersistenceError
                : failedIntegrations.Length == 0
                    ? snapshot.ConfigurationError
                    : string.Join("；", failedIntegrations.Select(static result => result.Message));
            var status = !saveResult.PreferencesSaved
                ? "本地偏好未保存；请按具体原因处理后重试。"
                : failedIntegrations.Length == 0
                    ? "偏好设置已保存。"
                    : "本地偏好已保存；部分系统集成未完成。";
            PublishPreferencesSnapshot(snapshot, status, error, saveResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishPreferencesFailure(generation, cancellationToken, exception);
        }
    }

    private async Task LoadPreferencesAsync(
        long generation,
        CancellationToken cancellationToken,
        string successStatus)
    {
        try
        {
            var snapshot = await _preferencesRepository.LoadAsync(cancellationToken);
            if (!IsCurrentPreferencesOperation(generation, cancellationToken))
            {
                return;
            }
            PublishPreferencesSnapshot(
                snapshot,
                snapshot.ConfigurationError is null
                    ? successStatus
                    : "部分配置需要修正；面板仍可继续使用。",
                snapshot.ConfigurationError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishPreferencesFailure(generation, cancellationToken, exception);
        }
    }

    private void PublishPreferencesSnapshot(
        UserPreferencesSnapshot snapshot,
        string status,
        string? error,
        UserPreferencesSaveResult? saveResult = null)
    {
        PublishPreferences(new
        {
            isOpen = true,
            busy = false,
            error,
            status,
            taskpane = snapshot.Taskpane.IsEnabled,
            folderMenu = snapshot.FolderMenu.IsEnabled,
            filePropertiesMenu = snapshot.FilePropertiesMenu?.IsEnabled ?? false,
            startup = snapshot.Startup.IsEnabled,
            propertyDisplayOrder = snapshot.PropertyDisplayOrder ?? Array.Empty<string>(),
            integrations = new
            {
                taskpane = IntegrationPayload(snapshot.Taskpane),
                folderMenu = IntegrationPayload(snapshot.FolderMenu),
                filePropertiesMenu = snapshot.FilePropertiesMenu is null ? null : IntegrationPayload(snapshot.FilePropertiesMenu),
                startup = IntegrationPayload(snapshot.Startup),
            },
            saveResults = SaveResultsPayload(saveResult),
        });
    }

    private (long Generation, CancellationToken Token) BeginPreferencesOperation()
    {
        _preferencesOperationCancellation?.Cancel();
        _preferencesOperationCancellation?.Dispose();
        _preferencesOperationCancellation = new CancellationTokenSource();
        return (Interlocked.Increment(ref _preferencesGeneration), _preferencesOperationCancellation.Token);
    }

    private bool IsCurrentPreferencesOperation(long generation, CancellationToken cancellationToken) =>
        !_disposed && _preferencesOpen && !cancellationToken.IsCancellationRequested &&
        generation == Volatile.Read(ref _preferencesGeneration);

    private void PublishPreferencesFailure(
        long generation,
        CancellationToken cancellationToken,
        Exception exception)
    {
        if (!IsCurrentPreferencesOperation(generation, cancellationToken))
        {
            return;
        }
        PublishPreferences(new
        {
            isOpen = true,
            busy = false,
            error = DescribePreferencesError(exception),
            status = "偏好设置操作失败；主搜索仍可正常使用。",
        });
    }

    private void PublishPreferences(object preferences)
    {
        if (_disposed || !_pageReady)
        {
            return;
        }
        // The main toolbar keeps consuming { preferences } through StateReady;
        // the standalone preferences window gets the small payload through the
        // dedicated event instead of the full state snapshot.
        StateReady?.Invoke(new { preferences });
        PreferencesStateReady?.Invoke(preferences);
    }

    private static object IntegrationPayload(UserPreferenceIntegrationState state) => new
    {
        available = state.IsAvailable,
        enabled = state.IsEnabled,
        message = state.Message,
        requiresSolidWorksRestart = state.RequiresSolidWorksRestart,
    };

    private static object? SaveResultsPayload(UserPreferencesSaveResult? result)
    {
        if (result is null)
        {
            return null;
        }
        object Item(UserPreferenceIntegrationTarget target)
        {
            var item = result.Integrations.FirstOrDefault(candidate => candidate.Target == target);
            return item is null
                ? new { attempted = false, succeeded = false, message = "未执行。" }
                : new
                {
                    attempted = true,
                    succeeded = item.Succeeded,
                    requestedEnabled = item.RequestedEnabled,
                    message = item.Message,
                };
        }
        return new
        {
            persistence = new
            {
                succeeded = result.PreferencesSaved,
                message = result.PersistenceError ?? "本地偏好已原子保存。",
            },
            taskpane = Item(UserPreferenceIntegrationTarget.Taskpane),
            folderMenu = Item(UserPreferenceIntegrationTarget.FolderMenu),
            filePropertiesMenu = Item(UserPreferenceIntegrationTarget.FilePropertiesMenu),
            startup = Item(UserPreferenceIntegrationTarget.Startup),
        };
    }

    private static string DescribePreferencesError(Exception exception) => exception switch
    {
        TimeoutException => "操作超时，请关闭面板后重试。",
        UnauthorizedAccessException => "当前用户没有修改该项设置的权限。",
        InvalidDataException => exception.Message,
        ArgumentException => exception.Message,
        IOException => "设置文件暂时无法读写，请稍后重试。",
        _ => "偏好设置未能完成操作，请检查输入后重试。",
    };

    public async Task FlushColumnPersistenceAsync(TimeSpan timeout)
    {
        if (_pendingColumnSettings is null)
        {
            return;
        }

        _columnPersistenceCancellation?.Cancel();
        _columnPersistenceCancellation?.Dispose();
        _columnPersistenceCancellation = null;
        var snapshot = _pendingColumnSettings;
        _pendingColumnSettings = null;
        using var deadline = new CancellationTokenSource(timeout);
        var result = await _viewModel.PersistSettingsSnapshotAsync(snapshot, deadline.Token);
        if (result.Error is not null)
        {
            _viewModel.ReportSettingsPersistenceFailure(result.Error);
        }
    }

    private void ApplyContextAction(JsonElement payload)
    {
        if (!ResultActionProtocol.TryParse(payload, out var request, out _))
        {
            _viewModel.ReportFileActionFailure("结果操作命令无效。");
            return;
        }
        switch (request.Action)
        {
            case ResultActionProtocol.Open:
                WithCurrentResult(request, _viewModel.Open);
                break;
            case ResultActionProtocol.Reveal:
                WithCurrentResult(request, _viewModel.Reveal);
                break;
            case ResultActionProtocol.CopyPath:
                WithCurrentResult(request, _viewModel.CopyPath);
                break;
            case ResultActionProtocol.ViewProperties:
                WithCurrentResult(request, _viewModel.ViewProperties);
                break;
        }
    }

    private void ArmShellFileDrag(JsonElement payload)
    {
        if (!FileDragProtocol.TryParse(payload, out var request, out _))
        {
            _viewModel.ReportFileActionFailure("结果路径无效，请重新选择。");
            return;
        }

        if (request.ResultSetId != _viewModel.CurrentResultSetId ||
            request.ResultRevision != _viewModel.CurrentPageRevision)
        {
            _viewModel.ReportFileActionFailure("结果已刷新，请重新拖动当前行。");
            return;
        }

        // The browser is never an authority for a pathname. Resolve its exact
        // selection to the broker-owned current row, then use that row's
        // original full path without normalizing or otherwise rewriting it.
        var result = _viewModel.Results.FirstOrDefault(item =>
            string.Equals(item.FullPath, request.Path, StringComparison.OrdinalIgnoreCase));
        if (result is null)
        {
            _viewModel.ReportFileActionFailure("该结果已刷新或不再可用，请重新选择当前行。");
            return;
        }

        if (!WebMessageValidators.IsSafeSolidWorksPath(result.FullPath))
        {
            _viewModel.ReportFileActionFailure("该结果不是可拖放的 SOLIDWORKS 零件或装配体。");
            return;
        }

        var arm = FileDragArmed;
        if (arm is null)
        {
            _viewModel.ReportFileActionFailure("文件拖放暂不可用，请稍后重试。");
            return;
        }

        _viewModel.SelectedResult = result;
        arm(request with
        {
            ResultSetId = _viewModel.CurrentResultSetId,
            ResultRevision = _viewModel.CurrentPageRevision,
            Path = result.FullPath,
        });
    }

    private void ApplyMode(JsonElement payload)
    {
        if (!TryReadString(payload, "value", 20, out var value) &&
            !TryReadString(payload, "mode", 20, out value))
        {
            return;
        }

        var mode = value switch
        {
            "all" => SearchMode.All,
            "name" => SearchMode.FileName,
            "fileName" => SearchMode.FileName,
            "property" => SearchMode.Property,
            _ => (SearchMode?)null,
        };
        if (mode is { } requested)
        {
            _modeSearchPending |= _viewModel.SelectedMode.Value != requested;
            _viewModel.SelectedMode = _viewModel.SearchModes.First(option => option.Value == requested);
        }
    }

    private void ApplySort(JsonElement payload)
    {
        if (!TryReadString(payload, "column", 40, out var column) ||
            !TryReadString(payload, "direction", 8, out var direction) ||
            direction is not ("asc" or "desc" or "none"))
        {
            return;
        }

        ApplyNativeSort(column, direction == "none" ? null : direction);
    }

    /// <summary>
    /// Shared sort ingress for the WebView grid and a future native Shell
    /// results host. <paramref name="direction"/> is <c>"asc"</c>,
    /// <c>"desc"</c>, or <see langword="null"/> to restore Broker relevance
    /// ordering. The native host must pass the same state rather than sorting
    /// a page locally, otherwise paged result order would diverge.
    /// </summary>
    internal void ApplyNativeSort(string column, string? direction)
    {
        if (!ResultColumnDefaults.All.Any(item => string.Equals(item.Id, column, StringComparison.Ordinal)))
        {
            return;
        }

        if (direction is null)
        {
            _viewModel.ClearSort();
            return;
        }

        if (direction is not ("asc" or "desc"))
        {
            return;
        }

        _viewModel.ApplySort(column, string.Equals(direction, "desc", StringComparison.Ordinal));
    }

    /// <summary>
    /// Native Explorer/Shell toolbar ingress.  The small toolbar WebView is
    /// intentionally not allowed to paint a drop-down over the Shell HWND, so
    /// its mode selector lives in a native ToolStrip and uses the same trusted
    /// view-model path as the web command.
    /// </summary>
    internal void ApplyNativeSearchMode(SearchMode mode)
    {
        if (_disposed || _viewModel.SelectedMode.Value == mode)
        {
            return;
        }

        var option = _viewModel.SearchModes.FirstOrDefault(item => item.Value == mode);
        if (option is not null)
        {
            _modeSearchPending = true;
            _viewModel.SelectedMode = option;
        }
    }

    private void ApplyColumnWidth(JsonElement payload)
    {
        if ((!TryReadString(payload, "id", 40, out var id) &&
             !TryReadString(payload, "column", 40, out id)) ||
            !TryReadDouble(payload, "width", out var requestedWidth) ||
            !double.IsFinite(requestedWidth) ||
            !ResultColumnDefaults.All.Any(column => string.Equals(column.Id, id, StringComparison.Ordinal)))
        {
            return;
        }

        var widths = _viewModel.ColumnLayouts.ToDictionary(static item => item.Id, static item => item.Width, StringComparer.Ordinal);
        widths[id] = Math.Clamp(requestedWidth, ResultColumnDefaults.MinimumWidth, ResultColumnDefaults.MaximumWidth);
        var layouts = ResultColumnDefaults.All.Select(column => column with
        {
            DisplayName = column.DisplayName,
            Width = widths.GetValueOrDefault(column.Id, column.Width),
            Order = column.Order,
            Visible = true,
        }).ToArray();
        _pendingColumnSettings = _viewModel.UpdateColumnLayoutInMemory(layouts);
        ScheduleColumnPersistence();
        SchedulePublish();
    }

    private void ScheduleColumnPersistence()
    {
        _columnPersistenceCancellation?.Cancel();
        _columnPersistenceCancellation?.Dispose();
        _columnPersistenceCancellation = new CancellationTokenSource();
        _ = PersistColumnsAfterDelayAsync(_columnPersistenceCancellation.Token);
    }

    private async Task PersistColumnsAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ColumnPersistenceDebounceMilliseconds, cancellationToken);
            var snapshot = _pendingColumnSettings;
            if (snapshot is null)
            {
                return;
            }
            _pendingColumnSettings = null;
            var result = await _viewModel.PersistSettingsSnapshotAsync(snapshot, cancellationToken);
            if (result.Error is not null)
            {
                _viewModel.ReportSettingsPersistenceFailure(result.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _viewModel.ReportSettingsPersistenceFailure(exception);
        }
    }

    private object[] CreateFixedColumns()
    {
        var widths = _viewModel.ColumnLayouts.ToDictionary(static item => item.Id, static item => item.Width, StringComparer.Ordinal);
        return ResultColumnDefaults.All
            .Where(static column => column.Visible && column.Id is not "fullPath" and not "cacheStatus")
            .Select(column => (object)new
        {
            id = column.Id,
            name = column.DisplayName,
            width = widths.GetValueOrDefault(column.Id, column.Width),
        }).ToArray();
    }

    private void RejectUnboundResultAction() =>
        _viewModel.ReportFileActionFailure("结果操作缺少当前行标识，已拒绝执行。请重新选择当前行后重试。");

    private void WithCurrentResult(ResultActionRequest request, Func<SearchResultItem, bool> action)
    {
        // The page can retain a cached viewport while a newer window is already
        // active in the desktop model. Bind all four pieces of row identity
        // before using the broker-owned path, rather than looking up a path in a
        // stale filter, scope root, or process working directory.
        if (request.ResultSetId != _viewModel.CurrentResultSetId ||
            request.ResultRevision != _viewModel.CurrentPageRevision ||
            request.ResultStart != _viewModel.ResultBaseOffset)
        {
            _viewModel.ReportFileActionFailure("结果已刷新，无法确认当前文件。请重新选择当前行后重试。");
            return;
        }

        if ((uint)request.ResultIndex >= (uint)_viewModel.Results.Count)
        {
            _viewModel.ReportFileActionFailure("该结果不在当前结果窗口中，请重新选择当前行。");
            return;
        }

        var result = _viewModel.Results[request.ResultIndex];
        if (!string.Equals(result.FullPath, request.Path, StringComparison.Ordinal))
        {
            _viewModel.ReportFileActionFailure("结果路径与当前行不一致，已拒绝执行。请重新选择当前行。");
            return;
        }

        // Only the broker-owned current row reaches the file action. In
        // particular, do not pass request.Path through to Explorer.
        _viewModel.SelectedResult = result;
        _ = action(result);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy))
        {
            if (!_viewModel.IsBusy && _pendingSearchPerformance is not null)
            {
                _searchPerformanceReady = true;
            }

            if (_queryUi is { } queryUi && queryUi.Phase != QueryUiPhase.Idle)
            {
                _queryUi = queryUi with
                {
                    Phase = _viewModel.IsBusy ? QueryUiPhase.Running : QueryUiPhase.Idle,
                };
            }
        }
        SchedulePublish();
    }

    private void ResultsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SchedulePublish();

    private void SchedulePublish()
    {
        if (!_pageReady || _disposed)
        {
            return;
        }
        if (!_publishTimer.IsEnabled) _publishTimer.Start();
    }

    private void PublishScheduledState()
    {
        _publishTimer.Stop();
        PublishState();
    }

    private static string ModeName(SearchMode mode) => mode switch
    {
        SearchMode.FileName => "name",
        SearchMode.Property => "property",
        _ => "all",
    };

    private static string QueryUiPhaseName(QueryUiPhase phase) => phase switch
    {
        QueryUiPhase.Debouncing => "debouncing",
        QueryUiPhase.Running => "running",
        _ => "idle",
    };

    private static bool TryCreatePreferencesSaveRequest(
        JsonElement payload,
        out UserPreferencesSaveRequest request)
    {
        request = new UserPreferencesSaveRequest(
            false,
            false,
            false,
            PropertyCardSourcePreference.Automatic,
            string.Empty,
            Array.Empty<string>());
        if (!TryReadBoolean(payload, "taskpane", out var taskpane) ||
            !TryReadBoolean(payload, "folderMenu", out var folderMenu) ||
            !TryReadOptionalBoolean(payload, "filePropertiesMenu", false, out var filePropertiesMenu) ||
            !TryReadBoolean(payload, "startup", out var startup) ||
            !TryReadOptionalStringArray(
                payload,
                "propertyDisplayOrder",
                PropertyDisplayOrderDefaults.MaximumCount,
                PropertyDisplayOrderDefaults.MaximumNameLength,
                out var propertyDisplayOrder))
        {
            return false;
        }
        request = new UserPreferencesSaveRequest(
            taskpane,
            folderMenu,
            startup,
            PropertyCardSourcePreference.Automatic,
            string.Empty,
            Array.Empty<string>(),
            filePropertiesMenu,
            propertyDisplayOrder);
        return true;
    }

    private static bool TryReadOptionalStringArray(
        JsonElement payload,
        string name,
        int maximumCount,
        int maximumLength,
        out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var property))
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maximumCount)
        {
            return false;
        }
        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || (item.GetString()?.Length ?? 0) > maximumLength)
            {
                return false;
            }
            result.Add(item.GetString() ?? string.Empty);
        }
        values = result;
        return true;
    }

    private static bool TryReadString(JsonElement payload, string name, int maximumLength, out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return value.Length <= maximumLength;
    }

    private static bool TryReadBoolean(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var property))
        {
            return false;
        }
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadOptionalBoolean(
        JsonElement payload,
        string name,
        bool defaultValue,
        out bool value)
    {
        value = defaultValue;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (!payload.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadDouble(JsonElement payload, string name, out double value)
    {
        value = 0;
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private static bool TryReadInt(JsonElement payload, string name, out int value)
    {
        value = 0;
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private void RecordFirstPaint(JsonElement payload)
    {
        if (!TryReadInt(payload, "clientSequence", out var sequence) ||
            !TryReadInt(payload, "totalMs", out var totalMilliseconds) ||
            !TryReadInt(payload, "renderMs", out var renderMilliseconds) ||
            !TryReadInt(payload, "resultCount", out var resultCount) ||
            _completedSearchPerformance is not { } completed || completed.ClientSequence != sequence)
        {
            return;
        }
        SearchPerformanceDiagnostics.RecordFirstPaint(
            sequence,
            completed.InputDelayMilliseconds,
            completed.HostMilliseconds,
            renderMilliseconds,
            totalMilliseconds,
            resultCount);
    }

    private sealed record PendingSearchPerformance(int ClientSequence, int InputDelayMilliseconds, long StartTimestamp);
    private sealed record CompletedSearchPerformance(int ClientSequence, int InputDelayMilliseconds, int HostMilliseconds);
    private enum QueryUiPhase
    {
        Debouncing,
        Running,
        Idle,
    }

    private sealed record QueryUiState(long QueryUiId, int ClientSequence, QueryUiPhase Phase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _publishTimer.Stop();
        _columnPersistenceCancellation?.Cancel();
        _columnPersistenceCancellation?.Dispose();
        _columnPersistenceCancellation = null;
        _preferencesOperationCancellation?.Cancel();
        _preferencesOperationCancellation?.Dispose();
        _preferencesOperationCancellation = null;
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _viewModel.Results.CollectionChanged -= ResultsOnCollectionChanged;
    }
}
