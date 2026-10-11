using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

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

[McpServerToolType]
public sealed class ViewTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_view", Title = "Solid Edge view", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("""
        Window view of a document. capture: returns an image of the current view (width/height in pixels, optional path to also save it as .png/.jpg/.bmp in an allowed folder). fit: zoom to fit (drawings: the whole sheet with its title block). orient: standard view (iso, front, top...) and fit. render_mode: set the display mode (SeRenderModeType name: seRenderModeSmooth, seRenderModeSmoothVHL, seRenderModeVHL, seRenderModeOutline, seRenderModeWireframe, seRenderModePhong).
        named_views: list the named views (the six standard ones and saved ones) with their view direction and up vector |
        save_view: save the current view of a part, sheet metal or assembly window under 'name' (optional description) |
        apply_view: show a named view (standard or saved) | delete_view: delete a saved named view.
        Changes only the display, never the model (saved views are stored in the document).
        """)]
    public Task<CallToolResult> View(
        ViewAction action,
        [Description("Document handle; default: active document.")] string? document = null,
        [Description("For 'orient'.")] ViewOrientation? orientation = null,
        [Description("For 'render_mode'.")] string? mode = null,
        [Description("For 'capture': image width in pixels (default 1024).")] int width = 1024,
        [Description("For 'capture': image height in pixels (default 768).")] int height = 768,
        [Description("For 'capture': optional absolute path to keep the image.")] string? path = null,
        [Description("For 'capture': zoom to fit first (default true; drawings: the whole sheet).")] bool fit = true,
        [Description("For save_view, apply_view, delete_view: the named view.")] string? name = null,
        [Description("For save_view: optional description.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_view";
        string op = action.ToString();
        return action switch
        {
            ViewAction.Capture => runner.RunAsync(tool, op, c =>
            {
                CapturedImage image = ViewService.Capture(c, document, width, height, path, fit);
                return new ToolImage(image.Data, image.MimeType, new { image.Document, image.Width, image.Height, image.SavedTo });
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
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
