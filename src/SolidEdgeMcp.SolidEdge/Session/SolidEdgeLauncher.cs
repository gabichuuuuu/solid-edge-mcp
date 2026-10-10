using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>
/// Arranca Solid Edge sin que dependa del servidor: algunos clientes MCP cierran todo el árbol de
/// procesos del servidor al terminar, y un Edge.exe hijo del servidor se cerraría con el trabajo sin
/// guardar de la persona. Primero por activación COM (lo lanza DCOM, fuera del árbol). Si falla (p. ej.
/// la licencia <c>SE_LICENSE_SERVER</c> se configuró después de iniciar sesión y el entorno de DCOM no
/// la tiene), se lanza Edge.exe directamente con la variable copiada del registro, avisando de que
/// entonces sí queda ligado al servidor.
/// </summary>
internal static class SolidEdgeLauncher
{
    public const string ProgId = "SolidEdge.Application";
    private const string LicenseVariable = "SE_LICENSE_SERVER";

    /// <summary>Ruta de Edge.exe según el registro COM (LocalServer32), o <c>null</c> si no está instalado.</summary>
    public static string? FindExecutable()
    {
        using RegistryKey? progId = Registry.ClassesRoot.OpenSubKey($@"{ProgId}\CLSID");
        string? clsid = progId?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(clsid))
        {
            return null;
        }

        using RegistryKey? server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
        string? command = server?.GetValue(null) as string;
        return ParseExecutable(command);
    }

    internal static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = command.Trim();
        string exe;
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            exe = end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }
        else
        {
            int index = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = index > 0 ? trimmed[..(index + 4)] : trimmed;
        }

        exe = Environment.ExpandEnvironmentVariables(exe);
        return File.Exists(exe) ? Path.GetFullPath(exe) : null;
    }

    /// <summary>Arranca Solid Edge y devuelve su objeto Application.</summary>
    public static object Start(TimeSpan timeout, IServerLog log)
    {
        string exe = FindExecutable() ?? throw new ToolException(
            "solid_edge_not_installed",
            "Solid Edge is not registered on this computer (no LocalServer32 for SolidEdge.Application).",
            "Install Solid Edge or repair its installation.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            log.Info($"Starting Solid Edge through COM activation: {exe}");
            Type type = Type.GetTypeFromProgID(ProgId, throwOnError: true)!;
            object app = Activator.CreateInstance(type)
                ?? throw new COMException("COM activation returned no object.");
            log.Info($"Solid Edge started by COM in {stopwatch.Elapsed.TotalSeconds:F1} s.");
            return app;
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException)
        {
            log.Warning($"COM activation of Solid Edge failed ({ComErrors.Unwrap(ex).Message}). Starting Edge.exe directly: "
                + "it will be a child of this server and may close if the MCP client ends the server's process tree.");
        }

        TimeSpan remaining = timeout - stopwatch.Elapsed;
        return StartProcess(exe, remaining > TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30), log);
    }

    /// <summary>Lanza Edge.exe con la licencia del registro y espera a que se registre en la ROT.</summary>
    private static object StartProcess(string exe, TimeSpan timeout, IServerLog log)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        if (!environment.ContainsKey(LicenseVariable))
        {
            string? license = Environment.GetEnvironmentVariable(LicenseVariable, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(LicenseVariable, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(license))
            {
                environment[LicenseVariable] = license;
            }
        }

        log.Info($"Starting Solid Edge: {exe}");
        using Process process = DetachedProcess.Start(exe, environment, log);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            object? app = NativeMethods.TryGetActiveObject(ProgId);
            if (app is not null)
            {
                log.Info($"Solid Edge registered after {stopwatch.Elapsed.TotalSeconds:F1} s (pid {process.Id}).");
                return app;
            }

            if (process.HasExited)
            {
                throw new ToolException(
                    "solid_edge_start_failed",
                    $"Solid Edge exited during startup (exit code {process.ExitCode}).",
                    "Start Solid Edge manually to see the error. A common cause is the license: check the SE_LICENSE_SERVER environment variable or sign out and back in to Windows.");
            }

            Thread.Sleep(500);
        }

        throw new ToolException(
            "solid_edge_start_timeout",
            $"Solid Edge did not become available within {timeout.TotalSeconds:F0} s.",
            "Check Solid Edge for a dialog waiting for input (license, first start), close it and retry. Increase startupTimeoutSeconds in policy.json if the computer is slow.");
    }
}
