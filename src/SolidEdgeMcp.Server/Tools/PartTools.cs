// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum HoleType
{
    Simple,
    Counterbore,
    Countersink,
    Tapped,
}

public enum HoleDirection
{
    Auto,
    Normal,
    Reverse,
}

public enum PatternType
{
    Rectangular,
    Circular,
    Mirror,
    Curve,
    Fill,
}

public enum ShellSide
{
    Inside,
    Outside,
}

public enum DraftDirection
{
    Normal,
    Reverse,
}

public enum SweepAction
{
    Loft,
    Sweep,
    Helix,
    Tube,
}

public enum HelixHand
{
    Right,
    Left,
}

public enum PartFeatureAction
{
    Rib,
    Thicken,
    MirrorBody,
    DeleteFaces,
    DeleteHoles,
    DeleteRounds,
    DeleteRegion,
    OffsetFaces,
    ReplaceFaces,
    RedefineFaces,
    ResizeHoles,
    ResizeRounds,
    NormalProtrusion,
    NormalCutout,
    Split,
    Unite,
    Slot,
    WebNetwork,
    Lip,
    ScaleBody,
    CopyPart,
    FilletWeld,
}

public enum PartFeatureSide
{
    Auto,
    Normal,
    Reverse,
    Symmetric,
}

public enum BodyAction
{
    List,
    Subtract,
    Intersect,
    Unite,
    Emboss,
    ToConstruction,
    ToDesign,
    Divide,
}

public enum BodySide
{
    Auto,
    Normal,
    Reverse,
}

public enum PrimitiveShape
{
    Box,
    Cylinder,
    Sphere,
}

public enum CurveAction
{
    List,
    Points,
    Intersection,
    Project,
    Cross,
    Derived,
    Contour,
    Wrap,
    Isocline,
    Split,
    Delete,
}

public enum SurfaceAction
{
    List,
    Extrude,
    Revolve,
    Offset,
    Loft,
    Sweep,
    Stitch,
    Bounded,
    Copy,
    Trim,
    Extend,
    Intersect,
    Bluesurf,
    Blend,
    Delete,
}

public enum SurfaceExtension
{
    Natural,
    Linear,
    Reflective,
}

/// <summary>Holes, patterns, shell, draft, lofts and other part features (ordered modelling), verified like the other operations.</summary>
[McpServerToolType]
public sealed class PartTools(ToolRunner runner)
{
    private const string VerifiedNote = "Verified like se_extrude: a feature that fails or has no effect is deleted and reported as an error.";

