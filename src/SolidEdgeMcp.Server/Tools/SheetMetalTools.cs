using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum SheetMetalAction
{
    Settings,
    SetSettings,
    Tab,
    ContourFlange,
    Flange,
    Hem,
    BreakCorner,
    Flatten,
}

public enum SheetMetalSide
{
    Inside,
    Outside,
    BendOutside,
}

public enum HemType
{
    Closed,
    Open,
    SFlange,
    ClosedLoop,
}

public enum CornerType
{
    Round,
    Chamfer,
}

public enum ChainSide
{
    Right,
    Left,
}

public enum SheetFormAction
{
    Dimple,
    DrawnCutout,
    NormalCutout,
    Etch,
    Jog,
    Bend,
    Bead,
}

public enum FormDirection
{
    Auto,
    Normal,
    Reverse,
}

public enum MovingSide
{
    Right,
    Left,
}

public enum BendPosition
{
    Inside,
    Outside,
    Symmetric,
}

public enum BeadType
{
    U,
    V,
}

public enum CutoutMethod
{
    Thickness,
    MidPlane,
    Clearance,
}

/// <summary>Chapa: pestañas, bridas, dobladillos, esquinas, desplegado y operaciones por boceto (mm y grados).</summary>
[McpServerToolType]
public sealed class SheetMetalTools(ToolRunner runner)
{
    private const string VerifiedNote = "Verified like se_extrude: a feature that fails or has no effect is deleted and reported as an error.";

