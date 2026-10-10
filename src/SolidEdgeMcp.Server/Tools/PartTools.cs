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
}

public enum HelixHand
{
    Right,
    Left,
}

/// <summary>Holes, patterns, shell and draft (ordered modelling), verified like the other operations.</summary>
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
        ExtrudeExtent extent = ExtrudeExtent.ThroughAll,
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
        mirror: symmetric copy about 'plane'. features: handles from se_feature_tree or from the tool that created them.
        {{VerifiedNote}}
        """)]
    public Task<CallToolResult> Pattern(
        [Description("Feature handles to copy.")] string[] features,
        PatternType type,
        [Description("Layout plane (rectangular/circular, normally the features' sketch plane) or mirror plane: '1'-'3', refplane or planar face handle.")] string plane,
        [Description("Rectangular: occurrences along x (default 1).")] int countX = 1,
        [Description("Rectangular: occurrences along y (default 1).")] int countY = 1,
        [Description("Rectangular: spacing along x in mm.")] double? spacingX = null,
        [Description("Rectangular: spacing along y in mm.")] double? spacingY = null,
        [Description("Circular: number of occurrences including the original.")] int count = 0,
        [Description("Circular: total angle in degrees (default 360).")] double angle = 360,
        [Description("Circular: axis point [x,y,z] in mm, model coordinates (default origin).")] double[]? center = null,
        [Description("Document handle; default: the features' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_pattern", Snake(type), c => FeatureService.Pattern(c, document,
            new PatternSpec(features, Snake(type), plane, countX, countY, spacingX, spacingY, count, angle, center)), cancellationToken);

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
        sweep: moves 'sketches' (>= 1 sections) along 'path' (a sketch with a chain of lines/arcs closed with 'close', open: true for an open chain; the section usually lies on a plane across the path start).
        helix: coils one section around 'axis' (se_sketch 'axis' on a line of the same sketch, before 'close'); give two of pitch (mm), turns and height (mm); it starts at the section and rises towards the end point of the axis line; hand right (default) | left.
        {{VerifiedNote}} The sketches are kept if it fails.
        Example: {"action":"loft","sketches":["sketch:3","sketch:7"]}; {"action":"helix","sketches":["sketch:2"],"axis":"axis:4","pitch":8,"turns":5}.
        """)]
    public Task<CallToolResult> LoftSweep(
        SweepAction action,
        [Description("Closed section sketch handles, in order.")] string[] sketches,
        FeatureOperation operation = FeatureOperation.Add,
        [Description("Sweep: path sketch handle.")] string? path = null,
        [Description("Loft/sweep: start point of each section [x1,y1,x2,y2,...] in mm, sketch coordinates.")] double[]? origins = null,
        [Description("Helix: axis handle from se_sketch 'axis'.")] string? axis = null,
        [Description("Helix: pitch in mm (rise per turn).")] double? pitch = null,
        [Description("Helix: number of turns.")] double? turns = null,
        [Description("Helix: height in mm.")] double? height = null,
        HelixHand hand = HelixHand.Right,
        [Description("Document handle; default: the sketches' document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_loft_sweep", Snake(action), c => FeatureService.LoftSweep(c, document,
            new SweepSpec(Snake(action), Snake(operation), sketches, path, origins, axis, pitch, turns, height, Snake(hand))), cancellationToken);
}
