// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum FeatureOperation
{
    Add,
    Cut,
}

public enum ExtrudeExtent
{
    Finite,
    ThroughAll,
    ThroughNext,
}

public enum FeatureDirection
{
    Normal,
    Reverse,
    Symmetric,
}

public enum FeatureEditAction
{
    Suppress,
    Unsuppress,
    Delete,
    Rename,
    Group,
    Ungroup,
    Reorder,
    RollTo,
    RollToEnd,
}

public enum ReorderPosition
{
    Before,
    After,
}

/// <summary>
/// Part operations (ordered modelling) and the feature tree. Each new operation is
/// verified by status and volume change; if it fails, it is deleted.
/// </summary>
[McpServerToolType]
public sealed class FeatureTools(ToolRunner runner)
{
    private const string VerifiedNote = "The result is verified (feature status and volume change in mm3); a feature that fails or has no effect is deleted and reported as an error.";

    [McpServerTool(Name = "se_extrude", Title = "Extrude", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Extrudes a closed sketch (se_sketch close): operation add (protrusion; also the first feature of a part) or cut (cutout).
        extent: finite (distance in mm) | through_all | through_next. direction: normal (along the sketch normal given by se_sketch create) | reverse | symmetric (finite: total distance centred on the sketch plane).
        {{VerifiedNote}}
        Example: {"sketch":"sketch:5","operation":"add","distance":20}.
        """)]
    public Task<CallToolResult> Extrude(
        [Description("Closed sketch handle.")] string sketch,
        FeatureOperation operation = FeatureOperation.Add,
        ExtrudeExtent extent = ExtrudeExtent.Finite,
        FeatureDirection direction = FeatureDirection.Normal,
        [Description("For extent 'finite': depth in mm.")] double? distance = null,
        [Description("Document handle; default: the sketch's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_extrude", Snake(operation), c => FeatureService.Extrude(c, document, sketch, Snake(operation),
            Snake(extent), Snake(direction), distance), cancellationToken);

    [McpServerTool(Name = "se_revolve", Title = "Revolve", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Revolves a closed sketch around its axis (se_sketch 'axis', set before 'close'): operation add (protrusion) or cut (cutout).
        angle in degrees (default 360); direction: normal | reverse | symmetric.
        {{VerifiedNote}}
        """)]
    public Task<CallToolResult> Revolve(
        [Description("Closed sketch handle.")] string sketch,
        [Description("Axis handle returned by se_sketch 'axis'.")] string axis,
        FeatureOperation operation = FeatureOperation.Add,
        [Description("Revolution angle in degrees (0 < angle <= 360).")] double angle = 360,
        FeatureDirection direction = FeatureDirection.Normal,
        [Description("Document handle; default: the sketch's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_revolve", Snake(operation), c => FeatureService.Revolve(c, document, sketch, axis, Snake(operation),
            angle, Snake(direction)), cancellationToken);

    [McpServerTool(Name = "se_round", Title = "Round edges", Destructive = false, OpenWorld = false)]
    [Description($"Rounds (fillets) edges with a constant radius in mm, or a variable one (radii: at the start and end of each edge, changing linearly along it). Get edge handles, with their start and end points, from se_geometry 'edges'. To resize rounds later use se_part_feature resize_rounds. {VerifiedNote}")]
    public Task<CallToolResult> Round(
        [Description("Edge handles, e.g. [\"edge:12\",\"edge:13\"].")] string[] edges,
        [Description("Constant radius in mm.")] double? radius = null,
        [Description("Variable radius instead of 'radius': two values in mm per edge, at its start and its end, e.g. [2, 8].")] double[]? radii = null,
        [Description("Document handle; default: the edges' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_round", radii is null ? "add" : "variable", c => FeatureService.Round(c, document, edges, radius, radii), cancellationToken);

    [McpServerTool(Name = "se_chamfer", Title = "Chamfer edges", Destructive = false, OpenWorld = false)]
    [Description($"Chamfers edges with an equal setback in mm. Get edge handles with se_geometry 'edges'. {VerifiedNote}")]
    public Task<CallToolResult> Chamfer(
        [Description("Edge handles, e.g. [\"edge:12\"].")] string[] edges,
        [Description("Setback distance in mm.")] double distance,
        [Description("Document handle; default: the edges' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_chamfer", "add", c => FeatureService.Chamfer(c, document, edges, distance), cancellationToken);

    [McpServerTool(Name = "se_feature_edit", Title = "Edit feature", Destructive = true, OpenWorld = false)]
    [Description("""
        Edits the feature tree (handles from se_feature_tree); returns the volume in mm3. To change dimensions use se_variables.
        suppress | unsuppress | delete | rename (name) a feature or a group.
        group: features = features next to each other in the tree (one or more), optional name; suppressing the group suppresses them | ungroup: feature = the group.
        reorder: feature moves before or after target (position); if a feature fails afterwards (it went before what it is built on), it is moved back.
        roll_to: rolls the model back to just after feature (PathFinder Go To): later features are rolled back and new features go after it | roll_to_end: brings them back.
        Example: {"action":"group","features":["feature:3","feature:4"],"name":"Bolt holes"}; {"action":"reorder","feature":"feature:5","target":"feature:3"}.
        """)]
    public Task<CallToolResult> FeatureEdit(
        FeatureEditAction action,
        [Description("Feature handle; for ungroup, the group (not used by group and roll_to_end).")] string? feature = null,
        [Description("rename: new name; group: optional name of the group.")] string? name = null,
        [Description("group: feature handles, next to each other in the tree.")] string[]? features = null,
        [Description("reorder: the feature to move it next to.")] string? target = null,
        [Description("reorder: before (default) or after the target.")] ReorderPosition position = ReorderPosition.Before,
        [Description("Document handle; default: the feature's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_feature_edit", Snake(action), c => FeatureService.Edit(c, document, feature, Snake(action), name,
            features, target, position == ReorderPosition.Before), cancellationToken);

    [McpServerTool(Name = "se_feature_tree", Title = "Feature tree", ReadOnly = true, OpenWorld = false)]
    [Description("Lists the features of a part or sheet metal document in order (handle, name, type, status ok/failed/warning/suppressed), with the modeling mode and the body volume in mm3.")]
    public Task<CallToolResult> FeatureTree(
        [Description("Document handle; default: active document.")] string? document = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_feature_tree", "list", c => FeatureService.Tree(c, document, offset, limit), cancellationToken);

    internal static string Snake(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
