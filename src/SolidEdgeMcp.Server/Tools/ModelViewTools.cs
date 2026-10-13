// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum ModelViewAction
{
    List,
    Add,
    Set,
    Include,
    Exclude,
    Apply,
    Delete,
}

public enum ModelViewOrientation
{
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom,
    Dimetric,
    Isometric,
    Trimetric,
}

public enum ModelViewRender
{
    None,
    VisibleEdges,
    VisibleAndHiddenEdges,
    Shaded,
    ShadedWithVisibleEdges,
}

/// <summary>PMI model views of parts, sheet metal and assemblies.</summary>
[McpServerToolType]
public sealed class ModelViewTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_model_view", Title = "PMI model views", Destructive = false, OpenWorld = false)]
    [Description("""
        PMI model views of a part, sheet metal or assembly: a named orientation with a render mode and the section views, PMI dimensions and annotations it shows. Drawings place them (se_drawing base_view modelView) and se_publish pdf3d modelViews puts them all in a 3D PDF.
        list (name, render, pdf, section captions, dimension and annotation counts) |
        add (orientation = front | back | left | right | top | bottom | dimetric | isometric | trimetric, or none = the window's current orientation with 'name'; render; name; replace = an existing view of that orientation) |
        set (view; name, render, pdf) | include, exclude (view, items = section view handles from se_section_view, or PMI dimension and annotation handles from se_pmi; the counts are read back) |
        apply (view: the window shows it) | delete (view).
        view = handle or name. Example: {"action":"add","orientation":"isometric","name":"Cut"}; {"action":"include","view":"Cut","items":["sectionview:7"]}.
        """)]
    public Task<CallToolResult> ModelView(
        ModelViewAction action,
        [Description("set, include, exclude, apply, delete: model view handle or name.")] string? view = null,
        [Description("add: standard orientation; omitted = the current orientation of the window (needs 'name').")] ModelViewOrientation? orientation = null,
        [Description("add, set: how the view is drawn (default shaded_with_visible_edges).")] ModelViewRender? render = null,
        [Description("add, set: name of the view.")] string? name = null,
        [Description("add: replace the view that has this orientation already.")] bool replace = false,
        [Description("set: the view's 3D PDF flag (as in Solid Edge; se_publish takes every model view whatever the flag).")] bool? pdf = null,
        [Description("include, exclude: section view, PMI dimension or annotation handles.")] string[]? items = null,
        [Description("Part, sheet metal or assembly document handle; default: the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_model_view";
        string act = Snake(action);
        string? look = render is { } r ? Snake(r) : null;
        return action switch
        {
            ModelViewAction.List => runner.RunAsync(tool, act, c => ModelViewService.List(c, document), cancellationToken),
            ModelViewAction.Add => runner.RunAsync(tool, act, c => ModelViewService.Add(c, document, orientation is { } o ? Snake(o) : null, name, look, replace), cancellationToken),
            ModelViewAction.Set => runner.RunAsync(tool, act, c => ModelViewService.Set(c, document, view, name, look, pdf), cancellationToken),
            ModelViewAction.Include => runner.RunAsync(tool, act, c => ModelViewService.Items(c, document, view, items, true), cancellationToken),
            ModelViewAction.Exclude => runner.RunAsync(tool, act, c => ModelViewService.Items(c, document, view, items, false), cancellationToken),
            ModelViewAction.Apply => runner.RunAsync(tool, act, c => ModelViewService.Apply(c, document, view), cancellationToken),
            ModelViewAction.Delete => runner.RunAsync(tool, act, c => ModelViewService.Delete(c, document, view), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
