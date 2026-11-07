// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Background sheets of a drawing: the frame and title block behind the working sheets. Verified in Solid Edge 2026: the
/// draft template brings one per paper size (A4-Sheet … A1-Sheet); Sheets.AddSheet(name, igBackgroundSection) adds an empty
/// one, Sheet.Delete removes one (also one in use, which leaves its sheets without background), the 2D tools draw on one
/// through its handle, and Sheet.ReplaceBackground(file, name) (only on a background sheet; E_FAIL on a working sheet or a
/// missing file) copies the content of the background of that name in another drawing and keeps the name and size of the
/// sheet. A name that the other drawing does not have changes nothing, silently: the content is compared before and after.
/// </summary>
public static partial class DrawingService
{
    /// <summary>Most drawing objects whose range is read to compare the content of a background.</summary>
    private const int MaxContent = 5000;

    /// <summary>Tolerance of the content comparison and of the sheet edge (m).</summary>
    private const double ContentTolerance = 1e-6;

    public static object AddBackground(SeContext context, string? documentHandle, string? name, string? paper, bool portrait, double? width, double? height)
    {
        DraftDocument draft = Draft(context, documentHandle);
        if (name is not null)
        {
            RequireFreeName(draft, name);
        }

        Sheet sheet = name is null
            ? draft.Sheets.AddSheet(SectionType: SheetSectionTypeConstants.igBackgroundSection)
            : draft.Sheets.AddSheet(name, SheetSectionTypeConstants.igBackgroundSection);
        if (!IsBackground(sheet))
        {
            TryDelete(sheet);
            throw new ToolException("sheet_failed", "Solid Edge did not add a background sheet.");
        }

        ApplySetup(draft, sheet, paper, portrait, width, height, null, null);
        return new
        {
            background = DescribeSheet(context, draft, sheet, draft.ActiveSheet.Name),
            note = "Draw the frame and title block on it with se_sketch and se_annotation (sheet = its handle), then give it to sheets with setup_sheet 'background'.",
        };
    }

    /// <summary>
    /// Copies onto a background sheet the content of a background of another drawing (a company template, an older
    /// drawing), as Replace Background does; the sheet keeps its name and size, and the sheets that use it show the new
    /// frame and title block.
    /// </summary>
    public static object ReplaceBackground(SeContext context, string? documentHandle, string? sheetRef, string? path, string? source, bool replaceBlocks)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet back = BackgroundTarget(ResolveSheet(context, draft, sheetRef));
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ToolException.InvalidArgument("replace_background requires 'path': a saved drawing (.dft) whose background is copied.");
        }

        string file = DocumentService.CheckTemplate(context, path, DocumentTypeConstants.igDraftDocument, "path");
        if (!string.Equals(Path.GetExtension(file), ".dft", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' is not a drawing (.dft).");
        }

        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{Path.GetFileName(file)}' does not exist.", "Pass the absolute path of a saved drawing (.dft).");
        }

        string name = string.IsNullOrWhiteSpace(source) ? back.Name : source.Trim();
        Content before = ContentOf(back);
        try
        {
            if (replaceBlocks)
            {
                back.ReplaceBackgroundEx(file, name, true);
            }
            else
            {
                back.ReplaceBackground(file, name);
            }
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("replace_failed", $"Solid Edge did not copy the background '{name}' of '{Path.GetFileName(file)}': {ex.Message.Trim()}",
                "Pass a drawing (.dft) that Solid Edge can open.", ex);
        }

        Content after = ContentOf(back);
        bool changed = !before.Same(after);
        string? note = changed
            ? null
            : $"Nothing changed: '{Path.GetFileName(file)}' has no background named '{name}' (Solid Edge then copies nothing) or its content is the same. "
              + "Open that drawing and list its backgrounds with 'sheets' to check the name.";
        string? warning = changed && after.Range is { } r && Beyond(back, r)
            ? $"The new content reaches [{Geo.Mm(r[2])}, {Geo.Mm(r[3])}] mm, beyond the {string.Join(" x ", SizeMm(back))} mm of '{back.Name}': the copied background was made for another paper size."
            : null;
        return new
        {
            background = DescribeSheet(context, draft, back, draft.ActiveSheet.Name),
            source = Path.GetFileName(file),
            sourceBackground = name,
            changed,
            before = before.Describe(),
            after = after.Describe(),
            note,
            warning,
        };
    }

    // Helpers

    private static bool IsBackground(Sheet sheet) => sheet.SectionType == SheetSectionTypeConstants.igBackgroundSection;

    /// <summary>Background of a working sheet, or null if it has none.</summary>
    private static Sheet? BackgroundOf(Sheet sheet)
    {
        try
        {
            return sheet.Background;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Names of the working sheets that use a background sheet.</summary>
    private static List<string> UsersOf(DraftDocument draft, Sheet background) =>
        [.. WorkingSheets(draft).Where(s => BackgroundOf(s) is { } b && string.Equals(b.Name, background.Name, StringComparison.Ordinal)).Select(s => s.Name)];

    /// <summary>The background sheet itself, or the background of a working sheet.</summary>
    private static Sheet BackgroundTarget(Sheet sheet) =>
        IsBackground(sheet)
            ? sheet
            : BackgroundOf(sheet) ?? throw new ToolException("no_background", $"The sheet '{sheet.Name}' has no background.",
                "Pass 'sheet' = a background sheet from 'sheets', or give the sheet one with setup_sheet 'background'.");

    private static bool Beyond(Sheet sheet, double[] range) =>
        range[0] < -1e-3 || range[1] < -1e-3
        || range[2] > sheet.SheetSetup.SheetWidth + 1e-3 || range[3] > sheet.SheetSetup.SheetHeight + 1e-3;

    /// <summary>Counts by kind and overall range (m) of the content of a sheet.</summary>
    private sealed record Content(Dictionary<string, int> Counts, double[]? Range)
    {
        public bool Same(Content other) =>
            Counts.Count == other.Counts.Count && Counts.All(c => other.Counts.GetValueOrDefault(c.Key, -1) == c.Value)
            && (Range, other.Range) switch
            {
                (null, null) => true,
                ({ } a, { } b) => a.Zip(b, (p, q) => Math.Abs(p - q)).All(d => d <= ContentTolerance),
                _ => false,
            };

        public object Describe() => new
        {
            elements = Counts,
            range = Range is { } r ? new[] { Geo.Mm(r[0]), Geo.Mm(r[1]), Geo.Mm(r[2]), Geo.Mm(r[3]) } : null,
        };
    }

    private static Content ContentOf(Sheet sheet)
    {
        Dictionary<string, int> counts = Counts(sheet, SheetCollections);
        foreach ((string collection, string kind) in new[] { ("TextBoxes", "texts"), ("Images2d", "images"), ("BlockOccurrences", "blocks") })
        {
            if (Late.TryGet(sheet, collection) is { } items && Late.TryGet(items, "Count") is int n && n > 0)
            {
                counts[kind] = n;
            }
        }

        object objects = sheet.DrawingObjects;
        counts["objects"] = Late.Count(objects);
        double[]? range = null;
        foreach (object item in Late.Items(objects, MaxContent))
        {
            double[] r;
            try
            {
                r = GraphicRange(item);
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                continue;
            }

            range = range is null ? r : [Math.Min(range[0], r[0]), Math.Min(range[1], r[1]), Math.Max(range[2], r[2]), Math.Max(range[3], r[3])];
        }

        return new Content(counts, range);
    }
}
