using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;

namespace SwSearch.PropertyViewer.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Rc34NativePropertyViewerTests
{
    private const string FilePath = @"C:\设计 库\泵 A.SLDPRT";
    private static readonly DateTimeOffset Modified = new(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CacheHit_DefaultsToConfigurationWithoutBrokerWait()
    {
        var never = new TaskCompletionSource<PropertyExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileSystem = new FakeFileSystem();
        var client = new FakeDataClient(Metadata(), broker: _ => new ValueTask<PropertyExtractionResult>(never.Task));
        var viewModel = CreateViewModel(fileSystem, client);
        Assert.AreEqual(0, fileSystem.AccessCount, "Construction must not perform file I/O on the UI thread.");
        var clock = Stopwatch.StartNew();

        await viewModel.LoadAsync().WaitAsync(TimeSpan.FromMilliseconds(150));

        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(150));
        Assert.AreEqual(1, viewModel.SelectedModeIndex);
        Assert.AreEqual(NativePropertyMode.Configuration, viewModel.SelectedMode);
        CollectionAssert.AreEqual(new[] { "配置字段" }, viewModel.Rows.Select(static row => row.Name).ToArray());
        Assert.AreEqual("属性已显示 · 正在鲜读", viewModel.StatusText);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(1, client.BrokerRefreshCount);
    }

    [TestMethod]
    public async Task ConfigurationMode_OnlyShowsSelectedConfigurationAndSwitchesCleanly()
    {
        var viewModel = CreateViewModel(new FakeFileSystem(), new FakeDataClient(Metadata()));
        await viewModel.LoadAsync();

        Assert.AreEqual(1, viewModel.SelectedModeIndex);
        Assert.AreEqual(NativePropertyMode.Configuration, viewModel.SelectedMode);
        Assert.AreEqual(System.Windows.Visibility.Visible, viewModel.ConfigurationSelectorVisibility);
        Assert.AreEqual("默认", viewModel.SelectedConfiguration);
        CollectionAssert.AreEqual(new[] { "配置字段" }, viewModel.Rows.Select(static row => row.Name).ToArray());

        viewModel.SelectedConfiguration = "派生";
        CollectionAssert.AreEqual(new[] { "派生字段" }, viewModel.Rows.Select(static row => row.Name).ToArray());
        Assert.IsFalse(viewModel.Rows.Any(static row => row.Name == "文档字段"));

        viewModel.SelectedModeIndex = 0;
        CollectionAssert.AreEqual(new[] { "文档字段" }, viewModel.Rows.Select(static row => row.Name).ToArray());
        Assert.AreEqual(System.Windows.Visibility.Collapsed, viewModel.ConfigurationSelectorVisibility);
    }

    [TestMethod]
    public async Task CacheMiss_ShowsImmediateNativeStatusThenUsesBrokerFreshRead()
    {
        var extracted = new TaskCompletionSource<PropertyExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDataClient(null, _ => new ValueTask<PropertyExtractionResult>(extracted.Task));
        var viewModel = CreateViewModel(new FakeFileSystem(), client);

        Assert.AreEqual("暂无已提取属性，正在读取此文件", viewModel.StatusText);
        Assert.IsTrue(viewModel.IsBusy);
        var load = viewModel.LoadAsync();
        await WaitUntilAsync(() => client.BrokerRefreshCount == 1);
        Assert.AreEqual("暂无已提取属性，正在读取此文件", viewModel.StatusText);

        extracted.SetResult(new PropertyExtractionResult(Metadata(), null, null, false, Fresh: true));
        await load.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual("属性已读取（鲜读）", viewModel.StatusText);
        Assert.IsFalse(viewModel.IsBusy);
        CollectionAssert.AreEqual(new[] { "配置字段" }, viewModel.Rows.Select(static row => row.Name).ToArray());
        Assert.AreEqual(1, client.BrokerRefreshCount);
    }

    [TestMethod]
    public async Task BrokerFailure_StopsBusyStateWithSpecificMessage()
    {
        var client = new FakeDataClient(
            null,
            _ => ValueTask.FromResult(new PropertyExtractionResult(
                null, "BROKER_TIMEOUT", "ignored", true)));
        var viewModel = CreateViewModel(new FakeFileSystem(), client);
        viewModel.SelectedModeIndex = 0;

        await viewModel.LoadAsync().WaitAsync(TimeSpan.FromMilliseconds(150));

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual("鲜读超时（6.5 秒），可稍后重试。", viewModel.StatusText);
        Assert.AreEqual(0, viewModel.Rows.Count);
    }

