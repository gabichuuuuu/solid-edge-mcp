using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum SketchAction
{
    Create,
    Line,
    Rectangle,
    Polyline,
    Circle,
    Arc,
    Ellipse,
    Spline,
    Point,
    Fillet,
    Project,
    Axis,
    Close,
    Info,
    Delete,
}

public enum SketchConstraintAction
{
    Relation,
    Dimension,
    List,
    Delete,
}

public enum SketchRelation
{
    Horizontal,
    Vertical,
    Fix,
    Parallel,
    Perpendicular,
    Equal,
    Tangent,
    Concentric,
    Colinear,
    Connect,
    Symmetric,
}

public enum SketchDimension
{
    Length,
    Radius,
    Diameter,
    Distance,
    Angle,
}

public enum RefPlaneAction
{
    List,
    Offset,
    Angle,
}

/// <summary>Sketches and reference planes (ordered modelling). Coordinates in mm.</summary>
[McpServerToolType]
public sealed class SketchTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_sketch", Title = "Sketch (profile)", Destructive = true, OpenWorld = false)]
    [Description("""
        2D sketches (profiles) for part features, in mm, in the sketch's own x/y coordinates.
        create (plane: '1' = XY/top (normal +Z), '2' = YZ/right (+X), '3' = XZ/front (-Y), or a refplane / planar face handle) returns the sketch handle and its frame (origin, xAxis, yAxis, normal in model coordinates). Sketch x/y are measured from the frame origin along xAxis/yAxis: on a face the origin is not the model origin (model point P -> x = (P - origin)·xAxis, y = (P - origin)·yAxis) |
        line (points [x1,y1,x2,y2]) | rectangle (points = opposite corners [x1,y1,x2,y2]) | polyline (points [x1,y1,x2,y2,...], closed, default true) | circle (center [x,y], radius) | arc (center, start, end; counter-clockwise) |
        ellipse (center, radius = semi-major axis, minorRadius, angle of the major axis in degrees) | spline (cubic through points [x1,y1,...] >= 3; closed, default false) | point (points [x,y]) |
        fillet (elements = two elements meeting at a corner, radius; trims both; points [x,y] inside the corner, default for two lines: their common corner) | project (elements = model edge handles from se_geometry, included and linked into the sketch) |
        axis (element = line handle; revolution axis for se_revolve, set it before close; a side of the profile can be used: a copy becomes the axis) | close (validates closed loops, or one open chain with open=true for contour flanges, bend/jog lines and paths; required before a feature) | info (frame, counts and every element with its handle and geometry) | delete.
        Example: create {"plane":"1"} → rectangle {"sketch":"sketch:5","points":[0,0,100,50]} → close → se_extrude.
        """)]
    public Task<CallToolResult> Sketch(
        SketchAction action,
        [Description("Sketch handle (all actions except create).")] string? sketch = null,
        [Description("For create: '1' XY, '2' YZ, '3' XZ, or a refplane / planar face handle.")] string? plane = null,
        [Description("For line, rectangle, polyline, spline, point: flat list of coordinates in mm [x1,y1,x2,y2,...]; for fillet: optional point inside the corner.")] double[]? points = null,
        [Description("For circle, arc, ellipse: centre [x,y] in mm.")] double[]? center = null,
        [Description("For circle and fillet: radius in mm; for ellipse: semi-major axis in mm.")] double? radius = null,
        [Description("For arc: start point [x,y] in mm.")] double[]? start = null,
        [Description("For arc: end point [x,y] in mm.")] double[]? end = null,
        [Description("For ellipse: semi-minor axis in mm (radius is the semi-major axis).")] double? minorRadius = null,
        [Description("For ellipse: angle of the major axis from the sketch x axis, in degrees (default 0).")] double? angle = null,
        [Description("For axis: handle of a line of the sketch.")] string? element = null,
        [Description("For fillet: the two sketch elements; for project: model edge handles.")] string[]? elements = null,
        [Description("For polyline (default true) and spline (default false): close the shape.")] bool? closed = null,
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
            SketchAction.Polyline => runner.RunAsync(tool, name, c => SketchService.Polyline(c, handle, points, closed ?? true), cancellationToken),
            SketchAction.Circle => runner.RunAsync(tool, name, c => SketchService.Circle(c, handle, center, radius), cancellationToken),
            SketchAction.Arc => runner.RunAsync(tool, name, c => SketchService.Arc(c, handle, center, start, end), cancellationToken),
            SketchAction.Ellipse => runner.RunAsync(tool, name, c => SketchService.Ellipse(c, handle, center, radius, minorRadius, angle), cancellationToken),
            SketchAction.Spline => runner.RunAsync(tool, name, c => SketchService.Spline(c, handle, points, closed ?? false), cancellationToken),
            SketchAction.Point => runner.RunAsync(tool, name, c => SketchService.Point(c, handle, points), cancellationToken),
            SketchAction.Fillet => runner.RunAsync(tool, name, c => SketchService.Fillet(c, handle, elements, radius, points), cancellationToken),
            SketchAction.Project => runner.RunAsync(tool, name, c => SketchService.Project(c, handle, elements), cancellationToken),
            SketchAction.Axis => runner.RunAsync(tool, name, c => SketchService.Axis(c, handle, ToolRunner.Require(element, "element", "axis")), cancellationToken),
            SketchAction.Close => runner.RunAsync(tool, name, c => SketchService.Close(c, handle, open), cancellationToken),
            SketchAction.Info => runner.RunAsync(tool, name, c => SketchService.Info(c, handle), cancellationToken),
            SketchAction.Delete => runner.RunAsync(tool, name, c => SketchService.Delete(c, handle), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_sketch_constraint", Title = "Sketch relations and dimensions", Destructive = true, OpenWorld = false)]
    [Description("""
        Geometric relations and driving dimensions of a sketch (before 'close'); Solid Edge moves the geometry to satisfy them. elements: handles returned by se_sketch.
        relation (relation: horizontal | vertical | fix (one element) | parallel | perpendicular | equal | tangent | concentric | colinear (two) | connect (two + keypoints [i1,i2]) | symmetric (symmetry line + two)) |
        dimension (dimension: length (line) | radius | diameter (circle or arc) | distance (true distance between two elements: parallel lines, point/centre to line, or points; circles/arcs by their centre, or keypoints [i1,i2]) | angle (two lines: the angle inside the corner they form, 0-180; the variable stores Solid Edge's counter-clockwise angle from the first line's direction to the second's, returned as variableValue); value in mm or degrees sets the size, omitted locks the current one; returns the variable name to change it later with se_variables) |
        list (relations with their type and elements, keypoint and point; dimensions with their variables) | delete (constraint = relation or dimension handle).
        Keypoints: lines 0 start, 1 end, 2 middle; circles 0 centre; arcs 0 centre, 1 start, 2 end.
        Example: {"action":"dimension","sketch":"sketch:5","dimension":"length","elements":["line2d:7"],"value":80}.
        """)]
    public Task<CallToolResult> SketchConstraint(
        SketchConstraintAction action,
        [Description("Sketch handle (relation, dimension, list).")] string? sketch = null,
        [Description("For relation: relation type.")] SketchRelation? relation = null,
        [Description("For dimension: dimension type.")] SketchDimension? dimension = null,
        [Description("Sketch element handles.")] string[]? elements = null,
        [Description("Keypoint index per element (connect; optional for distance).")] int[]? keypoints = null,
        [Description("For dimension: size in mm (angle: degrees); omitted = keep the current size.")] double? value = null,
        [Description("For delete: relation or dimension handle.")] string? constraint = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sketch_constraint";
        string name = Snake(action);
        if (action == SketchConstraintAction.Delete)
        {
            string target = ToolRunner.Require(constraint, "constraint", "delete");
            return runner.RunAsync(tool, name, c => SketchService.DeleteConstraint(c, target), cancellationToken);
        }

        string handle = ToolRunner.Require(sketch, "sketch", name);
        return action switch
        {
            SketchConstraintAction.Relation => runner.RunAsync(tool, name, c => SketchService.Relate(c, handle,
                Snake(relation ?? throw ToolException.InvalidArgument("Action 'relation' requires 'relation' (the relation type).")), elements, keypoints), cancellationToken),
            SketchConstraintAction.Dimension => runner.RunAsync(tool, name, c => SketchService.Dimension(c, handle,
                Snake(dimension ?? throw ToolException.InvalidArgument("Action 'dimension' requires 'dimension' (the dimension type).")), elements, keypoints, value), cancellationToken),
            SketchConstraintAction.List => runner.RunAsync(tool, name, c => SketchService.Constraints(c, handle), cancellationToken),
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
