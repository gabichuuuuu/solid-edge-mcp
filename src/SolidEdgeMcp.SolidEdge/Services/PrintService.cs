// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Printing of drawing sheets with the print utility of Solid Edge (<c>DraftPrintUtility</c>, what the Print command uses).
/// Verified in Solid Edge 2026: the utility is one object that keeps its settings for the session; setting <c>Printer</c>
/// resets the paper to that printer's default, and an unknown printer gives E_FAIL but stays set. <c>AddDocument</c> prints
/// the working sheets (not the backgrounds) and <c>AddSheet</c> one sheet. Printed to a file, each sheet goes to
/// "&lt;document&gt;_&lt;sheet&gt;.pri" in <c>PrintToFilePath</c>, written by the spooler after <c>PrintOut</c> returns,
/// and the copies are pages of it; <c>PrintToFileName</c> is ignored and cannot be emptied. The settings changed here are
/// put back after printing. Solid Edge records the print date in the drawing ("Last Print Date") and marks it as modified;
/// Windows may make the printer used its default printer ("Let Windows manage my default printer"): both are reported,
/// never changed here.
/// </summary>
public static class PrintService
{
    private static readonly TimeSpan SpoolWait = TimeSpan.FromSeconds(120);

    private static readonly Dictionary<string, DraftPrintPaperSizeConstants> Papers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a2"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_A2,
        ["a3"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_A3,
        ["a4"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_A4,
        ["a5"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_A5,
        ["a6"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_A6,
        ["letter"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_LETTER,
        ["legal"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_LEGAL,
        ["tabloid"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_TABLOID,
        ["ledger"] = DraftPrintPaperSizeConstants.igDraftPrintPaperSize_LEDGER,
    };

    /// <summary>The printers of this Windows user and the print settings of Solid Edge.</summary>
    public static object Printers(SeContext context)
    {
        IReadOnlyList<WindowsPrinter> printers = WindowsPrinters.List();
        string? windowsDefault = WindowsPrinters.Default();
        return new
        {
            count = printers.Count,
            printers = printers.Select(p => new
            {
                name = p.Name,
                @default = string.Equals(p.Name, windowsDefault, StringComparison.OrdinalIgnoreCase),
                connection = p.Connection,
                port = p.Port,
                driver = p.Driver,
                asksForFile = p.AsksForFile,
                state = p.States.Count > 0 ? p.States : null,
            }).ToArray(),
            windowsDefault,
            solidEdge = Settings.Read(Utility(context)).Describe(),
            note = "print without 'folder' sends the sheets to the printer (paper); a printer that asks for a file name (asksForFile) prints only to a folder.",
        };
    }

    public static object Print(SeContext context, string? documentHandle, string[]? sheets, string? printer, string? folder, int copies,
        string paper, string orientation, double? scale, bool black, bool overwrite)
    {
        DraftDocument draft = context.GetDocument<DraftDocument>(documentHandle, "draft");
        WindowsPrinter device = Printer(printer);
        string? target = string.IsNullOrWhiteSpace(folder) ? null : context.Paths.Check(folder, "folder");
        if (target is null && device.AsksForFile)
        {
            throw ToolException.InvalidArgument($"'{device.Name}' writes to a file and asks for its name in a dialog (port {device.Port}).",
                "Pass 'folder' to print the sheets to files there.");
        }

        if (copies is < 1 or > 99)
        {
            throw ToolException.InvalidArgument("'copies' must be 1 to 99.");
        }

        if (scale is { } factor && (!double.IsFinite(factor) || factor <= 0 || factor > 100))
        {
            throw ToolException.InvalidArgument("'scale' must be greater than 0: 1 prints the sheet at full size, 0.5 at half size; leave it out to fit the paper.");
        }

        DraftPrintPaperSizeConstants? paperSize = string.Equals(paper, "printer", StringComparison.OrdinalIgnoreCase) ? null
            : Papers.TryGetValue(paper, out DraftPrintPaperSizeConstants size) ? size
            : throw ToolException.InvalidArgument($"Unknown paper '{paper}'.", $"Use printer (its own paper) or one of: {string.Join(", ", Papers.Keys)}.");
        string mode = orientation.ToLowerInvariant();
        if (mode is not ("auto" or "portrait" or "landscape"))
        {
            throw ToolException.InvalidArgument($"Unknown orientation '{orientation}'.", "Use auto, portrait or landscape.");
        }

        List<Sheet> chosen = Sheets(draft, sheets);
        string stem = Path.GetFileNameWithoutExtension(SeContext.SafeName((SolidEdgeDocument)draft));
        if (target is not null && !overwrite)
        {
            string[] existing = [.. chosen.SelectMany(s => new[] { ".pdf", ".ps", ".prn" }.Select(e => Path.Combine(target, $"{stem}_{s.Name}{e}"))).Where(File.Exists)];
            if (existing.Length > 0)
            {
                throw new ToolException("file_exists", $"Already in the folder: {string.Join(", ", existing.Select(Path.GetFileName))}.",
                    "Pass overwrite=true to replace them, or another folder.");
            }
        }

        // Printed to a file, the spooler writes into a folder of the server first; the files are then moved to the target.
        string? scratch = target is null ? null : Path.Combine(context.Paths.WorkFolder, "print", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            if (scratch is not null)
            {
                Directory.CreateDirectory(scratch);
            }

            DraftPrintUtility utility = Utility(context);
            Settings before = Settings.Read(utility);
            string? defaultBefore = WindowsPrinters.Default();
            bool dirtyBefore = draft.Dirty;
            Settings used;
            List<string> notRestored;
            try
            {
                try
                {
                    used = Apply(utility, device.Name, paperSize, mode, scale, black, copies, scratch);
                    utility.RemoveAllDocuments();
                    if (sheets is { Length: > 0 })
                    {
                        foreach (Sheet sheet in chosen)
                        {
                            utility.AddSheet(sheet);
                        }
                    }
                    else
                    {
                        utility.AddDocument(draft);
                    }

                    utility.PrintOut();
                }
                catch (COMException ex) when (!ComErrors.IsGone(ex))
                {
                    throw new ToolException("print_failed", $"Solid Edge did not print: {ex.Message.Trim()}",
                        "Check the printer (se_print printers) and the drawing (se_drawing sheets).", ex);
                }
            }
            finally
            {
                notRestored = Restore(utility, before);
            }

            var result = new Dictionary<string, object?>
            {
                ["document"] = context.DocumentHandle((SolidEdgeDocument)draft),
                ["printer"] = device.Name,
                ["sheets"] = chosen.Select(s => s.Name).ToArray(),
                ["settings"] = used.Describe(),
            };
            if (scratch is null)
            {
                result["sent"] = true;
                result["note"] = $"Solid Edge handed the sheets to the Windows print queue of '{device.Name}'.";
            }
            else
            {
                result["files"] = Collect(scratch, target!, chosen.Count, overwrite);
            }

            result["restored"] = notRestored.Count == 0 ? true : notRestored;
            if (!dirtyBefore && draft.Dirty)
            {
                result["modified"] = "Solid Edge recorded the print date (Last Print Date) in the drawing, so it is marked as modified: save it to keep the date, or close it without saving.";
            }

            string? defaultNow = WindowsPrinters.Default();
            if (!string.Equals(defaultBefore, defaultNow, StringComparison.OrdinalIgnoreCase))
            {
                result["windowsDefaultPrinter"] = new
                {
                    before = defaultBefore,
                    now = defaultNow,
                    note = "Windows made the printer used its default printer ('Let Windows manage my default printer' is on). Set it back in the Windows printer settings if it should not stay.",
                };
            }

            return result;
        }
        finally
        {
            if (scratch is not null)
            {
                TryDelete(scratch);
            }
        }
    }

    private static Settings Apply(DraftPrintUtility utility, string printer, DraftPrintPaperSizeConstants? paper, string orientation, double? scale,
        bool black, int copies, string? folder)
    {
        // Setting the printer resets the paper to that printer's default.
        utility.Printer = printer;
        if (!string.Equals(utility.Printer, printer, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("print_failed", $"Solid Edge kept the printer '{utility.Printer}' instead of '{printer}'.", "Check the printer with se_print printers.");
        }

        if (paper is { } size)
        {
            utility.PaperSize = size;
            if (utility.PaperSize != size)
            {
                throw ToolException.InvalidArgument($"'{printer}' does not take {PaperName(size)} paper.", "Leave 'paper' out to use the printer's paper, or choose another size.");
            }
        }

        utility.AutoOrient = orientation == "auto";
        if (orientation != "auto")
        {
            utility.Orientation = orientation == "portrait" ? DraftPrintOrientationConstants.igDraftPrintPortrait : DraftPrintOrientationConstants.igDraftPrintLandscape;
        }

        utility.BestFit = scale is null;
        if (scale is { } factor)
        {
            utility.SingleSheetScale = factor;
        }

        utility.PrintAsBlack = black;
        utility.Copies = (short)copies;
        utility.PrintToFile = folder is not null;
        if (folder is not null)
        {
            utility.PrintToFilePath = folder;
        }

        return Settings.Read(utility);
    }

    /// <summary>Puts back the settings read before printing; gives those that did not come back.</summary>
    private static List<string> Restore(DraftPrintUtility utility, Settings before)
    {
        Try(utility.RemoveAllDocuments);
        // The printer first: it also puts back its default paper.
        Try(() => utility.Printer = before.Printer);
        Try(() =>
        {
            if (utility.PaperSize != before.Paper)
            {
                utility.PaperSize = before.Paper;
            }
        });
        Try(() => utility.AutoOrient = before.AutoOrient);
        Try(() => utility.Orientation = before.Orientation);
        Try(() => utility.BestFit = before.BestFit);
        Try(() => utility.SingleSheetScale = before.Scale);
        Try(() => utility.PrintAsBlack = before.Black);
        Try(() => utility.Copies = before.Copies);
        Try(() => utility.PrintToFile = before.ToFile);
        Try(() =>
        {
            if (before.FilePath.Length > 0)
            {
                utility.PrintToFilePath = before.FilePath;
            }
        });

        Settings after;
        try
        {
            after = Settings.Read(utility);
        }
        catch (COMException)
        {
            return ["the print settings could not be read back"];
        }

        var differences = new List<string>();
        void Compare(string name, object? was, object? now)
        {
            if (!Equals(was, now))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: was {was}, now {now}"));
            }
        }

        static string Size(Settings s) => string.Create(CultureInfo.InvariantCulture, $"{s.PaperWidth:F1} x {s.PaperHeight:F1} mm");

        Compare("printer", before.Printer, after.Printer);
        Compare("paper", PaperName(before.Paper), PaperName(after.Paper));
        Compare("paper size", Size(before), Size(after));
        Compare("orientation", before.Orientation, after.Orientation);
        Compare("auto orientation", before.AutoOrient, after.AutoOrient);
        Compare("fit to paper", before.BestFit, after.BestFit);
        Compare("scale", before.Scale, after.Scale);
        Compare("copies", before.Copies, after.Copies);
        Compare("print as black", before.Black, after.Black);
        Compare("print to file", before.ToFile, after.ToFile);
        Compare("file folder", before.FilePath, after.FilePath);
        return differences;
    }

    /// <summary>Waits for the spooler to write one file per sheet, then moves them to the target with the extension of their format.</summary>
    private static List<object> Collect(string scratch, string target, int expected, bool overwrite)
    {
        var watch = Stopwatch.StartNew();
        FileInfo[] written = [];
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        bool settled = false;
        while (!settled && watch.Elapsed < SpoolWait)
        {
            written = new DirectoryInfo(scratch).GetFiles();
            settled = written.Length >= expected
                && written.All(f => sizes.TryGetValue(f.Name, out long was) && was == f.Length && f.Length > 0 && Closed(f.FullName));
            if (!settled)
            {
                sizes = written.ToDictionary(f => f.Name, f => f.Length, StringComparer.OrdinalIgnoreCase);
                Thread.Sleep(250);
            }
        }

        if (!settled)
        {
            throw new ToolException("print_failed",
                $"The printer finished {written.Count(f => f.Length > 0)} of {expected} files in {SpoolWait.TotalSeconds:F0} s.",
                "Check the printer's queue in Windows; a dialog of the printer may be waiting.");
        }

        Directory.CreateDirectory(target);
        var files = new List<object>();
        foreach (FileInfo file in written.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            string format = Format(file.FullName);
            string path = Path.Combine(target, Path.GetFileNameWithoutExtension(file.Name) + "." + format);
            File.Move(file.FullName, path, overwrite);
            PdfFile.Info? info = format == "pdf" ? PdfFile.Inspect(path) : null;
            files.Add(new
            {
                path,
                bytes = new FileInfo(path).Length,
                format,
                pages = info?.Pages,
                pageSize = info?.PageSizes.FirstOrDefault(),
            });
        }

        return files;
    }

    /// <summary>Whether the spooler has finished writing the file (it keeps it open for writing until then).</summary>
    private static bool Closed(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>pdf, ps (PostScript) or prn (the printer's own language), from the first bytes.</summary>
    private static string Format(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[5];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read == 5 && head.SequenceEqual("%PDF-"u8) ? "pdf" : read >= 2 && head[..2].SequenceEqual("%!"u8) ? "ps" : "prn";
    }

    private static List<Sheet> Sheets(DraftDocument draft, string[]? names)
    {
        List<Sheet> working = [.. Late.Items(draft.Sections.WorkingSection.Sheets).OfType<Sheet>()];
        if (names is not { Length: > 0 })
        {
            return working;
        }

        return [.. names.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Select(n =>
            working.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"There is no working sheet '{n}'.", $"Its sheets: {string.Join(", ", working.Select(s => s.Name))}."))];
    }

    private static WindowsPrinter Printer(string? name)
    {
        IReadOnlyList<WindowsPrinter> printers = WindowsPrinters.List();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'printer' is required: the name of a printer from se_print printers.",
                $"Printers: {string.Join(", ", printers.Select(p => p.Name))}.");
        }

        return printers.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"There is no printer '{name.Trim()}'.", $"Printers: {string.Join(", ", printers.Select(p => p.Name))}.");
    }

    private static DraftPrintUtility Utility(SeContext context) =>
        context.Application.GetDraftPrintUtility() as DraftPrintUtility
        ?? throw new ToolException("print_failed", "Solid Edge did not give its print utility.");

    private static string PaperName(DraftPrintPaperSizeConstants paper) =>
        Papers.FirstOrDefault(p => p.Value == paper).Key
        ?? (paper == DraftPrintPaperSizeConstants.igDraftPrintPaperSize_Custom ? "custom" : paper.ToString().Replace("igDraftPrintPaperSize_", "", StringComparison.Ordinal).ToLowerInvariant());

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (COMException)
        {
            // Best effort: what does not come back is reported.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A file the spooler still holds: the folder stays in the work folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }

    /// <summary>The settings of the print utility that a print changes.</summary>
    private sealed record Settings(string Printer, DraftPrintPaperSizeConstants Paper, double PaperWidth, double PaperHeight,
        DraftPrintOrientationConstants Orientation, bool AutoOrient, bool BestFit, double Scale, short Copies, bool Black, bool ToFile, string FilePath)
    {
        public static Settings Read(DraftPrintUtility u) => new(u.Printer, u.PaperSize, u.PaperWidth, u.PaperHeight, u.Orientation, u.AutoOrient,
            u.BestFit, u.SingleSheetScale, u.Copies, u.PrintAsBlack, u.PrintToFile, u.PrintToFilePath ?? "");

        public object Describe() => new
        {
            printer = Printer,
            paper = PaperName(Paper),
            paperSize = new[] { Math.Round(PaperWidth, 1), Math.Round(PaperHeight, 1) },
            orientation = AutoOrient ? "auto" : Orientation == DraftPrintOrientationConstants.igDraftPrintPortrait ? "portrait" : "landscape",
            scale = BestFit ? (object)"fit" : Scale,
            copies = (int)Copies,
            black = Black,
        };
    }
}