    [TestMethod]
    public async Task BrokerRefresh_AtomicallyReplacesCachedProjectionWithoutBusyState()
    {
        var refreshed = new TaskCompletionSource<PropertyExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDataClient(
            Metadata(documentValue: "旧值"),
            broker: _ => new ValueTask<PropertyExtractionResult>(refreshed.Task));
        var viewModel = CreateViewModel(new FakeFileSystem(), client);
        viewModel.SelectedModeIndex = 0;

        await viewModel.LoadAsync();
        Assert.AreEqual("旧值", viewModel.Rows.Single().Value);
        Assert.IsFalse(viewModel.IsBusy);

        refreshed.SetResult(new PropertyExtractionResult(
            Metadata(documentValue: "新值", extractedAtUtc: Modified), null, null, false, Fresh: true));
        await WaitUntilAsync(() => viewModel.StatusText == "属性已更新（鲜读）");
        Assert.AreEqual("新值", viewModel.Rows.Single().Value);
        Assert.IsFalse(viewModel.IsBusy);
    }

    [TestMethod]
    public async Task BrokerRefreshFailure_KeepsCachedProjectionAndMarksItStale()
    {
        var client = new FakeDataClient(
            Metadata(documentValue: "缓存值"),
            broker: _ => ValueTask.FromResult(new PropertyExtractionResult(
                Metadata(documentValue: "Broker 返回的旧缓存"),
                "BROKER_TIMEOUT",
                "属性鲜读超时。",
                true,
                Fresh: false,
                CacheStale: true)));
        var viewModel = CreateViewModel(new FakeFileSystem(), client);
        viewModel.SelectedModeIndex = 0;

        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.StatusText.Contains("鲜读未完成", StringComparison.Ordinal));

