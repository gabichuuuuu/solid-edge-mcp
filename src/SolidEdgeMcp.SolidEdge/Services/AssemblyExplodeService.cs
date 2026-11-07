// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Exploded views of an assembly: Solid Edge's automatic explode, which moves the components apart along their relations and
/// keeps the exploded placements apart from the assembly ones (Occurrence.GetExplodeMatrix; the assembly placement does not
/// change). Verified: a pin inserted on a block (insert relation) moves along its axis by the spread distance (50 mm → its
/// origin from z=20 to z=70); spread 0 leaves Solid Edge's minimum clearance; a component that was never exploded answers
/// E_FAIL. There is no COM call to collapse the explode (the commands belong to the Explode environment). Saving the exploded
/// assembly stores the explode as the explode configuration "explode,Solid Edge" (neither the explode nor a display
/// configuration saved meanwhile creates it; a display configuration does not keep the explode, and after opening the
/// assembly again nothing reads as exploded); a drawing view of that configuration is exploded, in the orientation stored
/// with it (verified: a front view came out "custom", 133 mm tall instead of 73).
/// </summary>
public static class AssemblyExplodeService
{
    private const string Collapse = "Solid Edge keeps the exploded placements apart from the assembly ones; to collapse them, close the assembly without saving or use the Explode environment.";

    internal const string Keep = "To keep the explode, save the assembly: Solid Edge stores it as the explode configuration 'explode,Solid Edge', which se_drawing base_view takes as 'configuration' for an exploded view, in the orientation the assembly window had when it was exploded ('orientation' chooses it). A display configuration (se_assembly_config save) does not keep it.";

    /// <summary>
    /// Automatic explode of the top-level components, by part or by subassembly, with a spread distance (mm). The explode
    /// configuration keeps the orientation the window had when the assembly was exploded (verified in Solid Edge 2026: exploded
    /// in a front window, its drawing view is front, and after exploding again in a top window, top; turning the window and
    /// saving without exploding again does not change it), so 'orientation' turns the window first.
    /// </summary>
    public static object Explode(SeContext context, string? documentHandle, double? spread, string by, string? orientation = null)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        if (Items<Occurrence>(assembly.Occurrences).Count() < 2)
        {
            throw ToolException.InvalidArgument("The assembly needs at least two components to explode.");
        }

        if (orientation is not null)
        {
            ViewService.Orient(context, context.DocumentHandle((SolidEdgeDocument)assembly), orientation);
        }

        double distance = spread is { } s ? Units.MmToM(Units.RequirePositive(s, "spread")) : 0;
        AutoExplodeTechniqueConstants technique = by switch
        {
            "part" => AutoExplodeTechniqueConstants.seByIndividualPart,
            "subassembly" => AutoExplodeTechniqueConstants.seBySubassemblyLevel,
            _ => throw ToolException.InvalidArgument($"Unknown level '{by}'.", "Use part or subassembly."),
        };

        string result;
        try
        {
            assembly.AutoExplode(AutoExplodeSelectionTypeConstants.seTopLevelAssembly, false, technique, out result, distance);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("explode_failed", $"Solid Edge could not explode the assembly: {ex.Message.Trim()}",
                "Auto explode follows the assembly relations: relate the components first (se_relations).", ex);
        }

        // The status text comes in Solid Edge's language, so the explode is checked by the components it moved.
        object[] components = Components(context, assembly, out int moved);
        return moved > 0
            ? new { document = context.DocumentHandle((SolidEdgeDocument)assembly), moved, spread = spread, by, orientation, components, note = $"{Collapse} {Keep}" }
            : throw new ToolException("explode_failed", $"Solid Edge did not move any component ({result?.Trim()}).",
                "Auto explode follows the assembly relations: relate the components first (se_relations), or give a larger 'spread'.");
    }

    /// <summary>Exploded placement of each top-level component next to its assembly placement.</summary>
    public static object Exploded(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object[] components = Components(context, assembly, out int moved);
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), moved, components, note = Collapse };
    }

    /// <summary>Whether a top-level component of the assembly sits apart from its assembly placement in the explode.</summary>
    internal static bool IsExploded(AssemblyDocument assembly) => Items<Occurrence>(assembly.Occurrences).Any(occurrence =>
        ExplodedOrigin(occurrence) is { } exploded && Geo.Length([.. exploded.Zip(Placement.Of(occurrence).Origin, (a, b) => a - b)]) > 1e-7);

    private static object[] Components(SeContext context, AssemblyDocument assembly, out int moved)
    {
        int count = 0;
        object[] rows = Items<Occurrence>(assembly.Occurrences).Select(occurrence =>
        {
            double[] origin = Placement.Of(occurrence).Origin;
            double[]? exploded = ExplodedOrigin(occurrence);
            double[]? offset = exploded?.Zip(origin, (a, b) => a - b).ToArray();
            double shift = offset is null ? 0 : Geo.Length(offset);
            if (shift > 1e-7)
            {
                count++;
            }

            return new
            {
                occurrence = context.Register(occurrence, "occurrence", (SolidEdgeDocument)assembly),
                name = occurrence.Name,
                origin = origin.Select(Geo.Mm).ToArray(),
                exploded = exploded is not null,
                explodedOrigin = exploded?.Select(Geo.Mm).ToArray(),
                offset = offset?.Select(Geo.Mm).ToArray(),
                distance = Geo.Mm(shift),
            };
        }).ToArray();
        moved = count;
        return rows;
    }

    /// <summary>Origin of the exploded placement, or null if the component was never exploded (Solid Edge answers E_FAIL).</summary>
    private static double[]? ExplodedOrigin(Occurrence occurrence)
    {
        try
        {
            Array matrix = new double[16];
            occurrence.GetExplodeMatrix(ref matrix);
            double[] m = [.. Geo.Doubles(matrix)];
            return [m[12], m[13], m[14]];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }
}
