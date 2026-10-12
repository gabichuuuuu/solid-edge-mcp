// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum FaceRelationAction
{
    List,
    Coplanar,
    Offset,
    Parallel,
    Perpendicular,
    Tangent,
    Concentric,
    Fix,
    Edit,
    Delete,
    LiveRules,
    SetLiveRules,
    RestoreLiveRules,
}

public enum LiveRule
{
    Concentric,
    Coplanar,
    TangentEdge,
    TangentTouching,
    Parallel,
    Perpendicular,
    Symmetric,
    SymmetricXy,
    SymmetricYz,
    SymmetricZx,
    MaintainRadius,
    OrthoLocking,
    CoplanarAxes,
    CoplanarAxesX,
    CoplanarAxesY,
    CoplanarAxesZ,
    ThicknessChain,
    Offset,
}

/// <summary>3D relations between faces of a part (Relate commands of synchronous modelling) and live rules.</summary>
[McpServerToolType]
public sealed class FaceRelationTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_face_relation", Title = "Face relations and live rules", Destructive = true, OpenWorld = false)]
    [Description("""
        Relates faces of a part as the Relate commands of synchronous modelling do, and reads or sets the live rules. mm.
        list (relations of the part: index, handle, type, distance, target, faces) |
        coplanar | offset (distance between parallel planes; the faces move along their normal and stay on their side of the target) |
        parallel | perpendicular (planes, cylinders, cones) | tangent (a cylinder and a plane, or two cylinders with parallel axes) | concentric (cylinders, cones):
        faces = the faces that move (a relation each), target = the face that stays |
        fix (faces: a fixed relation each; later relations and edits keep them) | edit (relation, distance: a new distance for an offset or coplanar relation) |
        delete (relation; the faces stay where they are) |
        live_rules | set_live_rules (rules with maintain, or values = {rule: true|false}, e.g. the 'previous' of an earlier call) | restore_live_rules (Solid Edge's defaults; offset stays as it is).
        Live rules belong to the Solid Edge session: they apply to every part and sheet metal document, so put them back when done.
        Relations stay with the part and later synchronous edits keep them. The faces must be synchronous geometry (se_primitive solids, synchronous parts, imported bodies; not faces of ordered features); the document switches to synchronous modelling for the call and back.
        Every new relation is checked on the geometry: the target must stay and each face must meet it, or the relation is deleted and the call fails, saying whether the model changed.
        Solid Edge clears the undo list when a relation is added, so it cannot be undone: edit or delete it instead, and check the faces first (se_geometry faces).
        relation = handle or index from list.
        Example: {"action":"offset","faces":["face:8"],"target":"face:9","distance":10}.
        """)]
    public Task<CallToolResult> FaceRelation(
        FaceRelationAction action,
        [Description("The faces that move (se_geometry faces handles); fix: the faces to hold in place.")] string[]? faces = null,
        [Description("The face that stays.")] string? target = null,
        [Description("offset, edit: mm between the planes (0 = coplanar).")] double? distance = null,
        [Description("edit, delete: relation handle or index from list.")] string? relation = null,
        [Description("set_live_rules: the live rules to change.")] LiveRule[]? rules = null,
        [Description("set_live_rules: true = synchronous edits keep the rules (default), false = they ignore them.")] bool maintain = true,
        [Description("set_live_rules: instead of rules and maintain, the value of each rule, e.g. {\"coplanar\":true,\"offset\":false}.")] Dictionary<string, bool>? values = null,
        [Description("Part document handle (live rules: any part or sheet metal document); default: the document of the faces or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        string act = Snake(action);
        var spec = new FaceRelationSpec(act, faces, target, distance, relation, rules?.Select(r => Snake(r)).ToArray(), maintain, values);
        return runner.RunAsync("se_face_relation", act, c => FaceRelationService.Run(c, document, spec), cancellationToken);
    }
}
