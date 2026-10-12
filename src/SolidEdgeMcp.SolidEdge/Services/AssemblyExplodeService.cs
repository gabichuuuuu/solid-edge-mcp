// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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
/// E_FAIL. There is no COM call to collapse the explode (the commands belong to the Explode environment).
/// </summary>
public static class AssemblyExplodeService
{
    private const string Collapse = "Solid Edge keeps the exploded placements apart from the assembly ones; to collapse them, close the assembly without saving or use the Explode environment.";

    /// <summary>Automatic explode of the top-level components, by part or by subassembly, with a spread distance (mm).</summary>
    public static object Explode(SeContext context, string? documentHandle, double? spread, string by)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        if (Items<Occurrence>(assembly.Occurrences).Count() < 2)
        {
            throw ToolException.InvalidArgument("The assembly needs at least two components to explode.");
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
            ? new { document = context.DocumentHandle((SolidEdgeDocument)assembly), moved, spread = spread, by, components, note = Collapse }
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
