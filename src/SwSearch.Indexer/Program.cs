using System.Text.Json;
using System.Reflection;
using SwSearch.Contracts;
using SwSearch.Everything;
using SwSearch.Indexer;

var productVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(
        $"""
        SwSearch.Indexer {productVersion}
        用法：SwSearch.Indexer.exe [--help] [--database <path>] [--refresh] [--discovery-mode auto|everything|builtin] [--network-root <UNC>]

        --help               显示此帮助，不创建或修改索引。
        --refresh            执行一次文件发现并更新 SQLite schema v4 缓存。
        --discovery-mode     auto 默认自动回退；everything 仅表示优先；builtin 完全使用内置扫描。
        --network-root       显式添加 UNC 网络根，可重复指定。
        --audit-extensions   只读审计可选 Everything IPC 返回的扩展名。
        --benchmark-small    在临时目录运行小型 SQLite 基准并自动删除临时文件。

        默认后台预算：12 文件/秒、32 MiB/秒，正常 1–2 并发；单文件计费预算上限 4 MiB。
        WAL 在 32 MiB 尝试 checkpoint，达到 64 MiB 时暂停属性写入，压力解除后自动恢复。
        15 万文件无变化刷新当前隔离约 5–6 秒，是后台非阻断性能债；不承诺 ≤3 秒。
        无变化刷新保证业务 files/search_documents/FTS 零变化，不表示整个数据库零写。
        """);
    return;
}

var databaseOption = Array.IndexOf(args, "--database");
var databasePath = databaseOption >= 0 && databaseOption + 1 < args.Length
    ? args[databaseOption + 1]
    : null;

if (args.Contains("--audit-extensions", StringComparer.OrdinalIgnoreCase))
{
    await using var transport = new EverythingIpcTransport(TimeSpan.FromSeconds(15));
    var capability = await transport.ProbeAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(capability));
    if (capability.State != SwSearch.Contracts.CapabilityState.Available)
    {
        Environment.ExitCode = 2;
        return;
    }

    var audit = await EverythingExtensionAudit.RunAsync(transport, cancellationToken: CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(audit));
    Environment.ExitCode = audit.IsValid ? 0 : 3;
    return;
}

if (args.Contains("--benchmark-small", StringComparer.OrdinalIgnoreCase))
{
    var benchmarkPath = Path.Combine(
        Path.GetTempPath(),
        $"swsearch-benchmark-{Guid.NewGuid():N}.db");
    try
    {
        await using var benchmarkStore = new SqliteIndexStore(benchmarkPath);
        await benchmarkStore.InitializeAsync();
        await benchmarkStore.ApplyDiscoverySnapshotAsync(
            BenchmarkCandidates(2_000),
            CancellationToken.None);

        var timings = new List<double>(100);
        for (var index = 0; index < 100; index++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var hits = await benchmarkStore.SearchAsync(
                new SwSearch.Contracts.SearchQuery(
                    $"Part-{index % 50:D4}",
                    SwSearch.Contracts.SearchMode.FileName),
                CancellationToken.None);
            stopwatch.Stop();
            if (hits.Count != 1)
            {
                throw new InvalidOperationException("Small benchmark search returned an unexpected result count.");
            }

            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Files = 2_000,
            Queries = timings.Count,
            P50Milliseconds = timings[49],
            P95Milliseconds = timings[94],
            MaximumMilliseconds = timings[^1]
        }));
    }
    finally
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = benchmarkPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    return;
}

await using var store = new SqliteIndexStore(databasePath);
await store.InitializeAsync();

if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
{
    await using var transport = new EverythingIpcTransport(TimeSpan.FromSeconds(10));
    var capability = await transport.ProbeAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(capability));
    if (capability.State == SwSearch.Contracts.CapabilityState.Available)
    {
        var page = await transport.QueryPageAsync(
            SolidWorksEverythingQuery.Search,
            0,
            128,
            CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            page.TotalItems,
            page.ReturnedItems,
            ValidItems = page.Items.Count,
            WithSize = page.Items.Count(static item => item.Size >= 0),
            WithModified = page.Items.Count(static item => item.LastWriteTimeUtc != DateTimeOffset.MinValue),
        }));
    }

    return;
}

if (args.Contains("--refresh", StringComparer.OrdinalIgnoreCase))
{
    var networkRoots = RepeatedOptionValues(args, "--network-root").ToArray();
    foreach (var root in networkRoots)
    {
        if (!root.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException($"--network-root 必须是完整 UNC 路径：{root}");
        }
    }

    var discoveryMode = OptionValue(args, "--discovery-mode")?.ToLowerInvariant() ?? "auto";
    var builtInOptions = new BuiltInDiscoveryOptions
    {
        DiscoverAllLocalVolumes = true,
        ExplicitNetworkRoots = networkRoots,
    };
    IFileDiscoveryProvider provider = discoveryMode switch
    {
        "auto" or "everything" => FallbackDiscoveryProvider.CreateDefault(builtInOptions),
        "builtin" => new BuiltInFileDiscoveryProvider(builtInOptions),
        _ => throw new ArgumentException("--discovery-mode 仅支持 auto、everything 或 builtin。"),
    };
    try
    {
        var coordinator = new IndexingCoordinator(provider, store);
        Console.WriteLine(JsonSerializer.Serialize(await coordinator.RefreshAsync()));
    }
    finally
    {
        if (provider is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}

Console.WriteLine(JsonSerializer.Serialize(await store.GetStatusAsync()));

static async IAsyncEnumerable<SwSearch.Contracts.FileCandidate> BenchmarkCandidates(int count)
{
    var modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    for (var index = 0; index < count; index++)
    {
        yield return new SwSearch.Contracts.FileCandidate(
            $@"C:\SwSearchBenchmark\Part-{index:D4}.SLDPRT",
            SwSearch.Contracts.SolidWorksDocumentKind.Part,
            1_024 + index,
            modified);
    }

    await Task.CompletedTask;
}

static string? OptionValue(string[] arguments, string option)
{
    var index = Array.FindIndex(arguments, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static IEnumerable<string> RepeatedOptionValues(string[] arguments, string option)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= arguments.Length)
            {
                throw new ArgumentException($"{option} 缺少路径值。");
            }

            yield return arguments[++index];
        }
    }
}
