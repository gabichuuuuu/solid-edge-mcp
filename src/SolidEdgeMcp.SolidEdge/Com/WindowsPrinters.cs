// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>A printer installed for this Windows user, as the print spooler lists it.</summary>
public sealed record WindowsPrinter(string Name, string Port, string Driver, bool Network, bool Shared, uint Status)
{
    /// <summary>Printers redirected by Remote Desktop use the ports TS001, TS002...</summary>
    public bool RemoteDesktop => Port.Length > 2 && Port.StartsWith("TS", StringComparison.OrdinalIgnoreCase) && Port[2..].All(char.IsAsciiDigit);

    /// <summary>A printer on the FILE: or PORTPROMPT: port asks for the output file name in a dialog when it prints.</summary>
    public bool AsksForFile => Port.Split(',').Any(p => p.Trim() is var port
        && (port.Equals("FILE:", StringComparison.OrdinalIgnoreCase) || port.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase)));

    public string Connection => Network ? "network" : RemoteDesktop ? "remote_desktop" : "local";

    /// <summary>The spooler status flags that matter to someone printing (empty: ready).</summary>
    public IReadOnlyList<string> States => [.. StatusNames.Where(s => (Status & s.Flag) != 0).Select(s => s.Name)];

    private static readonly (uint Flag, string Name)[] StatusNames =
    [
        (0x1, "paused"), (0x2, "error"), (0x8, "paper_jam"), (0x10, "paper_out"), (0x80, "offline"), (0x200, "busy"),
        (0x400, "printing"), (0x1000, "not_available"), (0x40000, "toner_low"), (0x80000, "no_toner"), (0x400000, "door_open"),
    ];
}

/// <summary>
/// The printers of this Windows user and the default one, read from the print spooler (winspool). Read only: the default
/// printer is never changed here (Windows changes it by itself when "Let Windows manage my default printer" is on: the
/// last printer used becomes the default).
/// </summary>
public static class WindowsPrinters
{
    private const uint EnumLocal = 0x2;
    private const uint EnumConnections = 0x4;
    private const uint AttributeShared = 0x8;
    private const uint AttributeNetwork = 0x10;
    private const int ErrorInsufficientBuffer = 122;

    public static IReadOnlyList<WindowsPrinter> List()
    {
        uint flags = EnumLocal | EnumConnections;
        EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0)
        {
            return [];
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, 2, buffer, needed, out _, out uint returned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The print spooler did not list the printers.");
            }

            int size = Marshal.SizeOf<PrinterInfo2>();
            var printers = new List<WindowsPrinter>((int)returned);
            for (int i = 0; i < returned; i++)
            {
                PrinterInfo2 info = Marshal.PtrToStructure<PrinterInfo2>(buffer + (i * size));
                if (!string.IsNullOrEmpty(info.PrinterName))
                {
                    printers.Add(new WindowsPrinter(info.PrinterName, info.PortName ?? "", info.DriverName ?? "",
                        (info.Attributes & AttributeNetwork) != 0, (info.Attributes & AttributeShared) != 0, info.Status));
                }
            }

            return printers;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The default printer of this Windows user, or null if there is none.</summary>
    public static string? Default()
    {
        int size = 0;
        if (GetDefaultPrinter(null, ref size) || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size <= 0)
        {
            return null;
        }

        char[] buffer = new char[size];
        return GetDefaultPrinter(buffer, ref size) ? new string(buffer, 0, Math.Max(0, size - 1)) : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public string? ServerName;
        public string? PrinterName;
        public string? ShareName;
        public string? PortName;
        public string? DriverName;
        public string? Comment;
        public string? Location;
        public IntPtr DevMode;
        public string? SepFile;
        public string? PrintProcessor;
        public string? Datatype;
        public string? Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint Jobs;
        public uint AveragePpm;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter([Out] char[]? buffer, ref int size);
}
