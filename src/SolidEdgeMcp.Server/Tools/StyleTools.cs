// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum StyleAction
{
    Types,
    List,
    Get,
    Set,
    Activate,
    Add,
    Remove,
    Import,
}

public enum StyleType
{
    Dimension,
    Text,
    Character,
    Line,
    Fill,
    Hatch,
    Dash,
    DrawingView,
    Table,
    Frame,
    View,
    Face,
}

/// <summary>Styles of a document: the Styles dialog of drafts and the view and face styles of 3D documents.</summary>
[McpServerToolType]
public sealed class StyleTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_styles", Title = "Document styles", Destructive = true, OpenWorld = false)]
    [Description("""
        Styles of a document, as in its Styles dialog. types (the style types the document has, how many and the active one) |
        list (type; filter, offset, limit) | get (type, name: every property and its value) | set (type, name, property, value; returns the previous value) |
        activate (type, name: the style new elements take; for view, the style of the window) | add (type, name, basedOn default the active one) |
        remove (type, name; not the active one) | import (type, from = handle of another open document; replace = true overwrites styles with the same name).
        Types: dimension, text, character, line, fill, hatch, dash, drawing_view, table, frame (drafts); view and face (parts, sheet metal, assemblies).
        Values are in API units: lengths in metres (a 3.5 mm text height is 0.0035), angles in radians; colours '#RRGGBB'; enumerations by name or its end (e.g. 'gradient').
        Styles are saved with the document and every element that uses one changes with it: change the person's styles only when asked.
        Example: {"action":"set","type":"dimension","name":"ISO","property":"Height","value":0.005}.
        """)]
    public Task<CallToolResult> Styles(
        StyleAction action,
        [Description("Style type (not needed for 'types').")] StyleType? type = null,
        [Description("Style name as listed.")] string? name = null,
        [Description("For set: property name as given by get.")] string? property = null,
        [Description("For set: new value (number in API units, text, true/false, '#RRGGBB', enumeration name).")] JsonElement? value = null,
        [Description("For add: style to copy; default the active one.")] string? basedOn = null,
        [Description("For import: handle of the open document to copy the styles from.")] string? from = null,
        [Description("For import: overwrite the styles that have the same name.")] bool replace = false,
        [Description("For list: text contained in the name.")] string? filter = null,
        [Description("Document handle; default: active document.")] string? document = null,
        int offset = 0,
        [Description("For list: maximum number of styles (default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_styles";
        string actionName = Snake(action);
        string Type() => Snake(ToolRunner.RequireValue(type, "type", actionName));
        string Name() => ToolRunner.Require(name, "name", actionName);
        return action switch
        {
            StyleAction.Types => runner.RunAsync(tool, actionName, c => StyleService.Types(c, document), cancellationToken),
            StyleAction.List => runner.RunAsync(tool, actionName, c => StyleService.List(c, document, Type(), filter, offset, limit), cancellationToken),
            StyleAction.Get => runner.RunAsync(tool, actionName, c => StyleService.Get(c, document, Type(), Name()), cancellationToken),
            StyleAction.Set => runner.RunAsync(tool, actionName, c => StyleService.Set(c, document, Type(), Name(),
                ToolRunner.Require(property, "property", actionName), ToolRunner.RequireValue(value, "value", actionName)), cancellationToken),
            StyleAction.Activate => runner.RunAsync(tool, actionName, c => StyleService.Activate(c, document, Type(), Name()), cancellationToken),
            StyleAction.Add => runner.RunAsync(tool, actionName, c => StyleService.Add(c, document, Type(), Name(), basedOn), cancellationToken),
            StyleAction.Remove => runner.RunAsync(tool, actionName, c => StyleService.Remove(c, document, Type(), Name()), cancellationToken),
            StyleAction.Import => runner.RunAsync(tool, actionName, c => StyleService.Import(c, document, Type(),
                ToolRunner.Require(from, "from", actionName), replace), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
