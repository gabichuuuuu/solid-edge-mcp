using Microsoft.Win32;
using SolidEdgeFramework;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>
/// Settings that Solid Edge keeps for the current user (its options: templates, file locations), read from the key of the
/// running version, so each installation answers with its own configuration. Verified in Solid Edge 2026 (version 226):
/// HKCU\Software\Siemens\Solid Edge\Version 226\Global\Templates holds the default templates, including WeldAsmTemplate.
/// Earlier versions used the Unigraphics Solutions key.
/// </summary>
internal static class SolidEdgeSettings
{
    private static readonly string[] Roots = [@"Software\Siemens\Solid Edge", @"Software\Unigraphics Solutions\Solid Edge"];

    /// <summary>Value of a setting of the running version, or null when it is not set.</summary>
    public static string? Read(Application app, string subKey, string name)
    {
        string? major = MajorVersion(app);
        if (major is null)
        {
            return null;
        }

        foreach (string root in Roots)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{root}\Version {major}\{subKey}");
            if (key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return System.Environment.ExpandEnvironmentVariables(value);
            }
        }

        return null;
    }

    /// <summary>"226" from "226.00.01.04".</summary>
    private static string? MajorVersion(Application app)
    {
        try
        {
            string version = app.Version;
            int dot = version.IndexOf('.', StringComparison.Ordinal);
            return dot > 0 ? version[..dot] : version;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
