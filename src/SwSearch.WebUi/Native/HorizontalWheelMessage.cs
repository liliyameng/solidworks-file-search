using System.Drawing;
using Microsoft.Web.WebView2.Core;

namespace SwSearch.WebUi.Native;

/// <summary>Lossless decoding of WM_MOUSEHWHEEL, including sub-WHEEL_DELTA touchpad values.</summary>
public readonly record struct HorizontalWheelMessage(
    CoreWebView2MouseEventVirtualKeys VirtualKeys,
    short Delta,
    Point ScreenPoint)
{
    public uint WebViewMouseData => unchecked((uint)(int)Delta);

    public static HorizontalWheelMessage Decode(nint wParam, nint lParam)
    {
        var packedKeysAndDelta = unchecked((ulong)wParam.ToInt64());
        var keys = (CoreWebView2MouseEventVirtualKeys)(packedKeysAndDelta & 0xFFFF);
        var delta = unchecked((short)((packedKeysAndDelta >> 16) & 0xFFFF));

        // GET_X/Y_LPARAM deliberately sign-extend each 16-bit coordinate. This is
        // required for monitors placed left of or above the primary display.
        var packedPoint = unchecked((ulong)lParam.ToInt64());
        var x = unchecked((short)(packedPoint & 0xFFFF));
        var y = unchecked((short)((packedPoint >> 16) & 0xFFFF));
        return new HorizontalWheelMessage(keys, delta, new Point(x, y));
    }
}
