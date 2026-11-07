// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// A modal window of Solid Edge that waits for the person: title, message, buttons and the choices it offers (radio
/// buttons and check boxes, the checked ones in <see cref="Selected"/>).
/// </summary>
public sealed record DialogInfo(string Title, string Text, IReadOnlyList<string> Buttons, bool HasProgressBar)
{
    public IReadOnlyList<string> Options { get; init; } = [];

    public IReadOnlyList<string> Selected { get; init; } = [];

    /// <summary>A dialog with buttons and no progress bar waits for an answer (a progress window closes by itself).</summary>
    public bool WaitsForAnswer => Buttons.Count > 0 && !HasProgressBar;

    public override string ToString()
    {
        string text = Text.Length > 200 ? Text[..200] + "..." : Text;
        string buttons = Buttons.Count > 0 ? $" [{string.Join(", ", Buttons)}]" : "";
        string options = Options.Count > 0
            ? $" options [{string.Join(", ", Options.Select(o => Selected.Contains(o) ? o + " (selected)" : o))}]"
            : "";
        return text.Length > 0 ? $"'{Title}': \"{text}\"{buttons}{options}" : $"'{Title}'{buttons}{options}";
    }

    public static string Describe(IReadOnlyList<DialogInfo> dialogs) => string.Join("; ", dialogs);
}

/// <summary>
/// Finds the modal dialogs of the running Solid Edge (Edge.exe): visible, enabled top-level windows with a caption
/// (or of the standard dialog class) whose owner window is disabled, which is what a modal dialog does to its owner, and
/// message boxes that have no owner. <see cref="Answer"/> presses a button of one, after choosing one of its options: only
/// when asked, since an answer may change Solid Edge options ("don't ask me again").
/// </summary>
public static class SolidEdgeDialogs
{
    private const string ProcessName = "Edge";
    private const int GwOwner = 4;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const uint WmGetText = 0x000D;
    private const uint BmGetCheck = 0x00F0;
    private const uint BmClick = 0x00F5;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int MaxText = 1024;

    /// <summary>How long a dialog may take to close after its button is pressed before the answer is reported as not taken.</summary>
    private static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<DialogInfo> FindOpen() => [.. FindWindows().Select(Read)];

    /// <summary>
    /// Answers the dialog that waits (the one whose title contains <paramref name="title"/> when several do): checks
    /// <paramref name="option"/> (a radio button or check box) if given, then presses <paramref name="button"/>, both by the
    /// text they show, and waits for the dialog to close.
    /// </summary>
    public static object Answer(string button, string? option, string? title)
    {
        var waiting = FindWindows().Select(h => (Handle: h, Dialog: Read(h))).Where(d => d.Dialog.WaitsForAnswer).ToList();
        if (!string.IsNullOrWhiteSpace(title))
        {
            waiting = [.. waiting.Where(d => d.Dialog.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase))];
        }

        if (waiting.Count == 0)
        {
            throw ToolException.NotFound(title is null ? "Solid Edge shows no dialog that waits for an answer." : $"Solid Edge shows no dialog titled '{title}' that waits for an answer.",
                "se_session status lists the open dialogs.");
        }

        if (waiting.Count > 1)
        {
            throw ToolException.InvalidArgument($"Several dialogs wait for an answer: {string.Join(", ", waiting.Select(d => $"'{d.Dialog.Title}'"))}.",
                "Pass 'dialog' with the title of the one to answer.");
        }

        (IntPtr dialog, DialogInfo info) = waiting[0];
        if (IsFileDialog(dialog) && !button.Trim().Equals("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("file_dialog", $"'{info.Title}' chooses a file or folder, which the allowed folders do not cover, so it is only cancelled here.",
                "Press Cancel, or ask the person to answer it in Solid Edge; se_documents opens and saves files in the allowed folders.");
        }

        IntPtr pressed = Control(dialog, button, IsPushButton)
            ?? throw ToolException.NotFound($"'{info.Title}' has no button '{button}'.", $"Its buttons: {string.Join(", ", info.Buttons)}.");
        if (option is not null)
        {
            IntPtr choice = Control(dialog, option, IsOption)
                ?? throw ToolException.NotFound($"'{info.Title}' has no option '{option}'.",
                    info.Options.Count > 0 ? $"Its options: {string.Join(", ", info.Options)}." : "It has no options: pass only 'button'.");
            if (Check(choice) != 1)
            {
                SendMessageTimeout(choice, BmClick, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out _);
            }

            if (Check(choice) != 1)
            {
                throw new ToolException("dialog_failed", $"'{option}' could not be chosen in '{info.Title}'.", "Ask the person to answer the dialog in Solid Edge.");
            }
        }

