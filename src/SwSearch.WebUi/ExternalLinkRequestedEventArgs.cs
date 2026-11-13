namespace SwSearch.WebUi;

public sealed class ExternalLinkRequestedEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
}
