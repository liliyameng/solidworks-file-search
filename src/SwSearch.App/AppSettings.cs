using System.IO;
using System.Text;
using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.App;

public enum DiscoveryPreference
{
    Automatic,
    EverythingPreferred,
    BuiltInOnly,
}

public enum PropertyCardSourcePreference
{
    Automatic,
    Custom,
}

public sealed record ResultColumnLayout(
    string Id,
    string DisplayName,
    double Width,
    int Order,
    bool Visible);

public static class ResultColumnDefaults
{
    public const double MinimumWidth = 56;
    public const double MaximumWidth = 1600;

    public static IReadOnlyList<ResultColumnLayout> All { get; } =
    [
        new("fileName", "文件名", 220, 0, true),
        new("kind", "类型", 72, 1, true),
        new("matchedProperty", "命中属性", 120, 2, true),
        new("matchedValue", "属性值", 180, 3, true),
        new("source", "来源", 145, 4, true),
        new("directory", "所在文件夹", 330, 5, true),
        new("cacheStatus", "缓存状态", 95, 6, false),
    ];

    public static IReadOnlyList<ResultColumnLayout> Normalize(IEnumerable<ResultColumnLayout>? columns)
    {
        var defaults = All.ToDictionary(static column => column.Id, StringComparer.Ordinal);
        var supplied = (columns ?? Array.Empty<ResultColumnLayout>())
            .Where(column => defaults.ContainsKey(column.Id))
            .GroupBy(static column => column.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToDictionary(static column => column.Id, StringComparer.Ordinal);
        var result = new List<ResultColumnLayout>(defaults.Count);
        foreach (var fallback in All)
        {
            var column = supplied.GetValueOrDefault(fallback.Id) ?? fallback;
            result.Add(fallback with
            {
                Width = Math.Clamp(double.IsFinite(column.Width) ? column.Width : fallback.Width, MinimumWidth, MaximumWidth),
            });
        }
        return result;
    }
}

public static class PropertyDisplayOrderDefaults
{
    public const int MaximumCount = 256;
    public const int MaximumNameLength = 512;

    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? names)
    {
        var result = new List<string>();
        foreach (var raw in names ?? Array.Empty<string>())
        {
            var name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }
            if (name.Length > MaximumNameLength || name.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("属性名称无效或过长。", nameof(names));
            }
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
            if (result.Count > MaximumCount)
            {
                throw new ArgumentException($"属性显示顺序最多可配置 {MaximumCount} 项。", nameof(names));
            }
        }
        return result;
    }
}

public static class NativeShellColumnRatioDefaults
{
    private static readonly double[] DefaultValues = [0.42d, 0.18d, 0.40d];

    public static IReadOnlyList<double> All => DefaultValues;

    public static IReadOnlyList<double> Normalize(IEnumerable<double>? ratios)
    {
        var supplied = ratios?.ToArray();
        if (supplied is null || supplied.Length != DefaultValues.Length ||
            supplied.Any(static value => !double.IsFinite(value) || value <= 0d))
        {
            return (double[])DefaultValues.Clone();
        }

        var total = supplied.Sum();
        if (!double.IsFinite(total) || total <= 0d)
        {
            return (double[])DefaultValues.Clone();
        }

        return supplied.Select(value => value / total).ToArray();
    }
}

public sealed record AppSettings(
    DiscoveryPreference DiscoveryPreference,
    IReadOnlyList<string> NetworkRoots,
    bool BackgroundPaused,
    IReadOnlyList<ResultColumnLayout>? Columns = null,
    PropertyCardSourcePreference PropertyCardSource = PropertyCardSourcePreference.Automatic,
    string? PropertyCardPath = null,
    int SchemaVersion = 3,
    IReadOnlyList<string>? PropertyDisplayOrder = null,
    IReadOnlyList<double>? NativeShellColumnRatios = null)
{
    public static AppSettings Default { get; } = new(
        DiscoveryPreference.Automatic,
        Array.Empty<string>(),
        false,
        ResultColumnDefaults.All,
        PropertyCardSourcePreference.Automatic,
        null,
        3,
        Array.Empty<string>(),
        NativeShellColumnRatioDefaults.All);

    public IReadOnlyList<ResultColumnLayout> NormalizedColumns => ResultColumnDefaults.Normalize(Columns);
    public IReadOnlyList<string> NormalizedPropertyDisplayOrder =>
        PropertyDisplayOrderDefaults.Normalize(PropertyDisplayOrder);
    public IReadOnlyList<double> NormalizedNativeShellColumnRatios =>
        NativeShellColumnRatioDefaults.Normalize(NativeShellColumnRatios);
}

public static class SwSearchDataPaths
{
    public static string DataDirectory
    {
        get
        {
            var isolated = Environment.GetEnvironmentVariable("SWSEARCH_DATA_ROOT");
            return string.IsNullOrWhiteSpace(isolated)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwSearch")
                : Path.GetFullPath(isolated);
        }
    }
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    ValueTask<IPreparedAppSettingsSave> PrepareSaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken);
}

public interface IPreparedAppSettingsSave : IDisposable
{
    ValueTask CommitAsync(CancellationToken cancellationToken);
}

internal sealed class DeferredAppSettingsSave(
    Func<CancellationToken, ValueTask> commit) : IPreparedAppSettingsSave
{
    internal DeferredAppSettingsSave(Action commit)
        : this(_ =>
        {
            commit();
            return ValueTask.CompletedTask;
        })
    {
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken) => commit(cancellationToken);

    public void Dispose() { }
}

