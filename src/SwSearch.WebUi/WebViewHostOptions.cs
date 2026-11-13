namespace SwSearch.WebUi;

public enum WebUiView
{
    Main,
    /// <summary>
    /// Search controls only. The desktop window pairs this small WebView with
    /// the native ExplorerBrowser results host so the result area contains
    /// real Shell items and thumbnails instead of a simulated HTML grid.
    /// </summary>
    MainToolbar,
    /// <summary>
    /// A standalone preferences form.  It deliberately has no search or
    /// result surface, so the desktop host can open settings in a separate
    /// window without reviving the retired HTML result grid.
    /// </summary>
    Preferences,
    Property,
    Taskpane,
}

/// <summary>
/// The one runtime-selection configuration point. A null browser folder selects the
/// machine-wide Evergreen WebView2 Runtime. A future offline build can supply a
/// validated Fixed Version directory without changing the host implementation.
/// </summary>
public sealed record WebViewRuntimeConfiguration
{
    public static WebViewRuntimeConfiguration Evergreen { get; } = new();

    public string? BrowserExecutableFolder { get; init; }

    public string? UserDataFolder { get; init; }
}

public sealed record WebViewHostOptions
{
    public WebUiView View { get; init; } = WebUiView.Main;

    /// <summary>
    /// Effective UI language (e.g. "zh-CN", "en") used for the first paint of
    /// the page. Null leaves the page default (Simplified Chinese).
    /// </summary>
    public string? Language { get; init; }

    public string? AssetFolder { get; init; }

    public WebViewRuntimeConfiguration RuntimeConfiguration { get; init; } =
        WebViewRuntimeConfiguration.Evergreen;

    public IReadOnlySet<string> ExternalLinkHosts { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal string ResolveAssetFolder()
    {
        var path = AssetFolder;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "webui");
        }

        return Path.GetFullPath(path);
    }

    internal string ResolveUserDataFolder()
    {
        if (!string.IsNullOrWhiteSpace(RuntimeConfiguration.UserDataFolder))
        {
            return Path.GetFullPath(RuntimeConfiguration.UserDataFolder);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwSearch",
            "WebView2");
    }
}

public sealed class WebViewRuntimeUnavailableEventArgs(string message, Exception? exception) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}
