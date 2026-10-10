using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum OptionAction
{
    List,
    Get,
    Set,
    Units,
    SetUnits,
}

public enum OptionScope
{
    Application,
    Document,
}

public enum UiAction
{
    Status,
    Windows,
    Activate,
    Arrange,
    State,
    Environments,
    Ribbon,
    Tab,
}

public enum ArrangeStyle
{
    Tiled,
    Horizontal,
    Vertical,
    Cascade,
}

public enum WindowStateKind
{
    Normal,
    Minimized,
    Maximized,
}

/// <summary>Solid Edge and document options, units and interface (windows, environments and ribbon).</summary>
[McpServerToolType]
public sealed class SettingsTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_options", Title = "Solid Edge options and units", Destructive = true, OpenWorld = false)]
    [Description("""
        Solid Edge options (the Solid Edge Options dialog and global settings) and document units.
        list (scope application|document, filter = text in the name) | get (name) | set (name, value of the same type as the current one; colours as '#RRGGBB'; returns the previous value to restore it) |
        units (display units of the document: type, unit, precision) | set_units (unitType e.g. distance|angle|mass|area|volume, unit e.g. millimeter|inch|degree|kilogram, precision index).
        Application options belong to the person and persist between sessions: change them only when asked. Document options (scope document) depend on the type (part, sheet metal, assembly, draft, weldment) and are in SI units (m, rad, kg/m3).
        Units only change what Solid Edge displays: the se_* tools always use mm, degrees and kg.
        Example: {"action":"get","name":"PartandAsmUndoSteps"}.
        """)]
    public Task<CallToolResult> Options(
        OptionAction action,
        [Description("application (default) or document.")] OptionScope scope = OptionScope.Application,
        [Description("Option name as listed (prefix such as seApplicationGlobal optional).")] string? name = null,
        [Description("For set: new value (bool, number, text; colours '#RRGGBB').")] JsonElement? value = null,
        [Description("For list: text contained in the option name.")] string? filter = null,
        [Description("For set_units: unit type, e.g. distance, angle, mass, area, volume.")] string? unitType = null,
        [Description("For set_units: unit name (millimeter, inch, meter, degree, radian, kilogram...) or its numeric value.")] string? unit = null,
        [Description("For set_units: precision index (distance: 0 ones, 1 tenths, 2 hundredths, 3 thousandths...).")] int? precision = null,
        [Description("Document handle; default: active document.")] string? document = null,
        int offset = 0,
        [Description("For list: maximum number of options (default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_options";
        string actionName = Snake(action);
        string where = Snake(scope);
        return action switch
        {
            OptionAction.List => runner.RunAsync(tool, actionName, c => OptionService.List(c, where, document, filter, offset, limit), cancellationToken),
            OptionAction.Get => runner.RunAsync(tool, actionName, c => OptionService.Get(c, where, document,
                ToolRunner.Require(name, "name", "get")), cancellationToken),
            OptionAction.Set => runner.RunAsync(tool, actionName, c => OptionService.Set(c, where, document,
                ToolRunner.Require(name, "name", "set"), ToolRunner.RequireValue(value, "value", "set")), cancellationToken),
            OptionAction.Units => runner.RunAsync(tool, actionName, c => OptionService.Units(c, document), cancellationToken),
            OptionAction.SetUnits => runner.RunAsync(tool, actionName, c => OptionService.SetUnits(c, document,
                ToolRunner.Require(unitType, "unitType", "set_units"), unit, precision), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_ui", Title = "Solid Edge windows, environments and ribbon", Destructive = false, OpenWorld = false)]
    [Description("""
        The Solid Edge user interface. status (main window, active environment and document, automation flags) |
        windows (document windows/tabs: handle, caption, environment, document, active, state) | activate (window = index, window handle or document handle) |
        arrange (style tiled|horizontal|vertical|cascade) | state (state normal|minimized|maximized; window optional, default the main window) |
        environments (all environments, e.g. Part, SheetMetal, Assembly, Detail, Profile) |
        ribbon (environment, default the active one: tabs with their groups; with tab = name or index: its groups and controls with their command ids, runnable with se_command run) |
        tab (tab = name or index: activate that ribbon tab of the active environment).
        Example: {"action":"ribbon","tab":"Home"}.
        """)]
    public Task<CallToolResult> Ui(
        UiAction action,
        [Description("For activate/state: window index (1-based), window handle or document handle.")] string? window = null,
        [Description("For arrange.")] ArrangeStyle? style = null,
        [Description("For state.")] WindowStateKind? state = null,
        [Description("For ribbon: environment name (see 'environments'); default: the active one.")] string? environment = null,
        [Description("For ribbon/tab: tab name or 0-based index.")] string? tab = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_ui";
        string actionName = Snake(action);
        return action switch
        {
            UiAction.Status => runner.RunAsync(tool, actionName, UiService.Status, cancellationToken),
            UiAction.Windows => runner.RunAsync(tool, actionName, UiService.Windows, cancellationToken),
            UiAction.Activate => runner.RunAsync(tool, actionName, c => UiService.Activate(c, ToolRunner.Require(window, "window", "activate")), cancellationToken),
            UiAction.Arrange => runner.RunAsync(tool, actionName, c => UiService.Arrange(c,
                Snake(style ?? throw ToolException.InvalidArgument("Action 'arrange' requires 'style'."))), cancellationToken),
            UiAction.State => runner.RunAsync(tool, actionName, c => UiService.SetState(c, window,
                Snake(state ?? throw ToolException.InvalidArgument("Action 'state' requires 'state'."))), cancellationToken),
            UiAction.Environments => runner.RunAsync(tool, actionName, UiService.Environments, cancellationToken),
            UiAction.Ribbon => runner.RunAsync(tool, actionName, c => UiService.Ribbon(c, environment, tab), cancellationToken),
            UiAction.Tab => runner.RunAsync(tool, actionName, c => UiService.ActivateTab(c, ToolRunner.Require(tab, "tab", "tab")), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
