using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Xml.Linq;
using SwSearch.Contracts;

namespace SwSearch.PropertyConfig;

public sealed record PropertyCardConfigProviderOptions(
    string PluginDefaultsPath,
    string SnapshotPath,
    IReadOnlyList<string> ActiveCacheCandidates,
    IReadOnlyList<string> BackupXlsxCandidates,
    bool WatchForChanges = true,
    TimeSpan? Debounce = null,
    string? ProductSettingsPath = null)
{
    public static PropertyCardConfigProviderOptions CreateDefault(string dataDirectory, string installDirectory)
    {
        var vendorRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TuoChuang");
        return new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TuoChuang", "SwPlmProperties", "defaults.tsv"),
            Path.Combine(dataDirectory, "property-card-config-v1.json"),
            [
                Path.Combine(vendorRoot, "SWPropertyEditor", "Data", "plm-config.cache"),
                Path.Combine(vendorRoot, "QuickCode", "Data", "plm-config.cache"),
                Path.Combine(installDirectory, "plm-config.cache"),
            ],
            [
                Path.Combine(vendorRoot, "SWPropertyEditor", "Data", "plm-config.xlsx"),
                Path.Combine(vendorRoot, "QuickCode", "Data", "plm-config.xlsx"),
                Path.Combine(installDirectory, "plm-config.xlsx"),
            ],
            ProductSettingsPath: Path.Combine(dataDirectory, "settings.json"));
    }
}