        // Posted, not sent: the button runs what the dialog answers (an update, a save), which the call does not wait for.
        PostMessage(pressed, BmClick, IntPtr.Zero, IntPtr.Zero);
        var waited = Stopwatch.StartNew();
        while (IsWindow(dialog) && IsWindowVisible(dialog) && waited.Elapsed < CloseLimit)
        {
            Thread.Sleep(100);
        }

        bool closed = !IsWindow(dialog) || !IsWindowVisible(dialog);
        IReadOnlyList<DialogInfo> open = FindOpen();
        return new
        {
            dialog = info.Title,
            option,
            button,
            closed,
            dialogs = open.Count > 0 ? open : null,
            note = closed ? null : $"'{info.Title}' is still open after pressing '{button}': check it with se_session status.",
        };
    }

    private static List<IntPtr> FindWindows()
    {
        HashSet<uint> ids = ProcessIds();
        var found = new List<IntPtr>();
        if (ids.Count == 0)
        {
            return found;
        }

        EnumWindows((hwnd, _) =>
        {
            if (IsModal(hwnd, ids))
            {
                found.Add(hwnd);
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
        if (owner == IntPtr.Zero)
        {
            // A message box without an owner disables no window (Simulation's "You do not have a valid license to use this
            // command" while a study is solved): a standard dialog made only of buttons and text.
            return ClassOf(hwnd) == "#32770" && IsMessageBox(hwnd);
        }

        if (IsWindowEnabled(owner))
        {
            return false;
        }

        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        return ClassOf(hwnd) == "#32770" || (style & WsCaption) == WsCaption;
    }

    private static bool IsMessageBox(IntPtr hwnd)
    {
        bool button = false, other = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            string kind = ClassOf(child);
            button |= kind == "Button";
            other |= kind is not ("Button" or "Static");
            return true;
        }, IntPtr.Zero);
        return button && !other;
    }

    /// <summary>The Open and Save As dialogs of Windows and its folder picker, told apart by their shell views.</summary>
    private static bool IsFileDialog(IntPtr hwnd)
    {
        bool found = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            string kind = ClassOf(child);
            found |= kind is "SHELLDLL_DefView" or "DUIViewWndClassName" || kind.StartsWith("SHBrowseForFolder", StringComparison.Ordinal);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static DialogInfo Read(IntPtr hwnd)
    {
        var text = new List<string>();
        var buttons = new List<string>();
        var options = new List<string>();
        var selected = new List<string>();
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
                string label = Label(child);
                if (label.Length == 0 || IsGroupBox(child))
                {
                    return true;
                }

                if (IsOption(child))
                {
                    options.Add(label);
                    if (Check(child) == 1)
                    {
                        selected.Add(label);
                    }
                }
                else
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
        return new DialogInfo(title.ToString(), message, buttons, progress) { Options = options, Selected = selected };
    }

    /// <summary>The visible, enabled button-class control of the dialog that shows <paramref name="label"/> and passes <paramref name="kind"/>.</summary>
    private static IntPtr? Control(IntPtr dialog, string label, Func<IntPtr, bool> kind)
    {
        IntPtr? found = null;
        string wanted = Normalize(label);
        EnumChildWindows(dialog, (child, _) =>
        {
            if (IsWindowVisible(child) && IsWindowEnabled(child) && ClassOf(child).Contains("button", StringComparison.OrdinalIgnoreCase)
                && !IsGroupBox(child) && kind(child) && string.Equals(Normalize(Label(child)), wanted, StringComparison.OrdinalIgnoreCase))
            {
                found = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Normalize(string label) => label.Replace("&", "", StringComparison.Ordinal).Trim().TrimEnd('.').Trim();

    private static string Label(IntPtr hwnd) => ControlText(hwnd).Replace("&", "", StringComparison.Ordinal).Trim();

    // Button styles (BS_TYPEMASK): check boxes 2, 3, 5, 6; radio buttons 4, 9; group box 7; the others are push buttons.
    private static int ButtonType(IntPtr hwnd) => (int)(GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & 0x0F);

    private static bool IsGroupBox(IntPtr hwnd) => ButtonType(hwnd) == 7;

    private static bool IsOption(IntPtr hwnd) => ButtonType(hwnd) is 2 or 3 or 4 or 5 or 6 or 9;

    private static bool IsPushButton(IntPtr hwnd) => !IsOption(hwnd) && !IsGroupBox(hwnd);

    /// <summary>1 when the radio button or check box is checked (BM_GETCHECK).</summary>
    private static long Check(IntPtr hwnd) =>
        SendMessageTimeout(hwnd, BmGetCheck, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 200, out IntPtr state) == IntPtr.Zero ? 0 : state.ToInt64();

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
    private static extern bool IsWindow(IntPtr hwnd);

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

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
