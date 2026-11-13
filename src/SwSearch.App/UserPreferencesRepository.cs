using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwSearch.Contracts;
using SwSearch.Integration;

namespace SwSearch.App;

internal enum UserPreferenceIntegrationTarget
{
    Taskpane,
    FolderMenu,
    FilePropertiesMenu,
    Startup,
}

internal sealed record UserPreferenceIntegrationState(
    bool IsAvailable,
    bool IsEnabled,
    string Message,
    bool RequiresSolidWorksRestart = false);

internal sealed record UserPreferencesSnapshot(
    UserPreferenceIntegrationState Taskpane,
    UserPreferenceIntegrationState FolderMenu,
    UserPreferenceIntegrationState Startup,
    PropertyCardSourcePreference PropertyCardSource,
    string PropertyCardPath,
    string? ConfigurationError = null,
    IReadOnlyList<string>? LibraryRoots = null,
    UserPreferenceIntegrationState? FilePropertiesMenu = null,
    IReadOnlyList<string>? PropertyDisplayOrder = null);

internal sealed record UserPreferencesSaveRequest(
    bool Taskpane,
    bool FolderMenu,
    bool Startup,
    PropertyCardSourcePreference PropertyCardSource,
    string PropertyCardPath,
    IReadOnlyList<string>? LibraryRoots = null,
    bool FilePropertiesMenu = false,
    IReadOnlyList<string>? PropertyDisplayOrder = null);

internal sealed record UserPreferenceIntegrationSaveResult(
    UserPreferenceIntegrationTarget Target,
    bool RequestedEnabled,
    bool Succeeded,
    string Message);

internal sealed record UserPreferencesSaveResult(
    bool PreferencesSaved,
    string? PersistenceError,
    UserPreferencesSaveRequest NormalizedRequest,
    IReadOnlyList<UserPreferenceIntegrationSaveResult> Integrations);

internal interface IUserPreferencesRepository
{
    Task<UserPreferencesSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task<UserPreferencesSaveResult> SaveAsync(
        UserPreferencesSaveRequest request,
        CancellationToken cancellationToken);
}

internal interface IUserPreferencesIntegrationBackend
{
    ValueTask<IntegrationStatus> ProbeAsync(
        UserPreferenceIntegrationTarget target,
        CancellationToken cancellationToken);

    ValueTask SetEnabledAsync(
        UserPreferenceIntegrationTarget target,
        bool enabled,
        CancellationToken cancellationToken);
}

