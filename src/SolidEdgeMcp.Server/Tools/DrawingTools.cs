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
    Views,
    BaseView,
    ProjectedView,
    AuxiliaryView,
    SectionView,
    DetailView,
    MoveView,
    ScaleView,
    DeleteView,
    Dimension,
    RetrieveDimensions,
    Dimensions,
    PartsList,
    Update,
}

public enum DimensionKind
{
    Length,
    Distance,
    Diameter,
    Radius,
    Angle,
}

public enum MeasureDirection
{
    Aligned,
    Horizontal,
    Vertical,
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

/// <summary>Planos: hojas, vistas, cotas, lista de piezas y actualización.</summary>
[McpServerToolType]
public sealed class DrawingTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_drawing", Title = "Drawings (draft)", Destructive = true, OpenWorld = false)]
    [Description("""
        Drawings of a draft document (create it with se_documents new kind=draft). Sheet positions in mm from the lower-left corner of the sheet, referring to the centre of a view; model points in mm (part or assembly coordinates).
        Sheets: sheets (also lists the template backgrounds) | add_sheet (name, paper + portrait or width/height; the background of that size is applied) | setup_sheet (rename, resize, background) | activate_sheet | delete_sheet.
        Views: views (of a sheet) | base_view (path of a saved .par/.psm/.asm, or model = handle of an open saved model; orientation; scale 0.5 = 1:2, default the largest standard scale that fits; flat = sheet metal flat pattern) |
        projected_view (view + direction up/down/left/right; diagonals give a pictorial view) | auxiliary_view (view + line: 2 model points along the edge to look square at) |
        section_view (view + line: 2 model points of the cutting line, extended across the view; flip = look from the other side) | detail_view (view + center model point, radius mm of model, scale default 2x) |
        move_view (position or delta) | scale_view | delete_view. Without position, derived views are placed beside their source view.
        dimension (view + kind + points: model points that pick what to measure, like clicking the edges: length = a point on a straight edge; distance = two points, each a vertex/circle centre (measured exactly there) or a point on an edge (a horizontal/vertical edge is measured square to it), measure aligned/horizontal/vertical; diameter/radius = the centre or a point of a circle/arc; angle = a point on each of two edges. Linear dimensions go outside the outline, stacked; position [x, y] puts the dimension line there) |
        retrieve_dimensions (view: the model dimensions parallel to it, with centre marks) | dimensions (of a sheet) | parts_list (assembly view; balloons) | update (views and title block text; force = full update).
        Title block: set the draft's properties with se_properties, then 'update'. Export: se_export of the draft to .pdf/.dxf/.dwg. See it: se_view capture.
        """)]
    public Task<CallToolResult> Drawing(
        DrawingAction action,
        [Description("Sheet handle or name (default: the active sheet).")] string? sheet = null,
        [Description("Drawing view handle: the source of a projected/auxiliary/section/detail view, or the view to move, scale, delete, dimension, list or update.")] string? view = null,
        [Description("base_view: absolute path of the saved model file (allowed folders).")] string? path = null,
        [Description("base_view: handle of an open, saved model document instead of 'path'.")] string? model = null,
        [Description("base_view: view orientation (default front; top for a flat pattern). top_front_right is the usual isometric.")] DrawingOrientation? orientation = null,
        [Description("projected_view: side of the source view where the new view goes.")] FoldDirection? direction = null,
        [Description("base_view/detail_view/scale_view: scale factor (1 = 1:1, 0.5 = 1:2, 2 = 2:1).")] double? scale = null,
        [Description("View centre [x, y] in mm on the sheet (parts_list: its anchor corner).")] double[]? position = null,
        [Description("move_view: displacement [dx, dy] in mm on the sheet.")] double[]? delta = null,
        [Description("auxiliary_view/section_view: two model points [x1, y1, z1, x2, y2, z2] in mm.")] double[]? line = null,
        [Description("detail_view: model point [x, y, z] in mm at the centre of the detail.")] double[]? center = null,
        [Description("detail_view: radius of the detail circle in mm of the model.")] double? radius = null,
        [Description("dimension: what to measure.")] DimensionKind? kind = null,
        [Description("dimension: model points [x1, y1, z1, ...] in mm that pick the edges, vertices or circles in the view.")] double[]? points = null,
        [Description("dimension (distance): aligned (true distance, default), horizontal or vertical on the sheet.")] MeasureDirection measure = MeasureDirection.Aligned,
        [Description("section_view: look from the other side of the cutting line.")] bool flip = false,
        [Description("base_view: flat pattern of a sheet metal part.")] bool flat = false,
        [Description("add_sheet/setup_sheet: sheet name (setup_sheet renames).")] string? name = null,
        [Description("add_sheet/setup_sheet: standard paper size (landscape unless portrait).")] PaperSize? paper = null,
        [Description("add_sheet/setup_sheet: portrait paper.")] bool portrait = false,
        [Description("add_sheet/setup_sheet: custom sheet width in mm (with height).")] double? width = null,
        [Description("add_sheet/setup_sheet: custom sheet height in mm (with width).")] double? height = null,
        [Description("add_sheet/setup_sheet: background sheet name (frame and title block) from 'sheets'.")] string? background = null,
        [Description("parts_list: add balloons to the view (default true).")] bool balloons = true,
        [Description("parts_list: saved parts list setting name (default: the document default).")] string? style = null,
        [Description("update: full (forced) update of every view.")] bool force = false,
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
            DrawingAction.AddSheet => runner.RunAsync(tool, act, c => DrawingService.AddSheet(c, document, name, size, portrait, width, height, background), cancellationToken),
            DrawingAction.SetupSheet => runner.RunAsync(tool, act, c => DrawingService.SetupSheet(c, document, sheet, name, size, portrait, width, height, background), cancellationToken),
            DrawingAction.ActivateSheet => runner.RunAsync(tool, act, c => DrawingService.ActivateSheet(c, document, sheet), cancellationToken),
            DrawingAction.DeleteSheet => runner.RunAsync(tool, act, c => DrawingService.DeleteSheet(c, document, sheet), cancellationToken),
            DrawingAction.Views => runner.RunAsync(tool, act, c => DrawingService.Views(c, document, sheet), cancellationToken),
            DrawingAction.BaseView => runner.RunAsync(tool, act, c => DrawingService.BaseView(c, document, sheet, path, model, orient, scale, position, flat), cancellationToken),
            DrawingAction.ProjectedView => runner.RunAsync(tool, act, c => DrawingService.ProjectedView(c, view,
                Snake(ToolRunner.RequireValue(direction, "direction", act)), position), cancellationToken),
            DrawingAction.AuxiliaryView => runner.RunAsync(tool, act, c => DrawingService.AuxiliaryView(c, view, line, position), cancellationToken),
            DrawingAction.SectionView => runner.RunAsync(tool, act, c => DrawingService.SectionView(c, view, line, flip, position), cancellationToken),
            DrawingAction.DetailView => runner.RunAsync(tool, act, c => DrawingService.DetailView(c, view, center, radius, scale, position), cancellationToken),
            DrawingAction.MoveView => runner.RunAsync(tool, act, c => DrawingService.MoveView(c, view, position, delta), cancellationToken),
            DrawingAction.ScaleView => runner.RunAsync(tool, act, c => DrawingService.ScaleView(c, view, scale), cancellationToken),
            DrawingAction.DeleteView => runner.RunAsync(tool, act, c => DrawingService.DeleteView(c, view), cancellationToken),
            DrawingAction.Dimension => runner.RunAsync(tool, act, c => DrawingService.AddDimension(c, view,
                Snake(ToolRunner.RequireValue(kind, "kind", act)), points, Snake(measure), position), cancellationToken),
            DrawingAction.RetrieveDimensions => runner.RunAsync(tool, act, c => DrawingService.RetrieveDimensions(c, view), cancellationToken),
            DrawingAction.Dimensions => runner.RunAsync(tool, act, c => DrawingService.Dimensions(c, document, sheet, offset, limit), cancellationToken),
            DrawingAction.PartsList => runner.RunAsync(tool, act, c => DrawingService.PartsList(c, view, position, balloons, style), cancellationToken),
            DrawingAction.Update => runner.RunAsync(tool, act, c => DrawingService.Update(c, document, view, force), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