    [McpServerTool(Name = "se_sheetmetal", Title = "Sheet metal", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Sheet metal (.psm): settings, base, edge features and flat pattern. Lengths in mm, angles in degrees.
        settings: thickness, bend radius, relief width/depth, neutral factor | set_settings: changes any of them (the model updates).
        tab: closed sketch with ONE outline (se_sketch close) -> base tab with the document thickness (first feature) or an extra tab drawn on the plane of a sheet face; direction normal|reverse = side of the thickness. Cut holes afterwards (se_sheetmetal_form normal_cutout).
        contour_flange: open sketch (se_sketch close with open=true) -> base contour flange (Solid Edge keeps it as a tab plus one flange per further segment, all returned in 'features'): width always grows along the sketch normal, materialSide right|left of the chain, radius.
        flange: one flange per edge (straight edges on the sheet faces, from se_geometry 'edges'); it rises from the face of the edge, away from the material (top edge -> up). length from that face; angle between flange and sheet (90 = perpendicular, smaller folds it back over the sheet); side inside (default: flange outer face flush with the edge) | outside (inner face at the edge) | bend_outside (bend starts at the edge); radius.
        hem: one hem per edge (usually the free edge of a flange). hemType closed | open | s_flange (radius2, length2) | closed_loop; length of the folded part; radius; side. Solid Edge may fail (RPC_E_SERVERFAULT) on an edge that meets another hem; the call is then undone.
        break_corner: cornerType round | chamfer with 'size' on corner edges through the thickness (se_geometry 'edges' with direction = sheet normal).
        flatten: creates or updates the flat pattern and returns the cut size (x, y); export it with se_export flatPattern=true.
        radius defaults to the document bend radius. tab, contour_flange, flange, hem and break_corner are built in synchronous modeling (the only mode where Solid Edge solves them via the API) and the document returns to ordered; flanges and hems need edges of synchronous features (tabs, flanges).
        Sketch-based features (dimple, cutouts, jog, bend, bead...): se_sheetmetal_form. Verified: a feature that fails or has no effect is reported as an error and everything the call made is undone (Undo, so trimmed faces come back too).
        Example: {"action":"flange","edges":["edge:12","edge:15"],"length":20}.
        """)]
    public Task<CallToolResult> SheetMetal(
        SheetMetalAction action,
        [Description("tab / contour_flange: sketch handle.")] string? sketch = null,
        [Description("flange / hem / break_corner: edge handles from se_geometry 'edges'.")] string[]? edges = null,
        [Description("flange / hem: length in mm.")] double? length = null,
        [Description("flange: angle between flange and sheet in degrees (default 90).")] double angle = 90,
        [Description("flange / hem: material side.")] SheetMetalSide side = SheetMetalSide.Inside,
        [Description("flange / hem / contour_flange: bend radius in mm (default: document bend radius).")] double? radius = null,
        HemType hemType = HemType.Closed,
        [Description("hem s_flange: length of the second flange in mm (default: length).")] double? length2 = null,
        [Description("hem s_flange: second bend radius in mm (default: radius).")] double? radius2 = null,
        CornerType cornerType = CornerType.Round,
        [Description("break_corner: round radius or chamfer setback in mm.")] double? size = null,
        [Description("contour_flange: extent along the sketch normal in mm.")] double? width = null,
        [Description("tab: side of the thickness (normal or reverse); contour_flange: only normal.")] FeatureDirection direction = FeatureDirection.Normal,
        [Description("contour_flange: side of the chain where the thickness goes, walking from its first point, seen from the side the sketch normal points to.")] ChainSide materialSide = ChainSide.Right,
        [Description("set_settings: material thickness in mm.")] double? thickness = null,
        [Description("set_settings: default bend radius in mm.")] double? bendRadius = null,
        [Description("set_settings: bend relief width in mm.")] double? reliefWidth = null,
        [Description("set_settings: bend relief depth in mm.")] double? reliefDepth = null,
        [Description("set_settings: neutral factor (0-1, e.g. 0.33).")] double? neutralFactor = null,
        [Description("Document handle; default: the document of the sketch/edges, or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sheetmetal";
        string name = Snake(action);
        return action switch
        {
            SheetMetalAction.Settings => runner.RunAsync(tool, name, c => FeatureService.SheetSettings(c, document), cancellationToken),
            SheetMetalAction.SetSettings => runner.RunAsync(tool, name, c => FeatureService.SetSheetSettings(c, document,
                new SheetMetalValues(thickness, bendRadius, reliefWidth, reliefDepth, neutralFactor)), cancellationToken),
            SheetMetalAction.Tab => runner.RunAsync(tool, name, c => FeatureService.Tab(c, document, RequireSketch(sketch, name), Snake(direction)), cancellationToken),
            SheetMetalAction.ContourFlange => runner.RunAsync(tool, name, c => FeatureService.ContourFlange(c, document, RequireSketch(sketch, name),
                width, Snake(direction), Snake(materialSide), radius), cancellationToken),
            SheetMetalAction.Flange => runner.RunAsync(tool, name, c => FeatureService.Flange(c, document,
                new FlangeSpec(edges, length, angle, Snake(side), radius)), cancellationToken),
            SheetMetalAction.Hem => runner.RunAsync(tool, name, c => FeatureService.Hem(c, document,
                new HemSpec(edges, Snake(hemType), length, radius, length2, radius2, Snake(side))), cancellationToken),
            SheetMetalAction.BreakCorner => runner.RunAsync(tool, name, c => FeatureService.BreakCorner(c, document, edges, Snake(cornerType), size), cancellationToken),
            SheetMetalAction.Flatten => runner.RunAsync(tool, name, c => FeatureService.Flatten(c, document), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_sheetmetal_form", Title = "Sheet metal forming and cutting", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Sketch-based sheet metal features (ordered modeling), in mm and degrees. Draw the sketch on the sheet (se_sketch 'create' on a sheet face or a plane of it) and close it.
        dimple (closed sketch; depth) | drawn_cutout (closed sketch; depth) | normal_cutout (closed sketch; cuts through the thickness; method thickness | mid_plane | clearance) | etch (lines marked on the face) |
        jog (one line across the sheet; height; side inside | outside | bend_outside) | bend (one line; angle between the bent part and the sheet: 90 = perpendicular, 60 folds it back over the sheet; position of the bend relative to the line: inside = outer face of the bent part on the line | outside | symmetric) | bead (open path; beadType u | v; height, width).
        direction: auto (default; normal_cutout cuts into the material when the sketch lies on a sheet face, otherwise tries the sketch normal and then the other side; the rest use normal) | normal (towards the sketch normal returned by se_sketch create; for dimple, drawn_cutout, jog, bend and bead it is the side the form rises to) | reverse.
        movingSide (jog, bend): the part of the sheet that moves, right | left of the line walking from its first to its second point, seen from the side the sketch normal points to (right = line direction x normal).
        {{VerifiedNote}}
        Example: {"action":"normal_cutout","sketch":"sketch:30"}.
        """)]
    public Task<CallToolResult> SheetMetalForm(
        SheetFormAction action,
        [Description("Closed sketch handle (open for jog, bend, bead, etch).")] string sketch,
        [Description("dimple / drawn_cutout: depth in mm.")] double? depth = null,
        [Description("jog / bead: height in mm.")] double? height = null,
        [Description("bead: width in mm.")] double? width = null,
        [Description("bend: angle between the bent part and the sheet in degrees (0 < angle < 180; 90 = perpendicular).")] double? angle = null,
        FormDirection direction = FormDirection.Auto,
        MovingSide movingSide = MovingSide.Right,
        BendPosition position = BendPosition.Inside,
        [Description("jog: material side.")] SheetMetalSide side = SheetMetalSide.Inside,
        BeadType beadType = BeadType.U,
        CutoutMethod method = CutoutMethod.Thickness,
        [Description("Document handle; default: the sketch's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_sheetmetal_form", Snake(action), c => FeatureService.SheetForm(c, document, new SheetFormSpec(sketch, Snake(action),
            depth, height, width, angle, Snake(direction), Snake(movingSide), Snake(position), Snake(side), Snake(beadType), Snake(method))),
            cancellationToken);

    private static string RequireSketch(string? sketch, string action) => string.IsNullOrWhiteSpace(sketch)
        ? throw ToolException.InvalidArgument($"Action '{action}' requires 'sketch'.", "Create and close one with se_sketch.")
        : sketch;
}