        Assert.AreEqual("缓存值", viewModel.Rows.Single().Value);
        StringAssert.Contains(viewModel.StatusText, "当前显示缓存");
        StringAssert.Contains(viewModel.StatusText, "鲜读超时");
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(1, client.BrokerRefreshCount);
    }

    [TestMethod]
    public void ProductionEntry_IsNativeAndHasNoPropertyWebViewCallGraph()
    {
        var root = FindRepositoryRoot();
        var viewerRoot = Path.Combine(root, "src", "SwSearch.PropertyViewer");
        var app = File.ReadAllText(Path.Combine(viewerRoot, "App.xaml.cs"));
        var project = File.ReadAllText(Path.Combine(viewerRoot, "SwSearch.PropertyViewer.csproj"));
        var window = File.ReadAllText(Path.Combine(viewerRoot, "NativePropertyWindow.xaml"));

        StringAssert.Contains(app, "new NativePropertyViewModel");
        StringAssert.Contains(app, "new NativePropertyWindow");
        StringAssert.Contains(window, "<DataGrid");
        StringAssert.Contains(window, "ModeOptions");
        Assert.IsFalse(window.Contains("ReferencedModels", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("SelectedReferencedModel", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("SourceText", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("RowHeight=\"Auto\"", StringComparison.Ordinal));
        StringAssert.Contains(window, "MinRowHeight=\"36\"");
        StringAssert.Contains(window, "ColumnHeaderHeight=\"38\"");
        StringAssert.Contains(window, "Padding\" Value=\"10,6\"");
        StringAssert.Contains(window, "AlternationCount=\"2\"");
        StringAssert.Contains(window, "TextWrapping\" Value=\"Wrap\"");
        Assert.IsFalse(app.Contains("new PropertyWindow(", StringComparison.Ordinal));
        Assert.IsFalse(app.Contains("CompositionWebViewHost", StringComparison.Ordinal));
        Assert.IsFalse(project.Contains("SwSearch.WebUi", StringComparison.Ordinal));
        var client = File.ReadAllText(Path.Combine(viewerRoot, "PropertyDataClient.cs"));
        Assert.IsFalse(client.Contains("MetadataPipeName", StringComparison.Ordinal));
        Assert.IsFalse(client.Contains("Process.Start", StringComparison.Ordinal));
        Assert.IsFalse(client.Contains("ExtractSingleFileAsync", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(viewerRoot, "PropertyWebBridge.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(viewerRoot, "PropertyWindow.xaml")));
        Assert.IsFalse(File.Exists(Path.Combine(viewerRoot, "PropertyViewerViewModel.cs")));
        var viewModel = File.ReadAllText(Path.Combine(viewerRoot, "NativePropertyViewModel.cs"));
        Assert.IsFalse(viewModel.Contains("ReferencedModel", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("Drawing", StringComparison.Ordinal));
        Assert.IsFalse(client.Contains("EnrichDrawing", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeWindow_XamlLoadsShowsAndClosesOnStaThread()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application();
                application.Resources["ViewerBorder"] = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(198, 202, 208));
                var viewModel = new NativePropertyViewModel(
                    PathValidationResult.Invalid("测试参数", "TEST"),
                    new FakeFileSystem(),
                    new UnavailablePropertyDataClient());
                var window = new NativePropertyWindow(viewModel)
                {
                    ShowInTaskbar = false,
                    WindowStyle = System.Windows.WindowStyle.ToolWindow,
                    Width = 640,
                    Height = 440,
                };
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.Close();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(3)), "Native window smoke test timed out.");
        thread.Join(TimeSpan.FromSeconds(1));
        Assert.IsNull(failure, failure?.ToString());
    }

    [TestMethod]
    public void MissingInvalidAndLegacyPreferences_MigrateToConfiguration()
    {
        Assert.AreEqual(NativePropertyMode.Configuration, NativePropertyViewModel.ResolveInitialMode(null));
        Assert.AreEqual(NativePropertyMode.Configuration, NativePropertyViewModel.ResolveInitialMode(""));
        Assert.AreEqual(NativePropertyMode.Configuration, NativePropertyViewModel.ResolveInitialMode("Card"));
        Assert.AreEqual(NativePropertyMode.Configuration, NativePropertyViewModel.ResolveInitialMode("Custom"));
        Assert.AreEqual(NativePropertyMode.Configuration, NativePropertyViewModel.ResolveInitialMode("invalid"));
    }

    [TestMethod]
    public void PathValidator_AcceptsPartsAndAssembliesOnly()
    {
        Assert.IsTrue(PropertyPathValidator.ValidateSyntax(@"C:\Design\A.SLDPRT").IsValid);
        Assert.IsTrue(PropertyPathValidator.ValidateSyntax(@"C:\Design\B.SLDASM").IsValid);

        var drawing = PropertyPathValidator.ValidateSyntax(@"C:\Design\Legacy.SLDDRW");

        Assert.IsFalse(drawing.IsValid);
        Assert.AreEqual("EXTENSION_NOT_ALLOWED", drawing.ErrorCode);
    }

    [TestMethod]
    public async Task MaterialPhysicalFields_AreHiddenButMaterialNameAndStrengthGradeRemainInBothModes()
    {
        var viewModel = CreateViewModel(new FakeFileSystem(), new FakeDataClient(MaterialMetadata()));
        var hidden = new[]
        {
            "材料分类", "材料库", "材料ID", "比热", "弹性模量", "泊松比", "屈服力",
            "热扩张系数", "硬化因子", "张力强度", "质量密度",
        };

        await viewModel.LoadAsync();
        CollectionAssert.DoesNotContain(viewModel.Rows.Select(static row => row.Name).ToArray(), hidden[0]);
        Assert.IsFalse(viewModel.Rows.Any(row => hidden.Contains(row.Name, StringComparer.Ordinal)));
        Assert.AreEqual("AISI 321", viewModel.Rows.Single(row => row.Name == "材料名称").Value);
        Assert.AreEqual("A4", viewModel.Rows.Single(row => row.Name == "强度等级").Value);
        Assert.IsFalse(viewModel.Rows.Any(static row => row.Name is "材料" or "材质"),
            "Equivalent material-name aliases must be de-duplicated in favor of 材料名称/材质.");

        viewModel.SelectedModeIndex = 0;
        Assert.IsFalse(viewModel.Rows.Any(row => hidden.Contains(row.Name, StringComparer.Ordinal)));
        Assert.AreEqual("AISI 321", viewModel.Rows.Single(row => row.Name == "材料名称").Value);
        Assert.AreEqual("A4", viewModel.Rows.Single(row => row.Name == "强度等级").Value);
    }

    [TestMethod]
    public async Task ConfiguredPropertyOrderComesFirstAndOtherPropertiesFollowInBothModes()
    {
        var viewModel = CreateViewModel(
            new FakeFileSystem(),
            new FakeDataClient(OrderedMetadata()),
            ["材料名称", "图号", "不存在的属性"]);

        await viewModel.LoadAsync();
        CollectionAssert.AreEqual(
            new[] { "材料名称", "图号", "其他属性" },
            viewModel.Rows.Select(static row => row.Name).ToArray());

        viewModel.SelectedModeIndex = 0;
        CollectionAssert.AreEqual(
            new[] { "材料名称", "图号", "其他属性" },
            viewModel.Rows.Select(static row => row.Name).ToArray());
    }

    [TestMethod]
    public void PropertyDisplayOrderReader_IsFailSoftAndNormalizesExactNames()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "swsearch-property-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var path = Path.Combine(sandbox, "settings.json");
            File.WriteAllText(path, """{"propertyDisplayOrder":["图号"," 材料名称 ","图号",12]}""");
            CollectionAssert.AreEqual(
                new[] { "图号", "材料名称" },
                PropertyDisplayOrderReader.Read(path).ToArray());
            File.WriteAllText(path, "{broken");
            Assert.AreEqual(0, PropertyDisplayOrderReader.Read(path).Count);
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    private static NativePropertyViewModel CreateViewModel(
        IViewerFileSystem fileSystem,
        IPropertyDataClient client,
        IReadOnlyList<string>? propertyDisplayOrder = null) =>
        new(
            PathValidationResult.Valid(FilePath),
            fileSystem,
            client,
            propertyDisplayOrderLoader: () => propertyDisplayOrder ?? Array.Empty<string>());

    private static DocumentMetadata Metadata(
        string documentValue = "文档值",
        DateTimeOffset? extractedAtUtc = null) => new(
        new FileCandidate(FilePath, SolidWorksDocumentKind.Part, 123, Modified),
        [
            new DocumentProperty("文档字段", documentValue, documentValue, PropertyScope.Document, null, MetadataSource.PersistentCache),
            new DocumentProperty("配置字段", "默认值", "默认值", PropertyScope.Configuration, "默认", MetadataSource.PersistentCache),
            new DocumentProperty("派生字段", "派生值", "派生值", PropertyScope.Configuration, "派生", MetadataSource.PersistentCache),
        ],
        [], MetadataSource.PersistentCache, extractedAtUtc ?? Modified, ConfigurationNames: ["默认", "派生"]);

    private static DocumentMetadata MaterialMetadata()
    {
        var names = new[]
        {
            "材料分类", "材料库", "材料ID", "比热", "弹性模量", "泊松比", "屈服力",
            "热扩张系数", "硬化因子", "张力强度", "质量密度",
        };
        var properties = new List<DocumentProperty>();
        foreach (var scope in new[] { PropertyScope.Document, PropertyScope.Configuration })
        {
            var configuration = scope == PropertyScope.Configuration ? "默认" : null;
            properties.Add(new DocumentProperty("材料", "次选", "次选", scope, configuration, MetadataSource.PersistentCache));
            properties.Add(new DocumentProperty("材质", "次选2", "次选2", scope, configuration, MetadataSource.PersistentCache));
            properties.Add(new DocumentProperty("材料名称", "AISI 321", "AISI 321", scope, configuration, MetadataSource.PersistentCache));
            properties.Add(new DocumentProperty("强度等级", "A4", "A4", scope, configuration, MetadataSource.PersistentCache));
            properties.AddRange(names.Select(name => new DocumentProperty(
                name, "应隐藏", "应隐藏", scope, configuration, MetadataSource.PersistentCache)));
            properties.Add(new DocumentProperty(
                "SW-Material-Density", "应隐藏", "应隐藏", scope, configuration, MetadataSource.PersistentCache));
        }
        return new DocumentMetadata(
            new FileCandidate(FilePath, SolidWorksDocumentKind.Part, 123, Modified),
            properties, [], MetadataSource.PersistentCache, Modified, ConfigurationNames: ["默认"]);
    }

    private static DocumentMetadata OrderedMetadata()
    {
        var properties = new List<DocumentProperty>();
        foreach (var scope in new[] { PropertyScope.Document, PropertyScope.Configuration })
        {
            var configuration = scope == PropertyScope.Configuration ? "默认" : null;
            properties.Add(new DocumentProperty("其他属性", "3", "3", scope, configuration, MetadataSource.PersistentCache));
            properties.Add(new DocumentProperty("图号", "2", "2", scope, configuration, MetadataSource.PersistentCache));
            properties.Add(new DocumentProperty("材料名称", "AISI 321", "AISI 321", scope, configuration, MetadataSource.PersistentCache));
        }
        return new DocumentMetadata(
            new FileCandidate(FilePath, SolidWorksDocumentKind.Part, 123, Modified),
            properties, [], MetadataSource.PersistentCache, Modified, ConfigurationNames: ["默认"]);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FakeFileSystem : IViewerFileSystem
    {
        public int AccessCount { get; private set; }

        public bool FileExists(string fullPath)
        {
            AccessCount++;
            return true;
        }

        public ViewerFileSnapshot GetSnapshot(string fullPath)
        {
            AccessCount++;
            return new ViewerFileSnapshot(123, Modified);
        }
    }

    private sealed class FakeDataClient : IPropertyDataClient
    {
        private readonly DocumentMetadata? _cached;
        private readonly Func<CancellationToken, ValueTask<PropertyExtractionResult>> _broker;

        public FakeDataClient(
            DocumentMetadata? cached,
            Func<CancellationToken, ValueTask<PropertyExtractionResult>>? broker = null)
        {
            _cached = cached;
            _broker = broker ?? (_ => ValueTask.FromResult(new PropertyExtractionResult(
                null, "BROKER_UNUSED", "unused", false)));
        }

        public int BrokerRefreshCount { get; private set; }

        public ValueTask<DocumentMetadata?> TryGetCachedAsync(string fullPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_cached);

        public ValueTask<PropertyExtractionResult> TryRefreshFromBrokerAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            BrokerRefreshCount++;
            return _broker(cancellationToken);
        }
    }
}
