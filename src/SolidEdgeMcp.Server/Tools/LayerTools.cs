// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum LayerAction
{
    List,
    Create,
    Activate,
    Set,
    Delete,
    Assign,
}

/// <summary>Layers of models (sketch geometry) and of drawing sheets (views, dimensions, annotations, 2D geometry).</summary>
[McpServerToolType]
public sealed class LayerTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_layer", Title = "Layers", Destructive = true, OpenWorld = false)]
    [Description("""
        Layers of a part, sheet metal or assembly (they hold sketch geometry) or of a drawing sheet (drawing views, dimensions, annotations, 2D geometry). New elements go to the active layer.
        list (name, active, shown, locatable, empty) | create (name [+ activate]) | activate (name) | set (name + shown and/or locatable; the active layer cannot be hidden) |
        delete (name; only an empty layer that is not active, its elements are never deleted) | assign (objects + name: move elements to an existing layer).
        Drawings: the layer names are shared by all the sheets and 'empty' counts every sheet; the active layer, shown and locatable are per sheet ('sheet' = handle or name; default: the active sheet; another sheet is activated for the call and the active one restored).
        Example: {"action":"create","name":"Construction","activate":true}.
        """)]
    public Task<CallToolResult> Layer(
        LayerAction action,
        [Description("Layer name (all actions except list).")] string? name = null,
        [Description("create: also make it the active layer.")] bool activate = false,
        [Description("set: show (true) or hide (false) the layer.")] bool? shown = null,
        [Description("set: whether its elements can be selected.")] bool? locatable = null,
        [Description("assign: handles of 2D elements, drawing views, dimensions or annotations.")] string[]? objects = null,
        [Description("Drawings: sheet handle or name; default: the active sheet.")] string? sheet = null,
        [Description("Document handle; default: the active document (or the document of 'objects').")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_layer";
        string act = Snake(action);
        return action switch
        {
            LayerAction.List => runner.RunAsync(tool, act, c => LayerService.List(c, document, sheet), cancellationToken),
            LayerAction.Create => runner.RunAsync(tool, act, c => LayerService.Create(c, document, sheet, name, activate), cancellationToken),
            LayerAction.Activate => runner.RunAsync(tool, act, c => LayerService.Activate(c, document, sheet, name), cancellationToken),
            LayerAction.Set => runner.RunAsync(tool, act, c => LayerService.Set(c, document, sheet, name, shown, locatable), cancellationToken),
            LayerAction.Delete => runner.RunAsync(tool, act, c => LayerService.Delete(c, document, sheet, name), cancellationToken),
            LayerAction.Assign => runner.RunAsync(tool, act, c => LayerService.Assign(c, document, sheet, objects, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
