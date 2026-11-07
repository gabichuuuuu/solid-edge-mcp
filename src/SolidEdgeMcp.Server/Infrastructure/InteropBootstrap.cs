// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Runtime.Loader;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.Server.Infrastructure;

/// <summary>
/// The Solid Edge interop assemblies are generated from the type libraries of the Solid Edge installed on the machine
/// (scripts/generate-interop.ps1) and are not distributed, because they derive from Siemens files. A build from source has
/// them beside the server. The distributed package does not: on its first start the server generates them from the local
/// installation into a cache of its data folder, one folder per Solid Edge version, and loads them from there.
/// </summary>
public static class InteropBootstrap
{
    internal static readonly string[] Assemblies =
    [
        "Interop.SolidEdgeConstants", "Interop.SolidEdgeFramework", "Interop.SolidEdgeFrameworkSupport", "Interop.SolidEdgeGeometry",
        "Interop.SolidEdgePart", "Interop.SolidEdgeAssembly", "Interop.SolidEdgeDraft",
    ];

    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Makes the interop loadable: nothing to do when it is beside the server; otherwise the cached copy for the installed
    /// Solid Edge, generated first if needed. Messages go to <paramref name="log"/> (standard error: standard output is
    /// the MCP channel). Returns the folder the interop is loaded from, or null when it is beside the server or cannot be
    /// made; the server starts anyway and its tools then answer <c>interop_missing</c> (<see cref="IsMissing"/>).
    /// </summary>
    public static string? Ensure(AppPaths paths, Action<string> log)
    {
        if (Complete(AppContext.BaseDirectory))
        {
            return null;
        }

        try
        {
            string? exe = OperatingSystem.IsWindows() ? SolidEdgeLauncher.FindExecutable() : null;
            if (exe is null)
            {
                log("Solid Edge is not installed (SolidEdge.Application is not registered): the interop cannot be generated.");
                return null;
            }

            string programDir = Path.GetDirectoryName(exe)!;
            string version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Trim() ?? "unknown";
            string folder = Path.Combine(paths.DataRoot, "interop", Sanitize(version));
            if (!Complete(folder) && !Generate(programDir, folder, version, log))
            {
                return null;
            }

            AssemblyLoadContext.Default.Resolving += (context, name) =>
                Assemblies.Contains(name.Name, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, name.Name + ".dll"))
                    ? context.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll"))
                    : null;
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            log($"The Solid Edge interop could not be prepared: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when <paramref name="ex"/> comes from a Solid Edge interop assembly that could not be loaded.</summary>
    public static bool IsMissing(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            string? file = current switch
            {
                FileNotFoundException notFound => notFound.FileName,
                FileLoadException load => load.FileName,
                _ => null,
            };
            if (file?.StartsWith("Interop.SolidEdge", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool Complete(string folder) => Assemblies.All(a => File.Exists(Path.Combine(folder, a + ".dll")));

    internal static string Sanitize(string version) =>
        string.Concat(version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

    /// <summary>Runs the bundled generator with Windows PowerShell into a temporary folder, then moves it into place.</summary>
    private static bool Generate(string programDir, string folder, string version, Action<string> log)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "scripts", "generate-interop.ps1");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(script) || !File.Exists(powershell))
        {
            log($"The interop is missing and cannot be generated: '{(File.Exists(script) ? powershell : script)}' does not exist.");
            return false;
        }

        string temporary = $"{folder}.tmp-{Environment.ProcessId}";
        log($"Generating the Solid Edge interop for Solid Edge {version} in '{folder}' (first start; a few seconds).");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ProgramDir", programDir, "-OutputDir", temporary })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) log(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) log(line); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(GenerateTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            TryDelete(temporary);
            log($"The interop generator did not finish in {GenerateTimeout.TotalMinutes:0} minutes.");
            return false;
        }

        process.WaitForExit();
        if (process.ExitCode != 0 || !Complete(temporary))
        {
            log($"The interop generator failed (exit code {process.ExitCode}).");
            TryDelete(temporary);
            return false;
        }

        if (Directory.Exists(folder) && !Complete(folder))
        {
            TryDelete(folder);
        }

        try
        {
            Directory.Move(temporary, folder);
        }
        catch (IOException) when (Complete(folder))
        {
            // Another server generated it at the same time.
            TryDelete(temporary);
        }

        return Complete(folder);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is harmless.
        }
    }
}
