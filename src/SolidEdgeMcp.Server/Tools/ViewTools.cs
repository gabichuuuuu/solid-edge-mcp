using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum ViewAction
{
    Capture,
    Fit,
    Orient,
    RenderMode,
    NamedViews,
    SaveView,
    ApplyView,
    DeleteView,
    Style,
    Background,
    Camera,
    SetCamera,
    Zoom,
    Rotate,
}

public enum ViewOrientation
{
    Iso,
    Front,
    Back,
    Top,
    Bottom,
    Left,
    Right,
    Dimetric,
    Trimetric,
}

public enum BackgroundKind
{
    Solid,
    Gradient,
    Image,
}

public enum GradientShape
{
    Horizontal,
    Vertical,
    DiagonalUp,
    DiagonalDown,
    SquareSpot,
    CircularSpot,
}

[McpServerToolType]
public sealed class ViewTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_view", Title = "Solid Edge view", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("""
        Window view of a document. capture: image of the current view (width/height in pixels; path to also save it as .png/.jpg/.bmp in an allowed folder; samples = true adds the colours of its corners, edges and centre). fit: zoom to fit (drawings: the whole sheet with its title block). orient: standard view (iso, front, top...) and fit. render_mode: SeRenderModeType name (seRenderModeSmooth, seRenderModeSmoothVHL, seRenderModeVHL, seRenderModeOutline, seRenderModeWireframe, seRenderModePhong).
        named_views | save_view (name, description: the current view of a 3D window) | apply_view (name) | delete_view (name, a saved one).
        3D windows: style (name = view style to apply; without it, the current one and the list) |
        background (without kind: the current one and the images that come with Solid Edge; kind solid|gradient|image, colors = one or two '#RRGGBB', gradient horizontal|vertical|diagonal_up|..., path = image; returns the previous one, to put it back) |
        camera (eye and target in mm, up, direction, perspective, angle = field of view in degrees) | set_camera (eye, target [x,y,z] mm, up, perspective, angle; fit default true) |
        zoom (factor: 2 = twice as large) | rotate (turns the camera by angle degrees about axis [x,y,z] through the target, right-handed; default axis the camera's up).
        Changes only the display, never the model; view styles, backgrounds and saved views are kept in the document.
        Example: {"action":"background","kind":"gradient","colors":["#203040","#FFFFFF"]}.
        """)]
    public Task<CallToolResult> View(
        ViewAction action,
        [Description("Document handle; default: active document.")] string? document = null,
        [Description("For 'orient'.")] ViewOrientation? orientation = null,
        [Description("For 'render_mode'.")] string? mode = null,
        [Description("For 'capture': image width in pixels (default 1024).")] int width = 1024,
        [Description("For 'capture': image height in pixels (default 768).")] int height = 768,
        [Description("For capture: optional absolute path to keep the image; for background: the image file.")] string? path = null,
        [Description("For capture and set_camera: zoom to fit (default true; drawings: the whole sheet).")] bool fit = true,
        [Description("For capture: also return the colours (#RRGGBB) of the corners, the middle of the top and bottom edges and the centre.")] bool samples = false,
        [Description("For save_view, apply_view, delete_view: the named view; for style: the view style.")] string? name = null,
        [Description("For save_view: optional description.")] string? description = null,
        [Description("For background: kind of background.")] BackgroundKind? kind = null,
        [Description("For background: '#RRGGBB' colours (solid: one; gradient: one or two, the first at the top for horizontal and at the right for vertical).")] string[]? colors = null,
        [Description("For background with kind gradient (default horizontal).")] GradientShape? gradient = null,
        [Description("For set_camera: eye point [x,y,z] in mm.")] double[]? eye = null,
        [Description("For set_camera: point looked at [x,y,z] in mm.")] double[]? target = null,
        [Description("For set_camera: up direction [x,y,z].")] double[]? up = null,
        [Description("For set_camera: true for a perspective camera, false for a parallel one.")] bool? perspective = null,
        [Description("For set_camera: field of view of a perspective camera in degrees (default 30); for rotate: the angle in degrees.")] double? angle = null,
        [Description("For rotate: axis [x,y,z] through the target; default the up direction of the camera.")] double[]? axis = null,
        [Description("For zoom: scale factor (above 1 zooms in, below 1 zooms out).")] double? factor = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_view";
        string op = action.ToString();
        return action switch
        {
            ViewAction.Capture => runner.RunAsync(tool, op, c =>
            {
                CapturedImage image = ViewService.Capture(c, document, width, height, path, fit, samples);
                return new ToolImage(image.Data, image.MimeType, new { image.Document, image.Width, image.Height, image.SavedTo, image.Samples });
            }, cancellationToken),
            ViewAction.Fit => runner.RunAsync(tool, op, c => ViewService.Fit(c, document), cancellationToken),
            ViewAction.Orient => runner.RunAsync(tool, op, c => ViewService.Orient(c, document,
                ToolRunner.RequireValue(orientation, "orientation", "orient").ToString().ToLowerInvariant()), cancellationToken),
            ViewAction.RenderMode => runner.RunAsync(tool, op, c => ViewService.SetRenderMode(c, document,
                ToolRunner.Require(mode, "mode", "render_mode")), cancellationToken),
            ViewAction.NamedViews => runner.RunAsync(tool, op, c => ViewService.NamedViews(c, document), cancellationToken),
            ViewAction.SaveView => runner.RunAsync(tool, op, c => ViewService.SaveView(c, document, name, description), cancellationToken),
            ViewAction.ApplyView => runner.RunAsync(tool, op, c => ViewService.ApplyView(c, document, name), cancellationToken),
            ViewAction.DeleteView => runner.RunAsync(tool, op, c => ViewService.DeleteView(c, document, name), cancellationToken),
            ViewAction.Style => runner.RunAsync(tool, op, c => ViewService.Style(c, document, name), cancellationToken),
            ViewAction.Background => runner.RunAsync(tool, op, c => ViewService.Background(c, document,
                kind is null ? null : Snake(kind.Value), colors, gradient is null ? null : Snake(gradient.Value), path), cancellationToken),
            ViewAction.Camera => runner.RunAsync(tool, op, c => ViewService.GetCamera(c, document), cancellationToken),
            ViewAction.SetCamera => runner.RunAsync(tool, op, c => ViewService.SetCamera(c, document, eye, target, up, perspective, angle, fit), cancellationToken),
            ViewAction.Zoom => runner.RunAsync(tool, op, c => ViewService.Zoom(c, document,
                factor ?? throw ToolException.InvalidArgument("Action 'zoom' requires 'factor'.")), cancellationToken),
            ViewAction.Rotate => runner.RunAsync(tool, op, c => ViewService.Rotate(c, document,
                angle ?? throw ToolException.InvalidArgument("Action 'rotate' requires 'angle' (degrees)."), axis), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
