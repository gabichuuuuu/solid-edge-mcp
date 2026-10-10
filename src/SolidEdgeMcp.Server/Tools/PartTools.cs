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

/// <summary>Agujeros, patrones, vaciado y desmoldeo (modelado ordenado), verificados como el resto de operaciones.</summary>
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
}
