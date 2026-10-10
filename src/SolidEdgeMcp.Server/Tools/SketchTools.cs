using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum SketchAction
{
    Create,
    Line,
    Rectangle,
    Polyline,
    Circle,
    Arc,
    Axis,
    Close,
    Info,
    Delete,
}

public enum RefPlaneAction
{
    List,
    Offset,
    Angle,
}

/// <summary>Bocetos y planos de referencia (modelado ordenado). Coordenadas en mm.</summary>
[McpServerToolType]
public sealed class SketchTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_sketch", Title = "Sketch (profile)", Destructive = true, OpenWorld = false)]
    [Description("""
        2D sketches (profiles) for part features, in mm, in the sketch's own x/y coordinates.
        create (plane: '1' = XY/top (normal +Z), '2' = YZ/right (+X), '3' = XZ/front (-Y), or a refplane / planar face handle) returns the sketch handle and its frame (origin, xAxis, yAxis, normal in model coordinates) |
        line (points [x1,y1,x2,y2]) | rectangle (points = opposite corners [x1,y1,x2,y2]) | polyline (points [x1,y1,x2,y2,...], closed) | circle (center [x,y], radius) | arc (center, start, end; counter-clockwise) |
        axis (element = line handle; revolution axis for se_revolve, set it before close) | close (validates closed loops, or one open chain with open=true for contour flanges, bend/jog lines and paths; required before a feature) | info | delete.
        Example: create {"plane":"1"} → rectangle {"sketch":"sketch:5","points":[0,0,100,50]} → close → se_extrude.
        """)]
    public Task<CallToolResult> Sketch(
        SketchAction action,
        [Description("Sketch handle (all actions except create).")] string? sketch = null,
        [Description("For create: '1' XY, '2' YZ, '3' XZ, or a refplane / planar face handle.")] string? plane = null,
        [Description("For line, rectangle, polyline: flat list of coordinates in mm [x1,y1,x2,y2,...].")] double[]? points = null,
        [Description("For circle, arc: centre [x,y] in mm.")] double[]? center = null,
        [Description("For circle: radius in mm.")] double? radius = null,
        [Description("For arc: start point [x,y] in mm.")] double[]? start = null,
        [Description("For arc: end point [x,y] in mm.")] double[]? end = null,
        [Description("For axis: handle of a line of the sketch.")] string? element = null,
        [Description("For polyline: join the last point to the first (default true).")] bool closed = true,
        [Description("For close: validate one open chain instead of closed loops.")] bool open = false,
        [Description("For create: document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sketch";
        string name = action.ToString();
        if (action == SketchAction.Create)
        {
            string target = ToolRunner.Require(plane, "plane", "create");
            return runner.RunAsync(tool, name, c => SketchService.Create(c, document, target), cancellationToken);
        }

        string handle = ToolRunner.Require(sketch, "sketch", name.ToLowerInvariant());
        return action switch
        {
            SketchAction.Line => runner.RunAsync(tool, name, c => SketchService.Line(c, handle, points), cancellationToken),
            SketchAction.Rectangle => runner.RunAsync(tool, name, c => SketchService.Rectangle(c, handle, points), cancellationToken),
            SketchAction.Polyline => runner.RunAsync(tool, name, c => SketchService.Polyline(c, handle, points, closed), cancellationToken),
            SketchAction.Circle => runner.RunAsync(tool, name, c => SketchService.Circle(c, handle, center, radius), cancellationToken),
            SketchAction.Arc => runner.RunAsync(tool, name, c => SketchService.Arc(c, handle, center, start, end), cancellationToken),
            SketchAction.Axis => runner.RunAsync(tool, name, c => SketchService.Axis(c, handle, ToolRunner.Require(element, "element", "axis")), cancellationToken),
            SketchAction.Close => runner.RunAsync(tool, name, c => SketchService.Close(c, handle, open), cancellationToken),
            SketchAction.Info => runner.RunAsync(tool, name, c => SketchService.Info(c, handle), cancellationToken),
            SketchAction.Delete => runner.RunAsync(tool, name, c => SketchService.Delete(c, handle), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_ref_plane", Title = "Reference planes", Destructive = false, OpenWorld = false)]
    [Description("""
        Reference planes of a part or sheet metal document. list (index, handle, name, normal, root point in mm) |
        offset (plane = index or refplane / planar face handle; distance in mm, negative or flip=true for the other side) |
        angle (plane, pivot = plane whose intersection with 'plane' is the hinge, angle in degrees).
        New planes return a handle usable as 'plane' in se_sketch create.
        """)]
    public Task<CallToolResult> RefPlane(
        RefPlaneAction action,
        [Description("For offset/angle: parent plane index ('1'-'3') or refplane / planar face handle.")] string? plane = null,
        [Description("For offset: distance in mm.")] double? distance = null,
        [Description("For angle: hinge plane index or handle.")] string? pivot = null,
        [Description("For angle: angle in degrees.")] double? angle = null,
        [Description("Create the plane on the other side of the parent's normal.")] bool flip = false,
        [Description("Document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_ref_plane";
        string name = action.ToString();
        return action switch
        {
            RefPlaneAction.List => runner.RunAsync(tool, name, c => RefPlaneService.List(c, document), cancellationToken),
            RefPlaneAction.Offset => runner.RunAsync(tool, name, c => RefPlaneService.Offset(c, document,
                ToolRunner.Require(plane, "plane", "offset"), distance, flip), cancellationToken),
            RefPlaneAction.Angle => runner.RunAsync(tool, name, c => RefPlaneService.Angle(c, document,
                ToolRunner.Require(plane, "plane", "angle"), ToolRunner.Require(pivot, "pivot", "angle"), angle, flip), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
