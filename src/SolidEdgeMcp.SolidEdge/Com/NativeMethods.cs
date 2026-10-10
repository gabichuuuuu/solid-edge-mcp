using System.Runtime.InteropServices;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// P/Invoke de OLE y de ventanas. <c>Marshal.GetActiveObject</c> no existe en .NET moderno.
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

    /// <summary>Objeto activo registrado en la ROT para el ProgID, o <c>null</c> si no hay ninguno.</summary>
    internal static object? TryGetActiveObject(string progId)
    {
        CLSIDFromProgID(progId, out Guid clsid);
        int hr = GetActiveObject(ref clsid, IntPtr.Zero, out object? obj);
        return hr >= 0 ? obj : null;
    }
}

/// <summary>Contrato COM estándar de IOleMessageFilter.</summary>
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

/// <summary>Primeros métodos de IDispatch, para obtener la información de tipos de cualquier objeto.</summary>
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
