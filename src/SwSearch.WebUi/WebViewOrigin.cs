namespace SwSearch.WebUi;

public static class WebViewOrigin
{
    public const string HostName = "swsearch.local";
    public const string Origin = "https://" + HostName;

    public static Uri EntryPoint(WebUiView view) =>
        new($"{Origin}/{view.ToString().ToLowerInvariant()}.html");

    public static bool IsLocalResource(Uri? uri) =>
        uri is not null &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);
}
