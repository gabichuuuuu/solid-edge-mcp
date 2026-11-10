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

/// <summary>The simplified model of a part (Simplify tab) or of an assembly.</summary>
[McpServerToolType]
public sealed class SimplifyTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_simplify", Title = "Simplified model", Destructive = false, OpenWorld = false)]
    [Description("""
        The simplified model of a part or sheet metal part: a lighter copy of the design body, without small holes, rounds or chosen faces, that assemblies can show instead (se_options assembly simplification). The design body never changes. Lengths in mm.
        status: whether there is one, which model is active (design | simplified), the simplified body (handle, volume, faces) and its features |
        create: makes it from the design body and activates it (once per document: Solid Edge cannot remove it afterwards) |
        delete_holes: every hole up to 'diameter' | delete_rounds: 'faces' of rounds | delete_faces: 'faces' removed and healed | delete_region: 'edges' = the closed loop around a region, 'faces' = [one face inside it]; faces and edges of the simplified body (se_geometry with source = the body from status) |
        activate: model = design | simplified (what Solid Edge shows and edits) | delete: one simplification 'feature' (handle from status) | save_as (path = .x_t or .x_b: the simplified body as Parasolid).
        Each change is checked by the simplified body (volume or faces) and deleted if nothing changed.
        An assembly: status (what it leaves out, mode) | create (occurrences to leave out and/or ratio = leave out the components smaller than that share of the assembly, 0-1) |
        visible_faces (Simplify > Visible Faces: only what is seen from outside; occurrences, ratio; bodies = copy whole bodies) | update (made again from the components as they are) | delete | save_as (path = a .par of what is left).
        Example: {"action":"create"}; {"action":"delete_holes","diameter":12}; {"action":"activate","model":"design"}.
        """)]
    public Task<CallToolResult> Simplify(
        SimplifyAction action,
        [Description("delete_holes: largest hole diameter to delete (mm).")] double? diameter = null,
        [Description("delete_rounds, delete_faces: face handles of the simplified body; delete_region: one face inside the region.")] string[]? faces = null,
        [Description("delete_region: the closed loop of edges around the region (simplified body).")] string[]? edges = null,
        [Description("activate: design or simplified.")] SimplifyModel? model = null,
        [Description("delete: handle of a simplification feature (status).")] string? feature = null,
        [Description("Assembly create, visible_faces: occurrence handles to leave out.")] string[]? occurrences = null,
        [Description("Assembly create, visible_faces: leave out the components smaller than this share of the assembly size (0-1).")] double? ratio = null,
        [Description("save_as: absolute path to write (allowed folders): a .par for an assembly, .x_t or .x_b for a part.")] string? path = null,
        [Description("Assembly visible_faces: copy whole bodies instead of the visible faces.")] bool bodies = false,
        [Description("Document handle; default: the document of the faces or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_simplify", Snake(action), c => SimplifyService.Run(c, document, action, diameter, faces,
            model, feature, edges, occurrences, ratio, path, bodies), cancellationToken);
}