/// <summary>
/// RC20 preferences storage. This deliberately has no dependency on the former settings window,
/// isolation client, settings coordinators, or their WebView lifecycle.
/// </summary>
internal sealed class UserPreferencesRepository : IUserPreferencesRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly string _pluginDefaultsPath;
    private readonly IUserPreferencesIntegrationBackend _integrations;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _setTimeout;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    internal static IUserPreferencesRepository CreateFailSoft(
        Func<IUserPreferencesRepository>? factory = null,
        string? diagnosticLogPath = null)
    {
        try
        {
            return factory?.Invoke() ?? new UserPreferencesRepository();
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            AppStartupDiagnostics.Record("preferences-construction", exception, diagnosticLogPath);
            return new UnavailableUserPreferencesRepository(exception.GetType().Name);
        }
    }

    internal UserPreferencesRepository(
        string? settingsPath = null,
        IUserPreferencesIntegrationBackend? integrations = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? setTimeout = null,
        string? pluginDefaultsPath = null)
    {
        _settingsPath = Path.GetFullPath(
            settingsPath ?? Path.Combine(SwSearchDataPaths.DataDirectory, "settings.json"));
        _integrations = integrations ?? new UserPreferencesIntegrationBackend();
        _pluginDefaultsPath = Path.GetFullPath(pluginDefaultsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TuoChuang",
            "SwPlmProperties",
            "defaults.tsv"));
        _probeTimeout = Positive(probeTimeout ?? TimeSpan.FromSeconds(6), nameof(probeTimeout));
        _setTimeout = Positive(setTimeout ?? TimeSpan.FromSeconds(35), nameof(setTimeout));
    }

    public async Task<UserPreferencesSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await Task.Run(
                ReadPropertyCardPreferences,
                cancellationToken).ConfigureAwait(false);
            var taskpane = ProbeAsync(UserPreferenceIntegrationTarget.Taskpane, cancellationToken);
            var folderMenu = ProbeAsync(UserPreferenceIntegrationTarget.FolderMenu, cancellationToken);
            var filePropertiesMenu = ProbeAsync(UserPreferenceIntegrationTarget.FilePropertiesMenu, cancellationToken);
            var startup = ProbeAsync(UserPreferenceIntegrationTarget.Startup, cancellationToken);
            await Task.WhenAll(taskpane, folderMenu, filePropertiesMenu, startup).ConfigureAwait(false);
            return new UserPreferencesSnapshot(
                ApplySavedIntent(taskpane.Result, configuration.TaskpaneEnabled),
                ApplySavedIntent(folderMenu.Result, configuration.FolderMenuEnabled),
                ApplySavedIntent(startup.Result, configuration.StartupEnabled),
                configuration.Source,
                configuration.Path,
                configuration.Error,
                Array.Empty<string>(),
                ApplySavedIntent(filePropertiesMenu.Result, configuration.FilePropertiesMenuEnabled),
                configuration.PropertyDisplayOrder);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static UserPreferenceIntegrationState ApplySavedIntent(
        UserPreferenceIntegrationState current,
        bool? savedIntent)
    {
        if (savedIntent is null || current.IsEnabled == savedIntent.Value)
        {
            return current;
        }
        return current with
        {
            IsEnabled = savedIntent.Value,
            Message = $"已保存{(savedIntent.Value ? "启用" : "关闭")}意图；当前系统状态未同步：{current.Message}",
        };
    }

    public async Task<UserPreferencesSaveResult> SaveAsync(
        UserPreferencesSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request with
        {
            PropertyCardPath = NormalizePropertyCardPath(
                request.PropertyCardSource,
                request.PropertyCardPath),
            LibraryRoots = Array.Empty<string>(),
            PropertyDisplayOrder = PropertyDisplayOrderDefaults.Normalize(request.PropertyDisplayOrder),
        };

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await Task.Run(
                    () => WritePropertyCardPreferences(normalized, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedOperationFailure(exception))
            {
                return new UserPreferencesSaveResult(
                    false,
                    $"本地偏好未保存：{SafeMessage(exception)}",
                    normalized,
                    Array.Empty<UserPreferenceIntegrationSaveResult>());
            }

            var changes = new[]
            {
                ApplyIntegrationWithResultAsync(
                    UserPreferenceIntegrationTarget.Taskpane,
                    normalized.Taskpane,
                    cancellationToken),
                ApplyIntegrationWithResultAsync(
                    UserPreferenceIntegrationTarget.FolderMenu,
                    normalized.FolderMenu,
                    cancellationToken),
                ApplyIntegrationWithResultAsync(
                    UserPreferenceIntegrationTarget.FilePropertiesMenu,
                    normalized.FilePropertiesMenu,
                    cancellationToken),
                ApplyIntegrationWithResultAsync(
                    UserPreferenceIntegrationTarget.Startup,
                    normalized.Startup,
                    cancellationToken),
            };
            return new UserPreferencesSaveResult(
                true,
                null,
                normalized,
                await Task.WhenAll(changes).ConfigureAwait(false));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<UserPreferenceIntegrationSaveResult> ApplyIntegrationWithResultAsync(
        UserPreferenceIntegrationTarget target,
        bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await ApplyIntegrationAsync(target, enabled, cancellationToken).ConfigureAwait(false);
            return new UserPreferenceIntegrationSaveResult(
                target,
                enabled,
                true,
                enabled ? "已启用。" : "已关闭。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception) ||
                                          AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            return new UserPreferenceIntegrationSaveResult(
                target,
                enabled,
                false,
                $"{TargetDisplayName(target)}：{SafeMessage(exception)}");
        }
    }

    private async Task ApplyIntegrationAsync(
        UserPreferenceIntegrationTarget target,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var current = await ProbeCoreAsync(target, cancellationToken).ConfigureAwait(false);
        if (!current.IsAvailable)
        {
            if (enabled)
            {
                throw new InvalidOperationException($"{TargetDisplayName(target)}当前不可用：{current.Message}");
            }
            return;
        }
        if (current.IsEnabled == enabled)
        {
            return;
        }

        await RunBoundedAsync(
            token => _integrations.SetEnabledAsync(target, enabled, token).AsTask(),
            _setTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserPreferenceIntegrationState> ProbeAsync(
        UserPreferenceIntegrationTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await ProbeCoreAsync(target, cancellationToken).ConfigureAwait(false);
            return new UserPreferenceIntegrationState(
                status.IsAvailable,
                status.IsEnabled,
                status.Message,
                status.RequiresSolidWorksRestart);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new UserPreferenceIntegrationState(false, false, "状态读取超时，可关闭面板后重试。");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            return new UserPreferenceIntegrationState(
                false,
                false,
                $"状态读取失败：{SafeMessage(exception)}");
        }
    }

    private Task<IntegrationStatus> ProbeCoreAsync(
        UserPreferenceIntegrationTarget target,
        CancellationToken cancellationToken) =>
        RunBoundedAsync(
            token => _integrations.ProbeAsync(target, token).AsTask(),
            _probeTimeout,
            cancellationToken);

    private static async Task<T> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await Task.Run(
                    () => operation(deadline.Token),
                    deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("偏好设置操作超过时限。");
        }
    }

    private static async Task RunBoundedAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await Task.Run(
                    () => operation(deadline.Token),
                    deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("偏好设置操作超过时限。");
        }
    }

    private StoredPreferences ReadPropertyCardPreferences()
    {
        if (!File.Exists(_settingsPath))
        {
            return new StoredPreferences(
                PropertyCardSourcePreference.Automatic,
                ReadActivePluginPath(),
                null,
                null,
                null,
                null);
        }

        try
        {
            using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new StoredPreferences(
                    PropertyCardSourcePreference.Automatic,
                    string.Empty,
                    "设置文件不是有效的 JSON 对象。",
                    null,
                    null,
                    null);
            }

            var source = TryReadPropertyCardSource(root, out var parsed)
                ? parsed
                : PropertyCardSourcePreference.Automatic;
            var path = root.TryGetProperty("propertyCardPath", out var pathNode) &&
                       pathNode.ValueKind == JsonValueKind.String
                ? pathNode.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            try
            {
                path = NormalizePropertyCardPath(source, path);
                if (source == PropertyCardSourcePreference.Automatic && path.Length == 0)
                {
                    path = ReadActivePluginPath();
                }
                return new StoredPreferences(
                    source,
                    path,
                    null,
                    ReadOptionalBoolean(root, "taskpaneEnabled"),
                    ReadOptionalBoolean(root, "folderMenuEnabled"),
                    ReadOptionalBoolean(root, "startupEnabled"),
                    Array.Empty<string>(),
                    ReadOptionalBoolean(root, "filePropertiesMenuEnabled"),
                    ReadPropertyDisplayOrder(root));
            }
            catch (ArgumentException exception)
            {
                return new StoredPreferences(
                    source,
                    path,
                    exception.Message,
                    ReadOptionalBoolean(root, "taskpaneEnabled"),
                    ReadOptionalBoolean(root, "folderMenuEnabled"),
                    ReadOptionalBoolean(root, "startupEnabled"));
            }
        }
        catch (JsonException)
        {
            return new StoredPreferences(
                PropertyCardSourcePreference.Automatic,
                ReadActivePluginPath(),
                "设置文件 JSON 格式无效；下次保存将安全重建。",
                null,
                null,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new StoredPreferences(
                PropertyCardSourcePreference.Automatic,
                ReadActivePluginPath(),
                "设置文件暂时无法读取；主搜索仍可使用，可稍后在面板中重试。",
                null,
                null,
                null);
        }
    }

    private string ReadActivePluginPath()
    {
        try
        {
            if (!File.Exists(_pluginDefaultsPath))
            {
                return string.Empty;
            }
            foreach (var rawLine in File.ReadLines(_pluginDefaultsPath))
            {
                var line = rawLine.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }
                var cells = line.Split('\t');
                if (cells.Length < 2 ||
                    !string.Equals(cells[0].Trim(), "__ConfigFilePath", StringComparison.Ordinal))
                {
                    continue;
                }
                var encoded = cells.Skip(1).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
                if (encoded is null)
                {
                    return string.Empty;
                }
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim())).Trim().Trim('"');
                return Path.IsPathFullyQualified(decoded) &&
                       string.Equals(Path.GetExtension(decoded), ".xlsx", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(decoded)
                    : string.Empty;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
        }
        return string.Empty;
    }

    private void WritePropertyCardPreferences(
        UserPreferencesSaveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("设置文件目录不可用。");
        Directory.CreateDirectory(directory);

        JsonObject document;
        if (File.Exists(_settingsPath))
        {
            try
            {
                using var stream = new FileStream(
                    _settingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                document = JsonNode.Parse(stream) as JsonObject ?? CreatePreferencesDocument();
            }
            catch (JsonException)
            {
                document = CreatePreferencesDocument();
            }
        }
        else
        {
            document = CreatePreferencesDocument();
        }

        document["schemaVersion"] = Math.Max(ReadSchemaVersion(document), 4);
        document["propertyCardSource"] = (int)request.PropertyCardSource;
        document["propertyCardPath"] = request.PropertyCardPath;
        document["taskpaneEnabled"] = request.Taskpane;
        document["folderMenuEnabled"] = request.FolderMenu;
        document["filePropertiesMenuEnabled"] = request.FilePropertiesMenu;
        document["startupEnabled"] = request.Startup;
        document.Remove("libraryRoots");
        document["propertyDisplayOrder"] = new JsonArray((request.PropertyDisplayOrder ?? Array.Empty<string>())
            .Select(static name => JsonValue.Create(name))
            .ToArray());

        var content = Encoding.UTF8.GetBytes(document.ToJsonString(JsonOptions));
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(content);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_settingsPath))
            {
                File.Replace(temporary, _settingsPath, _settingsPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, _settingsPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The committed settings file is authoritative; a stale unique temp is harmless.
            }
        }
    }

    private static JsonObject CreatePreferencesDocument() => new()
    {
        ["schemaVersion"] = 4,
        ["discoveryPreference"] = 0,
        ["networkRoots"] = new JsonArray(),
        ["backgroundPaused"] = false,
    };

    private static bool? ReadOptionalBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) &&
        node.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? node.GetBoolean()
            : null;

    private static IReadOnlyList<string> ReadLibraryRoots(JsonElement root)
    {
        if (!root.TryGetProperty("libraryRoots", out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return NormalizeLibraryRoots(node.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()));
    }

    private static IReadOnlyList<string> ReadPropertyDisplayOrder(JsonElement root)
    {
        if (!root.TryGetProperty("propertyDisplayOrder", out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return PropertyDisplayOrderDefaults.Normalize(node.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()));
    }

    internal static IReadOnlyList<string> NormalizeLibraryRoots(IEnumerable<string?>? roots)
    {
        var normalized = new List<string>();
        foreach (var raw in roots ?? Array.Empty<string>())
        {
            var candidate = raw?.Trim().Trim('"') ?? string.Empty;
            if (candidate.Length == 0)
            {
                continue;
            }
            if (candidate.Length > 32_767 || candidate.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(candidate))
            {
                throw new ArgumentException("文件库路径必须是完整的本地或 UNC 文件夹路径。", nameof(roots));
            }
            var value = SearchPath.NormalizeFolder(candidate);
            if (!normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(value);
            }
        }
        return normalized;
    }

    private static bool TryReadPropertyCardSource(
        JsonElement root,
        out PropertyCardSourcePreference source)
    {
        source = PropertyCardSourcePreference.Automatic;
        if (!root.TryGetProperty("propertyCardSource", out var node))
        {
            return false;
        }
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var number) &&
            Enum.IsDefined(typeof(PropertyCardSourcePreference), number))
        {
            source = (PropertyCardSourcePreference)number;
            return true;
        }
        if (node.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        source = node.GetString()?.Trim().ToLowerInvariant() switch
        {
            "custom" => PropertyCardSourcePreference.Custom,
            _ => PropertyCardSourcePreference.Automatic,
        };
        return true;
    }

    private static string NormalizePropertyCardPath(
        PropertyCardSourcePreference source,
        string? path)
    {
        var candidate = path?.Trim().Trim('"') ?? string.Empty;
        if (candidate.Length == 0)
        {
            if (source == PropertyCardSourcePreference.Custom)
            {
                throw new ArgumentException("使用指定表格时必须填写 .xlsx 路径。", nameof(path));
            }
            return string.Empty;
        }
        if (candidate.Length > 32_767 || candidate.IndexOf('\0') >= 0 ||
            !Path.IsPathFullyQualified(candidate) ||
            !string.Equals(Path.GetExtension(candidate), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("属性卡配置路径必须是完整的 .xlsx 文件路径。", nameof(path));
        }
        return Path.GetFullPath(candidate);
    }

    private static int ReadSchemaVersion(JsonObject document) =>
        document["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out var version)
            ? version
            : 0;

    private static string TargetDisplayName(UserPreferenceIntegrationTarget target) => target switch
    {
        UserPreferenceIntegrationTarget.Taskpane => "SOLIDWORKS 右侧任务窗格",
        UserPreferenceIntegrationTarget.FolderMenu => "Windows 文件夹右键搜索",
        UserPreferenceIntegrationTarget.FilePropertiesMenu => "Windows 文件右键查看属性",
        UserPreferenceIntegrationTarget.Startup => "开机自启",
        _ => "系统集成",
    };

    private static string SafeMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? "系统未返回详细原因。"
            : exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool IsExpectedOperationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or System.ComponentModel.Win32Exception;

    private static TimeSpan Positive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(parameterName);

    private sealed record StoredPreferences(
        PropertyCardSourcePreference Source,
        string Path,
        string? Error,
        bool? TaskpaneEnabled,
        bool? FolderMenuEnabled,
        bool? StartupEnabled,
        IReadOnlyList<string>? LibraryRoots = null,
        bool? FilePropertiesMenuEnabled = null,
        IReadOnlyList<string>? PropertyDisplayOrder = null);
}

internal sealed class UnavailableUserPreferencesRepository(string failureType)
    : IUserPreferencesRepository
{
    private const string Message = "偏好设置初始化失败；主搜索仍可使用，请关闭面板后重试。";

    public Task<UserPreferencesSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unavailable = new UserPreferenceIntegrationState(false, false, Message);
        return Task.FromResult(new UserPreferencesSnapshot(
            unavailable,
            unavailable,
            unavailable,
            PropertyCardSourcePreference.Automatic,
            string.Empty,
            $"{Message}（{failureType}）"));
    }

    public Task<UserPreferencesSaveResult> SaveAsync(
        UserPreferencesSaveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new UserPreferencesSaveResult(
            false,
            Message,
            request,
            Array.Empty<UserPreferenceIntegrationSaveResult>()));
    }
}

