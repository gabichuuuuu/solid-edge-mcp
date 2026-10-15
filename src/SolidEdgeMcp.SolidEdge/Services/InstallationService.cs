// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// The Solid Edge installation, read through SolidEdge.InstallData, a component installed with it that answers without
/// starting Solid Edge. Verified in Solid Edge 2026: version "226.00.01.004 x64", the Program and Preferences folders,
/// language 1033 and Parasolid "38.0".
/// </summary>
public static class InstallationService
{
    /// <summary>
    /// What InstallData reports, or null when Solid Edge is not installed (the component is not registered); a component that
    /// is registered but does not answer (a damaged installation) gives its error instead of failing se_server_info.
    /// </summary>
    public static object? Read()
    {
        Type? type = Type.GetTypeFromProgID("SolidEdge.InstallData");
        if (type is null)
        {
            return null;
        }

        object? data = null;
        try
        {
            data = Activator.CreateInstance(type)!;
            int language = Convert.ToInt32(Call(data, "GetLanguageID"), CultureInfo.InvariantCulture);
            return new
            {
                version = Call(data, "GetVersion"),
                programFolder = Call(data, "GetInstalledPath"),
                preferencesFolder = Call(data, "GetPreferencesPath"),
                language = Culture(language),
                parasolid = Call(data, "GetParasolidVersion"),
            };
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) || ex is TargetInvocationException or FormatException)
        {
            return new { installed = true, error = $"SolidEdge.InstallData did not answer: {(ex.InnerException ?? ex).Message.Trim()}" };
        }
        finally
        {
            if (data is not null)
            {
                Marshal.FinalReleaseComObject(data);
            }
        }
    }

    private static object? Call(object target, string method) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, null, CultureInfo.InvariantCulture);

    private static string Culture(int lcid)
    {
        try
        {
            return CultureInfo.GetCultureInfo(lcid).Name;
        }
        catch (CultureNotFoundException)
        {
            return lcid.ToString(CultureInfo.InvariantCulture);
        }
    }
}
