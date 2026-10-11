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
    SurfaceFinish,
    Datum,
    FeatureControlFrame,
    Weld,
    Delete,
}

public enum AnnotationKind
{
    Text,
    Balloon,
    CenterMark,
    Centerline,
    Leader,
    SurfaceFinish,
    Datum,
    FeatureControlFrame,
    Weld,
}

public enum SurfaceFinishType
{
    Basic,
    Machined,
    NoMaterialRemoval,
}

public enum GeometricCharacteristic
{
    Straightness,
    Flatness,
    Circularity,
    Cylindricity,
    LineProfile,
    SurfaceProfile,
    Angularity,
    Perpendicularity,
    Parallelism,
    Position,
    Concentricity,
    Symmetry,
    CircularRunout,
    TotalRunout,
}

public enum WeldType
{
    Fillet,
    Square,
    VGroove,
    Bevel,
    UGroove,
    JGroove,
    Spot,
    Seam,
    Plug,
    Backing,
    Surfacing,
    FlareV,
    FlareBevel,
    Edge,
}

public enum WeldSide
{
    Arrow,
    Other,
    Both,
}

[McpServerToolType]
public sealed class AnnotationTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_annotation", Title = "Drawing annotations", Destructive = true, OpenWorld = false)]
    [Description("""
        Annotations of a drawing sheet (se_drawing makes the views). Sheet positions in mm from the lower-left corner; model points in mm pick what a view shows, as for se_drawing dimensions.
        list (sheet; kind filter) | text (text + position on the sheet) | balloon (view + points = one model point on an edge, vertex or circle + text; position = balloon centre, default 15 mm up-right) |
        callout (like balloon, the text in a callout box) | center_mark (view + points = centre of, or a point on, a circle/arc) |
        centerline (view + points = two model points: on two circles (centre to centre) or on two straight edges (their mid line)) | leader (view + points + position = the free end) |
        surface_finish (view + points = a point on the edge; finish, text = roughness e.g. 'Ra 3.2'; position = symbol on a leader, default on the edge) |
        datum (view + points + text = the letter; position = frame on a leader, default on the edge) |
        feature_control_frame (characteristic + tolerance (⌀ or Ø for a diameter zone) + datums, or text = the whole frame in Solid Edge codes;
        rows = up to 3 more rows 'characteristic|tolerance|datum…', or with composite 'tolerance|datum…' sharing the first characteristic;
        view + points = on a leader from that edge, position = the frame (default 15 mm up-right); or only position = a free frame) |
        weld (view + points = a point on the welded edge; weld type, weldSide (arrow / other / both), text = size e.g. 'a5', allAround, field; position = start of the reference line, default 15 mm up-right) | delete (annotation handle).
        Annotations picked from a view stay attached to it. Each one is checked (text kept, leader attached, drawn where expected) and removed if the check fails.
        Example: {"action":"feature_control_frame","view":"drawing_view:2","points":[100,30,20],"characteristic":"perpendicularity","tolerance":"0.02","datums":["A"]}.
        """)]
    public Task<CallToolResult> Annotation(
        AnnotationAction action,
        [Description("Drawing view handle (balloon, callout, center_mark, centerline, leader).")] string? view = null,
        [Description("Model points [x1, y1, z1, ...] in mm that pick the edges, vertices or circles in the view.")] double[]? points = null,
        [Description("Sheet point [x, y] in mm: the text box corner, the balloon centre or the leader end.")] double[]? position = null,
        [Description("text, balloon, callout: the text (a balloon's item number or label); surface_finish: roughness; datum: the letter; weld: the size or note; feature_control_frame: the whole frame in Solid Edge codes (cells split by %VB, e.g. '%PO%VB%DI0.1%MC%VBA').")] string? text = null,
        [Description("surface_finish: symbol type (default machined: material removal required).")] SurfaceFinishType finish = SurfaceFinishType.Machined,
        [Description("feature_control_frame: geometric characteristic.")] GeometricCharacteristic? characteristic = null,
        [Description("feature_control_frame: tolerance value, e.g. '0.05' or '⌀0.1'; %MC / %LC add the maximum / least material symbol.")] string? tolerance = null,
        [Description("feature_control_frame: datum labels in order, e.g. [\"A\",\"B\"].")] string[]? datums = null,
        [Description("feature_control_frame: up to 3 rows below the first, cells split by '|': 'perpendicularity|0.02|A', or with composite '⌀0.05|A'.")] string[]? rows = null,
        [Description("feature_control_frame: composite frame, the rows share the characteristic cell of the first row.")] bool composite = false,
        [Description("weld: weld type (default fillet).")] WeldType weld = WeldType.Fillet,
        [Description("weld: side of the reference line: arrow (below), other (above) or both.")] WeldSide weldSide = WeldSide.Arrow,
        [Description("weld: weld all around (circle at the bend of the leader).")] bool allAround = false,
        [Description("weld: field weld (flag).")] bool field = false,
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
            AnnotationAction.SurfaceFinish => runner.RunAsync(tool, act, c => DrawingService.AddSurfaceFinish(c, view, points, position, text, Snake(finish)), cancellationToken),
            AnnotationAction.Datum => runner.RunAsync(tool, act, c => DrawingService.AddDatum(c, view, points, position, text), cancellationToken),
            AnnotationAction.FeatureControlFrame => runner.RunAsync(tool, act, c => DrawingService.AddFeatureControlFrame(c, document, sheet, view, points, position,
                characteristic is { } g ? Snake(g) : null, tolerance, datums, text, rows, composite), cancellationToken),
            AnnotationAction.Weld => runner.RunAsync(tool, act, c => DrawingService.AddWeld(c, view, points, position, Snake(weld), Snake(weldSide), text, allAround, field), cancellationToken),
            AnnotationAction.Delete => runner.RunAsync(tool, act, c => DrawingService.DeleteAnnotation(c, annotation), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
