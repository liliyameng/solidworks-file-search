using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSearch.Contracts;
using SwSearch.Integration;
using SwSearch.WebUi;
using SwSearch.WebUi.Security;

namespace SwSearch.App.Tests;

/// <summary>
/// RC93 UI language feature: resolver semantics, persistence round-trips,
/// web-message contract, and web-asset source contracts.
/// </summary>
[TestClass]
public sealed class Rc93LanguageTests
{
    // ---- UiLanguage resolver --------------------------------------------

    [TestMethod]
    public void UiLanguage_ResolveEffective_PassesThroughExplicitChoices()
    {
        foreach (var configured in new[] { "zh-CN", "en", "ja", "de", "fr", "es" })
        {
            Assert.AreEqual(configured, UiLanguage.ResolveEffective(configured), configured);
        }
    }

    [TestMethod]
    public void UiLanguage_SanitizeConfigured_RejectsUnknownValues()
    {
        Assert.AreEqual(UiLanguage.Auto, UiLanguage.SanitizeConfigured("klingon"));
        Assert.AreEqual(UiLanguage.Auto, UiLanguage.SanitizeConfigured(string.Empty));
        Assert.AreEqual(UiLanguage.Auto, UiLanguage.SanitizeConfigured(null!));
        Assert.AreEqual("auto", UiLanguage.SanitizeConfigured("AUTO"), "Matching is ordinal.");
    }

    [TestMethod]
    public void UiLanguage_FromCulture_MapsSupportedPrefixesAndFallsBack()
    {
        Assert.AreEqual("zh-CN", UiLanguage.FromCulture(new System.Globalization.CultureInfo("zh-TW")));
        Assert.AreEqual("en", UiLanguage.FromCulture(new System.Globalization.CultureInfo("en-GB")));
        Assert.AreEqual("de", UiLanguage.FromCulture(new System.Globalization.CultureInfo("de-AT")));
        Assert.AreEqual(string.Empty, UiLanguage.FromCulture(new System.Globalization.CultureInfo("ko-KR")));
    }

    [TestMethod]
    public void UiLanguage_ResolveSystem_FallsBackToEnglishForUnsupportedOsLanguage()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ko-KR");
            Assert.AreEqual(UiLanguage.Fallback, UiLanguage.ResolveSystem());
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.AreEqual("fr", UiLanguage.ResolveSystem());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    // ---- Persistence round-trips -----------------------------------------

    [TestMethod]
    public async Task PreferencesRepository_PersistsAndReloadsLanguage()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            var repository = CreateRepository(settings, new AvailableIntegrations(), sandbox);

            var saved = await repository.SaveAsync(new UserPreferencesSaveRequest(
                true,
                true,
                false,
                PropertyCardSourcePreference.Automatic,
                string.Empty,
                Language: "ja"), CancellationToken.None);
            Assert.IsTrue(saved.PreferencesSaved);
            Assert.AreEqual("ja", saved.NormalizedRequest.Language);

