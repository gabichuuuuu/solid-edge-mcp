// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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
    Gauge,
    Tab,
    ContourFlange,
    Flange,
    Hem,
    BreakCorner,
    Flatten,
    BendAngle,
    BendRadius,
    Convert,
    MatchFace,
    Louver,
    TabSlot,
    Unbend,
    Gusset,
}

public enum GussetShape
{
    Round,
    Square,
}

public enum SheetMetalSide
{
    Inside,
    Outside,
    BendOutside,
}

public enum FlangeDimension
{
    Inside,
    Outside,
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

public enum LouverEnd
{
    Lanced,
    Formed,
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

/// <summary>Side of the thickness for the tab or contour flange; 'symmetric' does not exist in sheet metal.</summary>
public enum SheetDirection
{
    Normal,
    Reverse,
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
    Circular,
}

public enum CutoutExtent
{
    ThroughAll,
    ThroughNext,
    Finite,
    FromTo,
}

public enum BeadEnd
{
    Lanced,
    Punched,
    Formed,
}

public enum CutoutMethod
{
    Thickness,
    MidPlane,
    Clearance,
}

/// <summary>Sheet metal: tabs, flanges, hems, corners, flat pattern and sketch-based operations (mm and degrees).</summary>
[McpServerToolType]
public sealed class SheetMetalTools(ToolRunner runner)
{
    private const string VerifiedNote = "Verified like se_extrude: a feature that fails or has no effect is deleted and reported as an error.";

