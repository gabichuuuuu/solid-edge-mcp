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
}

/// <summary>
/// Operaciones de pieza (modelado ordenado) y árbol de operaciones. Cada operación nueva se
/// verifica por estado y cambio de volumen; si falla, se borra.
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
    [Description($"Rounds (fillets) edges with a constant radius in mm. Get edge handles with se_geometry 'edges'. {VerifiedNote}")]
    public Task<CallToolResult> Round(
        [Description("Edge handles, e.g. [\"edge:12\",\"edge:13\"].")] string[] edges,
        [Description("Radius in mm.")] double radius,
        [Description("Document handle; default: the edges' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_round", "add", c => FeatureService.Round(c, document, edges, radius), cancellationToken);

    [McpServerTool(Name = "se_chamfer", Title = "Chamfer edges", Destructive = false, OpenWorld = false)]
    [Description($"Chamfers edges with an equal setback in mm. Get edge handles with se_geometry 'edges'. {VerifiedNote}")]
    public Task<CallToolResult> Chamfer(
        [Description("Edge handles, e.g. [\"edge:12\"].")] string[] edges,
        [Description("Setback distance in mm.")] double distance,
        [Description("Document handle; default: the edges' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_chamfer", "add", c => FeatureService.Chamfer(c, document, edges, distance), cancellationToken);

    [McpServerTool(Name = "se_feature_edit", Title = "Edit feature", Destructive = true, OpenWorld = false)]
    [Description("Suppresses, unsuppresses, deletes or renames a feature (handle from se_feature_tree). Returns the new volume (mm3). To change dimensions use se_variables.")]
    public Task<CallToolResult> FeatureEdit(
        [Description("Feature handle.")] string feature,
        FeatureEditAction action,
        [Description("For rename: new name.")] string? name = null,
        [Description("Document handle; default: the feature's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_feature_edit", Snake(action), c => FeatureService.Edit(c, document, feature, Snake(action), name), cancellationToken);

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
