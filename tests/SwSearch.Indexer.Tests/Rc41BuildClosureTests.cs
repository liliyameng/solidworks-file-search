using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class Rc41BuildClosureTests
{
    [Fact]
    public void SourceSchemaContract_IsV10WithDrawingRemovalMigration()
    {
        Assert.Equal(10, ContractVersions.IndexSchema);
        var schema = typeof(SqliteIndexStore).Assembly.GetType("SwSearch.Indexer.SqliteSchema", throwOnError: true)!;
        Assert.Equal(10, schema.GetField("CurrentVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
        Assert.NotNull(schema.GetMethod(
            "MigrateDrawingSupportRemovalToV10Async",
            BindingFlags.Static | BindingFlags.NonPublic));
    }

    [Fact]
    public void FrozenCandidate_HasSchema6AndSingleRc41DependencyClosure()
    {
        var payload = Environment.GetEnvironmentVariable("SWSEARCH_CANDIDATE_PAYLOAD");
        if (string.IsNullOrWhiteSpace(payload)) return;
        payload = Path.GetFullPath(payload);

        var context = new AssemblyLoadContext("SwSearch.Rc41ClosureGate", isCollectible: true);
        context.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(payload, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        try
        {
            var indexer = context.LoadFromAssemblyPath(Path.Combine(payload, "SwSearch.Indexer.dll"));
            var schema = indexer.GetType("SwSearch.Indexer.SqliteSchema", throwOnError: true)!;
            Assert.Equal(6, schema.GetField("CurrentVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
            Assert.NotNull(schema.GetMethod(
                "MigrateSafeDrawingProjectionToV6Async",
                BindingFlags.Static | BindingFlags.NonPublic));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "SwSearch.Broker.deps.json")));
            var root = document.RootElement;
            var target = Assert.Single(
                root.GetProperty("targets").EnumerateObject(),
                static candidate => candidate.Name.EndsWith("/win-x64", StringComparison.Ordinal));
            var swSearchTargets = target.Value.EnumerateObject()
                .Where(static library => library.Name.StartsWith("SwSearch.", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(swSearchTargets);
            Assert.All(swSearchTargets, static library => Assert.EndsWith("/1.2.43", library.Name, StringComparison.Ordinal));

            var swSearchLibraries = root.GetProperty("libraries").EnumerateObject()
                .Where(static library => library.Name.StartsWith("SwSearch.", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(swSearchLibraries);
            Assert.All(swSearchLibraries, static library => Assert.EndsWith("/1.2.43", library.Name, StringComparison.Ordinal));

            var firstParty = Directory.EnumerateFiles(payload, "SwSearch.*", SearchOption.TopDirectoryOnly)
                .Where(static path => Path.GetExtension(path) is ".dll" or ".exe")
                .ToArray();
            Assert.NotEmpty(firstParty);
            Assert.All(firstParty, static path =>
                Assert.Equal("1.2.43.0", FileVersionInfo.GetVersionInfo(path).FileVersion));

            Assert.DoesNotContain(
                "1.2.39",
                File.ReadAllText(Path.Combine(payload, "SwSearch.Broker.deps.json")),
                StringComparison.Ordinal);
        }
        finally
        {
            context.Unload();
        }
    }
}