    [McpServerTool(Name = "se_sheetmetal", Title = "Sheet metal", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Sheet metal (.psm), mm and degrees; radius defaults to the document bend radius.
        settings | set_settings (thickness, bend radius, relief, neutral factor) | gauge (from a material library).
        tab: closed sketch with ONE outline -> base tab, tab on a sheet face's plane or (newBody) another body; direction = thickness side.
        contour_flange: open sketch (close open=true) -> a tab plus a flange per further segment; width along the sketch normal.
        flange: one per straight edge of a sheet face, rising away from the material; length, angle, side, radius, dimension.
        hem: one per edge (usually a flange's free edge); hemType, length of the folded part, radius.
        break_corner: cornerType with 'size' on corner edges through the thickness.
        flatten: flat pattern and cut size (x, y); export with se_export flatPattern=true.
        bend_angle: faces = a bend face, fixedFace = planar face that stays, angle | bend_radius: faces = bend faces, radius = inside radius.
        unbend: fixedFace, faces = bend faces (default all) -> flat; delete the feature to bend again.
        gusset: faces = a bend face, depth, width; distance from an end arc edge (edges; default middle), count or spacing.
        convert: thin-walled part copied in (se_part_feature copy_part) -> sheet metal; faces = base face, edges = corners to rip; set_settings thickness = wall first.
        match_face: faces = a flange's outer or inner face, plane = a parallel plane or face -> the flange moves onto it, or distance mm short.
        louver: faces = a flat sheet face, point = middle of the opening edge, along = its direction (opens to the left seen from the normal).
        tab_slot: edges of one sheet body on faces of another -> tabs and slots; count or spacing, length, depth, gap, distance.
        tab, edge features and louvers: synchronous (the document returns to ordered); flanges and hems need edges of synchronous features.
        Sketch-based features: se_sheetmetal_form. A feature that fails or does nothing is undone and reported.
        Example: {"action":"flange","edges":["edge:12","edge:15"],"length":20}.
        """)]
    public Task<CallToolResult> SheetMetal(
        SheetMetalAction action,
        [Description("tab / contour_flange: sketch handle.")] string? sketch = null,
        [Description("flange / hem / break_corner: edge handles from se_geometry 'edges' (break_corner: corner edges along the sheet normal); convert: corner edges to rip; tab_slot: straight edges of the tab body that lie on the slot faces; gusset: the arc edge at the end of the bend the distances start from (default: the end nearest the origin).")] string[]? edges = null,
        [Description("flange / hem / louver: length in mm; tab_slot: length of each tab along the edge.")] double? length = null,
        [Description("flange: angle between flange and sheet in degrees (default 90 = perpendicular; smaller folds it back over the sheet); bend_angle: the new angle between the bent part and the fixed face.")] double angle = 90,
        [Description("flange / hem: inside (default: the flange outer face is flush with the edge) | outside (the inner face is at the edge) | bend_outside (the bend starts at the edge).")] SheetMetalSide side = SheetMetalSide.Inside,
        [Description("flange / hem / contour_flange: bend radius in mm (default: document bend radius); bend_radius: the new inside radius; louver: die radius (rounded ends); gusset: formed radius (default 0.4 x width).")] double? radius = null,
        [Description("flange: inside (default: the length is measured from the sheet's inner face) | outside (from its outer face, 1 thickness shorter).")] FlangeDimension dimension = FlangeDimension.Inside,
        [Description("hem: closed | open | s_flange (with radius2, length2) | closed_loop.")] HemType hemType = HemType.Closed,
        [Description("hem s_flange: length of the second flange in mm (default: length).")] double? length2 = null,
        [Description("hem s_flange: second bend radius in mm (default: radius).")] double? radius2 = null,
        [Description("break_corner: round (size = radius) or chamfer (size = setback).")] CornerType cornerType = CornerType.Round,
        [Description("break_corner: round radius or chamfer setback in mm.")] double? size = null,
        [Description("contour_flange: extent along the sketch normal in mm; louver: size of the opening across it in mm; gusset: its width across the bend.")] double? width = null,
        [Description("tab: side of the thickness (normal or reverse); contour_flange: only normal.")] SheetDirection direction = SheetDirection.Normal,
        [Description("contour_flange: side of the chain where the thickness goes, walking from its first point, seen from the side the sketch normal points to.")] ChainSide materialSide = ChainSide.Right,
        [Description("set_settings: material thickness in mm.")] double? thickness = null,
        [Description("set_settings: default bend radius in mm.")] double? bendRadius = null,
        [Description("set_settings: bend relief width in mm.")] double? reliefWidth = null,
        [Description("set_settings: bend relief depth in mm.")] double? reliefDepth = null,
        [Description("set_settings: neutral factor (0-1, e.g. 0.33).")] double? neutralFactor = null,
        [Description("bend_angle: one bend face; bend_radius: bend faces (cylindrical faces of the bends, se_geometry faces of type cylinder); convert: the base face; match_face: one face of a flange; louver: one flat sheet face; tab_slot: faces of the other sheet body that get the slots; unbend: faces of the bends to unbend (default: all).")] string[]? faces = null,
        [Description("louver: [x, y, z] in mm on the face, the middle of the louver's opening edge.")] double[]? point = null,
        [Description("louver: [x, y, z] direction the louver runs, in the plane of the face.")] double[]? along = null,
        [Description("louver: how far it rises from the face in mm.")] double? height = null,
        [Description("louver: lanced (default: open ends) | formed (closed, drawn ends).")] LouverEnd louverEnd = LouverEnd.Lanced,
        [Description("bend_angle / unbend: planar face that stays in place.")] string? fixedFace = null,
        [Description("match_face: the plane the face goes to: index or handle from se_ref_plane, or a planar face (a plane is made on it).")] string? plane = null,
        [Description("match_face: mm the face stops short of the plane, on the side it comes from (negative: beyond the plane); tab_slot: mm from each end of the edges to the first and last tab (default 0); gusset: mm from the end of the bend (0 = the middle).")] double distance = 0,
        [Description("tab: true starts another sheet body instead of joining the sheet (e.g. the tab body of tab_slot).")] bool newBody = false,
        [Description("gusset: round (default) | square.")] GussetShape gussetShape = GussetShape.Round,
        [Description("tab_slot: number of tabs spread evenly along the edges (default 2); ignored with spacing; gusset: number of gussets (spread evenly, or every spacing).")] int? count = null,
        [Description("tab_slot: mm between tabs; given, the edges are filled with as many tabs as fit and count is ignored; gusset: mm between gussets, the first that far from the end.")] double? spacing = null,
        [Description("tab_slot: mm the tabs go into the slot body (default: the sheet thickness); gusset: how far it reaches from the bend.")] double? depth = null,
        [Description("tab_slot: slot clearance around each tab in mm (default 0).")] double gap = 0,
        [Description("gauge: gauge name as in the material library, e.g. '16 gage'.")] string? gauge = null,
        [Description("gauge: material library (se_material libraries); default: the first one that has the gauge.")] string? library = null,
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
            SheetMetalAction.Gauge => runner.RunAsync(tool, name, c => FeatureService.SheetGauge(c, document, gauge, library), cancellationToken),
            SheetMetalAction.Tab => runner.RunAsync(tool, name, c => FeatureService.Tab(c, document, RequireSketch(sketch, name), Snake(direction), newBody), cancellationToken),
            SheetMetalAction.ContourFlange => runner.RunAsync(tool, name, c => FeatureService.ContourFlange(c, document, RequireSketch(sketch, name),
                width, Snake(direction), Snake(materialSide), radius), cancellationToken),
            SheetMetalAction.Flange => runner.RunAsync(tool, name, c => FeatureService.Flange(c, document,
                new FlangeSpec(edges, length, angle, Snake(side), radius, Snake(dimension))), cancellationToken),
            SheetMetalAction.Hem => runner.RunAsync(tool, name, c => FeatureService.Hem(c, document,
                new HemSpec(edges, Snake(hemType), length, radius, length2, radius2, Snake(side))), cancellationToken),
            SheetMetalAction.BreakCorner => runner.RunAsync(tool, name, c => FeatureService.BreakCorner(c, document, edges, Snake(cornerType), size), cancellationToken),
            SheetMetalAction.Flatten => runner.RunAsync(tool, name, c => FeatureService.Flatten(c, document), cancellationToken),
            SheetMetalAction.BendAngle => runner.RunAsync(tool, name, c => FeatureService.BendAngle(c, document, faces, fixedFace, angle), cancellationToken),
            SheetMetalAction.BendRadius => runner.RunAsync(tool, name, c => FeatureService.BendRadius(c, document, faces, radius), cancellationToken),
            SheetMetalAction.Convert => runner.RunAsync(tool, name, c => FeatureService.ConvertToSheet(c, document, faces, edges), cancellationToken),
            SheetMetalAction.MatchFace => runner.RunAsync(tool, name, c => FeatureService.MatchFace(c, document, faces, plane, distance), cancellationToken),
            SheetMetalAction.Louver => runner.RunAsync(tool, name, c => FeatureService.Louver(c, document,
                new LouverSpec(faces, point, along, length, width, height, Snake(louverEnd), radius)), cancellationToken),
            SheetMetalAction.TabSlot => runner.RunAsync(tool, name, c => FeatureService.TabSlot(c, document,
                new TabSlotSpec(edges, faces, length, count, spacing, distance, gap, depth)), cancellationToken),
            SheetMetalAction.Unbend => runner.RunAsync(tool, name, c => FeatureService.Unbend(c, document, fixedFace, faces), cancellationToken),
            SheetMetalAction.Gusset => runner.RunAsync(tool, name, c => FeatureService.Gusset(c, document,
                new GussetSpec(faces, edges, depth, width, Snake(gussetShape), radius, count, spacing, distance)), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_sheetmetal_form", Title = "Sheet metal forming and cutting", Destructive = false, OpenWorld = false)]
    [Description($$"""
        Sketch-based sheet metal features (ordered modeling), in mm and degrees. Draw the sketch on the sheet (se_sketch 'create' on a sheet face or a plane of it) and close it.
        dimple (closed sketch; depth) | drawn_cutout (closed sketch; depth) | normal_cutout (closed sketch; cuts through the thickness; method thickness | mid_plane | clearance; extent through_all | through_next | finite (depth) | from_to (to)) | etch (lines marked on the face) |
        jog (one line across the sheet; height; side inside | outside | bend_outside) | bend (one line; angle between the bent part and the sheet: 90 = perpendicular, 60 folds it back over the sheet; position of the bend relative to the line: inside = outer face of the bent part on the line | outside | symmetric) | bead (open path; beadType u | v | circular; height, width; beadEnd lanced | punched | formed).
        direction: auto (default; normal_cutout cuts into the material when the sketch lies on a sheet face, otherwise tries the sketch normal and then the other side; the rest use normal) | normal (towards the sketch normal returned by se_sketch create; for dimple, drawn_cutout, jog, bend and bead it is the side the form rises to) | reverse.
        movingSide (jog, bend): the part of the sheet that moves, right | left of the line walking from its first to its second point, seen from the side the sketch normal points to (right = line direction x normal).
        {{VerifiedNote}}
        Example: {"action":"normal_cutout","sketch":"sketch:30"}.
        """)]
    public Task<CallToolResult> SheetMetalForm(
        SheetFormAction action,
        [Description("Closed sketch handle (open for jog, bend, bead, etch).")] string sketch,
        [Description("dimple / drawn_cutout / normal_cutout finite: depth in mm.")] double? depth = null,
        [Description("jog / bead: height in mm.")] double? height = null,
        [Description("bead: width in mm.")] double? width = null,
        [Description("bend: angle between the bent part and the sheet in degrees (0 < angle < 180; 90 = perpendicular).")] double? angle = null,
        FormDirection direction = FormDirection.Auto,
        MovingSide movingSide = MovingSide.Right,
        BendPosition position = BendPosition.Inside,
        [Description("jog: material side.")] SheetMetalSide side = SheetMetalSide.Inside,
        BeadType beadType = BeadType.U,
        CutoutMethod method = CutoutMethod.Thickness,
        [Description("bead: lanced (default: open ends) | punched (cut ends, punchWidth) | formed (closed, drawn ends).")] BeadEnd beadEnd = BeadEnd.Lanced,
        [Description("bead with beadEnd punched: width of the punched end in mm (default: the bead width).")] double? punchWidth = null,
        [Description("normal_cutout: through_all (default) | through_next (to the first wall it meets) | finite (depth from the sketch plane) | from_to (to).")] CutoutExtent extent = CutoutExtent.ThroughAll,
        [Description("normal_cutout from_to: the plane the cut ends at, as an index or handle from se_ref_plane, or a planar face.")] string? to = null,
        [Description("Document handle; default: the sketch's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_sheetmetal_form", Snake(action), c => FeatureService.SheetForm(c, document, new SheetFormSpec(sketch, Snake(action),
            depth, height, width, angle, Snake(direction), Snake(movingSide), Snake(position), Snake(side), Snake(beadType), Snake(method), Snake(beadEnd), punchWidth, Snake(extent), to)),
            cancellationToken);

    private static string RequireSketch(string? sketch, string action) => string.IsNullOrWhiteSpace(sketch)
        ? throw ToolException.InvalidArgument($"Action '{action}' requires 'sketch'.", "Create and close one with se_sketch.")
        : sketch;
}
