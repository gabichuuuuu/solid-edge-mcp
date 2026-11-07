// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

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
    Format,
    Categories,
    Snapshot,
    Restore,
}

public enum OptionScope
{
    Application,
    Document,
    Drawing,
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
    Find,
    Display,
    SetDisplay,
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
        Solid Edge options (every page of the Options dialog: general, save, file locations, interface, display, colours, sketching, assembly, drawing, PDF, standards, units...) and document units.
        categories (the pages, with their number of options) | list (scope application|document|drawing, category, filter = text in the name) | get (name) |
        set (name, value of the same type as the current one; colours '#RRGGBB'; coded options such as ColorTheme also take their name: light|medium|dark|framed; folder and file options must point into the allowed folders or the Solid Edge installation, and one without a value (such as Macros) cannot be emptied again; returns the previous value) |
        snapshot (saves every option of the scope to a .json file, default in the work folder) | restore (path of a snapshot; puts back every option that differs, including changes made since; dryRun = true only lists them; run it as the snapshot was made, with or without a document open) |
        units (display units of the document: type, unit, precision) | set_units (unitType e.g. distance|angle|mass|area|volume, unit e.g. millimeter|inch|degree|kilogram, precision index) |
        format (unitType, default distance; value: a number in tool units gives the text shown, 60 -> "2.36 in"; a text such as "2.5 in" gives its value).
        Application options belong to the person and persist between sessions: change them only when asked, preferably after a snapshot. Document options (scope document) depend on the type (part, sheet metal, assembly, draft, weldment) and are in SI units (m, rad, kg/m3); scope drawing holds what a draft saves (DVProjectionAngle, drawing view defaults, grid, DVEdgeStyleName[HiddenEdge]...) and its sketching buttons (MaintainRelationships, DisplayAlignmentIndicator, DisplayRelationshipHandles, LocateBackgroundSheetGeometry, MaintainAlignmentSet).
        Units only change what Solid Edge displays: the se_* tools always use mm, degrees and kg (format converts).
        Example: {"action":"list","category":"save"}; {"action":"set","name":"ColorTheme","value":"dark"}.
        """)]
    public Task<CallToolResult> Options(
        OptionAction action,
        [Description("application (default), document, or drawing (preferences saved in a draft).")] OptionScope scope = OptionScope.Application,
        [Description("Option name as listed (prefix such as seApplicationGlobal optional).")] string? name = null,
        [Description("For set: new value (bool, number, text; colours '#RRGGBB'). For format: a number in tool units, or a text with its unit.")] JsonElement? value = null,
        [Description("For list: text contained in the option name.")] string? filter = null,
        [Description("For list (application scope): category key from 'categories', e.g. save, colors, interface, file_locations.")] string? category = null,
        [Description("For snapshot (optional) and restore: absolute path of the .json snapshot in an allowed folder.")] string? path = null,
        [Description("For restore: only list what would change.")] bool dryRun = false,
        [Description("For set_units and format: unit type, e.g. distance, angle, mass, area, volume.")] string? unitType = null,
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
            OptionAction.List => runner.RunAsync(tool, actionName, c => OptionService.List(c, where, document, filter, category, offset, limit), cancellationToken),
            OptionAction.Categories => runner.RunAsync(tool, actionName, OptionService.Categories, cancellationToken),
            OptionAction.Snapshot => runner.RunAsync(tool, actionName, c => OptionService.Snapshot(c, where, document, path), cancellationToken),
            OptionAction.Restore => runner.RunAsync(tool, actionName, c => OptionService.Restore(c, document,
                ToolRunner.Require(path, "path", "restore"), dryRun), cancellationToken),
            OptionAction.Get => runner.RunAsync(tool, actionName, c => OptionService.Get(c, where, document,
                ToolRunner.Require(name, "name", "get")), cancellationToken),
            OptionAction.Set => runner.RunAsync(tool, actionName, c => OptionService.Set(c, where, document,
                ToolRunner.Require(name, "name", "set"), ToolRunner.RequireValue(value, "value", "set")), cancellationToken),
            OptionAction.Units => runner.RunAsync(tool, actionName, c => OptionService.Units(c, document), cancellationToken),
            OptionAction.SetUnits => runner.RunAsync(tool, actionName, c => OptionService.SetUnits(c, document,
                ToolRunner.Require(unitType, "unitType", "set_units"), unit, precision), cancellationToken),
            OptionAction.Format => runner.RunAsync(tool, actionName, c => OptionService.FormatUnits(c, document, unitType ?? "distance",
                ToolRunner.RequireValue(value, "value", "format")), cancellationToken),
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
        tab (tab = name or index: activate that ribbon tab of the active environment) |
        find (query = text of a button as this Solid Edge shows it, in any language or theme: the tabs, groups and menus that have it, with its command id for se_command run) |
        display (status bar, edge bar, full screen, start-up screen, recent files, colour theme and scheme, language, main window geometry, ribbon theme) |
        set_display (name = status_bar|edge_bar|full_screen|startup_screen|recent_files (true/false) | recent_files_count | theme (light|medium|dark|framed) | left|top|width|height (pixels, window in the normal state); value; returns the previous value). These settings belong to the person and persist: change them only when asked.
        Example: {"action":"ribbon","tab":"Home"}; {"action":"find","query":"Hole"}; {"action":"set_display","name":"theme","value":"dark"}.
        """)]
    public Task<CallToolResult> Ui(
        UiAction action,
        [Description("For activate/state: window index (1-based), window handle or document handle.")] string? window = null,
        [Description("For arrange.")] ArrangeStyle? style = null,
        [Description("For state.")] WindowStateKind? state = null,
        [Description("For ribbon: environment name (see 'environments'); default: the active one.")] string? environment = null,
        [Description("For ribbon/tab: tab name or 0-based index.")] string? tab = null,
        [Description("For find: text of the button (any part of it, any case).")] string? query = null,
        [Description("For find: maximum number of buttons (default 50).")] int limit = 50,
        [Description("For set_display: the setting (status_bar, edge_bar, full_screen, startup_screen, recent_files, recent_files_count, theme, left, top, width, height).")] string? name = null,
        [Description("For set_display: new value (true/false, a number, or for theme light|medium|dark|framed).")] JsonElement? value = null,
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
            UiAction.Find => runner.RunAsync(tool, actionName, c => UiService.FindControl(c, environment, ToolRunner.Require(query, "query", "find"), limit), cancellationToken),
            UiAction.Display => runner.RunAsync(tool, actionName, UiService.Display, cancellationToken),
            UiAction.SetDisplay => runner.RunAsync(tool, actionName, c => UiService.SetDisplay(c, ToolRunner.Require(name, "name", "set_display"),
                ToolRunner.RequireValue(value, "value", "set_display")), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
