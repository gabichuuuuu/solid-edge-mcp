// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Sketch blocks of a part or sheet metal part: 2D elements of a sketch made into a block of the document (Blocks.Add takes the
/// selected elements and removes them) and occurrences of it placed in sketches at any position, scale and angle. Verified: a
/// 40x20 rectangle became block B1; an occurrence at (100, 0) spans 100-140 x 0-20 and one at scale 2 turned 0.5 rad spans
/// 89.4 x 73.5 (the box of the turned 80x40 rectangle). The objects answer as drawing blocks, so they are used through IDispatch.
/// </summary>
public static partial class SketchService
{
    private const double BlockTolerance = 1e-5;

    public static object Blocks(SeContext context, string sketch)
    {
        Profile profile = Resolve(context, sketch);
        SolidEdgeDocument document = context.DocumentOf(sketch);
        object[] blocks = Late.Items(BlocksOf(document)).Select(DescribeBlock).ToArray();
        object[] occurrences = Late.Items(OccurrencesOf(profile)).Select(o => DescribeBlockOccurrence(context, document, o)).ToArray();
        return new { sketch, count = blocks.Length, blocks, occurrences };
    }

    /// <summary>Turns elements of the sketch into a block with its origin at <paramref name="origin"/> and puts one occurrence in their place.</summary>
    public static object CreateBlock(SeContext context, string sketch, string? name, string[]? elements, double[]? origin)
    {
        string title = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("create_block requires 'name'.") : name.Trim();
        if (elements is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("create_block requires 'elements': handles of 2D elements of the sketch (se_sketch info).");
        }

        (double x, double y) = BlockPoint(origin, "create_block requires 'center': the block origin [x, y] in mm in the sketch.");
        Profile profile = Resolve(context, sketch);
        SolidEdgeDocument document = context.DocumentOf(sketch);
        if (FindBlock(document, title) is not null)
        {
            throw ToolException.InvalidArgument($"The document already has a block '{title}'.", "Pass another 'name', or delete it first.");
        }

        object[] graphics = elements.Select(e => context.Handles.Resolve(e, "elements")).ToArray();
        double[] before = graphics.Select(BlockRange).Aggregate((a, b) =>
            [Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[2], b[2]), Math.Max(a[3], b[3])]);
        SelectSet selection = context.Application.ActiveSelectSet;
        selection.RemoveAll();
        foreach (object graphic in graphics)
        {
            selection.Add(graphic);
        }

        object block;
        try
        {
            block = Late.Call(BlocksOf(document), "Add", title, Units.MmToM(x), Units.MmToM(y), false, true)
                ?? throw new ToolException("block_failed", $"Solid Edge did not create the block '{title}'.");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("block_failed", $"Solid Edge did not create the block '{title}': {com.Message.Trim()}",
                "Pass handles of 2D elements of the sketch (se_sketch info).", ex);
        }
        finally
        {
            selection.RemoveAll();
        }

        object occurrence = PlaceOccurrence(profile, title, x, y, 1, 0);
        double[] after = BlockRange(occurrence);
        if (before.Zip(after, (p, q) => Math.Abs(p - q)).Any(d => d > BlockTolerance))
        {
            throw new ToolException("block_failed", $"The block '{title}' was created, but its occurrence does not cover the original elements.",
                "Check it with action 'blocks'; delete it with 'delete_block' if it is wrong.");
        }

        foreach (string element in elements)
        {
            context.Handles.Remove(element, "the element became part of a block");
        }

