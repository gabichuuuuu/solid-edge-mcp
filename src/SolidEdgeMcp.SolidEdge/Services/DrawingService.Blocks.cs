// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Blocks of a drawing: reusable groups of 2D graphics, placed on sheets at any position, scale and angle. Verified: a 40x20
/// rectangle and a circle selected on the sheet become the block (Blocks.Add with DeleteSelectedGraphics removes them from
/// the sheet); an occurrence at scale 2 spans 80x40 and one turned 30° spans the box of the turned rectangle. Templates may
/// bring blocks of their own (e.g. "COM"). The objects are
/// reached by name (they answer as SketchBlock objects, so they are used through IDispatch).
/// </summary>
public static partial class DrawingService
{
    /// <summary>Tolerance of the placement checks: Solid Edge reports ranges a fraction of a micrometre wide.</summary>
    private const double BlockTolerance = 1e-5;

    public static object Blocks(SeContext context, string? documentHandle)
    {
        DraftDocument draft = Draft(context, documentHandle);
        object[] blocks = Late.Items(BlocksOf(draft)).Select(DescribeBlock).ToArray();
        object[] occurrences = WorkingSheets(draft)
            .SelectMany(sheet => Late.Items(Late.Get(sheet, "BlockOccurrences")!).Select(o => DescribeBlockOccurrence(context, draft, sheet, o)))
            .ToArray();
        return new { document = DocumentHandle(context, draft), count = blocks.Length, blocks, occurrences };
    }

    /// <summary>
    /// Turns 2D elements drawn on a sheet into a block with its origin at <paramref name="origin"/> (mm) and puts one occurrence
    /// of it in their place, so the sheet looks the same.
    /// </summary>
    public static object CreateBlock(SeContext context, string? documentHandle, string? name, string[]? elements, double[]? origin)
    {
        string title = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("create_block requires 'name'.") : name.Trim();
        if (elements is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("create_block requires 'elements': handles of 2D elements drawn on the sheet (se_sketch on the sheet).");
        }

        (double x, double y) = Point2(origin, "position", "create_block requires 'position': the block origin [x, y] in mm on the sheet.");
        DraftDocument draft = DraftFor(context, documentHandle, elements[0]);
        if (FindBlock(draft, title) is not null)
        {
            throw ToolException.InvalidArgument($"The drawing already has a block '{title}'.", "Pass another 'name', or delete it first.");
        }

        object[] graphics = elements.Select(e => context.Handles.Resolve(e, "elements")).ToArray();
        Sheet sheet = graphics[0] is { } first && Late.TryGet(first, "Parent") is Sheet parent ? parent : draft.ActiveSheet;
        double[] before = Union(graphics.Select(GraphicRange));
        SelectSet selection = context.Application.ActiveSelectSet;
        selection.RemoveAll();
        foreach (object graphic in graphics)
        {
            selection.Add(graphic);
        }

        object block;
        try
        {
            block = Late.Call(BlocksOf(draft), "Add", title, Units.MmToM(x), Units.MmToM(y), false, true)
                ?? throw new ToolException("block_failed", $"Solid Edge did not create the block '{title}'.");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("block_failed", $"Solid Edge did not create the block '{title}': {com.Message.Trim()}",
                "Pass handles of 2D elements drawn on a sheet (se_sketch with the sheet handle).", ex);
        }
        finally
        {
            selection.RemoveAll();
        }

        object occurrence = Place(draft, sheet, title, x, y, 1, 0);
        double[] after = OccurrenceRange(occurrence);
        if (!SameRange(before, after))
        {
            throw new ToolException("block_failed",
                $"The block '{title}' was created, but its occurrence does not cover the original elements ({RangeText(after)} instead of {RangeText(before)}).",
                "Check it with action 'blocks'; delete it with 'delete_block' if it is wrong.");
        }

        foreach (string element in elements)
        {
            context.Handles.Remove(element, "the element became part of a block");
        }

        return new { block = DescribeBlock(block), occurrence = DescribeBlockOccurrence(context, draft, sheet, occurrence) };
    }

