// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum SimplifyAction
{
    Status,
    Create,
    DeleteHoles,
    DeleteRounds,
    DeleteFaces,
    Activate,
    Delete,
}

public enum SimplifyModel
{
    Design,
    Simplified,
}

/// <summary>The simplified model of a part (Simplify tab).</summary>
[McpServerToolType]
public sealed class SimplifyTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_simplify", Title = "Simplified model", Destructive = false, OpenWorld = false)]
    [Description("""
        The simplified model of a part or sheet metal part: a lighter copy of the design body, without small holes, rounds or chosen faces, that assemblies can show instead (se_options assembly simplification). The design body never changes. Lengths in mm.
        status: whether there is one, which model is active (design | simplified), the simplified body (handle, volume, faces) and its features |
        create: makes it from the design body and activates it (once per document: Solid Edge cannot remove it afterwards) |
        delete_holes: every hole up to 'diameter' | delete_rounds: 'faces' of rounds | delete_faces: 'faces' removed and healed; faces of the simplified body (se_geometry faces with source = the body from status) |
        activate: model = design | simplified (what Solid Edge shows and edits) | delete: one simplification 'feature' (handle from status).
        Each change is checked by the simplified body (volume or faces) and deleted if nothing changed.
        Example: {"action":"create"}; {"action":"delete_holes","diameter":12}; {"action":"activate","model":"design"}.
        """)]
    public Task<CallToolResult> Simplify(
        SimplifyAction action,
        [Description("delete_holes: largest hole diameter to delete (mm).")] double? diameter = null,
        [Description("delete_rounds, delete_faces: face handles of the simplified body.")] string[]? faces = null,
        [Description("activate: design or simplified.")] SimplifyModel? model = null,
        [Description("delete: handle of a simplification feature (status).")] string? feature = null,
        [Description("Document handle; default: the document of the faces or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_simplify", Snake(action), c => SimplifyService.Run(c, document, Snake(action), diameter, faces,
            model is { } m ? Snake(m) : null, feature), cancellationToken);
}
