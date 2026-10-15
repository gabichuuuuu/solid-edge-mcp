// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum DrawingAction
{
    Sheets,
    AddSheet,
    SetupSheet,
    ActivateSheet,
    DeleteSheet,
    AddBackground,
    ReplaceBackground,
    Views,
    BaseView,
    ProjectedView,
    AuxiliaryView,
    CuttingPlane,
    SectionView,
    DetailView,
    Model2dView,
    MoveView,
    ScaleView,
    RotateView,
    CropView,
    DeleteView,
    Dimension,
    RetrieveDimensions,
    Dimensions,
    PartsList,
    BendTable,
    ToleranceTable,
    FamilyTable,
    BlockTable,
    HoleTable,
    Tables,
    Update,
    Blocks,
    CreateBlock,
    PlaceBlock,
    ImportBlock,
    DeleteBlock,
    Unblock,
    UnblockAll,
}

public enum DimensionKind
{
    Length,
    Distance,
    Diameter,
    Radius,
    Angle,
    Coordinate,
    AngularCoordinate,
    Chamfer,
    SymmetricDiameter,
}

public enum MeasureDirection
{
    Aligned,
    Horizontal,
    Vertical,
    Counterclockwise,
    Clockwise,
}

public enum DrawingOrientation
{
    Front,
    Back,
    Top,
    Bottom,
    Left,
    Right,
    TopFrontRight,
    TopFrontLeft,
    TopBackRight,
    TopBackLeft,
    BottomFrontRight,
    BottomFrontLeft,
    BottomBackRight,
    BottomBackLeft,
}

public enum FoldDirection
{
    Up,
    Down,
    Left,
    Right,
    UpRight,
    UpLeft,
    DownRight,
    DownLeft,
}

public enum PaperSize
{
    A4,
    A3,
    A2,
    A1,
    A0,
    AnsiA,
    AnsiB,
    AnsiC,
    AnsiD,
    AnsiE,
}

