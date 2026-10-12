using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum DrawingViewAction
{
    Display,
    SetDisplay,
    Breaks,
    AddBreak,
    SetBroken,
    RemoveBreak,
    To2d,
}

public enum BreakDirection
{
    Vertical,
    Horizontal,
}

public enum BreakShape
{
    Straight,
    Cylindrical,
    ShortBreak,
    LongBreak,
    ShortCurvedBreak,
}

/// <summary>Display of a drawing view (its Properties dialog), broken views and conversion to 2D.</summary>
[McpServerToolType]
public sealed class DrawingViewTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_drawing_view", Title = "Drawing view display", Destructive = true, OpenWorld = false)]
    [Description("""
        Display of a drawing view (handle from se_drawing views), as in its Properties dialog.
        display (view: its properties, its model members (parts, assemblies, reference planes) with the member properties
        that can be set, and 'edges' = segments drawn by line style, e.g. {"Visible":4,"Hidden":2}) |
        set_display (view, property, value: a view property such as Shading or DisplayCaption, read back; or a model member
        property such as ShowHiddenEdges, ShowTangentEdges, DisplayAsReference, DisplayType (seHidePart hides a part) or
        VisibleEdgeStyleName, set on the top member (it reaches the parts of an assembly) or on 'member' = number or name
        from display; Solid Edge does not report member values, so the edges before and after show the effect; Defaults_*
        only apply to members added later) | breaks | add_break (direction of the break lines; from and to = their
        positions on the sheet in mm; shape, gap) | set_broken (broken = true shows the breaks, false the whole view) |
        remove_break (index) | to2d (turns the view into plain 2D elements: it no longer follows the model).
        Values: booleans, numbers in API units (metres), colours '#RRGGBB', enumerations by name or its end.
        Example: {"action":"set_display","view":"drawing_view:3","property":"ShowHiddenEdges","value":false}.
        """)]
    public Task<CallToolResult> DrawingView(
        DrawingViewAction action,
        [Description("Drawing view handle (se_drawing views).")] string? view = null,
        [Description("set_display: property name as listed by display.")] string? property = null,
        [Description("set_display: new value.")] JsonElement? value = null,
        [Description("set_display: model member number (from display) or component/file name; default the top members.")] string? member = null,
        [Description("add_break: vertical lines (they shorten the view horizontally) or horizontal lines.")] BreakDirection? direction = null,
        [Description("add_break: position in mm on the sheet of the first break line (x for vertical lines, y for horizontal).")] double? from = null,
        [Description("add_break: position in mm on the sheet of the second break line.")] double? to = null,
        [Description("add_break: shape of the break lines (default straight).")] BreakShape? shape = null,
        [Description("add_break: gap in mm left between the two halves on the sheet.")] double? gap = null,
        [Description("set_broken: true shows the breaks, false shows the whole view.")] bool? broken = null,
        [Description("remove_break: break number from breaks (1-based).")] int? index = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_drawing_view";
        string act = Snake(action);
        return action switch
        {
            DrawingViewAction.Display => runner.RunAsync(tool, act, c => DrawingService.ViewDisplay(c, view, offset, limit), cancellationToken),
            DrawingViewAction.SetDisplay => runner.RunAsync(tool, act, c => DrawingService.SetViewDisplay(c, view, property, value, member), cancellationToken),
            DrawingViewAction.Breaks => runner.RunAsync(tool, act, c => DrawingService.Breaks(c, view), cancellationToken),
            DrawingViewAction.AddBreak => runner.RunAsync(tool, act, c => DrawingService.AddBreak(c, view,
                Snake(ToolRunner.RequireValue(direction, "direction", act)), ToolRunner.RequireValue(from, "from", act),
                ToolRunner.RequireValue(to, "to", act), shape is { } s ? Snake(s) : null, gap), cancellationToken),
            DrawingViewAction.SetBroken => runner.RunAsync(tool, act, c => DrawingService.SetBroken(c, view,
                ToolRunner.RequireValue(broken, "broken", act)), cancellationToken),
            DrawingViewAction.RemoveBreak => runner.RunAsync(tool, act, c => DrawingService.RemoveBreak(c, view,
                ToolRunner.RequireValue(index, "index", act)), cancellationToken),
            DrawingViewAction.To2d => runner.RunAsync(tool, act, c => DrawingService.To2d(c, view), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