    [McpServerTool(Name = "se_hole", Title = "Hole", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Holes at points of a plane, in one call (no sketch needed). plane: '1' XY, '2' YZ, '3' XZ, or a refplane / planar face handle (e.g. the face to drill); centers [x1,y1,z1,x2,y2,z2,...] in mm, model coordinates, projected onto the plane.
        type: simple (diameter) | counterbore (counterboreDiameter, counterboreDepth) | countersink (countersinkDiameter, countersinkAngle, default 90) | tapped (thread 'M6' or 'M10x1.25'; cut at the tap drill 'diameter', default nominal - pitch, with the thread stored as hole data: cosmetic thread).
        extent: through_all (default) | through_next | finite (depth). direction: auto (on a face: into the material; on a plane: tries the normal, then the other side) | normal | reverse.
        Creates one Hole feature per centre ('feature' is the first, 'features' lists all when there are several); if any hole fails, the ones made in the same call are deleted too.
        {{VerifiedNote}}
        Example: {"plane":"face:12","centers":[10,10,20, 90,10,20],"diameter":6.6}.
        """)]
    public Task<CallToolResult> Hole(
        [Description("Plane: '1'-'3', refplane handle or planar face handle.")] string plane,
        [Description("Hole centres [x1,y1,z1,x2,y2,z2,...] in mm, model coordinates.")] double[] centers,
        [Description("Hole diameter in mm (tapped: tap drill diameter, optional).")] double? diameter = null,
        HoleType type = HoleType.Simple,
        [Description("Counterbore diameter in mm.")] double? counterboreDiameter = null,
        [Description("Counterbore depth in mm.")] double? counterboreDepth = null,
        [Description("Countersink diameter in mm.")] double? countersinkDiameter = null,
        [Description("Countersink included angle in degrees (default 90).")] double countersinkAngle = 90,
        [Description("Tapped holes: metric thread such as 'M6' or 'M10x1.25'.")] string? thread = null,
        SimpleExtent extent = SimpleExtent.ThroughAll,
        [Description("For extent 'finite': depth in mm.")] double? depth = null,
        HoleDirection direction = HoleDirection.Auto,
        [Description("Document handle; default: the plane's document or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_hole", Snake(type), c => FeatureService.Hole(c, document, new HoleSpec(plane, centers, diameter, Snake(type),
            counterboreDiameter, counterboreDepth, countersinkDiameter, countersinkAngle, thread, Snake(extent), depth, Snake(direction))),
            cancellationToken);

    [McpServerTool(Name = "se_pattern", Title = "Pattern / mirror", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Copies features. rectangular: on 'plane' along its x/y axes, countX/countY occurrences (including the original) every spacingX/spacingY mm |
        circular: 'count' occurrences around the normal of 'plane' through 'center' [x,y,z] (mm, model coordinates, default origin), spread over 'angle' degrees (360 = full circle) |
        mirror: symmetric copy about 'plane' |
        curve: 'count' occurrences every 'spacing' mm along an open edge ('curve', from se_geometry edges or a se_curve), from the original towards the edge end ('reverse': towards its start; se_geometry shows each edge's start and end).
        fill: copies on a grid from the original, spacingX by spacingY mm (default spacingX), wherever a node falls inside 'region' (a closed sketch on the features' plane); stagger = mm every other row shifts; angle = turn of the grid in degrees.
        features: handles from se_feature_tree or from the tool that created them; or construction geometry (se_surface, se_curve, construction bodies from se_body list, or their faces), copied as new construction bodies with mirror or curve.
        part: true (curve only, no features): copies the whole part, every design body, along the curve.
        {{VerifiedNote}}
        """)]
    public Task<CallToolResult> Pattern(
        PatternType type,
        [Description("Feature handles to copy, or construction geometry (surfaces, curves, construction bodies, faces of them: mirror and curve only). Not used with part.")] string[]? features = null,
        [Description("Layout plane (rectangular/circular, normally the features' sketch plane) or mirror plane: '1'-'3', refplane or planar face handle. Not used by curve.")] string? plane = null,
        [Description("Rectangular: occurrences along x (default 1).")] int countX = 1,
        [Description("Rectangular: occurrences along y (default 1).")] int countY = 1,
        [Description("Rectangular, fill: spacing along x in mm.")] double? spacingX = null,
        [Description("Rectangular, fill: spacing along y in mm.")] double? spacingY = null,
        [Description("Circular, curve: number of occurrences including the original.")] int count = 0,
        [Description("Circular: total angle in degrees (default 360); fill: turn of the grid in degrees (360 = none).")] double angle = 360,
        [Description("Circular: axis point [x,y,z] in mm, model coordinates (default origin).")] double[]? center = null,
        [Description("Curve: handle of the open edge to follow.")] string? curve = null,
        [Description("Curve: distance between occurrences in mm, measured along the edge.")] double? spacing = null,
        [Description("Curve: copies towards the start of the edge instead of its end.")] bool reverse = false,
        [Description("Curve: copy the whole part (every design body) instead of features.")] bool part = false,
        [Description("Fill: closed sketch handle whose outline the copies fill.")] string? region = null,
        [Description("Fill: shift in mm of every other row (staggered grid).")] double? stagger = null,
        [Description("Document handle; default: the features' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_pattern", Snake(type), c => FeatureService.Pattern(c, document,
            new PatternSpec(features, Snake(type), plane, countX, countY, spacingX, spacingY, count, angle, center, curve, spacing, reverse, part, region, stagger)), cancellationToken);

    [McpServerTool(Name = "se_shell", Title = "Shell (thin wall)", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Hollows the part with a common wall thickness (mm). openFaces: faces removed to open the shell (se_geometry faces); none = closed hollow body.
        side: inside (default, keeps the outer size) | outside. {{VerifiedNote}}
        """)]
    public Task<CallToolResult> Shell(
        [Description("Wall thickness in mm.")] double thickness,
        [Description("Face handles to remove (open faces).")] string[]? openFaces = null,
        ShellSide side = ShellSide.Inside,
        [Description("Document handle; default: the faces' document or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_shell", Snake(side), c => FeatureService.Shell(c, document, thickness, openFaces, Snake(side)), cancellationToken);

    [McpServerTool(Name = "se_draft", Title = "Draft", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Tilts faces by an angle (degrees) about their intersection with a neutral plane ('1'-'3', refplane or planar face handle), e.g. for moulding.
        direction: normal (default: the part narrows towards the plane normal, i.e. it is pulled out along the normal) | reverse (it widens towards the normal). {{VerifiedNote}}
        """)]
    public Task<CallToolResult> Draft(
        [Description("Neutral plane.")] string plane,
        [Description("Face handles to tilt.")] string[] faces,
        [Description("Draft angle in degrees (0 < angle < 90).")] double angle,
        DraftDirection direction = DraftDirection.Normal,
        [Description("Document handle; default: the faces' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_draft", "add", c => FeatureService.Draft(c, document, plane, faces, angle, Snake(direction)), cancellationToken);

    [McpServerTool(Name = "se_loft_sweep", Title = "Loft / sweep / helix", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Solid through sections drawn with se_sketch (each closed with 'close'): operation add (protrusion; also the first feature of a part) or cut (cutout).
        loft: blends 'sketches' (>= 2 sections on different planes, in order); 'origins' [x1,y1,x2,y2,...] (mm, each sketch's own x/y) picks the matching start point of each section to control twist (default: first vertex of each section; a circle starts at +x).
        sweep: moves 'sketches' (>= 1 sections) along 'path' (a sketch with a chain of lines/arcs closed with 'close', open: true for an open chain, a 3D curve from se_curve or a 3D sketch from se_sketch3d; the section usually lies on a plane across the path start).
        helix: coils one section around 'axis' (se_sketch 'axis' on a line of the same sketch, before 'close'); give two of pitch (mm), turns and height (mm); it starts at the section and rises towards the end point of the axis line; hand right (default) | left.
        wall (add): a thin wall of that thickness in mm along the sections instead of a solid (a pipe from a swept circle); wallSide inside | outside | symmetric; endCaps closes both ends.
        tube: no sketches; a circle of outerDiameter (with wall: a pipe of that wall) on a plane normal to the start of 'path' (a sketch chain of lines and arcs, a curve or a 3D sketch), swept along it; expectedVolume = section area x path length.
        {{VerifiedNote}} The sketches are kept if it fails.
        Example: {"action":"loft","sketches":["sketch:3","sketch:7"]}; {"action":"helix","sketches":["sketch:2"],"axis":"axis:4","pitch":8,"turns":5}.
        """)]
    public Task<CallToolResult> LoftSweep(
        SweepAction action,
        [Description("Closed section sketch handles, in order (not for tube).")] string[]? sketches = null,
        FeatureOperation operation = FeatureOperation.Add,
        [Description("Sweep: path sketch handle, a curve handle from se_curve or a 3D sketch handle from se_sketch3d.")] string? path = null,
        [Description("Loft/sweep: start point of each section [x1,y1,x2,y2,...] in mm, sketch coordinates.")] double[]? origins = null,
        [Description("Helix: axis handle from se_sketch 'axis'.")] string? axis = null,
        [Description("Helix: pitch in mm (rise per turn).")] double? pitch = null,
        [Description("Helix: number of turns.")] double? turns = null,
        [Description("Helix: height in mm.")] double? height = null,
        HelixHand hand = HelixHand.Right,
        [Description("tube: outer diameter in mm.")] double? outerDiameter = null,
        [Description("add: thin wall thickness in mm (omitted = solid); tube: the pipe wall (omitted = a solid rod).")] double? wall = null,
        [Description("wall: inside (default) | outside | symmetric of the section lines.")] WallSide wallSide = WallSide.Inside,
        [Description("wall: close both ends with caps of the wall thickness.")] bool endCaps = false,
        [Description("Document handle; default: the sketches' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_loft_sweep", Snake(action), c => action == SweepAction.Tube
            ? FeatureService.Tube(c, document, path, outerDiameter, wall, Snake(operation))
            : FeatureService.LoftSweep(c, document,
                new SweepSpec(Snake(action), Snake(operation), sketches, path, origins, axis, pitch, turns, height, Snake(hand),
                    wall is { } thickness ? new ThinWallSpec(thickness, Snake(wallSide), endCaps) : null)), cancellationToken);

    [McpServerTool(Name = "se_part_feature", Title = "Rib / thicken / mirror body / face edits / split and unite bodies / slot / web network / lip / scale / copy part / fillet weld", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Other part features (ordered modelling). Lengths in mm.
        rib: sketch = an open chain (se_sketch close open:true) across the part; thickness; side; depth |
        thicken: faces, thickness; side |
        mirror_body: a mirrored copy about 'plane'.
        split: 'plane' cuts the body (or 'bodies') into design bodies | unite: 'bodies' into one (default all); more in se_body.
        Direct edits (also imported parts): delete_faces (healed) | delete_holes (diameter: holes up to it) | delete_rounds (round faces) | delete_region (edges = closed loop, faces = [one face inside]) | offset_faces (distance, positive adds) | replace_faces (onto replaceWith: plane, surface or face) | redefine_faces (adjacent faces into one B-spline) | resize_holes (hole faces, diameter) | resize_rounds (round faces, radius).
        normal_protrusion | normal_cutout: 'edges' of closed curves on faces (se_curve project/wrap) raised or recessed by 'distance'; side auto = inside, reverse = outside.
        slot: sketch = an open path (lines, arcs) on a plane or face; width; rounded ends (path extended by half the width) or squareEnds; through, or depth; optional counterbore |
        web_network: sketch = open lines across a shelled part; thickness; walls to the next face, or depth |
        lip: edges = a chain of rim edges (outer edges of a shelled part's open face), width, height |
        scale_body: scale = one factor or three (x, y, z) about the part origin (the first body of a split part).
        copy_part: path = a saved .par/.psm copied into this part (base body of an empty part, or united with the body); optional scale and mirror plane.
        fillet_weld (part documents): a weld bead body between 'faces' (base) and 'targetFaces' meeting them along an edge; leg mm on the base faces, leg2 on the targets (default leg).
        Threads: se_hole tapped (cosmetic) or se_thread (physical).
        {{VerifiedNote}}
        Example: {"action":"rib","sketch":"sketch:6","thickness":6}; {"action":"scale_body","scale":[2]}.
        """)]
    public Task<CallToolResult> PartFeature(
        PartFeatureAction action,
        [Description("thicken, delete_faces, delete_rounds, offset_faces, replace_faces, redefine_faces (two or more that touch), resize_holes, resize_rounds: face handles; fillet_weld: the base faces; lip: [the rim face] when it is not the smaller face of the first edge.")] string[]? faces = null,
        [Description("rib, slot, web_network: open sketch handle.")] string? sketch = null,
        [Description("rib, thicken, web_network: thickness in mm.")] double? thickness = null,
        [Description("rib: auto = symmetric | normal | reverse (thickness to one side of the sketch plane); thicken: auto = the side that adds material | normal | reverse; normal_protrusion, normal_cutout: auto = inside the curves | reverse = outside.")] PartFeatureSide side = PartFeatureSide.Auto,
        [Description("mirror_body: mirror plane ('1'-'3', refplane or planar face handle); split: the cutting reference plane; copy_part: optional mirror reference plane.")] string? plane = null,
        [Description("rib, web_network: finite depth in mm (default: up to the part or the next face); slot: depth in mm (default: through the part).")] double? depth = null,
        [Description("offset_faces: distance in mm along the face normals (positive outwards adds material, negative inwards); normal_protrusion, normal_cutout: height or depth in mm.")] double? distance = null,
        [Description("delete_holes: largest hole diameter to delete; resize_holes: new diameter (mm).")] double? diameter = null,
        [Description("resize_rounds: new radius in mm.")] double? radius = null,
        [Description("slot, lip: width in mm.")] double? width = null,
        [Description("lip: height in mm.")] double? height = null,
        [Description("delete_region: the closed loop of edges around the region; lip: edge handles of the rim; normal_protrusion, normal_cutout: the edges of the curves (se_geometry edges with source = the curve).")] string[]? edges = null,
        [Description("slot: square ends instead of rounded.")] bool squareEnds = false,
        [Description("slot: counterbore at the top of the slot, [total width, depth] in mm (wider than the slot).")] double[]? counterbore = null,
        [Description("scale_body, copy_part: one factor or three (x, y, z), e.g. [2] or [1, 1, 0.5].")] double[]? scale = null,
        [Description("copy_part: absolute path of the saved part file to copy (allowed folders).")] string? path = null,
        [Description("replace_faces: what the faces become: a reference plane ('1'-'3' or refplane handle), a construction surface (se_surface handle) or a face of one.")] string? replaceWith = null,
        [Description("split: bodies to cut (default: the first); unite: bodies to join into one (default: all). Body handles from se_geometry summary.")] string[]? bodies = null,
        [Description("fillet_weld: target face handles, meeting the base faces along an edge.")] string[]? targetFaces = null,
        [Description("fillet_weld: leg in mm on the base faces.")] double? leg = null,
        [Description("fillet_weld: leg in mm on the target faces (default leg).")] double? leg2 = null,
        [Description("Document handle; default: the document of the faces, sketch or plane.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_part_feature", Snake(action), c => FeatureService.PartFeature(c, document,
            new PartFeatureSpec(Snake(action), faces, sketch, thickness, Snake(side), plane, depth, distance, diameter, width, squareEnds, scale, edges, height, path, counterbore, replaceWith, bodies, radius,
                targetFaces, leg, leg2)), cancellationToken);

    [McpServerTool(Name = "se_body", Title = "Bodies: list / subtract / intersect / unite / emboss / construction and design", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Design and construction bodies of a part (ordered modelling). Lengths in mm. Body handles come from action list; a surface (se_surface) or a face stands for the body it belongs to.
        list: design bodies (volume, box) and construction bodies (kind solid = closed, surface or curve) |
        subtract: 'tools' take material from 'targets' (design bodies, default the first): a closed tool removes what it overlaps (a tool design body stays); an open surface removes one side of it, 'side' normal (where the normal of its face points, se_geometry faces) or reverse |
        intersect: 'targets' keep only what they share with the closed 'tools', which are used up |
        unite: closed construction 'tools' (a surface with endCaps, a closed stitched surface) are added to one target (default the first body) and stay as construction bodies |
        emboss: closed 'tools' pressed into one thin target (default the first body): with thicken (default) a wall of 'thickness' wraps each tool, 'clearance' away from it (side reverse: towards the other face of the target); thicken false only cuts the tools' shape out |
        to_construction: design 'bodies' become construction bodies (left out of mass properties and drawings) |
        to_design: closed construction 'bodies' (a surface with endCaps, a closed stitched surface, a body made construction) become design bodies; they may overlap the others, e.g. to subtract or intersect them |
        divide: a saved part without unsaved changes is cut by 'tools' = [one reference plane or surface] into part files 'paths' (one per piece, default <part>_1, _2...), each a linked copy of its region; the part is then saved.
        Split, and unite design bodies: se_part_feature.
        {{VerifiedNote}}
        Example: {"action":"to_design","bodies":["surface:14"]}; {"action":"subtract","tools":["body:22"]}; {"action":"emboss","tools":["surface:9"],"thickness":2}.
        """)]
    public Task<CallToolResult> Body(
        BodyAction action,
        [Description("subtract, intersect, unite: design bodies to change (default: the first); emboss: the one thin target.")] string[]? targets = null,
        [Description("subtract, intersect, unite, emboss: body handles (action list), surfaces (se_surface) or faces.")] string[]? tools = null,
        [Description("to_construction: design bodies; to_design: closed construction bodies or closed surfaces.")] string[]? bodies = null,
        [Description("subtract with an open surface: normal | reverse = the side of the surface that loses its material; emboss: reverse = towards the other face of the target.")] BodySide side = BodySide.Auto,
        [Description("emboss: wall thickness in mm.")] double? thickness = null,
        [Description("emboss: gap between each tool and the wall in mm (default 0).")] double clearance = 0,
        [Description("emboss: false only cuts the tools' shape out of the target.")] bool thicken = true,
        [Description("divide: one new part file per piece, in an allowed folder (default: next to the part, <part>_1, _2...).")] string[]? paths = null,
        [Description("Document handle; default: the document of the bodies or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_body", Snake(action), c => FeatureService.BodyOperation(c, document,
            new BodySpec(Snake(action), targets, tools, bodies, Snake(side), thickness, clearance, thicken, paths)), cancellationToken);

    [McpServerTool(Name = "se_primitive", Title = "Box / cylinder / sphere", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Adds or cuts a box, cylinder or sphere placed by its centre on a plane, without a sketch (part documents).
        plane = '1'-'3', refplane or planar face handle (default '1', the XY base plane); center = [x,y,z] mm, a point on that plane
        (box: centre of its base rectangle; cylinder: centre of its base circle; sphere: its centre).
        box: size [width, height] mm along the x and y axes of the plane (the frame returned by se_sketch create on it) and depth mm,
        angle = rotation in degrees about the plane normal (from the plane's x axis towards its y axis);
        or, instead of center, size and angle, corners = 2 opposite corners (sides along the plane's axes) or 3 points (the ends of one edge, then a point on the opposite edge);
        cylinder: radius and depth mm; sphere: radius mm. side: auto (default: along the plane normal to add, against it to cut,
        then the other way if that changes nothing) | normal | reverse | symmetric.
        Solid Edge builds primitives only in synchronous modeling: the document switches to it for the call and back to ordered,
        and the primitive goes into the synchronous part of the feature tree.
        {{VerifiedNote}}
        Example: {"shape":"box","center":[50,30,0],"size":[100,60],"depth":20}; {"shape":"cylinder","operation":"cut","plane":"face:12","center":[50,30,20],"radius":5,"depth":20}.
        """)]
    public Task<CallToolResult> Primitive(
        PrimitiveShape shape,
        FeatureOperation operation = FeatureOperation.Add,
        [Description("Plane: '1'-'3', refplane or planar face handle (default '1').")] string? plane = null,
        [Description("Centre [x, y, z] in mm, on the plane.")] double[]? center = null,
        [Description("box: [width, height] in mm along the plane's x and y axes.")] double[]? size = null,
        [Description("cylinder, sphere: radius in mm.")] double? radius = null,
        [Description("box, cylinder: depth in mm from the plane.")] double? depth = null,
        [Description("box, cylinder: auto (along the plane normal to add, against it to cut, then the other way if that changes nothing) | normal | reverse | symmetric.")] PartFeatureSide side = PartFeatureSide.Auto,
        [Description("box: rotation in degrees about the plane normal, from the plane's x axis towards its y axis (default 0).")] double? angle = null,
        [Description("box: 2 opposite corners or 3 points (an edge, then a point on the opposite edge) [x1,y1,z1, ...] in mm on the plane, instead of center/size/angle.")] double[]? corners = null,
        [Description("Document handle; default: the document of the plane handle or the active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_primitive", Snake(shape), c => FeatureService.Primitive(c, document,
            new PrimitiveSpec(Snake(shape), Snake(operation), plane, center, size, radius, depth, Snake(side), angle, corners)), cancellationToken);

    [McpServerTool(Name = "se_curve", Title = "Construction curves", Destructive = false, OpenWorld = false)]
    [Description("""
        Construction curves of a part (they do not change the solid), e.g. as 3D paths: list (name, type, status, edges, length mm, ends, bounding box) |
        points (smooth curve through 'points' [x1,y1,z1,...] mm, >= 2 points; closed: true returns smoothly to the first point) |
        intersection (where 'first' and 'second' meet; each a face handle, a surface (se_surface) or feature handle = its faces, or 'body') |
        project (a 'sketch' projected onto 'onto' = a face, surface or feature handle, or 'body', along the sketch normal (direction normal) or against it (reverse)) |
        cross (two 'sketches' on planes that are not parallel: where their extrusions meet) | derived (one curve from a chain of 'edges') |
        contour (a curve on one face, 'faces': [face], through 'points' that lie on it; closed: true returns to the first point) |
        wrap (a 'sketch' drawn on a plane tangent to cylindrical or conical 'faces', wrapped onto them; its length equals the sketch's) |
        isocline (on 'faces', where the face normal makes 'angle' degrees with 'plane'; 0 = the silhouette; direction normal: where the normal tilts towards the side the plane normal points to, reverse: the other side) |
        split ('curve' split where 'tools' cross it: reference planes or curves; its length stays, it gets more edges) | delete ('name').
        Each curve is checked by its status and edges (points: it ends at the first and last points; contour: it passes through every point; derived: its length equals the edges') and deleted if it fails.
        Example: {"action":"points","points":[0,0,30,50,30,50,100,60,30]}; {"action":"intersection","first":"surface:5","second":"body"}.
        """)]
    public Task<CallToolResult> Curve(
        CurveAction action,
        [Description("points, contour: model points [x1,y1,z1,...] in mm (contour: on the face).")] double[]? points = null,
        [Description("points, contour: return smoothly to the first point.")] bool closed = false,
        [Description("intersection: first set: face, surface or feature handle, or 'body'.")] string? first = null,
        [Description("intersection: second set: face, surface or feature handle, or 'body'.")] string? second = null,
        [Description("project, wrap: sketch handle.")] string? sketch = null,
        [Description("project: face, surface or feature handle, or 'body', to project onto.")] string? onto = null,
        FeatureDirection direction = FeatureDirection.Normal,
        [Description("cross: two sketch handles on planes that are not parallel.")] string[]? sketches = null,
        [Description("derived: edge handles forming a chain (se_geometry edges).")] string[]? edges = null,
        [Description("contour: the one face the curve is drawn on; wrap: cylindrical or conical faces; isocline: the faces (se_geometry faces).")] string[]? faces = null,
        [Description("isocline: '1'-'3', 'xy', 'yz', 'xz' or a reference plane handle.")] string? plane = null,
        [Description("isocline: degrees between the face normal and the plane, from 0 to less than 90.")] double? angle = null,
        [Description("split: the curve handle (action 'list').")] string? curve = null,
        [Description("split: reference planes ('1'-'3', 'xy', 'yz', 'xz' or handles) or curves that cross the curve.")] string[]? tools = null,
        [Description("delete: curve name (action 'list').")] string? name = null,
        [Description("Document handle; default: the document of the handles given, or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_curve";
        string act = Snake(action);
        return action switch
        {
            CurveAction.List => runner.RunAsync(tool, act, c => FeatureService.Curves(c, document), cancellationToken),
            CurveAction.Points => runner.RunAsync(tool, act, c => FeatureService.CurveThroughPoints(c, document, points, closed), cancellationToken),
            CurveAction.Intersection => runner.RunAsync(tool, act, c => FeatureService.IntersectionCurve(c, document, first, second), cancellationToken),
            CurveAction.Project => runner.RunAsync(tool, act, c => FeatureService.ProjectedCurve(c, document, sketch, onto, Snake(direction)), cancellationToken),
            CurveAction.Cross => runner.RunAsync(tool, act, c => FeatureService.CrossCurve(c, document, sketches), cancellationToken),
            CurveAction.Derived => runner.RunAsync(tool, act, c => FeatureService.DerivedCurve(c, document, edges), cancellationToken),
            CurveAction.Contour => runner.RunAsync(tool, act, c => FeatureService.ContourCurve(c, document, faces, points, closed), cancellationToken),
            CurveAction.Wrap => runner.RunAsync(tool, act, c => FeatureService.WrapCurve(c, document, sketch, faces), cancellationToken),
            CurveAction.Isocline => runner.RunAsync(tool, act, c => FeatureService.IsoclineCurve(c, document, faces, plane, angle, Snake(direction)), cancellationToken),
            CurveAction.Split => runner.RunAsync(tool, act, c => FeatureService.SplitCurve(c, document, curve, tools), cancellationToken),
            CurveAction.Delete => runner.RunAsync(tool, act, c => FeatureService.DeleteCurve(c, document, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_surface", Title = "Construction surfaces", Destructive = false, OpenWorld = false)]
    [Description("""
        Construction surfaces of a part (they do not change the solid): list (name, type, status, faces, area mm², bounding box) |
        extrude ('sketch' closed with se_sketch 'close', open: true for an open chain; 'depth' mm; direction normal | reverse | symmetric; endCaps closes a closed profile) |
        revolve ('sketch', 'axis' from se_sketch 'axis' before closing; 'angle' degrees, default 360; direction; endCaps) |
        offset ('faces' = one face handle; 'distance' mm along its normal: positive out of the material, negative into it) |
        loft ('sketches' = two or more sections in order, open or closed; optional 'origins' [x1,y1,...] = the point of each section that is joined; endCaps) |
        sweep ('sketches' = sections, 'path' = a sketch (se_sketch 'close', open: true for an open chain) or a curve from se_curve; endCaps) |
        stitch ('surfaces' = two or more surface handles that meet along edges: one surface, its area checked) |
        bounded ('edges' = a closed chain of edge handles of the body, a surface or a curve: the surface that fills it) |
        copy ('faces' of the body or of surfaces, as one surface) |
        trim ('surfaces' = one surface; 'tools' = surfaces, faces of surfaces, curves or edges that cross it, not planes; 'point' [x,y,z] mm on the region to remove) |
        extend ('edges' = free edges of one surface; 'distance' mm; extension natural | linear | reflective) |
        intersect ('surfaces' that together enclose a volume, e.g. a tube and two caps: one solid construction body, the pieces outside trimmed away) |
        bluesurf ('edges' = one edge per section, in order, e.g. of curves; optional 'guides' = edges; endCaps) |
        blend ('faces' = one face of each of two surfaces; 'radius' mm; 'point' [x,y,z] mm in the corner where the blend goes; the surfaces are trimmed to it) | delete ('name').
        Each surface is checked by its status and its faces (copy: the same area; trim: less; extend: more), and deleted if Solid Edge does not make it.
        Example: {"action":"extrude","sketch":"sketch:5","depth":20}; {"action":"offset","faces":["face:12"],"distance":5}.
        """)]
    public Task<CallToolResult> Surface(
        SurfaceAction action,
        [Description("extrude, revolve: sketch handle (closed, or open with se_sketch 'close' open: true).")] string? sketch = null,
        [Description("extrude: depth in mm.")] double? depth = null,
        [Description("revolve: axis handle from se_sketch 'axis'.")] string? axis = null,
        [Description("revolve: angle in degrees (default 360).")] double angle = 360,
        FeatureDirection direction = FeatureDirection.Normal,
        [Description("extrude, revolve, loft, sweep, bluesurf: close the ends of closed profiles.")] bool endCaps = false,
        [Description("offset: one face handle; copy: face handles; blend: one face of each of two surfaces.")] string[]? faces = null,
        [Description("offset: distance in mm along the face normal (negative = into the material); extend: distance in mm.")] double? distance = null,
        [Description("loft, sweep: section sketch handles, in order.")] string[]? sketches = null,
        [Description("sweep: path = sketch handle or curve handle (se_curve).")] string? path = null,
        [Description("loft, sweep: point [x, y] (sketch mm) of each section that is joined, [x1,y1,x2,y2,...]; default: the start of each section's first element.")] double[]? origins = null,
        [Description("stitch, intersect: surface handles (action 'list'); trim: the one surface to trim.")] string[]? surfaces = null,
        [Description("stitch: gap tolerance in mm (default: Solid Edge's); intersect: tolerance in mm (default 0.01).")] double? tolerance = null,
        [Description("bounded: edge handles of a closed chain; extend: free edges of one surface; bluesurf: one edge per section, in order (se_geometry edges).")] string[]? edges = null,
        [Description("trim: surfaces, faces of surfaces, curves or edges that cross the surface.")] string[]? tools = null,
        [Description("trim: [x, y, z] in mm, a point on the region of the surface to remove; blend: a point in the corner where the blend goes.")] double[]? point = null,
        [Description("blend: radius in mm.")] double? radius = null,
        [Description("extend: natural (default) | linear | reflective.")] SurfaceExtension extension = SurfaceExtension.Natural,
        [Description("bluesurf: guide edge handles.")] string[]? guides = null,
        [Description("delete: surface name (action 'list').")] string? name = null,
        [Description("Document handle; default: the document of the sketch or face, or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_surface";
        string act = Snake(action);
        return action switch
        {
            SurfaceAction.List => runner.RunAsync(tool, act, c => FeatureService.Surfaces(c, document), cancellationToken),
            SurfaceAction.Extrude => runner.RunAsync(tool, act, c => FeatureService.ExtrudeSurface(c, document, sketch, depth, Snake(direction), endCaps), cancellationToken),
            SurfaceAction.Revolve => runner.RunAsync(tool, act, c => FeatureService.RevolveSurface(c, document, sketch, axis, angle, Snake(direction), endCaps), cancellationToken),
            SurfaceAction.Offset => runner.RunAsync(tool, act, c => FeatureService.OffsetSurface(c, document, faces, distance), cancellationToken),
            SurfaceAction.Loft => runner.RunAsync(tool, act, c => FeatureService.LoftSurface(c, document, sketches, origins, endCaps), cancellationToken),
            SurfaceAction.Sweep => runner.RunAsync(tool, act, c => FeatureService.SweepSurface(c, document, sketches, path, origins, endCaps), cancellationToken),
            SurfaceAction.Stitch => runner.RunAsync(tool, act, c => FeatureService.StitchSurface(c, document, surfaces, tolerance), cancellationToken),
            SurfaceAction.Bounded => runner.RunAsync(tool, act, c => FeatureService.BoundedSurface(c, document, edges), cancellationToken),
            SurfaceAction.Copy => runner.RunAsync(tool, act, c => FeatureService.CopySurface(c, document, faces), cancellationToken),
            SurfaceAction.Trim => runner.RunAsync(tool, act, c => FeatureService.TrimSurface(c, document, surfaces, tools, point), cancellationToken),
            SurfaceAction.Extend => runner.RunAsync(tool, act, c => FeatureService.ExtendSurface(c, document, edges, distance, Snake(extension)), cancellationToken),
            SurfaceAction.Intersect => runner.RunAsync(tool, act, c => FeatureService.IntersectSurfaces(c, document, surfaces, tolerance), cancellationToken),
            SurfaceAction.Bluesurf => runner.RunAsync(tool, act, c => FeatureService.BlueSurf(c, document, edges, guides, endCaps), cancellationToken),
            SurfaceAction.Blend => runner.RunAsync(tool, act, c => FeatureService.BlendSurfaces(c, document, faces, radius, point), cancellationToken),
            SurfaceAction.Delete => runner.RunAsync(tool, act, c => FeatureService.DeleteSurface(c, document, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
