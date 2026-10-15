// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>Translates COM errors into actionable errors for the client.</summary>
public static class ComErrors
{
    public const int EFail = unchecked((int)0x80004005);
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int EInvalidArg = unchecked((int)0x80070057);
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int DispEMemberNotFound = unchecked((int)0x80020003);
    public const int DispEParamNotFound = unchecked((int)0x80020004);
    public const int DispETypeMismatch = unchecked((int)0x80020005);
    public const int DispEUnknownName = unchecked((int)0x80020006);
    public const int DispEException = unchecked((int)0x80020009);
    public const int DispEBadParamCount = unchecked((int)0x8002000E);
    public const int DispEBadIndex = unchecked((int)0x8002000B);
    public const int RpcECallRejected = unchecked((int)0x80010001);
    public const int RpcERetryLater = unchecked((int)0x8001010A);
    public const int RpcEDisconnected = unchecked((int)0x80010108);
    public const int RpcEServerDied = unchecked((int)0x80010007);
    public const int RpcEServerDiedDne = unchecked((int)0x80010012);
    public const int RpcSServerUnavailable = unchecked((int)0x800706BA);
    public const int RpcSCallFailed = unchecked((int)0x800706BE);
    public const int CoEObjNotConnected = unchecked((int)0x800401FD);
    public const int CoEServerExecFailure = unchecked((int)0x80080005);
    public const int RegDbEClassNotReg = unchecked((int)0x80040154);

    /// <summary>Does the error indicate that the Solid Edge process is no longer running?</summary>
    public static bool IsDisconnected(Exception ex) => Unwrap(ex) switch
    {
        InvalidComObjectException => true,
        COMException com => IsDisconnected(com.HResult),
        _ => false,
    };

    public static bool IsDisconnected(int hr) =>
        hr is RpcEDisconnected or RpcEServerDied or RpcEServerDiedDne or RpcSServerUnavailable or RpcSCallFailed or CoEObjNotConnected;

    /// <summary>
    /// Is Solid Edge itself gone (closed, crashed or unreachable)? Unlike <see cref="IsDisconnected(Exception)"/>, an object
    /// that Solid Edge deleted does not count (CO_E_OBJNOTCONNECTED: verified in Solid Edge 2026, <c>Holes.AddThroughAll</c>
    /// raises it for a hole it cannot build and removes), so undoing and cleaning up after a failed call still run.
    /// </summary>
    public static bool IsGone(Exception ex) => Unwrap(ex) switch
    {
        InvalidComObjectException => true,
        COMException com => IsDisconnected(com.HResult) && com.HResult != CoEObjNotConnected,
        _ => false,
    };

    /// <summary>Did Solid Edge delete the object the call used (CO_E_OBJNOTCONNECTED) while it keeps running?</summary>
    public static bool IsObjectRemoved(Exception ex) => Unwrap(ex) is COMException { HResult: CoEObjNotConnected };

    /// <summary>
    /// Was a single direct COM call rejected while Solid Edge keeps running? .NET raises some HRESULTs as other exceptions
    /// (E_INVALIDARG as ArgumentException, E_POINTER as NullReferenceException, E_NOINTERFACE as InvalidCastException), so
    /// only wrap one COM call with it.
    /// </summary>
    public static bool IsCallFailure(Exception ex) => ex switch
    {
        COMException com => !IsDisconnected(com.HResult),
        ArgumentException or NullReferenceException or InvalidCastException or NotImplementedException => true,
        _ => false,
    };