/// <summary>Drawings: sheets, views, dimensions, parts list and updating.</summary>
[McpServerToolType]
public sealed class DrawingTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_drawing", Title = "Drawings (draft)", Destructive = true, OpenWorld = false)]
    [Description("""
        Drawings of a draft document (se_documents new kind=draft). Sheet positions in mm from its lower-left corner (a view position is its centre); model points in mm.
        Sheets: sheets (also backgrounds: frame, title block; draw with se_sketch) | add_sheet (name, paper + portrait or width/height) | setup_sheet (rename, resize, background, showBackground) | activate_sheet (backgrounds too) | delete_sheet | add_background | replace_background ('path' .dft).
        Views: views | base_view (path of a saved .par/.psm/.asm or model = an open one; orientation; scale, default the largest that fits; flat; modelView; member) | projected_view (view + direction; diagonals = pictorial) | auxiliary_view (view + line) | cutting_plane (view + line; flip; name = caption; no view) | section_view (view + line; flip; or a cutting plane) | detail_view (view + center, radius; scale default 2) | model2d_view (the 2D model sheet; scale default 1) | move_view (position or delta) | scale_view | rotate_view (angle) | crop_view (area) | delete_view (a cutting plane with its section).
        Dimensions: dimension (view + kind + points: model points picking what to measure) | retrieve_dimensions (view: model dimensions parallel to it, centre marks) | dimensions (of a sheet).
        Tables: parts_list (assembly view; balloons; family/members: a quantity column per member) | bend_table (flat pattern view) | tolerance_table (view: its dimensions with a fit) | family_table (view of a family of parts) | block_table (sheet: its placed blocks) | hole_table (view; points: the origin, then the holes) | tables (read all) | update (views, title block text; force = full).
        Blocks: blocks (definitions, occurrences) | create_block (name + elements + position = block origin) | place_block (name + position; scale, angle) | import_block (a saved .dft path) | delete_block (name) | unblock (occurrence: to 2D elements) | unblock_all (nested too).
        Title block: se_properties, then update. Export: se_export.
        Example: {"action":"projected_view","view":"view:3","direction":"down"}.
        """)]
    public Task<CallToolResult> Drawing(
        DrawingAction action,
        [Description("Sheet handle or name (default: the active sheet); background sheets too.")] string? sheet = null,
        [Description("Drawing view handle: the source of a projected/auxiliary/section/detail view, or the view to move, scale, delete, dimension, list or update. section_view, delete_view: or a cutting plane handle (cutting_plane, views).")] string? view = null,
        [Description("base_view: absolute path of the saved model file; import_block: a saved .dft drawing, which becomes a block named after the file (the blocks it contains are copied too); replace_background: a saved .dft (also in the template folder) whose background is copied. Allowed folders only.")] string? path = null,
        [Description("base_view: handle of an open, saved model document instead of 'path'.")] string? model = null,
        [Description("base_view: view orientation (default front; top for a flat pattern). top_front_right is the usual isometric.")] DrawingOrientation? orientation = null,
        [Description("projected_view: side of the source view where the new view goes. The side of the model it shows follows the drawing's projection angle (in the result): first_angle (ISO E) puts the left view to the right of the front view and the top view below it; third_angle puts the right view to the right and the top view above.")] FoldDirection? direction = null,
        [Description("base_view/detail_view/scale_view/place_block: scale factor (1 = 1:1, 0.5 = 1:2, 2 = 2:1).")] double? scale = null,
        [Description("View centre [x, y] in mm on the sheet, default beside the source view for derived views (tables: the upper left corner; default above the title block; create_block/place_block: the block origin; dimension: a point the dimension line passes through, default outside the outline, stacked; diameter: where its text goes, default outside the circle away from the view centre).")] double[]? position = null,
        [Description("move_view: displacement [dx, dy] in mm on the sheet. Views projected from the moved view move only to stay aligned (a top view below it follows horizontal moves).")] double[]? delta = null,
        [Description("Two model points [x1, y1, z1, x2, y2, z2] in mm. auxiliary_view: along the edge to look square at; section_view/cutting_plane: the cutting line, extended across the view.")] double[]? line = null,
        [Description("detail_view: model point [x, y, z] in mm at the centre of the detail.")] double[]? center = null,
        [Description("detail_view: radius of the detail circle in mm of the model.")] double? radius = null,
        [Description("dimension: what to measure.")] DimensionKind? kind = null,
        [Description("dimension: model points [x1, y1, z1, ...] in mm that pick what to measure in the view (in a detail view, on the edges inside its circle). length: a point on a straight edge; distance: two points, each a vertex or circle centre (measured exactly there) or a point on an edge (a horizontal/vertical edge is measured square to it); diameter/radius: the centre or a point of the circle/arc (a hole or shaft seen from the side, two parallel edges: symmetric_diameter); angle: a point on each of two edges; coordinate: the origin (a vertex or circle centre) first, then each point measured from it; angular_coordinate: the centre, a point for the zero direction, then the points; chamfer: a point on the chamfer edge, then one on the edge it is measured from; symmetric_diameter: a point on a centre line (se_annotation centerline), then one on an edge parallel to it. hole_table: the origin (a vertex or circle centre), then a point of each hole to list (default the holes of a part view, one circle each: the narrowest on a concave cylinder or cone along the view; every circle of a flat pattern or assembly view, or of a view out of date).")] double[]? points = null,
        [Description("dimension (distance): aligned (true distance, default), horizontal or vertical on the sheet; coordinate: horizontal (default) or vertical distances from the origin; angular_coordinate: counterclockwise (default) or clockwise.")] MeasureDirection measure = MeasureDirection.Aligned,
        [Description("section_view/cutting_plane: look from the other side of the cutting line.")] bool flip = false,
        [Description("base_view: flat pattern of a sheet metal part.")] bool flat = false,
        [Description("base_view: a configuration of the assembly (se_assembly_config list): a display one (e.g. some parts hidden) or the explode one, 'explode,Solid Edge', for an exploded view in its stored orientation.")] string? configuration = null,
        [Description("base_view: name of a PMI model view saved in the model (exact case), shown as saved: its orientation and PMI.")] string? modelView = null,
        [Description("base_view: member of an assembly with alternate positions or a family (se_assembly_family) to show; default its first.")] string? member = null,
        [Description("add_sheet/add_background/setup_sheet: sheet name (setup_sheet renames); create_block/place_block/delete_block: block name; cutting_plane: its caption (default the next letter).")] string? name = null,
        [Description("add_sheet/add_background/setup_sheet: standard paper size (landscape unless portrait).")] PaperSize? paper = null,
        [Description("add_sheet/add_background/setup_sheet: portrait paper.")] bool portrait = false,
        [Description("add_sheet/add_background/setup_sheet: custom sheet width in mm (with height).")] double? width = null,
        [Description("add_sheet/add_background/setup_sheet: custom sheet height in mm (with width).")] double? height = null,
        [Description("add_sheet/setup_sheet: background sheet name (frame and title block) from 'sheets'; replace_background: the background of the 'path' drawing to copy (default: the same name as the one replaced).")] string? background = null,
        [Description("add_sheet/setup_sheet: show (true) or hide (false) the background of the sheet.")] bool? showBackground = null,
        [Description("replace_background: also replace the blocks of the same name with those of the 'path' drawing.")] bool replaceBlocks = false,
        [Description("parts_list: add balloons to the view (default true).")] bool balloons = true,
        [Description("parts_list, bend_table, tolerance_table, block_table, hole_table: saved table setting name (default: the document default; tolerance tables: e.g. ISO).")] string? style = null,
        [Description("update: full (forced) update of every view.")] bool force = false,
        [Description("create_block: handles of 2D elements drawn on the sheet (se_sketch with the sheet handle); they become the block and one occurrence takes their place.")] string[]? elements = null,
        [Description("place_block: rotation in degrees, counter-clockwise (default 0); rotate_view: the view's angle on the sheet in degrees, counter-clockwise (0 = as projected).")] double? angle = null,
        [Description("crop_view: the rectangle of the sheet to keep, [x1, y1, x2, y2] in mm (it may reach past the view); omit to show the whole view again.")] double[]? area = null,
        [Description("unblock: block occurrence handle (action 'blocks').")] string? occurrence = null,
        [Description("parts_list: a family of assemblies parts list of the view's assembly family, one quantity column per member (default all members).")] bool family = false,
        [Description("parts_list: the assembly family members to list (se_assembly_family list), e.g. [\"NoPin\", \"WithPin\"]; implies family.")] string[]? members = null,
        int offset = 0,
        int limit = 100,
        [Description("Draft document handle; default: the document of the sheet/view handle or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_drawing";
        string act = Snake(action);
        string? orient = orientation is { } o ? Snake(o) : null;
        string? size = paper is { } p ? Snake(p) : null;
        return action switch
        {
            DrawingAction.Sheets => runner.RunAsync(tool, act, c => DrawingService.Sheets(c, document), cancellationToken),
            DrawingAction.AddSheet => runner.RunAsync(tool, act, c => DrawingService.AddSheet(c, document, name, size, portrait, width, height, background, showBackground), cancellationToken),
            DrawingAction.SetupSheet => runner.RunAsync(tool, act, c => DrawingService.SetupSheet(c, document, sheet, name, size, portrait, width, height, background, showBackground), cancellationToken),
            DrawingAction.ActivateSheet => runner.RunAsync(tool, act, c => DrawingService.ActivateSheet(c, document, sheet), cancellationToken),
            DrawingAction.DeleteSheet => runner.RunAsync(tool, act, c => DrawingService.DeleteSheet(c, document, sheet), cancellationToken),
            DrawingAction.AddBackground => runner.RunAsync(tool, act, c => DrawingService.AddBackground(c, document, name, size, portrait, width, height), cancellationToken),
            DrawingAction.ReplaceBackground => runner.RunAsync(tool, act, c => DrawingService.ReplaceBackground(c, document, sheet, path, background, replaceBlocks), cancellationToken),
            DrawingAction.Views => runner.RunAsync(tool, act, c => DrawingService.Views(c, document, sheet), cancellationToken),
            DrawingAction.BaseView => runner.RunAsync(tool, act, c => DrawingService.BaseView(c, document, sheet, path, model, orient, scale, position, flat, configuration, modelView, member), cancellationToken),
            DrawingAction.Model2dView => runner.RunAsync(tool, act, c => DrawingService.Model2dView(c, document, sheet, scale, position), cancellationToken),
            DrawingAction.ProjectedView => runner.RunAsync(tool, act, c => DrawingService.ProjectedView(c, view,
                Snake(ToolRunner.RequireValue(direction, "direction", act)), position), cancellationToken),
            DrawingAction.AuxiliaryView => runner.RunAsync(tool, act, c => DrawingService.AuxiliaryView(c, view, line, position), cancellationToken),
            DrawingAction.CuttingPlane => runner.RunAsync(tool, act, c => DrawingService.AddCuttingPlane(c, view, line, flip, name), cancellationToken),
            DrawingAction.SectionView => runner.RunAsync(tool, act, c => DrawingService.SectionView(c, view, line, flip, position), cancellationToken),
            DrawingAction.DetailView => runner.RunAsync(tool, act, c => DrawingService.DetailView(c, view, center, radius, scale, position), cancellationToken),
            DrawingAction.MoveView => runner.RunAsync(tool, act, c => DrawingService.MoveView(c, view, position, delta), cancellationToken),
            DrawingAction.ScaleView => runner.RunAsync(tool, act, c => DrawingService.ScaleView(c, view, scale), cancellationToken),
            DrawingAction.RotateView => runner.RunAsync(tool, act, c => DrawingService.RotateView(c, view, angle), cancellationToken),
            DrawingAction.CropView => runner.RunAsync(tool, act, c => DrawingService.CropView(c, view, area), cancellationToken),
            DrawingAction.DeleteView => runner.RunAsync(tool, act, c => DrawingService.DeleteView(c, view), cancellationToken),
            DrawingAction.Dimension => runner.RunAsync(tool, act, c => DrawingService.AddDimension(c, view,
                Snake(ToolRunner.RequireValue(kind, "kind", act)), points, Snake(measure), position), cancellationToken),
            DrawingAction.RetrieveDimensions => runner.RunAsync(tool, act, c => DrawingService.RetrieveDimensions(c, view), cancellationToken),
            DrawingAction.Dimensions => runner.RunAsync(tool, act, c => DrawingService.Dimensions(c, document, sheet, offset, limit), cancellationToken),
            DrawingAction.Tables => runner.RunAsync(tool, act, c => DrawingService.Tables(c, document), cancellationToken),
            DrawingAction.PartsList => runner.RunAsync(tool, act, c => DrawingService.PartsList(c, view, position, balloons, style, family, members), cancellationToken),
            DrawingAction.BendTable => runner.RunAsync(tool, act, c => DrawingService.BendTable(c, view, position, style), cancellationToken),
            DrawingAction.ToleranceTable => runner.RunAsync(tool, act, c => DrawingService.ToleranceTable(c, view, position, style), cancellationToken),
            DrawingAction.FamilyTable => runner.RunAsync(tool, act, c => DrawingService.FamilyTable(c, view, position), cancellationToken),
            DrawingAction.BlockTable => runner.RunAsync(tool, act, c => DrawingService.BlockTable(c, document, sheet, position, style), cancellationToken),
            DrawingAction.HoleTable => runner.RunAsync(tool, act, c => DrawingService.HoleTable(c, view, points, position, style), cancellationToken),
            DrawingAction.Update => runner.RunAsync(tool, act, c => DrawingService.Update(c, document, view, force), cancellationToken),
            DrawingAction.Blocks => runner.RunAsync(tool, act, c => DrawingService.Blocks(c, document), cancellationToken),
            DrawingAction.CreateBlock => runner.RunAsync(tool, act, c => DrawingService.CreateBlock(c, document, name, elements, position), cancellationToken),
            DrawingAction.PlaceBlock => runner.RunAsync(tool, act, c => DrawingService.PlaceBlock(c, document, sheet, name, position, scale, angle), cancellationToken),
            DrawingAction.ImportBlock => runner.RunAsync(tool, act, c => DrawingService.ImportBlock(c, document, path), cancellationToken),
            DrawingAction.DeleteBlock => runner.RunAsync(tool, act, c => DrawingService.DeleteBlock(c, document, name), cancellationToken),
            DrawingAction.Unblock => runner.RunAsync(tool, act, c => DrawingService.Unblock(c, occurrence), cancellationToken),
            DrawingAction.UnblockAll => runner.RunAsync(tool, act, c => DrawingService.UnblockAll(c, document), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