    /// <summary>Places an occurrence of a block on a sheet: origin at <paramref name="position"/> (mm), scale and angle (degrees).</summary>
    public static object PlaceBlock(SeContext context, string? documentHandle, string? sheetName, string? name, double[]? position, double? scale, double? angle)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetName);
        string title = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("place_block requires 'name': the block name (action 'blocks').") : name.Trim();
        object block = FindBlock(draft, title) ?? throw ToolException.NotFound($"The drawing has no block '{title}'.", "Action 'blocks' lists them; create one with 'create_block' or 'import_block'.");
        (double x, double y) = Point2(position, "position", "place_block requires 'position': where the block origin goes, [x, y] in mm on the sheet.");
        double factor = scale is { } s ? Units.RequirePositive(s, "scale") : 1;
        double turn = Units.DegToRad(Units.RequireFinite(angle ?? 0, "angle"));
        Sheet sheet = ResolveSheet(context, draft, sheetName);
        int before = Convert.ToInt32(Late.Get(block, "OccurrenceCount"));
        object occurrence = Place(draft, sheet, title, x, y, factor, turn);
        int count = Convert.ToInt32(Late.Get(block, "OccurrenceCount"));
        double[] range = OccurrenceRange(occurrence);
        return count == before + 1 && range[2] - range[0] > 0 && range[3] - range[1] > 0
            ? DescribeBlockOccurrence(context, draft, sheet, occurrence)
            : throw new ToolException("block_failed", $"Solid Edge did not place the block '{title}'.", "Check the block with action 'blocks'.");
    }

    /// <summary>
    /// Brings a saved drawing (.dft) in as a block named after the file; verified: the blocks it contains are copied too.
    /// </summary>
    public static object ImportBlock(SeContext context, string? documentHandle, string? path)
    {
        DraftDocument draft = Draft(context, documentHandle);
        string file = context.Paths.Check(path, "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{Path.GetFileName(file)}' does not exist.", "Pass the absolute path of a saved drawing (.dft).");
        }

        HashSet<string> existing = Late.Items(BlocksOf(draft)).Select(b => Late.TryGetString(b, "Name") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        object block;
        try
        {
            block = Late.Call(BlocksOf(draft), "AddBlockByFile", file)
                ?? throw new ToolException("block_failed", $"Solid Edge did not import a block from '{Path.GetFileName(file)}'.");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("block_failed", $"Solid Edge did not import a block from '{Path.GetFileName(file)}': {com.Message.Trim()}",
                "Pass a saved drawing (.dft).", ex);
        }

        string? imported = Late.TryGetString(block, "Name");
        return new { block = DescribeBlock(block), added = imported is not null && !existing.Contains(imported), count = Late.Count(BlocksOf(draft)) };
    }

    /// <summary>Deletes a block and its occurrences.</summary>
    public static object DeleteBlock(SeContext context, string? documentHandle, string? name)
    {
        DraftDocument draft = Draft(context, documentHandle);
        string title = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("delete_block requires 'name'.") : name.Trim();
        object block = FindBlock(draft, title) ?? throw ToolException.NotFound($"The drawing has no block '{title}'.", "Action 'blocks' lists them.");
        Late.Call(block, "Delete");
        return FindBlock(draft, title) is null
            ? new { deleted = title, count = Late.Count(BlocksOf(draft)) }
            : throw new ToolException("block_failed", $"Solid Edge did not delete the block '{title}'.", "Check it with action 'blocks'.");
    }

    private static object Place(DraftDocument draft, Sheet sheet, string title, double x, double y, double scale, double angle)
    {
        try
        {
            return Late.Call(Late.Get(sheet, "BlockOccurrences")!, "Add", title, Units.MmToM(x), Units.MmToM(y), Type.Missing, scale, angle)
                ?? throw new ToolException("block_failed", $"Solid Edge did not place the block '{title}'.");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("block_failed", $"Solid Edge did not place the block '{title}': {com.Message.Trim()}",
                $"Check the block in '{SeContext.SafeName(Document(draft))}' with action 'blocks'.", ex);
        }
    }

    private static object BlocksOf(DraftDocument draft) => Late.Get(draft, "Blocks")!;

    private static object? FindBlock(DraftDocument draft, string name) =>
        Late.Items(BlocksOf(draft)).FirstOrDefault(b => string.Equals(Late.TryGetString(b, "Name"), name, StringComparison.OrdinalIgnoreCase));

    private static object DescribeBlock(object block) => new
    {
        name = Late.TryGetString(block, "Name"),
        occurrences = Convert.ToInt32(Late.Get(block, "OccurrenceCount")),
        views = Late.Items(Late.Get(block, "Views")!).Select(v => Late.TryGetString(v, "Name")).ToArray(),
    };

    private static object DescribeBlockOccurrence(SeContext context, DraftDocument draft, Sheet sheet, object occurrence)
    {
        object?[] origin = Late.CallByRef(occurrence, "GetOrigin", [0.0, 0.0], 0, 1);
        double[] range = OccurrenceRange(occurrence);
        return new
        {
            handle = context.Register(occurrence, "block_occurrence", Document(draft)),
            block = Late.Get(occurrence, "Block") is { } block ? Late.TryGetString(block, "Name") : null,
            sheet = sheet.Name,
            origin = new[] { Geo.Mm(Convert.ToDouble(origin[0])), Geo.Mm(Convert.ToDouble(origin[1])) },
            scale = Geo.R(Convert.ToDouble(Late.Get(occurrence, "ScaleFactor"))),
            angle = Geo.R(Units.RadToDeg(Convert.ToDouble(Late.Get(occurrence, "RotationAngle")))),
            range = new { min = new[] { Geo.Mm(range[0]), Geo.Mm(range[1]) }, max = new[] { Geo.Mm(range[2]), Geo.Mm(range[3]) } },
        };
    }

    private static double[] OccurrenceRange(object occurrence) => GraphicRange(occurrence);

    /// <summary>Range [xmin, ymin, xmax, ymax] in metres of a 2D graphic or block occurrence.</summary>
    private static double[] GraphicRange(object graphic)
    {
        object?[] r = Late.CallByRef(graphic, "Range", [0.0, 0.0, 0.0, 0.0], 0, 1, 2, 3);
        return [.. r.Select(Convert.ToDouble)];
    }

    private static double[] Union(IEnumerable<double[]> ranges) => ranges.Aggregate((a, b) =>
        [Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[2], b[2]), Math.Max(a[3], b[3])]);

    private static bool SameRange(double[] a, double[] b) => a.Zip(b, (p, q) => Math.Abs(p - q)).All(d => d <= BlockTolerance);

    private static string RangeText(double[] r) => $"[{Geo.Mm(r[0])}, {Geo.Mm(r[1])}]-[{Geo.Mm(r[2])}, {Geo.Mm(r[3])}]";

    private static (double X, double Y) Point2(double[]? value, string argument, string missing) => value switch
    {
        null => throw ToolException.InvalidArgument(missing),
        [double px, double py] => (Units.RequireFinite(px, argument), Units.RequireFinite(py, argument)),
        _ => throw ToolException.InvalidArgument($"'{argument}' must be [x, y] in mm."),
    };
}
