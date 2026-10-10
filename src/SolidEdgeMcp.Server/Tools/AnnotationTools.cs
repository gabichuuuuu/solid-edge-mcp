using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum AnnotationAction
{
    List,
    Text,
    Balloon,
    Callout,
    CenterMark,
    Centerline,
    Leader,
    Delete,
}

public enum AnnotationKind
{
    Text,
    Balloon,
    CenterMark,
    Centerline,
    Leader,
}

[McpServerToolType]
public sealed class AnnotationTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_annotation", Title = "Drawing annotations", Destructive = true, OpenWorld = false)]
    [Description("""
        Annotations of a drawing sheet (se_drawing makes the views). Sheet positions in mm from the lower-left corner; model points in mm pick what a view shows, as for se_drawing dimensions.
        list (sheet; kind filter) | text (text + position on the sheet) | balloon (view + points = one model point on an edge, vertex or circle + text; position = balloon centre, default 15 mm up-right) |
        callout (like balloon, the text in a callout box) | center_mark (view + points = centre of, or a point on, a circle/arc) |
        centerline (view + points = two model points: on two circles (centre to centre) or on two straight edges (their mid line)) | leader (view + points + position = the free end) | delete (annotation handle).
        Annotations picked from a view stay attached to it. Each one is checked (text kept, leader attached, drawn where expected) and removed if the check fails.
        Example: {"action":"center_mark","view":"drawing_view:2","points":[50,30,20]}.
        """)]
    public Task<CallToolResult> Annotation(
        AnnotationAction action,
        [Description("Drawing view handle (balloon, callout, center_mark, centerline, leader).")] string? view = null,
        [Description("Model points [x1, y1, z1, ...] in mm that pick the edges, vertices or circles in the view.")] double[]? points = null,
        [Description("Sheet point [x, y] in mm: the text box corner, the balloon centre or the leader end.")] double[]? position = null,
        [Description("text, balloon, callout: the text (a balloon's item number or label).")] string? text = null,
        [Description("list: only this kind.")] AnnotationKind? kind = null,
        [Description("delete: annotation handle.")] string? annotation = null,
        [Description("Sheet handle or name (default: the active sheet).")] string? sheet = null,
        int offset = 0,
        int limit = 100,
        [Description("Draft document handle; default: the document of the sheet/view handle or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_annotation";
        string act = Snake(action);
        return action switch
        {
            AnnotationAction.List => runner.RunAsync(tool, act, c => DrawingService.Annotations(c, document, sheet, kind is { } k ? Snake(k) : null, offset, limit), cancellationToken),
            AnnotationAction.Text => runner.RunAsync(tool, act, c => DrawingService.AddText(c, document, sheet, text, position), cancellationToken),
            AnnotationAction.Balloon => runner.RunAsync(tool, act, c => DrawingService.AddBalloon(c, view, points, position, text, callout: false), cancellationToken),
            AnnotationAction.Callout => runner.RunAsync(tool, act, c => DrawingService.AddBalloon(c, view, points, position, text, callout: true), cancellationToken),
            AnnotationAction.CenterMark => runner.RunAsync(tool, act, c => DrawingService.AddCenterMark(c, view, points), cancellationToken),
            AnnotationAction.Centerline => runner.RunAsync(tool, act, c => DrawingService.AddCenterLine(c, view, points), cancellationToken),
            AnnotationAction.Leader => runner.RunAsync(tool, act, c => DrawingService.AddLeader(c, view, points, position), cancellationToken),
            AnnotationAction.Delete => runner.RunAsync(tool, act, c => DrawingService.DeleteAnnotation(c, annotation), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