    public static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        return ex;
    }

    /// <summary>Disconnection error for a specific object while Solid Edge is still responding: the object no longer exists.</summary>
    public static ToolException ObjectGone(Exception exception, string operation)
    {
        Exception ex = Unwrap(exception);
        string code = ex is COMException com ? $" (0x{com.HResult:X8})" : "";
        return new ToolException("object_deleted",
            $"'{operation}' used a Solid Edge object that no longer exists{code}: it was deleted (for example together with a feature that used it) or its document was closed.",
            "Query the object again to get a new handle (se_feature_tree, se_documents list, se_geometry).", ex);
    }

    /// <summary>Converts a COM exception into a <see cref="ToolException"/>; others are returned unchanged.</summary>
    public static Exception Translate(Exception exception, string operation)
    {
        Exception ex = Unwrap(exception);
        if (ex is ToolException)
        {
            return ex;
        }

        if (ex is InvalidComObjectException)
        {
            return new ToolException("object_released", $"'{operation}' used a Solid Edge object that is no longer available.",
                "Query the object again to get a new handle.", ex);
        }

        if (ex is MissingMethodException or MissingMemberException)
        {
            return new ToolException("member_not_found", $"'{operation}': {ex.Message}",
                "Use se_com_describe on the object to list its members.", ex);
        }

        // The interop layer cannot convert some values that Solid Edge returns (verified in Solid Edge 2026:
        // Query.Criteria). Solid Edge keeps running and the object stays usable.
        if (ex is InvalidOleVariantTypeException or SafeArrayTypeMismatchException or SafeArrayRankMismatchException or MarshalDirectiveException)
        {
            return new ToolException("unreadable_value",
                $"'{operation}': Solid Edge returned a value that .NET cannot convert ({ex.GetType().Name}: {ex.Message.Trim()}).",
                "Read the related members or methods instead (se_com_describe lists them); the object itself is still usable.", ex);
        }

        // .NET converts some COM HRESULTs into other exceptions (E_NOTIMPL, E_INVALIDARG, E_POINTER,
        // E_NOINTERFACE, STG_E_PATHNOTFOUND as DirectoryNotFoundException, E_ACCESSDENIED). NotImplementedException never leaves this code; the others are only treated as COM
        // errors when they come from a reflection call (wrapped in TargetInvocationException).
        bool fromCall = !ReferenceEquals(ex, exception);
        Exception? com = ex switch
        {
            COMException => ex,
            NotImplementedException => ex,
            ArgumentException or NullReferenceException or InvalidCastException or IOException or UnauthorizedAccessException when fromCall => ex,
            _ => null,
        };
        if (com is null)
        {
            return ex;
        }

        int hr = com.HResult;
        string detail = string.IsNullOrWhiteSpace(com.Message) ? "" : $" Detail: {com.Message.Trim()}";
        string code = $"0x{hr:X8}";
        return hr switch
        {
            _ when IsDisconnected(hr) => new ToolException("solid_edge_disconnected",
                $"Solid Edge closed or stopped responding during '{operation}' ({code}).",
                "Retry: the server reconnects (or starts Solid Edge) on the next call. Handles from before are no longer valid.", com),
            RpcECallRejected or RpcERetryLater => new ToolException("solid_edge_busy",
                $"Solid Edge is busy and rejected '{operation}' ({code}).",
                "Close any dialog open in Solid Edge or wait until the current command finishes, then retry.", com),
            DispEMemberNotFound or DispEUnknownName => new ToolException("member_not_found",
                $"'{operation}': the object has no such member ({code}).{detail}",
                "Use se_com_describe on the object to list its members.", com),
            DispETypeMismatch or DispEBadParamCount or DispEParamNotFound or EInvalidArg => new ToolException("invalid_com_argument",
                $"'{operation}': Solid Edge rejected the arguments ({code}).{detail}",
                "Check the number, order and types of the arguments (se_com_describe shows the signature). Lengths are in metres and angles in radians at the COM level.", com),
            DispEBadIndex => new ToolException("index_out_of_range",
                $"'{operation}': index out of range ({code}).", "Collections in Solid Edge start at 1.", com),
            ENotImpl => new ToolException("not_implemented",
                $"'{operation}' is not implemented by Solid Edge for this object ({code}).", null, com),
            ENoInterface => new ToolException("wrong_object_type",
                $"'{operation}': the object does not support the required interface ({code}).",
                "Check that the handle refers to the right kind of object (se_com_describe).", com),
            EAccessDenied => new ToolException("access_denied",
                $"'{operation}': access denied ({code}).{detail}", "Check file permissions and that the file is not open elsewhere or read-only.", com),
            CoEServerExecFailure => new ToolException("solid_edge_start_failed",
                $"Solid Edge could not be started ({code}).",
                "Start Solid Edge manually once and check its license (SE_LICENSE_SERVER).", com),
            // Solid Edge itself answered: a component it needs for the operation is missing (verified: SaveCopyAs of an
            // assembly or sheet metal to .catproduct). A Solid Edge that is not installed is reported when it is started.
            RegDbEClassNotReg => new ToolException("component_not_registered",
                $"'{operation}': a component Solid Edge needs for it is not registered on this computer ({code}), such as a translator that is not installed.{detail}",
                "Use another format or route, or add the component by repairing the Solid Edge installation.", com),
            _ => new ToolException("solid_edge_error",
                $"Solid Edge failed in '{operation}' ({code}).{detail}",
                "Check the arguments and the state of the document (e.g. the right environment or an active sketch). Use se_view capture to see the current state.", com),
        };
    }
}