            var loaded = await repository.LoadAsync(CancellationToken.None);
            Assert.AreEqual("ja", loaded.Language);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings));
            Assert.AreEqual("ja", json.RootElement.GetProperty("language").GetString());
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [TestMethod]
    public async Task PreferencesRepository_SanitizesInvalidLanguageToAuto()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            var repository = CreateRepository(settings, new AvailableIntegrations(), sandbox);

            var saved = await repository.SaveAsync(new UserPreferencesSaveRequest(
                true,
                true,
                false,
                PropertyCardSourcePreference.Automatic,
                string.Empty,
                Language: "klingon"), CancellationToken.None);

            Assert.AreEqual(UiLanguage.Auto, saved.NormalizedRequest.Language);
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [TestMethod]
    public async Task JsonAppSettingsStore_ColumnSave_PreservesPersistedLanguage()
    {
        var sandbox = CreateSandbox();
        try
        {
            var settings = Path.Combine(sandbox, "settings.json");
            // The preferences repository owns the language key.
            var repository = CreateRepository(settings, new AvailableIntegrations(), sandbox);
            await repository.SaveAsync(new UserPreferencesSaveRequest(
                true,
                true,
                false,
                PropertyCardSourcePreference.Automatic,
                string.Empty,
                Language: "de"), CancellationToken.None);

            // A later column-width debounce save must not erase it.
            var store = new JsonAppSettingsStore(settings, new DirectExecutor());
            var snapshot = await store.PrepareSaveAsync(new AppSettings(
                DiscoveryPreference.Automatic,
                Array.Empty<string>(),
                false,
                ResultColumnDefaults.All), CancellationToken.None);
            using (snapshot)
            {
                await snapshot.CommitAsync(CancellationToken.None);
            }

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings));
            Assert.AreEqual(
                "de",
                json.RootElement.TryGetProperty("language", out var language) ? language.GetString() : null,
                "Column saves must preserve the persisted UI language.");
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    private sealed class DirectExecutor : ISettingsSaveExecutor
    {
        public async ValueTask ExecuteAsync(
            string settingsPath,
            string temporaryPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
    }

    private class AvailableIntegrations : IUserPreferencesIntegrationBackend
    {
        public ValueTask<IntegrationStatus> ProbeAsync(UserPreferenceIntegrationTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IntegrationStatus(true, false, "available"));
        public ValueTask SetEnabledAsync(UserPreferenceIntegrationTarget target, bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    // ---- Web message contract ---------------------------------------------

    [TestMethod]
    public void PreferencesSaveValidator_AcceptsEveryConfiguredLanguage()
    {
        var router = new WebMessageRouter();
        foreach (var language in UiLanguage.ConfiguredValues)
        {
            AssertAccepted(router, new
            {
                taskpane = true,
                folderMenu = false,
                filePropertiesMenu = true,
                startup = true,
                language,
            }, language);
        }
    }

    [TestMethod]
    public void PreferencesSaveValidator_RejectsUnknownLanguage()
    {
        var router = new WebMessageRouter();
        AssertRejected(router, new
        {
            taskpane = true,
            folderMenu = true,
            filePropertiesMenu = true,
            startup = true,
            language = "klingon",
        });
        AssertRejected(router, new
        {
            taskpane = true,
            folderMenu = true,
            filePropertiesMenu = true,
            startup = true,
            language = 7,
        });
    }

    // ---- Web asset source contracts ----------------------------------------

    private static readonly string WebUiRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "SwSearch.WebUi", "Assets", "webui"));

    [TestMethod]
    public void LangJs_IsReferencedBeforeAppJs_ByEveryWebViewShell()
    {
        foreach (var page in new[] { "index", "main", "maintoolbar", "preferences", "property", "taskpane" })
        {
            var html = File.ReadAllText(Path.Combine(WebUiRoot, page + ".html"));
            var langIndex = html.IndexOf("lang.js", StringComparison.Ordinal);
            var appIndex = html.IndexOf("app.js", StringComparison.Ordinal);
            Assert.IsTrue(langIndex >= 0, page + ".html must load lang.js");
            Assert.IsTrue(appIndex > langIndex, page + ".html must load lang.js before app.js");
        }
    }

    [TestMethod]
    public void LangJs_OptionValues_MatchSharedConfiguredLanguageList()
    {
        var lang = File.ReadAllText(Path.Combine(WebUiRoot, "lang.js"));
        foreach (var language in UiLanguage.ConfiguredValues.Where(static value => value != UiLanguage.Auto))
        {
            Assert.IsTrue(
                lang.Contains("\"" + language + "\"", StringComparison.Ordinal),
                "lang.js must offer option " + language);
        }
        CollectionAssert.AreEquivalent(
            UiLanguage.ConfiguredValues.Where(static value => value != UiLanguage.Auto).ToArray(),
            new[] { "zh-CN", "en", "ja", "de", "fr", "es" });
    }

    [TestMethod]
    public void LangJs_AllLanguageTablesExposeIdenticalKeySets()
    {
        var lang = File.ReadAllText(Path.Combine(WebUiRoot, "lang.js"));
        var headers = new[] { "\"zh-CN\": {", "en: {", "ja: {", "de: {", "fr: {", "es: {" };
        var keySets = new List<HashSet<string>>();
        foreach (var header in headers)
        {
            var start = lang.IndexOf(header, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "lang.js must contain table header " + header);
            start += header.Length;
            // Each table body ends at the first two-space closing brace.
            var end = lang.IndexOf("\n  }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "lang.js table " + header + " must close");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lang[start..end].Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("\"", StringComparison.Ordinal))
                {
                    var separator = trimmed.IndexOf("\":", StringComparison.Ordinal);
                    if (separator > 1)
                    {
                        keys.Add(trimmed[1..separator]);
                    }
                }
            }
            Assert.IsTrue(keys.Count >= 10, "Table " + header + " looks unparsed");
            keySets.Add(keys);
        }
        Assert.AreEqual(6, keySets.Count);
        foreach (var keys in keySets.Skip(1))
        {
            Assert.AreEqual(keySets[0].Count, keys.Count, "Every table must match the zh-CN key count.");
            foreach (var key in keySets[0])
            {
                Assert.IsTrue(keys.Contains(key), "Missing key in a language table: " + key);
            }
        }
    }

    [TestMethod]
    public void AppJs_PreferencesSavePostsLanguageAndTracksSelectDirtyState()
    {
        var appJs = File.ReadAllText(Path.Combine(WebUiRoot, "app.js"));
        Assert.IsTrue(appJs.Contains("language: document.getElementById(\"preferenceLanguage\").value", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains(".preferences-panel select", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("languageOptionsMarkup", StringComparison.Ordinal));
        // The settings echo path restores the persisted language selection.
        Assert.IsTrue(appJs.Contains("values.language", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppJs_StateLanguageSwitch_RerendersShellWithoutReload()
    {
        var appJs = File.ReadAllText(Path.Combine(WebUiRoot, "app.js"));
        Assert.IsTrue(appJs.Contains("nextLanguage !== language", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("applyDocumentLanguage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppUiText_NativeColumnAndMatchKeys_ExistInEveryLanguage()
    {
        string[] keys =
        [
            "nativeColumn.fileName",
            "nativeColumn.kind",
            "nativeColumn.directory",
            "native.matchPropertyPrefix",
            "native.matchFallback",
            "native.matchValuePrefix",
            "native.matchDash",
        ];
        foreach (var language in new[] { UiLanguage.Chinese, "en", "ja", "de", "fr", "es" })
        {
            foreach (var key in keys)
            {
                Assert.AreNotEqual(
                    key,
                    AppUiText.Get(language, key),
                    $"{key} must be defined for {language}");
            }
        }
        // The painted header keeps the exact historical zh-CN contract labels.
        Assert.AreEqual("文件类型", AppUiText.Get(UiLanguage.Chinese, "nativeColumn.kind"));
        Assert.AreEqual("所在文件夹", AppUiText.Get(UiLanguage.Chinese, "nativeColumn.directory"));
    }

    [TestMethod]
    public void NativeShellResultsColumnHeader_ResolvesPaintedLabelsThroughLanguageTable()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SwSearch.App", "NativeShellResultsColumnHeader.cs"));
        var source = File.ReadAllText(root);
        Assert.IsTrue(source.Contains("internal void ApplyLanguage", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("\"nativeColumn.\" +", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("AppUiText.Get", StringComparison.Ordinal));
        // The MainWindow host must re-localize on an in-session change.
        var window = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(root)!, "MainWindow.xaml.cs"));
        Assert.IsTrue(window.Contains("UiLanguageChanged += ViewModelOnUiLanguageChanged", StringComparison.Ordinal));
        Assert.IsTrue(window.Contains("ApplyLanguage(ViewModel.UiLanguageEffective)", StringComparison.Ordinal));
    }

    private static void AssertAccepted(WebMessageRouter router, object payload, string language)
    {
        var json = JsonSerializer.Serialize(new { type = "preferences.save", payload });
        Assert.IsTrue(
            router.TryValidate(json, out _, out var error),
            "language=" + language + " must be accepted: " + error);
    }

    private static void AssertRejected(WebMessageRouter router, object payload)
    {
        var json = JsonSerializer.Serialize(new { type = "preferences.save", payload });
        Assert.IsFalse(router.TryValidate(json, out _, out _));
    }

    private static UserPreferencesRepository CreateRepository(
        string settings,
        IUserPreferencesIntegrationBackend integrations,
        string sandbox) => new(
            settings,
            integrations,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500),
            Path.Combine(sandbox, "defaults.tsv"),
            languageResolver: () => UiLanguage.Chinese);

    private static string CreateSandbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "swsearch-rc93-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
