namespace SwSearch.WebUi;

public static class WebViewOrigin
{
    public const string HostName = "swsearch.local";
    public const string Origin = "https://" + HostName;

    public static Uri EntryPoint(WebUiView view) =>
        new($"{Origin}/{view.ToString().ToLowerInvariant()}.html");

    /// <summary>
    /// Entry point carrying the effective UI language so the page paints with
    /// the right string table before the first state message arrives.
    /// </summary>
    public static Uri EntryPoint(WebUiView view, string language) =>
        new(
            $"{Origin}/{view.ToString().ToLowerInvariant()}.html?lang={Uri.EscapeDataString(string.IsNullOrEmpty(language) ? UiLanguage.Chinese : language)}",
            UriKind.Absolute);

    public static bool IsLocalResource(Uri? uri) =>
        uri is not null &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);
}
