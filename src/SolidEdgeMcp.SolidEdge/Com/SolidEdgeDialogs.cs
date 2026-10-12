// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>A modal window of Solid Edge that waits for the person: title, message, buttons.</summary>
public sealed record DialogInfo(string Title, string Text, IReadOnlyList<string> Buttons, bool HasProgressBar)
{
    /// <summary>A dialog with buttons and no progress bar waits for an answer (a progress window closes by itself).</summary>
    public bool WaitsForAnswer => Buttons.Count > 0 && !HasProgressBar;

    public override string ToString()
    {
        string text = Text.Length > 200 ? Text[..200] + "..." : Text;
        string buttons = Buttons.Count > 0 ? $" [{string.Join(", ", Buttons)}]" : "";
        return text.Length > 0 ? $"'{Title}': \"{text}\"{buttons}" : $"'{Title}'{buttons}";
    }

    public static string Describe(IReadOnlyList<DialogInfo> dialogs) => string.Join("; ", dialogs);
}

/// <summary>
/// Finds the modal dialogs of the running Solid Edge (Edge.exe): visible, enabled top-level windows with a caption
/// (or of the standard dialog class) whose owner window is disabled, which is what a modal dialog does to its owner.
/// Read only: the server never presses a dialog button, because the answer may change Solid Edge options.
/// </summary>
public static class SolidEdgeDialogs
{
    private const string ProcessName = "Edge";
    private const int GwOwner = 4;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const uint WmGetText = 0x000D;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int MaxText = 1024;

    public static IReadOnlyList<DialogInfo> FindOpen()
    {
        HashSet<uint> ids = ProcessIds();
        if (ids.Count == 0)
        {
            return [];
        }

        var found = new List<DialogInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (IsModal(hwnd, ids))
            {
                found.Add(Read(hwnd));
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static HashSet<uint> ProcessIds()
    {
        Process[] processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return [.. processes.Select(p => (uint)p.Id)];
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsModal(IntPtr hwnd, HashSet<uint> ids)
    {
        if (!IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!ids.Contains(pid))
        {
            return false;
        }

        IntPtr owner = GetWindow(hwnd, GwOwner);
        if (owner == IntPtr.Zero || IsWindowEnabled(owner))
        {
            return false;
        }

        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        return ClassOf(hwnd) == "#32770" || (style & WsCaption) == WsCaption;
    }

    private static DialogInfo Read(IntPtr hwnd)
    {
        var text = new List<string>();
        var buttons = new List<string>();
        bool progress = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (!IsWindowVisible(child))
            {
                return true;
            }

            string kind = ClassOf(child);
            if (kind.Contains("progress", StringComparison.OrdinalIgnoreCase))
            {
                progress = true;
            }
            else if (kind.Contains("button", StringComparison.OrdinalIgnoreCase))
            {
                string label = ControlText(child).Replace("&", "", StringComparison.Ordinal).Trim();
                if (label.Length > 0)
                {
                    buttons.Add(label);
                }
            }
            else if (kind.Contains("static", StringComparison.OrdinalIgnoreCase))
            {
                string line = ControlText(child).Trim();
                if (line.Length > 0)
                {
                    text.Add(line);
                }
            }

            return true;
        }, IntPtr.Zero);

        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        string message = string.Join(" ", text).ReplaceLineEndings(" ");
        return new DialogInfo(title.ToString(), message, buttons, progress);
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    /// <summary>Text of a control of another process: GetWindowText does not read it, WM_GETTEXT does.</summary>
    private static string ControlText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(MaxText);
        IntPtr sent = SendMessageTimeout(hwnd, WmGetText, (IntPtr)MaxText, buffer, SmtoAbortIfHung, 200, out _);
        return sent == IntPtr.Zero ? "" : buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, StringBuilder lParam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);
}
