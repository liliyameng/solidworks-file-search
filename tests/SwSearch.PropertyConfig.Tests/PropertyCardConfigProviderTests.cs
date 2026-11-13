using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SwSearch.Contracts;
using SwSearch.PropertyConfig;

namespace SwSearch.PropertyConfig.Tests;

public sealed class PropertyCardConfigProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwSearch-PropertyConfig-" + Guid.NewGuid().ToString("N"));

    public PropertyCardConfigProviderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Rc28_StartupPreloadRunsOnce_LastGoodIsImmediate_AndSessionSnapshotIsImmutable()
    {
        var active = Path.Combine(_root, "session", "config.xlsx");
        WriteWorkbook(active, displayName: "首次启动");
        var options = Options(WriteDefaults(active));

        await using (var first = new PropertyCardConfigProvider(options))
        {
            var immediate = await first.GetCurrentAsync();
            Assert.Equal(PropertyCardConfigSource.Unavailable, immediate.Source);
            var preload = first.StartPreload();
            Assert.Same(preload, first.StartPreload());
            await preload;
            Assert.Equal("首次启动", (await first.GetCurrentAsync()).Fields[0].DisplayName);

            File.Delete(active);
            WriteWorkbook(active, displayName: "会话内变化");
            await first.StartPreload();
            Assert.Equal("首次启动", (await first.GetCurrentAsync()).Fields[0].DisplayName);
        }

        await using (var second = new PropertyCardConfigProvider(options))
        {
            var lastGood = await second.GetCurrentAsync();
            Assert.Equal(PropertyCardConfigSource.LastKnownGood, lastGood.Source);
            Assert.Equal("首次启动", lastGood.Fields[0].DisplayName);
            await second.StartPreload();
            Assert.Equal("会话内变化", (await second.GetCurrentAsync()).Fields[0].DisplayName);
        }

        var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(options.SnapshotPath));
        Assert.Equal(2, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(persisted.RootElement.GetProperty("sourceLength").GetInt64() > 0);
        Assert.Equal(64, persisted.RootElement.GetProperty("contentSha256").GetString()!.Length);
    }

    [Fact]
    public async Task ActiveBase64Xlsx_ParsesModulesOrderOptionsAndClassification()
    {
        var active = Path.Combine(_root, "坚果云同步", "当前配置.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        WriteWorkbook(active);
        var defaults = WriteDefaults(active, includeSecretNoise: true);
        var options = Options(defaults);

        await using var provider = new PropertyCardConfigProvider(options);
        var snapshot = await provider.GetCurrentAsync();

        Assert.Equal(PropertyCardConfigSource.ActivePluginSetting, snapshot.Source);
        Assert.Equal(new[] { "物料信息", "材质", "属性填写" },
            snapshot.Fields.Select(static field => field.Module).Distinct());
        Assert.Equal(new[] { "编码", "材料", "规格" }, snapshot.Fields.Select(static field => field.DisplayName));
        Assert.Equal(new[] { "A", "B" }, snapshot.Fields.Single(static field => field.PropertyName == "规格").Options);
        Assert.Single(snapshot.ClassificationRules);
        Assert.DoesNotContain(Path.GetDirectoryName(active)!, snapshot.SourceLabel, StringComparison.OrdinalIgnoreCase);
        var persisted = await File.ReadAllTextAsync(options.SnapshotPath);
        Assert.DoesNotContain(active, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD_NOT_BE_READ", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingActive_UsesLastKnownGoodBeforeBackup()
    {
        var active = Path.Combine(_root, "active.xlsx");
        WriteWorkbook(active);
        var defaults = WriteDefaults(active);
        var options = Options(defaults);
        await using (var first = new PropertyCardConfigProvider(options))
        {
            Assert.Equal(PropertyCardConfigSource.ActivePluginSetting, (await first.GetCurrentAsync()).Source);
        }
        File.Delete(active);
        WriteWorkbook(options.BackupXlsxCandidates.Single(), displayName: "备用字段");

        await using var second = new PropertyCardConfigProvider(options);
        var snapshot = await second.GetCurrentAsync();

        Assert.Equal(PropertyCardConfigSource.LastKnownGood, snapshot.Source);
        Assert.Equal("编码", snapshot.Fields[0].DisplayName);
        Assert.True(snapshot.IsDegraded);
    }

    [Fact]
    public async Task FreshCache_IsUsedOnlyWhenItMatchesNormalizedXlsx()
    {
        var active = Path.Combine(_root, "active.xlsx");
        WriteWorkbook(active);
        var defaults = WriteDefaults(active);
        var options = Options(defaults);
        WriteCache(options.ActiveCacheCandidates.Single(), active, changedDisplayName: false);

        await using (var provider = new PropertyCardConfigProvider(options))
        {
            var snapshot = await provider.GetCurrentAsync();
            Assert.Contains("缓存", snapshot.SourceLabel, StringComparison.Ordinal);
        }

        File.Delete(options.SnapshotPath);
        WriteCache(options.ActiveCacheCandidates.Single(), active, changedDisplayName: true);
        await using (var provider = new PropertyCardConfigProvider(options))
        {
            var snapshot = await provider.GetCurrentAsync();
            Assert.DoesNotContain("缓存", snapshot.SourceLabel, StringComparison.Ordinal);
            Assert.Equal("编码", snapshot.Fields[0].DisplayName);
        }
    }

    [Fact]
    public async Task SettingsSwitch_BadTableAndRecovery_HotReloadWithoutReplacingGoodModel()
    {
        var firstPath = Path.Combine(_root, "sync-a", "config.xlsx");
        var secondPath = Path.Combine(_root, "sync-b", "config.xlsx");
        WriteWorkbook(firstPath, displayName: "第一份");
        var defaults = WriteDefaults(firstPath);
        var options = Options(defaults) with { WatchForChanges = true, Debounce = TimeSpan.FromMilliseconds(100) };
        await using var provider = new PropertyCardConfigProvider(options);
        Assert.Equal("第一份", (await provider.GetCurrentAsync()).Fields[0].DisplayName);

        WriteWorkbook(secondPath, displayName: "第二份");
        RewriteDefaults(defaults, secondPath);
        var switched = await WaitForAsync(provider, snapshot => snapshot.Source == PropertyCardConfigSource.ActivePluginSetting &&
                                                            snapshot.Fields[0].DisplayName == "第二份");
        Assert.Equal("第二份", switched.Fields[0].DisplayName);

        File.WriteAllText(secondPath, "not-an-xlsx", new UTF8Encoding(false));
        var degraded = await WaitForAsync(provider, snapshot => snapshot.Source == PropertyCardConfigSource.LastKnownGood);
        Assert.Equal("第二份", degraded.Fields[0].DisplayName);

        File.Delete(secondPath);
        WriteWorkbook(secondPath, displayName: "恢复后");
        var recovered = await WaitForAsync(provider, snapshot => snapshot.Source == PropertyCardConfigSource.ActivePluginSetting &&
                                                             snapshot.Fields[0].DisplayName == "恢复后");
        Assert.Equal("恢复后", recovered.Fields[0].DisplayName);
    }

    [Fact]
    public async Task Backup_IsUsedOnlyWhenActiveAndLastKnownGoodAreUnavailable()
    {
        var missing = Path.Combine(_root, "missing", "config.xlsx");
        var defaults = WriteDefaults(missing);
        var options = Options(defaults);
        WriteWorkbook(options.BackupXlsxCandidates.Single(), displayName: "备用字段");
        await using var provider = new PropertyCardConfigProvider(options);

        var snapshot = await provider.GetCurrentAsync();

        Assert.Equal(PropertyCardConfigSource.BackupXlsx, snapshot.Source);
        Assert.Equal("备用字段", snapshot.Fields[0].DisplayName);
        Assert.True(snapshot.IsDegraded);
    }

    [Fact]
    public async Task Rc20CustomPreference_OverridesPluginPointerWithoutChangingPluginFile()
    {
        var pluginActive = Path.Combine(_root, "plugin", "active.xlsx");
        var custom = Path.Combine(_root, "nutstore", "selected.xlsx");
        WriteWorkbook(pluginActive, displayName: "插件路径");
        WriteWorkbook(custom, displayName: "RC20 自定义路径");
        var defaults = WriteDefaults(pluginActive);
        var originalDefaults = File.ReadAllText(defaults);
        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new
        {
            propertyCardSource = 1,
            propertyCardPath = custom,
        }));

        await using var provider = new PropertyCardConfigProvider(
            Options(defaults) with { ProductSettingsPath = settings });
        var snapshot = await provider.GetCurrentAsync();

        Assert.Equal(PropertyCardConfigSource.ActivePluginSetting, snapshot.Source);
        Assert.Equal("RC20 自定义路径", snapshot.Fields[0].DisplayName);
        Assert.Equal(originalDefaults, File.ReadAllText(defaults));
    }

    [Fact]
    public async Task Rc20MissingCustomPath_UsesSameDirectoryBackupWorkbook()
    {
        var directory = Path.Combine(_root, "nutstore");
        var missing = Path.Combine(directory, "selected.xlsx");
        var backup = Path.Combine(directory, "plm-config.xlsx");
        WriteWorkbook(backup, displayName: "同目录备用表格");
        var defaults = WriteDefaults(Path.Combine(_root, "plugin-missing.xlsx"));
        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new
        {
            propertyCardSource = "custom",
            propertyCardPath = missing,
        }));

        await using var provider = new PropertyCardConfigProvider(
            Options(defaults) with { ProductSettingsPath = settings });
        var snapshot = await provider.GetCurrentAsync();

        Assert.Equal(PropertyCardConfigSource.BackupXlsx, snapshot.Source);
        Assert.Equal("同目录备用表格", snapshot.Fields[0].DisplayName);
        Assert.True(snapshot.IsDegraded);
    }

    [Fact]
    public async Task RealisticWorkbook_HasRealScaleMultiPathRulesWithoutSyntheticModule()
    {
        var active = Path.Combine(_root, "realistic.xlsx");
        WriteLargeWorkbook(active);
        var defaults = WriteDefaults(active);
        await using var provider = new PropertyCardConfigProvider(Options(defaults));

        var snapshot = await provider.GetCurrentAsync();

        Assert.Equal(97, snapshot.Fields.Count);
        Assert.Equal(7, snapshot.Fields.Count(static field => field.Module == "物料信息"));
        Assert.Equal(2, snapshot.Fields.Count(static field => field.Module == "材质"));
        Assert.Equal(88, snapshot.Fields.Count(static field => field.Module == "属性填写"));
        Assert.Equal(605, snapshot.Classifications.Count);
        Assert.Equal(666, snapshot.ClassificationRules.Count);
        Assert.Equal(327, RulePathCount(snapshot.ClassificationRules));
        Assert.Equal(326, CompleteRulePathCount(snapshot.ClassificationRules));
        Assert.Equal(86, snapshot.ClassificationRules.Select(static rule => rule.PropertyName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(85, snapshot.ClassificationRules.Select(static rule => rule.PropertyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Intersect(snapshot.Fields.Select(static field => field.PropertyName), StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(MaxConfiguredFieldsPerPath(snapshot) <= 5);
        Assert.DoesNotContain(snapshot.Fields, static field => field.Module == "物料分类");
        Assert.Equal(new[] { "物料信息", "材质", "属性填写" },
            snapshot.Fields.Select(static field => field.Module).Distinct());
    }

    [Fact]
    public async Task CurrentInstalledPluginConfiguration_ParsesWithoutExposingPath()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWSEARCH_REAL_PROPERTY_CONFIG"), "1", StringComparison.Ordinal)) return;
        var options = PropertyCardConfigProviderOptions.CreateDefault(_root, _root) with { WatchForChanges = false };
        var defaultsLine = File.ReadLines(options.PluginDefaultsPath)
            .Single(static line => line.StartsWith("__ConfigFilePath\t", StringComparison.Ordinal));
        var activePath = Encoding.UTF8.GetString(Convert.FromBase64String(defaultsLine.Split('\t')[1]));
        var direct = XlsxPropertyCardParser.Parse(activePath);
        Assert.Equal(97, direct.Fields.Count);
        Assert.Equal(7, direct.Fields.Count(static field => field.Module == "物料信息"));
        Assert.Equal(2, direct.Fields.Count(static field => field.Module == "材质"));
        Assert.Equal(88, direct.Fields.Count(static field => field.Module == "属性填写"));
        Assert.Equal(605, direct.Classifications.Count);
        Assert.Equal(666, direct.ClassificationRules.Count);
        Assert.Equal(327, RulePathCount(direct.ClassificationRules));
        Assert.Equal(322, CompleteRulePathCount(direct.ClassificationRules));
        Assert.Equal(86, direct.ClassificationRules.Select(static rule => rule.PropertyName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(85, direct.ClassificationRules.Select(static rule => rule.PropertyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Intersect(direct.Fields.Select(static field => field.PropertyName), StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(5, MaxConfiguredFieldsPerPath(direct));
        Assert.DoesNotContain(direct.Fields, static field => field.Module == "物料分类");
        await using var provider = new PropertyCardConfigProvider(options);
        var snapshot = await provider.GetCurrentAsync();
        Assert.NotEqual(PropertyCardConfigSource.Unavailable, snapshot.Source);
        Assert.NotEmpty(snapshot.Fields);
        Assert.DoesNotContain("\\", snapshot.SourceLabel, StringComparison.Ordinal);
    }

    private static int CompleteRulePathCount(IReadOnlyList<PropertyCardClassificationRule> rules) =>
        rules.Where(static rule => !string.IsNullOrWhiteSpace(rule.Level1) &&
                                   !string.IsNullOrWhiteSpace(rule.Level2) &&
                                   !string.IsNullOrWhiteSpace(rule.Level3))
            .Select(static rule => (rule.Level1!, rule.Level2!, rule.Level3!))
            .Distinct()
            .Count();

    private static int RulePathCount(IReadOnlyList<PropertyCardClassificationRule> rules) =>
        rules.Select(static rule => (rule.Level1, rule.Level2, rule.Level3))
            .Distinct()
            .Count();

    private static int MaxConfiguredFieldsPerPath(PropertyCardConfigSnapshot snapshot) =>
        MaxConfiguredFieldsPerPath(snapshot.Fields, snapshot.ClassificationRules);

    private static int MaxConfiguredFieldsPerPath(ParsedPropertyCardWorkbook workbook) =>
        MaxConfiguredFieldsPerPath(workbook.Fields, workbook.ClassificationRules);

    private static int MaxConfiguredFieldsPerPath(
        IReadOnlyList<PropertyCardFieldDefinition> fields,
        IReadOnlyList<PropertyCardClassificationRule> rules)
    {
        var fieldNames = fields.Select(static field => field.PropertyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rules.Where(static rule => !string.IsNullOrWhiteSpace(rule.Level1) &&
                                          !string.IsNullOrWhiteSpace(rule.Level2) &&
                                          !string.IsNullOrWhiteSpace(rule.Level3))
            .GroupBy(static rule => (rule.Level1!, rule.Level2!, rule.Level3!))
            .Select(group => group.Select(static rule => rule.PropertyName)
                .Where(fieldNames.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count())
            .DefaultIfEmpty(0)
            .Max();
    }

    private PropertyCardConfigProviderOptions Options(string defaults) => new(
        defaults,
        Path.Combine(_root, "snapshot.json"),
        [Path.Combine(_root, "plm-config.cache")],
        [Path.Combine(_root, "backup.xlsx")],
        WatchForChanges: false);

    private string WriteDefaults(string activePath, bool includeSecretNoise = false)
    {
        var defaults = Path.Combine(_root, "defaults.tsv");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(activePath));
        var text = includeSecretNoise
            ? $"OtherKey\tSHOULD_NOT_BE_READ\n__ConfigFilePath\t{encoded}\nCookie\tSECRET\n"
            : $"__ConfigFilePath\t{encoded}\n";
        File.WriteAllText(defaults, text, new UTF8Encoding(false));
        return defaults;
    }

    private static void RewriteDefaults(string defaults, string activePath)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(activePath));
        File.WriteAllText(defaults, $"__ConfigFilePath\t{encoded}\n", new UTF8Encoding(false));
    }

    private static async Task<PropertyCardConfigSnapshot> WaitForAsync(
        PropertyCardConfigProvider provider,
        Func<PropertyCardConfigSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        PropertyCardConfigSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await provider.GetCurrentAsync();
            if (predicate(last)) return last;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Hot reload did not reach the expected state; last source={last?.Source}.");
    }

    private static void WriteWorkbook(string path, string displayName = "编码")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sheets = new Dictionary<string, string[][]>
        {
            ["说明"] = [["说明"], ["测试"]],
            ["物料分类"] = [["一级分类", "二级分类", "三级分类", "顺序"], ["管件", "接头", "等径接头", "0"]],
            ["字段设置"] =
            [
                ["模块", "SW属性名", "显示名", "控件", "默认值", "只读", "顺序"],
                ["物料信息", "编码", displayName, "TextBox", "", "0", "1"],
                ["材质", "材料", "材料", "ComboBox", "", "0", "2"],
                ["属性填写", "规格", "规格", "ComboBox", "", "0", "3"],
            ],
            ["分类属性"] = [["一级分类", "二级分类", "三级分类", "SW属性名", "顺序"], ["管件", "接头", "等径接头", "规格", "0"]],
            ["下拉选项"] = [["属性名", "选项", "顺序"], ["规格", "A", "1"], ["规格", "B", "2"]],
        };
        WriteSheets(path, sheets);
    }

    private static void WriteLargeWorkbook(string path)
    {
        var fields = new List<string[]>
        {
            new[] { "模块", "属性名称", "显示名称", "控件类型", "默认值", "只读", "顺序" },
        };
        for (var index = 0; index < 7; index++)
            fields.Add(["物料信息", $"信息-{index:D2}", $"信息 {index:D2}", "TextBox", "", "0", (index + 1).ToString()]);
        for (var index = 0; index < 2; index++)
            fields.Add(["材质", $"材质表字段-{index:D2}", $"材质表字段 {index:D2}", "ComboBox", "", "0", (index + 8).ToString()]);
        for (var index = 0; index < 88; index++)
            fields.Add(["属性填写", $"分类字段-{index:D2}", $"分类字段 {index:D2}", index == 0 ? "ComboBox" : "TextBox", "", "0", (index + 10).ToString()]);

        var classifications = new List<string[]> { new[] { "一级分类", "二级分类", "三级分类", "顺序" } };
        var completePaths = new List<(string Level1, string Level2, string Level3)>();
        for (var index = 0; index < 327; index++)
        {
            var level1 = $"一级{index % 7:D2}";
            var level2 = $"二级{index % 7:D2}-{(index / 7) % 13:D2}";
            var level3 = $"三级{index:D3}";
            completePaths.Add((level1, level2, level3));
            classifications.Add([level1, level2, level3, index.ToString()]);
        }
        for (var index = 0; index < 278; index++)
        {
            var level1 = $"一级{index % 7:D2}";
            var level2 = $"二级{index % 7:D2}-{(index / 7) % 13:D2}";
            classifications.Add((index % 3) switch
            {
                0 => [level1, "", "", (327 + index).ToString()],
                1 => [level1, level2, "", (327 + index).ToString()],
                _ => ["", $"孤立二级{index:D3}", "", (327 + index).ToString()],
            });
        }

        var rules = new List<string[]> { new[] { "一级分类", "二级分类", "三级分类", "属性名称", "显示顺序" } };
        for (var index = 0; index < 326; index++)
        {
            var pathKey = completePaths[index];
            rules.Add([pathKey.Level1, pathKey.Level2, pathKey.Level3, $"分类字段-{(index * 2) % 85:D2}", rules.Count.ToString()]);
            rules.Add([pathKey.Level1, pathKey.Level2, pathKey.Level3, $"分类字段-{(index * 2 + 1) % 85:D2}", rules.Count.ToString()]);
            if (index < 13)
                rules.Add([pathKey.Level1, pathKey.Level2, pathKey.Level3, $"分类字段-{(index * 2 + 2) % 85:D2}", rules.Count.ToString()]);
        }
        rules.Add([completePaths[0].Level1, "", "", "规则中但字段表不存在", "999"]);
        var sheets = new Dictionary<string, string[][]>
        {
            ["说明"] = [["说明"], ["真实结构规模测试"]],
            ["物料分类"] = classifications.ToArray(),
            ["字段设置"] = fields.ToArray(),
            ["分类属性"] = rules.ToArray(),
            ["下拉选项"] = [["属性名称", "选项", "排序"], ["分类字段-00", "先", "1"], ["分类字段-00", "后", "2"]],
        };
        WriteSheets(path, sheets);
    }

    private static void WriteSheets(string path, IReadOnlyDictionary<string, string[][]> sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var workbook = new XDocument(new XElement(main + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", rel),
            new XElement(main + "sheets", sheets.Keys.Select((name, index) =>
                new XElement(main + "sheet", new XAttribute("name", name), new XAttribute("sheetId", index + 1),
                    new XAttribute(rel + "id", "rId" + (index + 1)))))));
        WriteXml(archive, "xl/workbook.xml", workbook);
        var relationships = new XDocument(new XElement(packageRel + "Relationships", sheets.Keys.Select((_, index) =>
            new XElement(packageRel + "Relationship", new XAttribute("Id", "rId" + (index + 1)),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", "worksheets/sheet" + (index + 1) + ".xml")))));
        WriteXml(archive, "xl/_rels/workbook.xml.rels", relationships);
        var sheetIndex = 0;
        foreach (var rows in sheets.Values)
        {
            sheetIndex++;
            var sheet = new XDocument(new XElement(main + "worksheet", new XElement(main + "sheetData",
                rows.Select((row, rowIndex) => new XElement(main + "row", new XAttribute("r", rowIndex + 1),
                    row.Select((value, column) => new XElement(main + "c",
                        new XAttribute("r", ColumnName(column) + (rowIndex + 1)), new XAttribute("t", "inlineStr"),
                        new XElement(main + "is", new XElement(main + "t", value)))))))));
            WriteXml(archive, $"xl/worksheets/sheet{sheetIndex}.xml", sheet);
        }
    }

    private static void WriteCache(string path, string sourcePath, bool changedDisplayName)
    {
        var info = new FileInfo(sourcePath);
        var lines = new List<string>
        {
            $"TCPLMCONFIGCACHE|2|{info.LastWriteTimeUtc.Ticks}|{info.Length}",
            CacheLine("C", "管件", "接头", "等径接头"),
            CacheLine("F", "物料信息", "编码", changedDisplayName ? "不同" : "编码", "TextBox", "", "0", "1", ""),
            CacheLine("F", "材质", "材料", "材料", "ComboBox", "", "0", "2", ""),
            CacheLine("F", "属性填写", "规格", "规格", "ComboBox", "", "0", "3", ""),
            CacheLine("R", "等径接头", "规格"),
            CacheLine("D", "规格", "A"),
            CacheLine("D", "规格", "B"),
        };
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }

    private static string CacheLine(string tag, params string[] values) => tag + "|" + string.Join('|', values.Select(value =>
        value is "0" or "1" or "2" or "3" or "4" ? value : Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));

    private static void WriteXml(ZipArchive archive, string name, XDocument document)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        document.Save(stream);
    }

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        for (var value = column + 1; value > 0; value = (value - 1) / 26) result = (char)('A' + (value - 1) % 26) + result;
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
