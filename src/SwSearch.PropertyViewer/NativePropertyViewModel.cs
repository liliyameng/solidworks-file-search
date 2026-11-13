using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using SwSearch.Contracts;

namespace SwSearch.PropertyViewer;

public sealed record NativePropertyRow(string Name, string Value);

public enum NativePropertyMode
{
    Custom,
    Configuration,
}

public sealed class NativePropertyViewModel : ObservableObject
{
    private readonly PathValidationResult _validation;
    private readonly IViewerFileSystem _fileSystem;
    private readonly IPropertyDataClient _dataClient;
    private readonly Func<IReadOnlyList<string>> _propertyDisplayOrderLoader;
    private IReadOnlyList<string> _propertyDisplayOrder = Array.Empty<string>();
    private DocumentMetadata? _metadata;
    private int _selectedModeIndex;
    private string? _selectedConfiguration;
    private string _statusText;
    private bool _isBusy;

    public NativePropertyViewModel(
        PathValidationResult validation,
        IViewerFileSystem fileSystem,
        IPropertyDataClient dataClient,
        string? legacyDisplayPreference = null,
        Func<IReadOnlyList<string>>? propertyDisplayOrderLoader = null)
    {
        _validation = validation;
        _fileSystem = fileSystem;
        _dataClient = dataClient;
        _propertyDisplayOrderLoader = propertyDisplayOrderLoader ?? (() => Array.Empty<string>());
        _selectedModeIndex = ResolveInitialMode(legacyDisplayPreference) == NativePropertyMode.Configuration ? 1 : 0;
        FullPath = validation.FullPath ?? string.Empty;
        FileName = validation.IsValid ? Path.GetFileName(FullPath) : "无法打开文件";
        _statusText = validation.IsValid
            ? "暂无已提取属性，正在读取此文件"
            : validation.ErrorMessage ?? "文件参数无效。";
        _isBusy = validation.IsValid;
    }

    public string FileName { get; }
    public string FullPath { get; }
    public IReadOnlyList<string> ModeOptions { get; } = ["自定义属性", "配置属性"];
    public ObservableCollection<string> Configurations { get; } = [];
    public ObservableCollection<NativePropertyRow> Rows { get; } = [];

    public int SelectedModeIndex
    {
        get => _selectedModeIndex;
        set
        {
            var normalized = value == 1 ? 1 : 0;
            if (!SetProperty(ref _selectedModeIndex, normalized)) return;
            RaisePropertyChanged(nameof(SelectedMode));
            RaisePropertyChanged(nameof(ConfigurationSelectorVisibility));
            RebuildRows();
        }
    }

    public NativePropertyMode SelectedMode =>
        SelectedModeIndex == 1 ? NativePropertyMode.Configuration : NativePropertyMode.Custom;

