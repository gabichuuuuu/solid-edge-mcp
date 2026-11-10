// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>Solid Edge as a program: application information, add-ins, templates, recent files and customization.</summary>
[McpServerToolType]
public sealed class ProgramTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_app", Title = "Solid Edge application, add-ins and templates", Destructive = true, OpenWorld = false)]
    [Description("""
        Solid Edge as a program. info (version, edition from the licence, language, operating system, installation and template folders) |
        addins (the Add-In Manager: ProgID, description, loaded) | set_addin (addin = index, ProgID or description; connect true/false; returns the previous state) |
        templates (default template of each document type: part, sheet_metal, assembly, draft, weldment, weldment_assembly) | set_template (documentType, path of a template in an allowed folder or the Solid Edge template folder) |
        recent (the recent files list) | suspend_recent / resume_recent (Solid Edge stops and resumes updating it; documents saved with save_as are still added) | clear_recent (empties the person's list: only when asked).
        ai_drawing_data (path = a saved .dft; output = .csv, default next to it): the data Solid Edge's AI automatic drawing creation learns from (sheet, views, placement).
        numbering (the file naming rule; count > 0 hands out and uses up the next document numbers) | reset_dxf_settings (mode import | export: the DXF/DWG translation settings back to defaults; only when asked).
        Add-ins, templates and the recent files belong to the person and persist between sessions: change them only when asked.
        Example: {"action":"set_template","documentType":"part","path":"C:\\Users\\me\\Documents\\Templates\\Company.par"}.
        """)]
    public Task<CallToolResult> App(
        AppAction action,
        [Description("For set_addin: add-in index, ProgID, GUID or description (or a unique part of it).")] string? addin = null,
        [Description("For set_addin: true loads the add-in, false unloads it.")] bool? connect = null,
        [Description("For set_template: part, sheet_metal, assembly, draft, weldment or weldment_assembly.")] string? documentType = null,
        [Description("For set_template: absolute path of the template file; ai_drawing_data: of the saved drawing.")] string? path = null,
        [Description("For ai_drawing_data: the .csv file to write (allowed folders).")] string? output = null,
        [Description("For numbering: how many document numbers to hand out (0 = only read the naming rule).")] int count = 0,
        [Description("For reset_dxf_settings: import or export.")] string? mode = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_app";
        string actionName = Snake(action);
        return action switch
        {
            AppAction.Info => runner.RunAsync(tool, actionName, ApplicationService.Info, cancellationToken),
            AppAction.Addins => runner.RunAsync(tool, actionName, ApplicationService.AddIns, cancellationToken),
            AppAction.SetAddin => runner.RunAsync(tool, actionName, c => ApplicationService.SetAddIn(c, ToolRunner.Require(addin, "addin", "set_addin"),
                connect ?? throw ToolException.InvalidArgument("Action 'set_addin' requires 'connect' (true or false).")), cancellationToken),
            AppAction.Templates => runner.RunAsync(tool, actionName, ApplicationService.Templates, cancellationToken),
            AppAction.SetTemplate => runner.RunAsync(tool, actionName, c => ApplicationService.SetTemplate(c,
                ToolRunner.Require(documentType, "documentType", "set_template"), ToolRunner.Require(path, "path", "set_template")), cancellationToken),
            AppAction.Recent => runner.RunAsync(tool, actionName, ApplicationService.Recent, cancellationToken),
            AppAction.SuspendRecent => runner.RunAsync(tool, actionName, c => ApplicationService.RecentList(c, "suspend"), cancellationToken),
            AppAction.ResumeRecent => runner.RunAsync(tool, actionName, c => ApplicationService.RecentList(c, "resume"), cancellationToken),
            AppAction.AiDrawingData => runner.RunAsync(tool, actionName, c => ApplicationService.AiDrawingData(c, ToolRunner.Require(path, "path", actionName), output), cancellationToken),
            AppAction.ClearRecent => runner.RunAsync(tool, actionName, c => ApplicationService.RecentList(c, "clear"), cancellationToken),
            AppAction.Numbering => runner.RunAsync(tool, actionName, c => ApplicationService.Numbering(c, count), cancellationToken),
            AppAction.ResetDxfSettings => runner.RunAsync(tool, actionName, c => ApplicationService.ResetDxfSettings(c, ToolRunner.Require(mode, "mode", actionName)), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_customize", Title = "Customize the ribbon and keyboard shortcuts", Destructive = true, OpenWorld = false)]
    [Description("""
        Customization of Solid Edge (the Customize dialog). Ribbon themes: ribbon_themes | create_ribbon_theme (name, basedOn default the active one) | activate_ribbon_theme (theme) | remove_ribbon_theme (theme, not the active one) |
        set_ribbon_theme (theme; newName, controlSize default|small|large, controlText default|on|off).
        Ribbon contents of a theme (default the active one) and an environment (default the active one): add_tab (name, position) | add_group (tab, name, position) | add_command (tab, group, command = name or id from se_command find or se_ui ribbon, position) |
        set_item (tab, optional group and control = caption or command id; visible and/or newName) | remove_item (tab, optional group and control). Positions are 0-based, default at the end.
        The Solid Edge default theme (isDefault in ribbon_themes) is never edited: create a theme from it, edit that one and activate it.
        Keyboard: shortcuts (environment) | add_shortcut (keys such as 'Ctrl+Shift+K' or 'Alt+F5', command; replace = true to take keys already in use) | remove_shortcut (keys) | reset_shortcuts (puts back the Solid Edge shortcuts, removing the person's own: only when asked).
        These settings belong to the person and persist: change them only when asked and say how to undo it.
        Example: {"action":"add_command","theme":"Mine","environment":"Part","tab":"Home","group":"Clipboard","command":"PartToolsOptions"}.
        """)]
    public Task<CallToolResult> Customize(
        CustomizeAction action,
        [Description("Ribbon theme name or index; default: the active theme.")] string? theme = null,
        [Description("Environment name (Part, SheetMetal, Assembly, Detail...); default: the active one.")] string? environment = null,
        [Description("For create_ribbon_theme, add_tab, add_group: the new name.")] string? name = null,
        [Description("For create_ribbon_theme: theme to copy; default: the active one.")] string? basedOn = null,
        [Description("For set_ribbon_theme and set_item: new name.")] string? newName = null,
        [Description("For set_ribbon_theme: default, small or large.")] string? controlSize = null,
        [Description("For set_ribbon_theme: default, on or off (labels under the buttons).")] string? controlText = null,
        [Description("Tab name or 0-based index.")] string? tab = null,
        [Description("Group name or 0-based index.")] string? group = null,
        [Description("For set_item and remove_item: button caption or command id.")] string? control = null,
        [Description("For add_command and add_shortcut: command name or numeric id.")] string? command = null,
        [Description("For add_tab, add_group, add_command: 0-based position; default at the end.")] int? position = null,
        [Description("For set_item: show (true) or hide (false).")] bool? visible = null,
        [Description("For add_shortcut and remove_shortcut: key combination, e.g. 'Ctrl+Shift+K', 'Alt+F5', 'Ctrl+Delete'.")] string? keys = null,
        [Description("For add_shortcut: take the keys even if they already run another command.")] bool replace = false,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_customize";
        string actionName = Snake(action);
        string Tab() => ToolRunner.Require(tab, "tab", actionName);
        return action switch
        {
            CustomizeAction.RibbonThemes => runner.RunAsync(tool, actionName, CustomizationService.RibbonThemes, cancellationToken),
            CustomizeAction.CreateRibbonTheme => runner.RunAsync(tool, actionName, c => CustomizationService.CreateRibbonTheme(c,
                ToolRunner.Require(name, "name", actionName), basedOn), cancellationToken),
            CustomizeAction.ActivateRibbonTheme => runner.RunAsync(tool, actionName, c => CustomizationService.ActivateRibbonTheme(c,
                ToolRunner.Require(theme, "theme", actionName)), cancellationToken),
            CustomizeAction.RemoveRibbonTheme => runner.RunAsync(tool, actionName, c => CustomizationService.RemoveRibbonTheme(c,
                ToolRunner.Require(theme, "theme", actionName)), cancellationToken),
            CustomizeAction.SetRibbonTheme => runner.RunAsync(tool, actionName, c => CustomizationService.SetRibbonTheme(c,
                ToolRunner.Require(theme, "theme", actionName), newName, controlSize, controlText), cancellationToken),
            CustomizeAction.AddTab => runner.RunAsync(tool, actionName, c => CustomizationService.AddTab(c, theme, environment,
                ToolRunner.Require(name, "name", actionName), position), cancellationToken),
            CustomizeAction.AddGroup => runner.RunAsync(tool, actionName, c => CustomizationService.AddGroup(c, theme, environment, Tab(),
                ToolRunner.Require(name, "name", actionName), position), cancellationToken),
            CustomizeAction.AddCommand => runner.RunAsync(tool, actionName, c => CustomizationService.AddCommand(c, theme, environment, Tab(),
                ToolRunner.Require(group, "group", actionName), ToolRunner.Require(command, "command", actionName), position), cancellationToken),
            CustomizeAction.SetItem => runner.RunAsync(tool, actionName, c => CustomizationService.SetItem(c, theme, environment, Tab(),
                group, control, visible, newName), cancellationToken),
            CustomizeAction.RemoveItem => runner.RunAsync(tool, actionName, c => CustomizationService.RemoveItem(c, theme, environment, Tab(),
                group, control), cancellationToken),
            CustomizeAction.Shortcuts => runner.RunAsync(tool, actionName, c => CustomizationService.Shortcuts(c, environment), cancellationToken),
            CustomizeAction.AddShortcut => runner.RunAsync(tool, actionName, c => CustomizationService.AddShortcut(c, environment,
                ToolRunner.Require(keys, "keys", actionName), ToolRunner.Require(command, "command", actionName), replace), cancellationToken),
            CustomizeAction.RemoveShortcut => runner.RunAsync(tool, actionName, c => CustomizationService.RemoveShortcut(c, environment,
                ToolRunner.Require(keys, "keys", actionName)), cancellationToken),
            CustomizeAction.ResetShortcuts => runner.RunAsync(tool, actionName, c => CustomizationService.ResetShortcuts(c, environment), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
