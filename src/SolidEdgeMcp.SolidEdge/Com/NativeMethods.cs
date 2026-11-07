// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// P/Invoke for OLE and windows. <c>Marshal.GetActiveObject</c> does not exist in modern .NET.
/// </summary>
internal static class NativeMethods
{
    internal const int SwShowNoActivate = 4;
    internal const int SwShowMinNoActive = 7;
    private const uint DesktopReadObjects = 0x0001;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>
    /// Whether the desktop that takes the input cannot be opened: the Windows session is locked (verified: OpenInputDesktop
    /// fails with access denied) or a secure prompt is shown. Meanwhile Solid Edge draws nothing and its images come out empty.
    /// </summary>
    internal static bool InputDesktopUnavailable()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        CloseDesktop(desktop);
        return false;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    internal static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    /// <summary>Active object registered in the ROT for the ProgID, or <c>null</c> if there is none.</summary>
    internal static object? TryGetActiveObject(string progId)
    {
        CLSIDFromProgID(progId, out Guid clsid);
        int hr = GetActiveObject(ref clsid, IntPtr.Zero, out object? obj);
        return hr >= 0 ? obj : null;
    }
}

/// <summary>Standard COM contract of IOleMessageFilter.</summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

/// <summary>First methods of IDispatch, to obtain the type information of any object.</summary>
[ComImport]
[Guid("00020400-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDispatchTypeInfo
{
    [PreserveSig]
    int GetTypeInfoCount(out uint count);

    [PreserveSig]
    int GetTypeInfo(uint index, uint lcid, [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.ITypeInfo? typeInfo);
}
