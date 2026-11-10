// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>Read-only model queries: geometry, measurements and physical properties (mm).</summary>
[McpServerToolType]
public sealed class InspectTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_geometry", Title = "Model geometry", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Geometry of a part or sheet metal body, in mm. summary (volume mm3, bounding box, face/edge/vertex counts) |
        faces (handle, type, area mm2, bounding box, outward normal of planes, radius and axis of cylinders) |
        edges (handle, type, start/end points, length, direction of lines, centre and radius of circles).
        draft (direction = the pull direction of the mould; minDraft degrees, default 1: each face's least and greatest draft, asin(normal . direction), and its class positive | negative | insufficient | mixed, with counts).
        'source' limits faces/edges to a feature (handle from se_feature_tree) or, for edges, to a face; 'direction' selects by orientation (e.g. [0,0,1]: top faces / vertical edges).
        In an assembly, 'source' = an occurrence handle (se_assembly) gives the summary or the faces/edges of its part in assembly coordinates, with handles tied to that occurrence: use them for se_relations and se_measure (they stay valid when the occurrence moves; query again for new coordinates).
        Face and edge handles expire when the model changes.
        """)]
    public Task<CallToolResult> Geometry(
        GeometryAction action = GeometryAction.Summary,
        [Description("Feature handle (faces/edges of that feature), face handle (its edges) or assembly occurrence handle (its part, in assembly coordinates; also for summary); default: whole body.")] string? source = null,
        [Description("Filter. Faces: all | plane | cylinder | cone | sphere | torus. Edges: all | line | circle | spline | open (edges of one face only: the free edges of a surface).")] string type = "all",
        [Description("Filter [x,y,z]: faces = planar faces whose outward normal points this way; edges = straight edges parallel to it.")] double[]? direction = null,
        int offset = 0,
        int limit = 100,
        [Description("draft: least draft in degrees a face needs (default 1).")] double minDraft = 1,
        [Description("Document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_geometry";
        string name = action.ToString();
        return action switch
        {
            GeometryAction.Summary => runner.RunAsync(tool, name, c => InspectService.Summary(c, document, source), cancellationToken),
            GeometryAction.Faces => runner.RunAsync(tool, name, c => InspectService.Faces(c, document, source, type, direction, offset, limit), cancellationToken),
            GeometryAction.Edges => runner.RunAsync(tool, name, c => InspectService.Edges(c, document, source, type, direction, offset, limit), cancellationToken),
            GeometryAction.Draft => runner.RunAsync(tool, name, c => InspectService.Draft(c, document, source, direction, minDraft, offset, limit), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_measure", Title = "Measure", ReadOnly = true, OpenWorld = false)]
    [Description("""
        distance: minimum (type minimum, default) or maximum distance in mm between two elements (faces, edges, vertices, bodies, planes), with the points it runs between |
        angle: degrees between planar faces / reference planes (normals) or straight edges (directions) |
        normal_distance: distance along the normal of 'first' (reference plane, '1'-'3' / 'xy', or planar face) to the start (or 'at' end) of the edge 'second', with its x, y, z components |
        wall_thickness (part or sheet metal, no first/second): Solid Edge's wall thickness analysis of the body (resolution coarse | standard | fine): thinnest and thickest wall and where (measured along each point's inward normal: at the end of a wall that runs along it, so the thickest can be a wall's length), and the thinnest uniform wall (an edge, such as the open rim of a shell, can read near 0); with 'threshold' (mm) the area of the walls thinner than it.
        In an assembly (distance, angle): occurrences, assembly planes and face/edge handles from se_geometry with 'source' = an occurrence (assembly coordinates).
        Example: {"action":"distance","first":"face:12","second":"face:15","type":"maximum"}; {"action":"wall_thickness","threshold":2}.
        """)]
    public Task<CallToolResult> Measure(
        MeasureAction action,
        [Description("First element handle (normal_distance: the plane or planar face).")] string? first = null,
        [Description("Second element handle (normal_distance: an edge).")] string? second = null,
        [Description("distance: minimum (default) | maximum.")] DistanceType type = DistanceType.Minimum,
        [Description("normal_distance: the end of the edge 'second' measured, start (default) | end.")] EdgeEnd at = EdgeEnd.Start,
        [Description("wall_thickness: mesh of the analysis, coarse | standard (default) | fine.")] ThicknessResolution resolution = ThicknessResolution.Standard,
        [Description("wall_thickness: report the area of walls thinner than this, in mm.")] double? threshold = null,
        [Description("Document handle; default: the first element's document (wall_thickness: the active one).")] string? document = null,
        CancellationToken cancellationToken = default) =>
        action switch
        {
            MeasureAction.Distance => runner.RunAsync("se_measure", "distance", c => InspectService.Distance(c, document, first, second, type), cancellationToken),
            MeasureAction.Angle => runner.RunAsync("se_measure", "angle", c => InspectService.Angle(c,
                first ?? throw ToolException.InvalidArgument("angle needs 'first' and 'second': element handles."),
                second ?? throw ToolException.InvalidArgument("angle needs 'second': an element handle.")), cancellationToken),
            MeasureAction.NormalDistance => runner.RunAsync("se_measure", "normal_distance", c => InspectService.NormalDistance(c, document, first, second, at), cancellationToken),
            MeasureAction.WallThickness => runner.RunAsync("se_measure", "wall_thickness", c => InspectService.WallThickness(c, document, resolution, threshold), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    [McpServerTool(Name = "se_physical", Title = "Physical properties", ReadOnly = true, OpenWorld = false)]
    [Description("Volume (mm3), area (mm2) and centre of volume of the part; with a density (argument in kg/m3, or the document material) also mass (kg), centre of gravity (mm), inertia about the global axes through the origin (kg*mm2; products of inertia as +integral of xy dm) and principal moments about the centre of gravity. An assembly is measured with the material of each part (no 'density'; area is not given); mass and inertia only when every part has a density (else 'partsWithoutDensity') and so do the weld beads of a weldment (else 'weldBeadsWithoutDensity': se_material apply on the weldment).")]
    public Task<CallToolResult> Physical(
        [Description("Part: density in kg/m3 (e.g. 7850 for steel); default: the document's material density.")] double? density = null,
        [Description("Document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_physical", "compute", c => InspectService.Physical(c, document, density), cancellationToken);
}
