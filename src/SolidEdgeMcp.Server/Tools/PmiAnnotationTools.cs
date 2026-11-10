// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>PMI (3D) annotations of parts and sheet metal, attached to the edges of the model.</summary>
[McpServerToolType]
public sealed class PmiAnnotationTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_pmi_annotation", Title = "PMI annotations (3D)", Destructive = true, OpenWorld = false)]
    [Description("""
        3D (PMI) annotations of a part or sheet metal document, attached to its edges. points = model points in mm on the edges (at an edge's end: the vertex).
        plane = '1'-'3' or a reference plane, default the locked PMI plane (se_pmi plane): the annotation is drawn parallel to it through the picked point.
        position = model point [x, y, z] mm where the leader ends (the balloon or symbol), projected onto that plane; default 15 mm along the plane's x and y.
        list (kind filter) | text (text, lines split by \n; position = where it goes) | balloon | callout (points + text) | leader (points) |
        datum (points + text = the letter; position = on a leader) | datum_target (like datum, text e.g. 'A1') | datum_point (points: a vertex or a point on an edge) |
        surface_finish (points; finish, text = roughness e.g. 'Ra 3.2'; position = on a leader) |
        feature_control_frame (points; characteristic + tolerance (⌀: diameter zone) + datums, or text = the frame in Solid Edge codes; rows, composite as in se_annotation) |
        weld (points; weld, weldSide, text = size e.g. 'a5', allAround, field) | gost_weld (points; text = 'above' or 'above|below' the shelf; allAround, field) |
        corner (points; text = deviation mm '+0.3' or 'upper|lower') | center_mark (points = on a circular edge) |
        centerline (points = on two straight edges: mid line, or two circular edges: centre to centre) | bolt_circle (points = on three hole edges) |
        move (annotation, position: its leader end, or a text (plane = the text's own), goes there; datum frames keep their leader square to the edge) | delete (annotation).
        Each new annotation is checked (attached to its edge, content and leader end read back) and removed if wrong. PMI dimensions: se_pmi dimension.
        Example: {"action":"feature_control_frame","plane":"3","points":[50,0,20],"characteristic":"flatness","tolerance":"0.05"}.
        """)]
    public Task<CallToolResult> PmiAnnotation(
        PmiAnnotationAction action,
        [Description("The plane the annotation is drawn in: '1'-'3' or a reference plane handle; default: the locked PMI plane.")] string? plane = null,
        [Description("Model points [x1, y1, z1, ...] in mm on the edges to annotate.")] double[]? points = null,
        [Description("Model point [x, y, z] in mm: where the leader ends (text: where the text goes), projected onto the plane.")] double[]? position = null,
        [Description("text, balloon, callout: the text; datum: the letter; datum_target: e.g. 'A1'; surface_finish: roughness; weld: size or note; gost_weld: 'above|below'; corner: deviations; feature_control_frame: the whole frame in Solid Edge codes.")] string? text = null,
        [Description("surface_finish: symbol type (default machined).")] SurfaceFinishType finish = SurfaceFinishType.Machined,
        [Description("feature_control_frame: geometric characteristic.")] GeometricCharacteristic? characteristic = null,
        [Description("feature_control_frame: tolerance value, e.g. '0.05' or '⌀0.1'.")] string? tolerance = null,
        [Description("feature_control_frame: datum labels in order, e.g. [\"A\",\"B\"].")] string[]? datums = null,
        [Description("feature_control_frame: up to 3 rows below the first, cells split by '|'.")] string[]? rows = null,
        [Description("feature_control_frame: composite frame (the rows share the first characteristic).")] bool composite = false,
        [Description("weld: weld type (default fillet).")] WeldType weld = WeldType.Fillet,
        [Description("weld: arrow, other or both sides.")] WeldSide weldSide = WeldSide.Arrow,
        [Description("weld, gost_weld: weld all around.")] bool allAround = false,
        [Description("weld, gost_weld: field weld.")] bool field = false,
        [Description("list: only this kind.")] AnnotationKind? kind = null,
        [Description("move, delete: annotation handle.")] string? annotation = null,
        [Description("Part or sheet metal document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new PmiAnnotationSpec(action, plane, points, position, text, finish, characteristic, tolerance, datums, rows,
            composite, weld, weldSide, allAround, field, annotation, kind);
        return runner.RunAsync("se_pmi_annotation", Snake(action), c => PmiAnnotationService.Run(c, document, spec), cancellationToken);
    }
}