public sealed class PropertyCardConfigProvider : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PropertyCardConfigProviderOptions _options;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _watchGate = new();
    private FileSystemWatcher? _defaultsWatcher;
    private readonly List<FileSystemWatcher> _sourceWatchers = [];
    private Timer? _debounceTimer;
    private (string Path, long Length, DateTime LastWrite)? _observedActiveSignature;
    private PropertyCardConfigSnapshot? _lastKnownGood;
    private PropertyCardConfigSnapshot? _current;
    private readonly object _preloadGate = new();
    private Task? _preloadTask;
    private bool _disposed;

    public PropertyCardConfigProvider(PropertyCardConfigProviderOptions options)
    {
        _options = options with
        {
            PluginDefaultsPath = Path.GetFullPath(options.PluginDefaultsPath),
            SnapshotPath = Path.GetFullPath(options.SnapshotPath),
            ProductSettingsPath = string.IsNullOrWhiteSpace(options.ProductSettingsPath)
                ? null
                : Path.GetFullPath(options.ProductSettingsPath),
            ActiveCacheCandidates = options.ActiveCacheCandidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            BackupXlsxCandidates = options.BackupXlsxCandidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        _lastKnownGood = TryLoadSnapshot();
        _current = _lastKnownGood is null ? UnavailableSnapshot("配置预解析正在后台进行。") : AsFallback(_lastKnownGood, null);
    }

    public event EventHandler? Updated;

    public ValueTask<PropertyCardConfigSnapshot> GetCurrentAsync(
        bool forceReload = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _ = StartPreload();
        return ValueTask.FromResult(_current ?? UnavailableSnapshot("配置预解析正在后台进行。"));
    }

    public Task StartPreload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_preloadGate)
        {
            return _preloadTask ??= Task.Run(PreloadOnceAsync);
        }
    }

    private async Task PreloadOnceAsync()
    {
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolved = await ResolveAsync(CancellationToken.None).ConfigureAwait(false);
            _current = resolved;
            if (resolved.Source is not PropertyCardConfigSource.LastKnownGood and not PropertyCardConfigSource.Unavailable)
            {
                _lastKnownGood = resolved;
                await SaveSnapshotAsync(resolved, TryReadActivePath(), CancellationToken.None).ConfigureAwait(false);
            }
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A failed startup parse never erases or replaces last-good.
            _current = _lastKnownGood is null
                ? UnavailableSnapshot("本次启动配置解析失败；暂无上次成功快照。")
                : AsFallback(_lastKnownGood, "本次启动配置解析失败；继续使用上次成功快照。");
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async ValueTask<PropertyCardConfigSnapshot> ResolveAsync(CancellationToken cancellationToken)
    {
        var activePath = TryReadActivePath();
        string? activeFailure = null;
        if (activePath is not null)
        {
            try
            {
                await WaitForStableFileAsync(activePath, cancellationToken).ConfigureAwait(false);
                var xlsx = XlsxPropertyCardParser.Parse(activePath);
                var sourceLabel = SafeActiveSourceLabel(activePath, fromCache: false);
                var active = CreateSnapshot(xlsx, activePath, PropertyCardConfigSource.ActivePluginSetting, sourceLabel);
                foreach (var cachePath in CandidateCachePaths(activePath))
                {
                    if (!TuoChuangCacheV2Reader.TryRead(cachePath, activePath, out var cache) ||
                        !Equivalent(cache, xlsx)) continue;
                    active = CreateSnapshot(cache, activePath, PropertyCardConfigSource.ActivePluginSetting,
                        SafeActiveSourceLabel(activePath, fromCache: true));
                    break;
                }
                _lastKnownGood = active;
                return active;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                activeFailure = "当前配置文件暂时不可读取或格式无效。";
            }
        }
        else
        {
            activeFailure = "未找到拓创插件当前配置指向。";
        }

        if (activePath is not null)
        {
            foreach (var sameDirectoryBackup in SameDirectoryBackupCandidates(activePath))
            {
                try
                {
                    return await ParseStableXlsxAsync(
                        sameDirectoryBackup,
                        PropertyCardConfigSource.BackupXlsx,
                        "当前配置同目录备用表格",
                        cancellationToken,
                        isDegraded: true,
                        degradationReason: activeFailure).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                }
            }
        }

        if (_lastKnownGood is not null)
        {
            return AsFallback(_lastKnownGood, activeFailure);
        }

        var persisted = await TryLoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            _lastKnownGood = persisted;
            return AsFallback(persisted, activeFailure);
        }

        foreach (var backupPath in _options.BackupXlsxCandidates)
        {
            try
            {
                return await ParseStableXlsxAsync(
                    backupPath,
                    PropertyCardConfigSource.BackupXlsx,
                    "安装目录备用配置",
                    cancellationToken,
                    isDegraded: true,
                    degradationReason: activeFailure).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
            }
        }
        return new PropertyCardConfigSnapshot(
            Array.Empty<PropertyCardFieldDefinition>(),
            Array.Empty<PropertyCardClassificationRule>(),
            Array.Empty<PropertyCardClassificationNode>(),
            PropertyCardConfigSource.Unavailable,
            "配置不可用",
            DateTimeOffset.UtcNow,
            true,
            "当前配置、上次成功快照和安装目录备用配置均不可用。");
    }

    private static PropertyCardConfigSnapshot AsFallback(PropertyCardConfigSnapshot snapshot, string? reason) =>
        snapshot with
        {
            Source = PropertyCardConfigSource.LastKnownGood,
            SourceLabel = "本产品上次成功配置",
            IsDegraded = true,
            DegradationReason = reason ?? "当前配置不可用，继续使用上次成功配置。",
        };

    private string? TryReadActivePath()
    {
        try
        {
            var productPath = TryReadProductPreferredPath();
            if (productPath is not null)
            {
                return productPath;
            }
            if (!File.Exists(_options.PluginDefaultsPath)) return null;
            foreach (var rawLine in File.ReadLines(_options.PluginDefaultsPath))
            {
                var line = rawLine.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var cells = line.Split('\t');
                if (cells.Length >= 2 && string.Equals(cells[0].Trim(), "__ConfigFilePath", StringComparison.Ordinal))
                {
                    var encoded = cells.Skip(1).FirstOrDefault(static cell => !string.IsNullOrWhiteSpace(cell));
                    if (encoded is null) return null;
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
                    return ValidateActivePath(decoded);
                }
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
        return null;
    }

    private string? TryReadProductPreferredPath()
    {
        if (string.IsNullOrWhiteSpace(_options.ProductSettingsPath) ||
            !File.Exists(_options.ProductSettingsPath))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(_options.ProductSettingsPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("propertyCardSource", out var source))
        {
            return null;
        }
        var custom = source.ValueKind switch
        {
            JsonValueKind.Number => source.TryGetInt32(out var number) && number == 1,
            JsonValueKind.String => string.Equals(source.GetString(), "custom", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!custom || !root.TryGetProperty("propertyCardPath", out var path) ||
            path.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return ValidateActivePath(path.GetString());
    }

    private static string? ValidateActivePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var unquoted = value.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(unquoted) ||
            !string.Equals(Path.GetExtension(unquoted), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.GetFullPath(unquoted);
    }

    private IEnumerable<string> CandidateCachePaths(string activePath)
    {
        var besideActive = Path.Combine(Path.GetDirectoryName(activePath)!, "plm-config.cache");
        return new[] { besideActive }.Concat(_options.ActiveCacheCandidates).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SameDirectoryBackupCandidates(string activePath)
    {
        var directory = Path.GetDirectoryName(activePath);
        if (string.IsNullOrWhiteSpace(directory)) yield break;
        var backup = Path.Combine(directory, "plm-config.xlsx");
        if (!string.Equals(backup, activePath, StringComparison.OrdinalIgnoreCase))
        {
            yield return backup;
        }
    }

    private static PropertyCardConfigSnapshot CreateSnapshot(
        ParsedPropertyCardWorkbook parsed,
        string sourcePath,
        PropertyCardConfigSource source,
        string sourceLabel) => new(
        parsed.Fields,
        parsed.ClassificationRules,
        parsed.Classifications,
        source,
        sourceLabel,
        new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath), TimeSpan.Zero),
        false);

    private static string SafeActiveSourceLabel(string activePath, bool fromCache)
    {
        var root = Path.GetPathRoot(activePath);
        var location = string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal)
            ? "网络"
            : root.TrimEnd(Path.DirectorySeparatorChar);
        return fromCache ? $"坚果云同步配置缓存 ({location})" : $"坚果云同步配置 ({location})";
    }

    private static bool Equivalent(ParsedPropertyCardWorkbook left, ParsedPropertyCardWorkbook right)
    {
        static string Canonical(ParsedPropertyCardWorkbook workbook) => JsonSerializer.Serialize(new
        {
            Fields = workbook.Fields.OrderBy(static item => item.Order).ThenBy(static item => item.Module).ThenBy(static item => item.PropertyName),
            Rules = workbook.ClassificationRules.OrderBy(static item => item.Order).ThenBy(static item => item.PropertyName),
            Nodes = workbook.Classifications.OrderBy(static item => item.Order).ThenBy(static item => item.Level1).ThenBy(static item => item.Level2).ThenBy(static item => item.Level3),
        }, JsonOptions);
        return string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
    }

    private static async ValueTask<PropertyCardConfigSnapshot> ParseStableXlsxAsync(
        string path,
        PropertyCardConfigSource source,
        string sourceLabel,
        CancellationToken cancellationToken,
        bool isDegraded = false,
        string? degradationReason = null)
    {
        await WaitForStableFileAsync(path, cancellationToken).ConfigureAwait(false);
        var parsed = XlsxPropertyCardParser.Parse(path);
        var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return new PropertyCardConfigSnapshot(
            parsed.Fields,
            parsed.ClassificationRules,
            parsed.Classifications,
            source,
            sourceLabel,
            updated,
            isDegraded,
            degradationReason);
    }

    private static async Task WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Property card configuration was not found.");
        (long Length, DateTime LastWrite)? previous = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var current = (info.Length, info.LastWriteTimeUtc);
            if (current.Length > 0 && previous == current) return;
            previous = current;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new IOException("Property card configuration did not become stable.");
    }

    private async ValueTask SaveSnapshotAsync(
        PropertyCardConfigSnapshot snapshot,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_options.SnapshotPath)!;
            Directory.CreateDirectory(directory);
            var source = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath)
                ? new FileInfo(sourcePath)
                : null;
            var envelope = new PersistedPropertyCardSnapshot(
                2,
                source?.FullName,
                source?.Length,
                source?.LastWriteTimeUtc,
                source is null ? null : Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source.FullName, cancellationToken).ConfigureAwait(false))),
                snapshot);
            var temporary = _options.SnapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(envelope, JsonOptions), cancellationToken).ConfigureAwait(false);
            if (File.Exists(_options.SnapshotPath))
                File.Replace(temporary, _options.SnapshotPath, _options.SnapshotPath + ".bak", true);
            else
                File.Move(temporary, _options.SnapshotPath);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // An in-memory last-known-good remains available when the local snapshot cannot be written.
        }
    }

    private async ValueTask<PropertyCardConfigSnapshot?> TryLoadSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_options.SnapshotPath)) return null;
            var json = await File.ReadAllTextAsync(_options.SnapshotPath, cancellationToken).ConfigureAwait(false);
            var snapshot = DeserializeSnapshot(json);
            return snapshot is { Fields.Count: > 0 } ? snapshot : null;
        }
        catch (Exception exception) when (IsRecoverable(exception) || exception is JsonException)
        {
            return null;
        }
    }

    private PropertyCardConfigSnapshot? TryLoadSnapshot()
    {
        try
        {
            if (!File.Exists(_options.SnapshotPath)) return null;
            return DeserializeSnapshot(File.ReadAllText(_options.SnapshotPath));
        }
        catch (Exception exception) when (IsRecoverable(exception) || exception is JsonException)
        {
            return null;
        }
    }

    private static PropertyCardConfigSnapshot? DeserializeSnapshot(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<PersistedPropertyCardSnapshot>(json, JsonOptions);
            if (envelope is { SchemaVersion: 2, Snapshot.Fields.Count: > 0 }) return envelope.Snapshot;
        }
        catch (JsonException)
        {
        }
        return JsonSerializer.Deserialize<PropertyCardConfigSnapshot>(json, JsonOptions);
    }

    private static PropertyCardConfigSnapshot UnavailableSnapshot(string reason) => new(
        Array.Empty<PropertyCardFieldDefinition>(),
        Array.Empty<PropertyCardClassificationRule>(),
        Array.Empty<PropertyCardClassificationNode>(),
        PropertyCardConfigSource.Unavailable,
        "配置预解析",
        DateTimeOffset.UtcNow,
        true,
        reason);

    private void ConfigureDefaultsWatcher()
    {
        var directory = Path.GetDirectoryName(_options.PluginDefaultsPath);
        if (directory is null || !Directory.Exists(directory)) return;
        _defaultsWatcher = CreateWatcher(directory, Path.GetFileName(_options.PluginDefaultsPath));
    }

    private void ConfigureSourceWatchers(string? activePath)
    {
        if (!_options.WatchForChanges) return;
        lock (_watchGate)
        {
            foreach (var watcher in _sourceWatchers) watcher.Dispose();
            _sourceWatchers.Clear();
            if (!string.IsNullOrWhiteSpace(_options.ProductSettingsPath))
            {
                AddSourceWatcher(_options.ProductSettingsPath);
            }
            if (activePath is not null)
            {
                AddSourceWatcher(activePath);
                foreach (var cache in CandidateCachePaths(activePath)) AddSourceWatcher(cache);
                var info = new FileInfo(activePath);
                _observedActiveSignature = info.Exists ? (activePath, info.Length, info.LastWriteTimeUtc) : null;
            }
        }
    }

    private void AddSourceWatcher(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) return;
        if (_sourceWatchers.Any(watcher => string.Equals(watcher.Path, directory, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(watcher.Filter, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))) return;
        _sourceWatchers.Add(CreateWatcher(directory, Path.GetFileName(path)));
    }

    private void PollActiveSource()
    {
        if (_disposed) return;
        var activePath = TryReadActivePath();
        if (activePath is null)
        {
            if (_observedActiveSignature is not null) OnWatchedFileChanged(this, new FileSystemEventArgs(WatcherChangeTypes.Deleted, string.Empty, string.Empty));
            return;
        }
        var info = new FileInfo(activePath);
        var current = info.Exists ? (activePath, info.Length, info.LastWriteTimeUtc) : ((string, long, DateTime)?)null;
        if (current != _observedActiveSignature) OnWatchedFileChanged(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, string.Empty, string.Empty));
    }

    private FileSystemWatcher CreateWatcher(string directory, string filter)
    {
        var watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnWatchedFileChanged;
        watcher.Created += OnWatchedFileChanged;
        watcher.Deleted += OnWatchedFileChanged;
        watcher.Renamed += OnWatchedFileChanged;
        return watcher;
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs args)
    {
        lock (_watchGate)
        {
            _debounceTimer ??= new Timer(
                static state => ((PropertyCardConfigProvider)state!).ReloadFromWatcher(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(_options.Debounce ?? TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
        }
    }

    private async void ReloadFromWatcher()
    {
        if (_disposed) return;
        try
        {
            await GetCurrentAsync(forceReload: true).ConfigureAwait(false);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (IsRecoverable(exception) || exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
            NotSupportedException or JsonException;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_watchGate)
        {
            _defaultsWatcher?.Dispose();
            foreach (var watcher in _sourceWatchers) watcher.Dispose();
            _debounceTimer?.Dispose();
        }
        // A debounced async callback may already have passed its disposed check.
        // Keeping this small semaphore alive avoids an async-void disposal race.
        return ValueTask.CompletedTask;
    }
}

internal sealed record PersistedPropertyCardSnapshot(
    int SchemaVersion,
    string? SourcePath,
    long? SourceLength,
    DateTime? SourceLastWriteUtc,
    string? ContentSha256,
    PropertyCardConfigSnapshot Snapshot);

internal sealed record ParsedPropertyCardWorkbook(
    IReadOnlyList<PropertyCardFieldDefinition> Fields,
    IReadOnlyList<PropertyCardClassificationRule> ClassificationRules,
    IReadOnlyList<PropertyCardClassificationNode> Classifications);

internal static class TuoChuangCacheV2Reader
{
    internal static bool TryRead(string cachePath, string sourcePath, out ParsedPropertyCardWorkbook workbook)
    {
        workbook = new ParsedPropertyCardWorkbook([], [], []);
        try
        {
            if (!File.Exists(cachePath) || !File.Exists(sourcePath)) return false;
            var lines = File.ReadAllLines(cachePath, new System.Text.UTF8Encoding(false, true));
            if (lines.Length == 0) return false;
            var header = lines[0].TrimStart('\uFEFF').Split('|');
            if (header.Length != 4 || header[0] != "TCPLMCONFIGCACHE" || header[1] != "2" ||
                !long.TryParse(header[2], out var ticks) || !long.TryParse(header[3], out var length)) return false;
            var source = new FileInfo(sourcePath);
            if (source.LastWriteTimeUtc.Ticks != ticks || source.Length != length) return false;

            var rawFields = new List<(string Module, string Name, string Display, string Control, string? Default, bool ReadOnly, int Order)>();
            var nodes = new List<PropertyCardClassificationNode>();
            var rawRules = new List<(string Category, string Property, int Order)>();
            var optionValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var parts = lines[index].Split('|');
                if (parts.Length < 2) continue;
                var values = parts.Skip(1).Select(DecodeToken).ToArray();
                switch (parts[0])
                {
                    case "C" when values.Length >= 3:
                        nodes.Add(new PropertyCardClassificationNode(Null(values[0]), Null(values[1]), Null(values[2]), nodes.Count));
                        break;
                    case "D" when values.Length >= 2 && values[0].Length > 0 && values[1].Length > 0:
                        if (!optionValues.TryGetValue(values[0], out var options)) optionValues[values[0]] = options = [];
                        options.Add(values[1]);
                        break;
                    case "F" when values.Length >= 7 && values[0].Length > 0 && values[1].Length > 0:
                        rawFields.Add((
                            values[0], values[1], values[2].Length == 0 ? values[1] : values[2], values[3], Null(values[4]),
                            IsTrue(values[5]), int.TryParse(values[6], out var fieldOrder) ? fieldOrder : rawFields.Count));
                        break;
                    case "R" when values.Length >= 2 && values[0].Length > 0 && values[1].Length > 0:
                        rawRules.Add((values[0], values[1], rawRules.Count));
                        break;
                }
            }
            var normalizedNodes = FillClassificationHierarchy(nodes);
            var fields = rawFields.Select(field => new PropertyCardFieldDefinition(
                    field.Module, field.Name, field.Display, field.Control, field.Default, field.ReadOnly, field.Order,
                    optionValues.TryGetValue(field.Name, out var options)
                        ? options.Distinct(StringComparer.Ordinal).ToArray()
                        : null))
                .OrderBy(static field => field.Order).ThenBy(static field => field.DisplayName, StringComparer.Ordinal).ToArray();
            var rules = rawRules.Select(rule =>
                {
                    var node = normalizedNodes.LastOrDefault(node =>
                        string.Equals(node.Level3, rule.Category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(node.Level2, rule.Category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(node.Level1, rule.Category, StringComparison.OrdinalIgnoreCase));
                    return new PropertyCardClassificationRule(
                        node?.Level1,
                        node?.Level2,
                        node?.Level3 ?? rule.Category,
                        rule.Property,
                        rule.Order);
                })
                .OrderBy(static rule => rule.Order).ToArray();
            if (fields.Length == 0) return false;
            workbook = new ParsedPropertyCardWorkbook(fields, rules, normalizedNodes);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyList<PropertyCardClassificationNode> FillClassificationHierarchy(
        IEnumerable<PropertyCardClassificationNode> source)
    {
        var result = new List<PropertyCardClassificationNode>();
        string? level1 = null;
        string? level2 = null;
        foreach (var item in source.OrderBy(static item => item.Order))
        {
            if (item.Level1 is not null)
            {
                level1 = item.Level1;
                level2 = null;
            }
            if (item.Level2 is not null) level2 = item.Level2;
            result.Add(item with { Level1 = item.Level1 ?? level1, Level2 = item.Level2 ?? level2 });
        }
        return result;
    }

    private static string DecodeToken(string value)
    {
        if (value.Length == 0 || value.All(char.IsDigit) || value is "true" or "false") return value;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception exception) when (exception is FormatException or System.Text.DecoderFallbackException)
        {
            return value;
        }
    }

    private static bool IsTrue(string value) => value.Trim().ToUpperInvariant() is "1" or "TRUE" or "YES" or "是" or "只读";
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class XlsxPropertyCardParser
{
    internal static ParsedPropertyCardWorkbook Parse(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        copy.Position = 0;
        using var archive = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: false);
        var workbook = XlsxWorkbook.Read(archive);
        var fieldsSheet = workbook.RequireSheet("字段设置");
        var options = ParseOptions(workbook.TryGetSheet("下拉选项"));
        var fields = ParseFields(fieldsSheet, options);
        if (fields.Count == 0) throw new InvalidDataException("字段设置表没有可用字段。");
        var classifications = ParseClassifications(workbook.TryGetSheet("物料分类"));
        return new ParsedPropertyCardWorkbook(
            fields,
            ParseRules(workbook.TryGetSheet("分类属性"), classifications),
            classifications);
    }

    private static IReadOnlyList<PropertyCardFieldDefinition> ParseFields(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        var table = Table.Find(rows, ["SW属性名", "属性名", "属性名称"]);
        var result = new List<PropertyCardFieldDefinition>();
        for (var rowIndex = table.DataStart; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var propertyName = table.Value(row, "SW属性名", "属性名", "属性名称");
            var module = table.Value(row, "模块");
            if (string.IsNullOrWhiteSpace(propertyName) || string.IsNullOrWhiteSpace(module)) continue;
            var displayName = table.Value(row, "显示名", "显示名称", "名称");
            var orderText = table.Value(row, "顺序", "排序", "全局顺序");
            var order = int.TryParse(orderText, out var parsedOrder) ? parsedOrder : rowIndex - table.DataStart;
            result.Add(new PropertyCardFieldDefinition(
                module.Trim(),
                propertyName.Trim(),
                string.IsNullOrWhiteSpace(displayName) ? propertyName.Trim() : displayName.Trim(),
                table.Value(row, "控件", "控件类型")?.Trim() ?? string.Empty,
                EmptyToNull(table.Value(row, "默认值")),
                IsTrue(table.Value(row, "只读")),
                order,
                options.GetValueOrDefault(propertyName.Trim())));
        }
        return result
            .OrderBy(static field => field.Order)
            .ThenBy(static field => field.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseOptions(IReadOnlyList<IReadOnlyList<string>>? rows)
    {
        var result = new Dictionary<string, List<(int Order, int Row, string Value)>>(StringComparer.OrdinalIgnoreCase);
        if (rows is null || rows.Count == 0) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var table = Table.Find(rows, ["属性名", "属性名称", "SW属性名"]);
        var propertyColumn = table.Column("属性名", "属性名称", "SW属性名");
        var orderColumn = table.Column("顺序", "排序");
        var optionColumns = table.Headers
            .Where(pair => pair.Value != propertyColumn && pair.Value != orderColumn &&
                           (pair.Key.Contains("选项", StringComparison.Ordinal) || pair.Key is "值" or "内容"))
            .Select(static pair => pair.Value)
            .Distinct()
            .ToArray();
        if (optionColumns.Length == 0)
        {
            optionColumns = table.Headers.Values.Where(column => column != propertyColumn && column != orderColumn).Distinct().ToArray();
        }
        for (var rowIndex = table.DataStart; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var propertyName = Cell(row, propertyColumn).Trim();
            if (propertyName.Length == 0) continue;
            var orderText = Cell(row, orderColumn);
            var order = int.TryParse(orderText, out var parsed) ? parsed : rowIndex;
            foreach (var column in optionColumns)
            {
                foreach (var value in SplitOptions(Cell(row, column)))
                {
                    if (!result.TryGetValue(propertyName, out var list)) result[propertyName] = list = [];
                    list.Add((order, rowIndex, value));
                }
            }
        }
        return result.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value
                .OrderBy(static item => item.Order).ThenBy(static item => item.Row)
                .Select(static item => item.Value).Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PropertyCardClassificationRule> ParseRules(
        IReadOnlyList<IReadOnlyList<string>>? rows,
        IReadOnlyList<PropertyCardClassificationNode> classifications)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<PropertyCardClassificationRule>();
        var table = Table.Find(rows, ["SW属性名", "属性名", "属性名称", "字段"]);
        var result = new List<PropertyCardClassificationRule>();
        for (var rowIndex = table.DataStart; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var propertyName = table.Value(row, "SW属性名", "属性名", "属性名称", "字段");
            if (string.IsNullOrWhiteSpace(propertyName)) continue;
            var category = EmptyToNull(table.Value(row, "物料分类", "分类"));
            var level1 = EmptyToNull(table.Value(row, "一级分类", "一级", "大类"));
            var level2 = EmptyToNull(table.Value(row, "二级分类", "二级", "中类"));
            var level3 = EmptyToNull(table.Value(row, "三级分类", "三级", "小类")) ?? category;
            if (category is not null)
            {
                var node = classifications.LastOrDefault(node =>
                    string.Equals(node.Level3, category, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Level2, category, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Level1, category, StringComparison.OrdinalIgnoreCase));
                if (node is not null) (level1, level2, level3) = (node.Level1, node.Level2, node.Level3 ?? category);
            }
            var orderText = table.Value(row, "顺序", "排序", "显示顺序");
            result.Add(new PropertyCardClassificationRule(
                level1,
                level2,
                level3,
                propertyName.Trim(),
                int.TryParse(orderText, out var order) ? order : rowIndex - table.DataStart,
                EmptyToNull(table.Value(row, "模块"))));
        }
        return result.OrderBy(static rule => rule.Order).ToArray();
    }

    private static IReadOnlyList<PropertyCardClassificationNode> ParseClassifications(IReadOnlyList<IReadOnlyList<string>>? rows)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<PropertyCardClassificationNode>();
        var table = Table.Find(rows, ["一级分类", "一级", "大类"]);
        var result = new List<PropertyCardClassificationNode>();
        for (var rowIndex = table.DataStart; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var level1 = EmptyToNull(table.Value(row, "一级分类", "一级", "大类"));
            var level2 = EmptyToNull(table.Value(row, "二级分类", "二级", "中类"));
            var level3 = EmptyToNull(table.Value(row, "三级分类", "三级", "小类"));
            if (level1 is null && level2 is null && level3 is null) continue;
            var orderText = table.Value(row, "顺序", "排序");
            result.Add(new PropertyCardClassificationNode(
                level1, level2, level3,
                int.TryParse(orderText, out var order) ? order : rowIndex - table.DataStart));
        }
        return result.OrderBy(static node => node.Order).ToArray();
    }

    private static IEnumerable<string> SplitOptions(string value) => value
        .Split(['\r', '\n', '|', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static item => item.Length > 0);

    private static string Cell(IReadOnlyList<string> row, int column) =>
        column >= 0 && column < row.Count ? row[column] : string.Empty;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsTrue(string? value) => value?.Trim().ToUpperInvariant() is "1" or "TRUE" or "YES" or "是" or "只读";

    private sealed record Table(IReadOnlyDictionary<string, int> Headers, int DataStart)
    {
        internal static Table Find(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> requiredAlternatives)
        {
            for (var rowIndex = 0; rowIndex < Math.Min(rows.Count, 30); rowIndex++)
            {
                var headers = rows[rowIndex]
                    .Select((value, column) => (Name: Normalize(value), Column: column))
                    .Where(static pair => pair.Name.Length > 0)
                    .GroupBy(static pair => pair.Name, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.First().Column, StringComparer.Ordinal);
                if (requiredAlternatives.Any(name => headers.ContainsKey(Normalize(name)))) return new Table(headers, rowIndex + 1);
            }
            throw new InvalidDataException("配置表缺少预期表头。");
        }

        internal int Column(params string[] names)
        {
            foreach (var name in names)
            {
                if (Headers.TryGetValue(Normalize(name), out var column)) return column;
            }
            return -1;
        }

        internal string? Value(IReadOnlyList<string> row, params string[] names)
        {
            var column = Column(names);
            return column >= 0 && column < row.Count ? row[column] : null;
        }

        private static string Normalize(string value) => new(value
            .Where(static character => !char.IsWhiteSpace(character) && character is not '_' and not '-')
            .Select(char.ToUpperInvariant).ToArray());
    }
}

internal sealed class XlsxWorkbook
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> _sheets;
    private XlsxWorkbook(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> sheets) => _sheets = sheets;

    internal IReadOnlyList<IReadOnlyList<string>> RequireSheet(string name) =>
        TryGetSheet(name) ?? throw new InvalidDataException($"配置工作簿缺少{name}表。");

    internal IReadOnlyList<IReadOnlyList<string>>? TryGetSheet(string name) =>
        _sheets.TryGetValue(name, out var rows) ? rows : null;

    internal static XlsxWorkbook Read(ZipArchive archive)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        var sharedStrings = ReadSharedStrings(archive, main);
        var workbook = ReadXml(archive, "xl/workbook.xml");
        var rels = ReadXml(archive, "xl/_rels/workbook.xml.rels")
            .Root!.Elements(packageRelationships + "Relationship")
            .ToDictionary(
                element => (string)element.Attribute("Id")!,
                element => NormalizeEntryPath("xl", (string)element.Attribute("Target")!),
                StringComparer.Ordinal);
        var sheets = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Descendants(main + "sheet"))
        {
            var name = (string?)sheet.Attribute("name");
            var relationId = (string?)sheet.Attribute(relationships + "id");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(relationId) || !rels.TryGetValue(relationId, out var entry)) continue;
            sheets[name] = ReadRows(ReadXml(archive, entry), main, sharedStrings);
        }
        return new XlsxWorkbook(sheets);
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive, XNamespace main)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return Array.Empty<string>();
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        return document.Root!.Elements(main + "si")
            .Select(item => string.Concat(item.Descendants(main + "t").Select(static text => text.Value)))
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(
        XDocument document,
        XNamespace main,
        IReadOnlyList<string> sharedStrings)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in document.Descendants(main + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements(main + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
                var column = ColumnIndex(reference);
                var type = (string?)cell.Attribute("t");
                var value = type == "inlineStr"
                    ? string.Concat(cell.Descendants(main + "t").Select(static text => text.Value))
                    : cell.Element(main + "v")?.Value ?? string.Empty;
                if (type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                {
                    value = sharedStrings[sharedIndex];
                }
                cells[column] = value;
            }
            if (cells.Count == 0)
            {
                rows.Add(Array.Empty<string>());
                continue;
            }
            var values = new string[cells.Keys.Max() + 1];
            foreach (var cell in cells) values[cell.Key] = cell.Value;
            rows.Add(values);
        }
        return rows;
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"XLSX entry is missing: {path}");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static string NormalizeEntryPath(string root, string target)
    {
        var combined = target.StartsWith('/') ? target.TrimStart('/') : $"{root}/{target}";
        var stack = new Stack<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..") { if (stack.Count > 0) stack.Pop(); }
            else if (part != ".") stack.Push(part);
        }
        return string.Join('/', stack.Reverse());
    }

    private static int ColumnIndex(string reference)
    {
        var column = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character)) break;
            column = checked(column * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return Math.Max(0, column - 1);
    }
}
