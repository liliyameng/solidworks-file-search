using System;
using System.Runtime.InteropServices;

namespace SwSearch.SolidWorksAddin
{
    [ComVisible(true)]
    [Guid("DA306A0D-EAC5-4406-8610-B1DA805D9270")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISwAddin
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        bool ConnectToSW([MarshalAs(UnmanagedType.IDispatch)] object thisSw, int cookie);

        [return: MarshalAs(UnmanagedType.Bool)]
        bool DisconnectFromSW();
    }
}