internal interface ISettingsSaveExecutor
{
    ValueTask ExecuteAsync(
        string settingsPath,
        string temporaryPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

internal sealed class SettingsTemporaryCleanupPendingException(Exception innerException)
    : IOException("Temporary settings cleanup is pending.", innerException);

internal sealed class IsolatedSettingsSaveExecutor(IAppIsolationClient isolationClient)
    : ISettingsSaveExecutor
{
    public async ValueTask ExecuteAsync(
        string settingsPath,
        string temporaryPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        try
        {
            await isolationClient.SaveSettingsAsync(
                settingsPath,
                temporaryPath,
                content,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                if (!await isolationClient.CleanupSettingsTemporaryAsync(
                        temporaryPath,
                        cleanupDeadline.Token).ConfigureAwait(false))
                {
                    throw new SettingsTemporaryCleanupPendingException(exception);
                }
            }
            catch (SettingsTemporaryCleanupPendingException)
            {
                throw;
            }
            catch (Exception cleanupException)
            {
                throw new SettingsTemporaryCleanupPendingException(cleanupException);
            }
            throw;
        }
    }
}

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ISettingsSaveExecutor _saveExecutor;

    public JsonAppSettingsStore(string? settingsPath = null)
        : this(settingsPath, new IsolatedSettingsSaveExecutor(new AppIsolationClient()))
    {
    }

    internal JsonAppSettingsStore(string? settingsPath, ISettingsSaveExecutor saveExecutor)
    {
        ArgumentNullException.ThrowIfNull(saveExecutor);
        SettingsPath = Path.GetFullPath(
            settingsPath ?? Path.Combine(SwSearchDataPaths.DataDirectory, "settings.json"));
        _saveExecutor = saveExecutor;
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        if (TryLoad(SettingsPath, out var settings))
        {
            return settings;
        }

        var backup = SettingsPath + ".bak";
        if (File.Exists(SettingsPath))
        {
            try
            {
                var quarantine = SettingsPath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
                File.Move(SettingsPath, quarantine, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return TryLoad(backup, out settings) ? settings : AppSettings.Default;
    }

    public void Save(AppSettings settings)
    {
        using var prepared = PrepareSaveAsync(settings, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        prepared.CommitAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask<IPreparedAppSettingsSave> PrepareSaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = new SettingsDocument
        {
            SchemaVersion = 3,
            // RC18 has one automatic discovery policy. Keep the legacy fields in
            // the JSON schema so older binaries can still read the file safely.
            DiscoveryPreference = DiscoveryPreference.Automatic,
            NetworkRoots = NormalizeNetworkRoots(settings.NetworkRoots).ToArray(),
            BackgroundPaused = false,
            Columns = ResultColumnDefaults.Normalize(settings.Columns).ToArray(),
            PropertyCardSource = settings.PropertyCardSource,
            PropertyCardPath = NormalizePropertyCardPath(settings.PropertyCardSource, settings.PropertyCardPath),
            PropertyDisplayOrder = settings.NormalizedPropertyDisplayOrder.ToArray(),
            NativeShellColumnRatios = settings.NormalizedNativeShellColumnRatios.ToArray(),
        };
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var content = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedJsonSettingsSave(_saveExecutor, temporary, SettingsPath, content);
    }

    private sealed class PreparedJsonSettingsSave(
        ISettingsSaveExecutor saveExecutor,
        string temporaryPath,
        string settingsPath,
        ReadOnlyMemory<byte> content) : IPreparedAppSettingsSave
    {
        public ValueTask CommitAsync(CancellationToken cancellationToken) =>
            saveExecutor.ExecuteAsync(settingsPath, temporaryPath, content, cancellationToken);

        public void Dispose() { }
    }

    public static IReadOnlyList<string> NormalizeNetworkRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in roots)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            var root = SearchPath.NormalizeFolder(value);
            if (!root.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException($"网络根目录必须是完整 UNC 路径：{root}", nameof(roots));
            }
            normalized.Add(root);
        }
        return normalized.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? NormalizePropertyCardPath(
        PropertyCardSourcePreference source,
        string? path)
    {
        if (source == PropertyCardSourcePreference.Automatic)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("属性卡配置路径必须是完整的 .xlsx 文件路径。", nameof(path));
        }
        return Path.GetFullPath(path.Trim().Trim('"'));
    }

    private static bool TryLoad(string path, out AppSettings settings)
    {
        settings = AppSettings.Default;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || !Enum.IsDefined(document.DiscoveryPreference) ||
                !Enum.IsDefined(document.PropertyCardSource))
            {
                return false;
            }
            settings = new AppSettings(
                DiscoveryPreference.Automatic,
                NormalizeNetworkRoots(document.NetworkRoots ?? Array.Empty<string>()),
                false,
                ResultColumnDefaults.Normalize(document.Columns),
                document.PropertyCardSource,
                NormalizePropertyCardPath(document.PropertyCardSource, document.PropertyCardPath),
                3,
                PropertyDisplayOrderDefaults.Normalize(document.PropertyDisplayOrder),
                NativeShellColumnRatioDefaults.Normalize(document.NativeShellColumnRatios));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed class SettingsDocument
    {
        public int SchemaVersion { get; set; }
        public DiscoveryPreference DiscoveryPreference { get; set; }
        public string[]? NetworkRoots { get; set; }
        public bool BackgroundPaused { get; set; }
        public ResultColumnLayout[]? Columns { get; set; }
        public PropertyCardSourcePreference PropertyCardSource { get; set; }
        public string? PropertyCardPath { get; set; }
        public string[]? PropertyDisplayOrder { get; set; }
        public double[]? NativeShellColumnRatios { get; set; }
    }
}

public sealed record DiscoveryPreferenceOption(DiscoveryPreference Value, string Label)
{
    public override string ToString() => Label;
}
