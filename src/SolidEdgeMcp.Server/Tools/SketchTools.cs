// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

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
    Polygon,
    Circle,
    Arc,
    Ellipse,
    Spline,
    Conic,
    Point,
    Fillet,
    Chamfer,
    Project,
    Axis,
    Close,
    Info,
    Delete,
    TearOff,
    Blocks,
    CreateBlock,
    PlaceBlock,
    DeleteBlock,
}

public enum SketchConstraintAction
{
    Relation,
    Dimension,
    List,
    Delete,
    Autoscale,
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
    ThreePoints,
    Mid,
    NormalToEdge,
    Tangent,
    CoordinateSystem,
    CoordinateSystems,
    Show,
    Hide,
    Rename,
    Delete,
}

public enum EdgeEnd
{
    Start,
    End,
}

/// <summary>Sketches and reference planes (ordered modelling). Coordinates in mm.</summary>
[McpServerToolType]
public sealed class SketchTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_sketch", Title = "Sketch (profile)", Destructive = true, OpenWorld = false)]
    [Description("""
        2D sketches (profiles) for features, in mm, in its own x/y. On drawings pass a sheet (se_drawing sheets) or drawing handle as 'sketch' (x/y from its lower-left corner); create, axis, close, project, delete and blocks are model-only.
        create (plane: 'xy', 'yz', 'xz' = the base plane with that orientation in any template; '1'-'3' = the template's base planes; or a refplane / planar face handle; in an assembly, an assembly plane for se_assembly_feature; layout = true: an assembly layout instead) returns the handle and frame (origin, xAxis, yAxis, normal): on a face the origin is not the model's, so model point P -> x = (P - origin)·xAxis, y = (P - origin)·yAxis |
        line (points [x1,y1,x2,y2]) | rectangle (points = opposite corners) | polyline (points [x1,y1,x2,y2,...]; closed, default true) | polygon (center, vertex radius, sides, first vertex angle) | circle (center, radius) | arc (center, start, end; counter-clockwise) | ellipse (center, radius, minorRadius, angle) | spline (through points, >= 3; closed, default false) | conic (points [start, end, control: where the end tangents meet], rho 0-1: 0.5 parabola) | point (points [x,y]) |
        fillet (elements = two meeting at a corner, radius) | chamfer (elements = two lines at a corner, setbacks); both trim | project (elements = se_geometry edges, linked) |
        axis (element = line: se_revolve axis, set before close; a profile side becomes a copy) | close (validates closed loops, or one open chain with open=true; then fully defines it in place (relations, sizes, angles, position from 'elements', model edges, base planes or enclosing contour); required before a feature) | info (frame, fullyDefined, counts, elements: handle, geometry) | delete | tear_off (closed part sketch: a linked copy with the same x/y on 'plane').
        Blocks: blocks | create_block (name, elements, center = origin; an occurrence replaces them) | place_block (name, center, scale, angle) | delete_block (name).
        Example: create {"plane":"1"} → rectangle {"sketch":"sketch:5","points":[0,0,100,50]} → close → se_extrude
        """)]
    public Task<CallToolResult> Sketch(
        SketchAction action,
        [Description("Sketch handle (all actions except create), or a drawing sheet / drawing document handle for the drawing actions and info.")] string? sketch = null,
        [Description("For create and tear_off: '1' XY, '2' YZ, '3' XZ, or a refplane / planar face handle.")] string? plane = null,
        [Description("For line, rectangle, polyline, spline, conic, point: flat list of coordinates in mm [x1,y1,x2,y2,...]; for fillet and chamfer: optional point [x,y] inside the corner (default for two lines: their common corner).")] double[]? points = null,
        [Description("For circle, arc, ellipse, polygon: centre [x,y] in mm.")] double[]? center = null,
        [Description("For circle and fillet: radius in mm; for ellipse: semi-major axis in mm; for polygon: centre to each vertex in mm.")] double? radius = null,
        [Description("For polygon: number of sides (3-100).")] int? sides = null,
        [Description("For arc: start point [x,y] in mm.")] double[]? start = null,
        [Description("For arc: end point [x,y] in mm.")] double[]? end = null,
        [Description("For ellipse: semi-minor axis in mm (radius is the semi-major axis).")] double? minorRadius = null,
        [Description("For ellipse: angle of the major axis from the sketch x axis; for polygon: angle of the first vertex; in degrees (default 0).")] double? angle = null,
        [Description("For conic: shape between 0 and 1 (0.5 parabola, less an ellipse, more a hyperbola; default 0.5).")] double? rho = null,
        [Description("For axis: handle of a line of the sketch.")] string? element = null,
        [Description("For fillet and chamfer: the two sketch elements; for project: model edge handles; for close: lines of the sketch to place it from first (model edges brought in with project, construction lines).")] string[]? elements = null,
        [Description("For chamfer: one setback in mm from the corner (equal) or two (along the first and the second line).")] double[]? setbacks = null,
        [Description("For polyline (default true) and spline (default false): close the shape.")] bool? closed = null,
        [Description("For close: validate one open chain instead of closed loops.")] bool open = false,
        [Description("For create in an assembly: make a 2D layout (a layout drawing of the assembly) instead of a sketch for assembly features.")] bool layout = false,
        [Description("For create_block, place_block, delete_block: block name.")] string? name = null,
        [Description("For place_block: scale factor (default 1).")] double? scale = null,
        [Description("For create: document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sketch";
        string op = action.ToString();
        if (action == SketchAction.Create)
        {
            string target = ToolRunner.Require(plane, "plane", "create");
            return runner.RunAsync(tool, op, c => SketchService.Create(c, document, target, layout), cancellationToken);
        }

        string handle = ToolRunner.Require(sketch, "sketch", Snake(action));
        return action switch
        {
            SketchAction.Line => runner.RunAsync(tool, op, c => SketchService.Line(c, handle, points), cancellationToken),
            SketchAction.Rectangle => runner.RunAsync(tool, op, c => SketchService.Rectangle(c, handle, points), cancellationToken),
            SketchAction.Polyline => runner.RunAsync(tool, op, c => SketchService.Polyline(c, handle, points, closed ?? true), cancellationToken),
            SketchAction.Polygon => runner.RunAsync(tool, op, c => SketchService.Polygon(c, handle, center, radius, sides, angle), cancellationToken),
            SketchAction.Circle => runner.RunAsync(tool, op, c => SketchService.Circle(c, handle, center, radius), cancellationToken),
            SketchAction.Arc => runner.RunAsync(tool, op, c => SketchService.Arc(c, handle, center, start, end), cancellationToken),
            SketchAction.Ellipse => runner.RunAsync(tool, op, c => SketchService.Ellipse(c, handle, center, radius, minorRadius, angle), cancellationToken),
            SketchAction.Spline => runner.RunAsync(tool, op, c => SketchService.Spline(c, handle, points, closed ?? false), cancellationToken),
            SketchAction.Conic => runner.RunAsync(tool, op, c => SketchService.Conic(c, handle, points, rho), cancellationToken),
            SketchAction.Point => runner.RunAsync(tool, op, c => SketchService.Point(c, handle, points), cancellationToken),
            SketchAction.Fillet => runner.RunAsync(tool, op, c => SketchService.Fillet(c, handle, elements, radius, points), cancellationToken),
            SketchAction.Chamfer => runner.RunAsync(tool, op, c => SketchService.Chamfer(c, handle, elements, setbacks, points), cancellationToken),
            SketchAction.Project => runner.RunAsync(tool, op, c => SketchService.Project(c, handle, elements), cancellationToken),
            SketchAction.Axis => runner.RunAsync(tool, op, c => SketchService.Axis(c, handle, ToolRunner.Require(element, "element", "axis")), cancellationToken),
            SketchAction.Close => runner.RunAsync(tool, op, c => SketchService.Close(c, handle, open, elements), cancellationToken),
            SketchAction.Info => runner.RunAsync(tool, op, c => SketchService.Info(c, handle), cancellationToken),
            SketchAction.Delete => runner.RunAsync(tool, op, c => SketchService.Delete(c, handle), cancellationToken),
            SketchAction.TearOff => runner.RunAsync(tool, op, c => SketchService.TearOff(c, handle, plane), cancellationToken),
            SketchAction.Blocks => runner.RunAsync(tool, op, c => SketchService.Blocks(c, handle), cancellationToken),
            SketchAction.CreateBlock => runner.RunAsync(tool, op, c => SketchService.CreateBlock(c, handle, name, elements, center), cancellationToken),
            SketchAction.PlaceBlock => runner.RunAsync(tool, op, c => SketchService.PlaceBlock(c, handle, name, center, scale, angle), cancellationToken),
            SketchAction.DeleteBlock => runner.RunAsync(tool, op, c => SketchService.DeleteBlock(c, handle, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_sketch_constraint", Title = "Sketch relations and dimensions", Destructive = true, OpenWorld = false)]
    [Description("""
        Geometric relations and driving dimensions of a sketch (before 'close') or of the 2D geometry of a drawing sheet; Solid Edge moves the geometry to satisfy them. elements: handles returned by se_sketch.
        relation (relation: horizontal | vertical | fix (one element) | parallel | perpendicular | equal | tangent | concentric | colinear (two) | connect (two + keypoints [i1,i2]) | symmetric (symmetry line + two)) |
        dimension (dimension: length (line) | radius | diameter (circle or arc) | distance (true distance between two elements: parallel lines, point/centre to line, or points; circles/arcs by their centre, or keypoints [i1,i2]) | angle (two lines: the angle inside the corner they form, 0-180; the variable stores Solid Edge's counter-clockwise angle from the first line's direction to the second's, returned as variableValue); value in mm or degrees sets the size, omitted locks the current one; returns the variable name to change it later or drive it by a formula with se_variables set, e.g. formula 'Dext') |
        list (relations with their type and elements, keypoint and point; dimensions with their variables) | delete (constraint = relation or dimension handle) |
        autoscale (constraint = a driving length dimension, value mm: the whole sketch is scaled so that it takes the value, as Auto-Scale Sketch: while it is the sketch's only driving dimension).
        Keypoints: lines 0 start, 1 end, 2 middle; circles 0 centre; arcs 0 centre, 1 start, 2 end.
        Example: {"action":"dimension","sketch":"sketch:5","dimension":"length","elements":["line2d:7"],"value":80}.
        """)]
    public Task<CallToolResult> SketchConstraint(
        SketchConstraintAction action,
        [Description("Sketch or drawing sheet handle (relation, dimension, list).")] string? sketch = null,
        [Description("For relation: relation type.")] SketchRelation? relation = null,
        [Description("For dimension: dimension type.")] SketchDimension? dimension = null,
        [Description("Sketch element handles.")] string[]? elements = null,
        [Description("Keypoint index per element (connect; optional for distance).")] int[]? keypoints = null,
        [Description("For dimension: size in mm (angle: degrees); omitted = keep the current size. For autoscale: the new size in mm.")] double? value = null,
        [Description("For delete: relation or dimension handle; for autoscale: a driving dimension handle.")] string? constraint = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sketch_constraint";
        string name = Snake(action);
        if (action == SketchConstraintAction.Delete)
        {
            string target = ToolRunner.Require(constraint, "constraint", "delete");
            return runner.RunAsync(tool, name, c => SketchService.DeleteConstraint(c, target), cancellationToken);
        }

        if (action == SketchConstraintAction.Autoscale)
        {
            string target = ToolRunner.Require(constraint, "constraint", "autoscale");
            return runner.RunAsync(tool, name, c => SketchService.AutoScale(c, target, value), cancellationToken);
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

    [McpServerTool(Name = "se_ref_plane", Title = "Reference planes and coordinate systems", Destructive = false, OpenWorld = false)]
    [Description("""
        Reference planes and coordinate systems of a part, sheet metal or assembly. list (index, handle, name, normal, root point in mm) |
        offset (plane = index, 'xy'/'yz'/'xz' or plane / planar face handle; distance in mm, negative or flip=true for the other side) |
        angle (plane, pivot = plane whose intersection with 'plane' is the hinge, or a straight edge, angle in degrees: 90 = Perpendicular,
        0 = Coincident by Axis) |
        three_points (points = 3 model points in mm: vertices or midpoints of straight edges) | mid (plane + plane2: midway between two parallel planes or faces) |
        normal_to_edge (edge handle; at start|end, optional distance in mm or ratio 0-1 of the edge length along it from that end) |
        tangent (plane = the parallel plane, face = cylindrical or conical face; flip = the other side; or face = any curved face + points = [x,y,z] an edge end on it:
        tangent there, plane optional (sets the x axis); or face = cylinder + plane through its axis + angle in degrees about the axis from it, positive towards
        the plane's normal; with points or angle the normal points out of the face, flip = into it) |
        coordinate_system (origin [x,y,z] mm, angles [ax,ay,az] degrees about X, then Y, then Z; or edges = [X edge, Y edge] straight edges,
        the axes along them pointing away from the origin, an end of either edge) | coordinate_systems (list them with their axes) |
        show, hide (plane = index, plane or coordinate system handle, or 'all' = every reference plane) | rename (plane, name) |
        delete (a plane or coordinate system nothing is built on; not the base ones).
        In an assembly: offset and angle from assembly planes or planar faces of occurrences, normal_to_edge on an occurrence edge
        (se_geometry with 'source' = the occurrence), coordinate_system by origin and angles; no three_points, mid or tangent.
        Planes are global, as the Planes commands make them, and checked.
        New planes return a handle usable as 'plane' in se_sketch create. Example: {"action":"normal_to_edge","edge":"edge:12","at":"start","distance":30}.
        """)]
    public Task<CallToolResult> RefPlane(
        RefPlaneAction action,
        [Description("For offset/angle/tangent: parent plane index ('1'-'3'), 'xy'/'yz'/'xz' or plane / planar face handle; show/hide/rename/delete: the plane or coordinate system.")] string? plane = null,
        [Description("For offset: distance in mm.")] double? distance = null,
        [Description("For angle: hinge plane index or handle, or a straight edge handle.")] string? pivot = null,
        [Description("For angle: angle in degrees; tangent: angle about the cylinder axis from 'plane'.")] double? angle = null,
        [Description("Create the plane on the other side of the parent's normal (tangent: the other side of the face).")] bool flip = false,
        [Description("For three_points: [x1,y1,z1, x2,y2,z2, x3,y3,z3] in mm; tangent: the point [x,y,z] on the face.")] double[]? points = null,
        [Description("For mid: the second plane (index or refplane / planar face handle).")] string? plane2 = null,
        [Description("For normal_to_edge: edge handle (se_geometry edges).")] string? edge = null,
        [Description("For normal_to_edge and angle about an edge (its x axis runs along the edge from this end): the end of the edge (default start).")] EdgeEnd at = EdgeEnd.Start,
        [Description("For normal_to_edge: fraction 0-1 of the edge length from 'at' (instead of distance), e.g. 0.5 = the middle.")] double? ratio = null,
        [Description("For tangent: cylindrical or conical face handle (with points: any curved face).")] string? face = null,
        [Description("For coordinate_system: origin [x,y,z] in mm.")] double[]? origin = null,
        [Description("For coordinate_system: rotations [ax,ay,az] in degrees.")] double[]? angles = null,
        [Description("For coordinate_system: two straight edge handles, the X axis then the Y axis (instead of angles).")] string[]? edges = null,
        [Description("For rename: the new name.")] string? name = null,
        [Description("Document handle; default: active document. list: also an occurrence handle (its part's planes in assembly coordinates, for se_relations).")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_ref_plane";
        string actionName = Snake(action);
        return action switch
        {
            RefPlaneAction.List => runner.RunAsync(tool, actionName, c => RefPlaneService.List(c, document), cancellationToken),
            RefPlaneAction.Offset => runner.RunAsync(tool, actionName, c => RefPlaneService.Offset(c, document,
                ToolRunner.Require(plane, "plane", "offset"), distance, flip), cancellationToken),
            RefPlaneAction.Angle => runner.RunAsync(tool, actionName, c => RefPlaneService.Angle(c, document,
                ToolRunner.Require(plane, "plane", "angle"), ToolRunner.Require(pivot, "pivot", "angle"), angle, flip, Snake(at)), cancellationToken),
            RefPlaneAction.ThreePoints => runner.RunAsync(tool, actionName, c => RefPlaneService.ThreePoints(c, document, points), cancellationToken),
            RefPlaneAction.Mid => runner.RunAsync(tool, actionName, c => RefPlaneService.Mid(c, document,
                ToolRunner.Require(plane, "plane", "mid"), ToolRunner.Require(plane2, "plane2", "mid")), cancellationToken),
            RefPlaneAction.NormalToEdge => runner.RunAsync(tool, actionName, c => RefPlaneService.NormalToEdge(c, document,
                ToolRunner.Require(edge, "edge", "normal_to_edge"), Snake(at), distance, ratio), cancellationToken),
            RefPlaneAction.Tangent => runner.RunAsync(tool, actionName, c => RefPlaneService.Tangent(c, document,
                plane, ToolRunner.Require(face, "face", "tangent"), flip, points, angle), cancellationToken),
            RefPlaneAction.CoordinateSystem => runner.RunAsync(tool, actionName, c => RefPlaneService.AddCoordinateSystem(c, document, origin, angles, edges), cancellationToken),
            RefPlaneAction.CoordinateSystems => runner.RunAsync(tool, actionName, c => RefPlaneService.ListCoordinateSystems(c, document), cancellationToken),
            RefPlaneAction.Show or RefPlaneAction.Hide => runner.RunAsync(tool, actionName, c => RefPlaneService.SetVisible(c, document,
                ToolRunner.Require(plane, "plane", actionName), action == RefPlaneAction.Show), cancellationToken),
            RefPlaneAction.Rename => runner.RunAsync(tool, actionName, c => RefPlaneService.Rename(c, document,
                ToolRunner.Require(plane, "plane", "rename"), name), cancellationToken),
            RefPlaneAction.Delete => runner.RunAsync(tool, actionName, c => RefPlaneService.Delete(c, document,
                ToolRunner.Require(plane, "plane", "delete")), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
