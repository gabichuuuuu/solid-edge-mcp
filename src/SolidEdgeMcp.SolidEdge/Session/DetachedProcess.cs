using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>
/// Crea un proceso independiente del servidor: sin heredar handles (los pipes de stdio del
/// protocolo MCP no deben quedar abiertos en Solid Edge) y, si se puede, fuera del job object del
/// cliente, para que cerrar el cliente no cierre Solid Edge con el trabajo de la persona.
/// </summary>
internal static class DetachedProcess
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const int ErrorAccessDenied = 5;

    public static Process Start(string exe, IReadOnlyDictionary<string, string> environment, IServerLog log)
    {
        IntPtr block = BuildEnvironmentBlock(environment);
        try
        {
            uint flags = CreateUnicodeEnvironment | CreateNewProcessGroup;
            if (TryCreate(exe, flags | CreateBreakawayFromJob, block, out ProcessInformation info, out int error))
            {
                return Attach(info, exe);
            }

            if (error == ErrorAccessDenied)
            {
                // El job del cliente no permite salir de él: Solid Edge se cerrará con el cliente.
                log.Warning("Solid Edge could not be started outside the client's job object; it will close if the MCP client closes.");
                if (TryCreate(exe, flags, block, out info, out error))
                {
                    return Attach(info, exe);
                }
            }

            throw new ToolException("solid_edge_start_failed",
                $"Could not start '{exe}': {new Win32Exception(error).Message}",
                "Start Solid Edge manually and retry.");
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>Obtiene el <see cref="Process"/> mientras se conserva el handle, para que el pid siga siendo válido.</summary>
    private static Process Attach(ProcessInformation info, string exe)
    {
        try
        {
            return Process.GetProcessById(info.dwProcessId);
        }
        catch (ArgumentException)
        {
            throw new ToolException("solid_edge_start_failed", $"'{exe}' exited immediately after starting.",
                "Start Solid Edge manually to see the error (often the license: SE_LICENSE_SERVER).");
        }
        finally
        {
            CloseHandle(info.hProcess);
        }
    }

    private static bool TryCreate(string exe, uint flags, IntPtr environment, out ProcessInformation info, out int error)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{exe}\"");
        bool ok = CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment,
            Path.GetDirectoryName(exe), ref startup, out info);
        if (!ok)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        CloseHandle(info.hThread);
        error = 0;
        return true;
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Length == 0 || pair.Key.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
