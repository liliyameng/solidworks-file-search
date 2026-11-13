using System.Windows.Forms;
using SwSearch.WebUi.Security;

namespace SwSearch.WebUi;

/// <summary>
/// Builds the Shell-native payload used when a desktop result row is dragged
/// outside WebView2. <see cref="DataFormats.FileDrop"/> is exposed to OLE as
/// CF_HDROP, allowing Explorer and SOLIDWORKS to receive the actual file rather
/// than a copied display string.
/// </summary>
public static class ShellFileDrop
{
    /// <summary>
    /// Accepts only a fully-qualified SOLIDWORKS part or assembly. This
    /// intentionally preserves the supplied full path; callers must not
    /// canonicalize it into a different client-owned string before file drop.
    /// </summary>
    public static bool IsSupportedSolidWorksDocument(string? fullPath) =>
        WebMessageValidators.IsSafeSolidWorksPath(fullPath);

    /// <summary>
    /// Performs the filesystem portion of validation. The composition host
    /// always invokes this on a worker before arming a held native gesture: UNC
    /// and cloud-provider paths must never block the WebView/UI callback.
    /// </summary>
    internal static bool Exists(string? fullPath)
    {
        if (!IsSupportedSolidWorksDocument(fullPath))
        {
            return false;
        }

        try
        {
            return File.Exists(fullPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a FileDrop-only data object after <see cref="Exists"/> has
    /// already completed for the current held drag gesture. No text format is
    /// populated as a fallback: a valid shell drag must remain a file drag.
    /// </summary>
    internal static bool TryCreateDataObject(string? fullPath, out DataObject? dataObject)
    {
        dataObject = null;
        if (!IsSupportedSolidWorksDocument(fullPath))
        {
            return false;
        }

        var shellData = new DataObject();
        shellData.SetData(DataFormats.FileDrop, autoConvert: false, new[] { fullPath! });
        dataObject = shellData;
        return true;
    }
}