internal sealed class UserPreferencesIntegrationBackend : IUserPreferencesIntegrationBackend
{
    private readonly SolidWorksTaskpaneRegistrationService _taskpane;
    private readonly IFolderContextMenuRegistration _folderMenu;
    private readonly IFilePropertiesContextMenuRegistration _filePropertiesMenu;
    private readonly StartupRegistrationService _startup;
    private readonly string _applicationPath;
    private readonly string _brokerPath;

    internal UserPreferencesIntegrationBackend(
        IRegistryAccess? registry = null,
        string? installDirectory = null,
        bool? useModernFolderMenu = null)
    {
        var root = Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory);
        var registryAccess = registry ?? new WindowsRegistryAccess();
        _taskpane = new SolidWorksTaskpaneRegistrationService(registryAccess);
        _folderMenu = new FolderContextMenuService(registryAccess, useModernFolderMenu, root);
        _filePropertiesMenu = new FilePropertiesContextMenuRegistration(root);
        _startup = new StartupRegistrationService(registryAccess);
        _applicationPath = Path.Combine(root, "SwSearch.App.exe");
        _brokerPath = Path.Combine(root, "SwSearch.Broker.exe");
    }

    public ValueTask<IntegrationStatus> ProbeAsync(
        UserPreferenceIntegrationTarget target,
        CancellationToken cancellationToken) => target switch
    {
        UserPreferenceIntegrationTarget.Taskpane => _taskpane.ProbeAsync(cancellationToken),
        UserPreferenceIntegrationTarget.FolderMenu => _folderMenu.ProbeAsync(_applicationPath, cancellationToken),
        UserPreferenceIntegrationTarget.FilePropertiesMenu => _filePropertiesMenu.ProbeAsync(cancellationToken),
        UserPreferenceIntegrationTarget.Startup => _startup.ProbeAsync(_brokerPath, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public ValueTask SetEnabledAsync(
        UserPreferenceIntegrationTarget target,
        bool enabled,
        CancellationToken cancellationToken) => target switch
    {
        UserPreferenceIntegrationTarget.Taskpane => _taskpane.SetEnabledAsync(enabled, cancellationToken),
        UserPreferenceIntegrationTarget.FolderMenu => _folderMenu.SetEnabledAsync(
            enabled,
            _applicationPath,
            cancellationToken),
        UserPreferenceIntegrationTarget.FilePropertiesMenu => _filePropertiesMenu.SetEnabledAsync(enabled, cancellationToken),
        UserPreferenceIntegrationTarget.Startup => _startup.SetEnabledAsync(
            enabled,
            _brokerPath,
            cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}
