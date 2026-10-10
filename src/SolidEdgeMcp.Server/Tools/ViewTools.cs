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
        Changes only the display, never the model.
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
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_view";
        string name = action.ToString();
        return action switch
        {
            ViewAction.Capture => runner.RunAsync(tool, name, c =>
            {
                CapturedImage image = ViewService.Capture(c, document, width, height, path, fit);
                return new ToolImage(image.Data, image.MimeType, new { image.Document, image.Width, image.Height, image.SavedTo });
            }, cancellationToken),
            ViewAction.Fit => runner.RunAsync(tool, name, c => ViewService.Fit(c, document), cancellationToken),
            ViewAction.Orient => runner.RunAsync(tool, name, c => ViewService.Orient(c, document,
                ToolRunner.RequireValue(orientation, "orientation", "orient").ToString().ToLowerInvariant()), cancellationToken),
            ViewAction.RenderMode => runner.RunAsync(tool, name, c => ViewService.SetRenderMode(c, document,
                ToolRunner.Require(mode, "mode", "render_mode")), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
