// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// P/Invoke for OLE and windows. <c>Marshal.GetActiveObject</c> does not exist in modern .NET.
/// </summary>
internal static class NativeMethods
{
    internal const int SwShowNoActivate = 4;
    internal const int SwShowMinNoActive = 7;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