    public string? SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (!SetProperty(ref _selectedConfiguration, value)) return;
            RebuildRows();
        }
    }

    public Visibility ConfigurationSelectorVisibility =>
        SelectedMode == NativePropertyMode.Configuration && Configurations.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaisePropertyChanged(nameof(BusyVisibility));
        }
    }

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    internal static NativePropertyMode ResolveInitialMode(string? legacyDisplayPreference)
    {
        _ = legacyDisplayPreference;
        return NativePropertyMode.Configuration;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        PropertyViewerPerformanceDiagnostics.Record("native-load-start", 0);
        if (!_validation.IsValid)
        {
            IsBusy = false;
            return;
        }

        try
        {
            var local = await Task.Run(async () =>
            {
                IReadOnlyList<string> displayOrder;
                try
                {
                    displayOrder = PropertyDisplayOrderReader.Normalize(_propertyDisplayOrderLoader());
                }
                catch
                {
                    displayOrder = Array.Empty<string>();
                }
                if (!_fileSystem.FileExists(FullPath)) throw new FileNotFoundException("文件不存在。", FullPath);
                _ = _fileSystem.GetSnapshot(FullPath);
                var cached = await _dataClient.TryGetCachedAsync(FullPath, cancellationToken).ConfigureAwait(false);
                return (Cached: cached, DisplayOrder: displayOrder);
            }, cancellationToken);

            _propertyDisplayOrder = local.DisplayOrder;
            if (local.Cached is not null)
            {
                ApplyMetadata(local.Cached);
                StatusText = "属性已显示 · 正在鲜读";
                IsBusy = false;
                PropertyViewerPerformanceDiagnostics.Record("native-cache-hit", clock.Elapsed.TotalMilliseconds);
                _ = RefreshFromBrokerAsync(cachedWasDisplayed: true, cancellationToken);
                return;
            }

            PropertyViewerPerformanceDiagnostics.Record("native-cache-miss", clock.Elapsed.TotalMilliseconds);
            StatusText = "暂无已提取属性，正在读取此文件";
            await RefreshFromBrokerAsync(cachedWasDisplayed: false, cancellationToken);
            PropertyViewerPerformanceDiagnostics.Record("native-broker-first-read", clock.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsBusy = false;
        }
        catch (FileNotFoundException)
        {
            StatusText = "文件不存在，可能已被移动或删除。";
            IsBusy = false;
        }
        catch (Exception exception)
        {
            StatusText = "暂时无法读取此文件的属性。";
            IsBusy = false;
            PropertyViewerPerformanceDiagnostics.Record(
                "native-load-error", clock.Elapsed.TotalMilliseconds, exception.GetType().Name);
        }
    }

    private async Task RefreshFromBrokerAsync(bool cachedWasDisplayed, CancellationToken cancellationToken)
    {
        try
        {
            var previousExtractionUtc = _metadata?.ExtractedAtUtc;
            var result = await _dataClient.TryRefreshFromBrokerAsync(FullPath, cancellationToken);
            if (result.Metadata is { } metadata)
            {
                // New Brokers provide an authoritative result.  The timestamp
                // comparison is retained only for old protocol-compatible
                // responses that predate MetadataRefreshStatus.
                var isFresh = result.Fresh ??
                              (!cachedWasDisplayed || previousExtractionUtc is null ||
                               metadata.ExtractedAtUtc > previousExtractionUtc.Value);
                if (isFresh)
                {
                    ApplyMetadata(metadata);
                    StatusText = cachedWasDisplayed ? "属性已更新（鲜读）" : "属性已读取（鲜读）";
                }
                else
                {
                    if (!cachedWasDisplayed && result.CacheStale)
                    {
                        ApplyMetadata(metadata);
                    }
                    StatusText = (cachedWasDisplayed || result.CacheStale
                                     ? "属性已显示 · 鲜读未完成，当前显示缓存 · "
                                     : string.Empty) +
                                 RefreshFailureText(result);
                }
            }
            else if (cachedWasDisplayed)
            {
                StatusText = "属性已显示 · 鲜读失败，当前显示缓存 · " +
                             RefreshFailureText(result);
            }
            else
            {
                StatusText = RefreshFailureText(result);
            }
            IsBusy = false;
            PropertyViewerPerformanceDiagnostics.Record(
                "native-broker-refresh", 0, result.Metadata is null ? result.ErrorCode ?? "unavailable" : "fresh");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsBusy = false;
        }
        catch (Exception exception)
        {
            if (cachedWasDisplayed)
            {
                StatusText = "属性已显示 · 鲜读失败，当前显示缓存";
            }
            else
            {
                StatusText = "暂时无法鲜读此文件的属性。";
            }
            IsBusy = false;
            PropertyViewerPerformanceDiagnostics.Record("native-broker-refresh", 0, exception.GetType().Name);
        }
    }

    private void ApplyMetadata(DocumentMetadata metadata)
    {
        _metadata = metadata;
        RebuildConfigurations();
        RebuildRows();
    }

    private void RebuildConfigurations()
    {
        var previous = SelectedConfiguration;
        Configurations.Clear();
        if (_metadata is null) return;

        var names = (_metadata.ConfigurationNames ?? Array.Empty<string>())
            .Concat(_metadata.Properties
                .Where(static property => property.Scope == PropertyScope.Configuration)
                .Select(static property => property.ConfigurationName)
                .OfType<string>());

        foreach (var name in names
                     .Where(static name => !string.IsNullOrWhiteSpace(name) &&
                                           !IsInternalConfigurationStream(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Configurations.Add(name);
        _selectedConfiguration = Configurations.FirstOrDefault(name =>
            string.Equals(name, previous, StringComparison.OrdinalIgnoreCase)) ?? Configurations.FirstOrDefault();
        RaisePropertyChanged(nameof(SelectedConfiguration));
        RaisePropertyChanged(nameof(ConfigurationSelectorVisibility));
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (_metadata is null) return;

        var selected = SelectedMode == NativePropertyMode.Custom
            ? _metadata.Properties.Where(static property => property.Scope == PropertyScope.Document)
            : _metadata.Properties.Where(property =>
                property.Scope == PropertyScope.Configuration &&
                string.Equals(property.ConfigurationName, SelectedConfiguration,
                    StringComparison.OrdinalIgnoreCase));

        var preferredOrder = _propertyDisplayOrder
            .Select(static (name, index) => new { Name = name.Trim(), Index = index })
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Index,
                StringComparer.OrdinalIgnoreCase);
        foreach (var property in selected
                     .Where(static property => SolidWorksPropertyValue.TryGetResolvedDisplayValue(property, out _))
                     .Where(NativePropertyDisplayFilter.ShouldShow)
                     .GroupBy(NativePropertyDisplayFilter.ProjectionKey, StringComparer.OrdinalIgnoreCase)
                     .Select(static group => NativePropertyDisplayFilter.SelectPreferred(group.Key, group))
                     .OrderBy(property => preferredOrder.ContainsKey(property.Name.Trim()) ? 0 : 1)
                     .ThenBy(property => preferredOrder.GetValueOrDefault(property.Name.Trim(), int.MaxValue))
                     .ThenBy(static property => property.Name, StringComparer.CurrentCulture))
        {
            Rows.Add(new NativePropertyRow(property.Name, ActualValue(property)));
        }

        if (Rows.Count == 0) Rows.Add(new NativePropertyRow("状态", "暂无已提取属性"));
    }

    private static string ActualValue(DocumentProperty property) =>
        SolidWorksPropertyValue.TryGetResolvedDisplayValue(property, out var value) ? value : "—";

    private static bool IsInternalConfigurationStream(string name) =>
        name.StartsWith("Config-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith("-Properties", StringComparison.OrdinalIgnoreCase);

    private static string RefreshFailureText(PropertyExtractionResult result) => result.ErrorCode switch
    {
        "BROKER_TIMEOUT" => "鲜读超时（6.5 秒），可稍后重试。",
        "BROKER_UNAVAILABLE" => "属性服务未运行或暂不可用。",
        "BROKER_PROTOCOL_MISMATCH" => "属性服务版本不匹配。",
        _ when !string.IsNullOrWhiteSpace(result.ChineseErrorMessage) =>
            result.ChineseErrorMessage.Length <= 120
                ? result.ChineseErrorMessage
                : "暂时无法鲜读此文件的属性。",
        _ => "暂时无法鲜读此文件的属性。",
    };
}