        return new { block = DescribeBlock(block), occurrence = DescribeBlockOccurrence(context, document, occurrence) };
    }

    /// <summary>Places an occurrence of a block in the sketch: origin at <paramref name="position"/> (mm), scale and angle (degrees).</summary>
    public static object PlaceBlock(SeContext context, string sketch, string? name, double[]? position, double? scale, double? angle)
    {
        string title = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("place_block requires 'name': the block name (action 'blocks').") : name.Trim();
        Profile profile = Resolve(context, sketch);
        SolidEdgeDocument document = context.DocumentOf(sketch);
        object block = FindBlock(document, title) ?? throw ToolException.NotFound($"The document has no block '{title}'.", "Action 'blocks' lists them; create one with 'create_block'.");
        (double x, double y) = BlockPoint(position, "place_block requires 'center': where the block origin goes, [x, y] in mm in the sketch.");
        double factor = scale is { } s ? Units.RequirePositive(s, "scale") : 1;
        double turn = Units.DegToRad(Units.RequireFinite(angle ?? 0, "angle"));
        int before = Convert.ToInt32(Late.Get(block, "OccurrenceCount"), CultureInfo.InvariantCulture);
        object occurrence = PlaceOccurrence(profile, title, x, y, factor, turn);
        int count = Convert.ToInt32(Late.Get(block, "OccurrenceCount"), CultureInfo.InvariantCulture);
        double[] range = BlockRange(occurrence);
        return count == before + 1 && range[2] - range[0] > 0 && range[3] - range[1] > 0
            ? DescribeBlockOccurrence(context, document, occurrence)
            : throw new ToolException("block_failed", $"Solid Edge did not place the block '{title}'.", "Check the block with action 'blocks'.");
    }

    /// <summary>Deletes a block and its occurrences.</summary>
    public static object DeleteBlock(SeContext context, string sketch, string? name)
    {
        string title = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("delete_block requires 'name'.") : name.Trim();
        SolidEdgeDocument document = context.DocumentOf(sketch);
        object block = FindBlock(document, title) ?? throw ToolException.NotFound($"The document has no block '{title}'.", "Action 'blocks' lists them.");
        Late.Call(block, "Delete");
        return FindBlock(document, title) is null
            ? new { deleted = title, count = Late.Count(BlocksOf(document)) }
            : throw new ToolException("block_failed", $"Solid Edge did not delete the block '{title}'.", "Check it with action 'blocks'.");
    }

    private static object PlaceOccurrence(Profile profile, string title, double x, double y, double scale, double angle)
    {
        try
        {
            return Late.Call(OccurrencesOf(profile), "Add", title, Units.MmToM(x), Units.MmToM(y), Type.Missing, scale, angle)
                ?? throw new ToolException("block_failed", $"Solid Edge did not place the block '{title}'.");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("block_failed", $"Solid Edge did not place the block '{title}': {com.Message.Trim()}",
                "Check the block with action 'blocks'.", ex);
        }
    }

    private static object BlocksOf(SolidEdgeDocument document) =>
        Late.TryGet(document, "Blocks") ?? throw new ToolException("not_supported", $"'{SeContext.SafeName(document)}' has no sketch blocks.",
            "Use a sketch of a part or sheet metal part (drawings: se_drawing create_block).");

    private static object OccurrencesOf(Profile profile) => Late.Get(profile, "BlockOccurrences")!;

    private static object? FindBlock(SolidEdgeDocument document, string name) =>
        Late.Items(BlocksOf(document)).FirstOrDefault(b => string.Equals(Late.TryGetString(b, "Name"), name, StringComparison.OrdinalIgnoreCase));

    private static object DescribeBlock(object block) => new
    {
        name = Late.TryGetString(block, "Name"),
        occurrences = Convert.ToInt32(Late.Get(block, "OccurrenceCount"), CultureInfo.InvariantCulture),
    };

    private static object DescribeBlockOccurrence(SeContext context, SolidEdgeDocument document, object occurrence)
    {
        object?[] origin = Late.CallByRef(occurrence, "GetOrigin", [0.0, 0.0], 0, 1);
        double[] range = BlockRange(occurrence);
        return new
        {
            handle = context.Register(occurrence, "block_occurrence", document),
            block = Late.Get(occurrence, "Block") is { } block ? Late.TryGetString(block, "Name") : null,
            origin = new[] { Geo.Mm(Convert.ToDouble(origin[0], CultureInfo.InvariantCulture)), Geo.Mm(Convert.ToDouble(origin[1], CultureInfo.InvariantCulture)) },
            scale = Geo.R(Convert.ToDouble(Late.Get(occurrence, "ScaleFactor"), CultureInfo.InvariantCulture)),
            angle = Geo.R(Units.RadToDeg(Convert.ToDouble(Late.Get(occurrence, "RotationAngle"), CultureInfo.InvariantCulture))),
            range = new { min = new[] { Geo.Mm(range[0]), Geo.Mm(range[1]) }, max = new[] { Geo.Mm(range[2]), Geo.Mm(range[3]) } },
        };
    }

    /// <summary>Range [xmin, ymin, xmax, ymax] in metres of a 2D element or block occurrence.</summary>
    private static double[] BlockRange(object graphic)
    {
        object?[] r = Late.CallByRef(graphic, "Range", [0.0, 0.0, 0.0, 0.0], 0, 1, 2, 3);
        return [.. r.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture))];
    }

    private static (double X, double Y) BlockPoint(double[]? value, string missing) => value switch
    {
        null => throw ToolException.InvalidArgument(missing),
        [double px, double py] => (Units.RequireFinite(px, "center"), Units.RequireFinite(py, "center")),
        _ => throw ToolException.InvalidArgument("'center' must be [x, y] in mm."),
    };
}
