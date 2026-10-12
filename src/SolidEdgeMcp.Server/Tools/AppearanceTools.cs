// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum AppearanceAction
{
    List,
    Create,
    Delete,
    Get,
    Apply,
    Reset,
}

/// <summary>Colours (face styles) of parts, sheet metal and assembly components.</summary>
[McpServerToolType]
public sealed class AppearanceTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_appearance", Title = "Colours and face styles", Destructive = true, OpenWorld = false)]
    [Description("""
        Colours of parts, sheet metal and assemblies, through the face styles of the document. Colours are '#RRGGBB' with an opacity from 0 (transparent) to 1 (opaque).
        list (styles of the document with their colour and opacity; filter = text in the name) | create (name + color [+ opacity]) | delete (name; refused while in use) |
        get (targets: the style each one has) | apply (targets + style = a style name, or color [+ opacity]: a style named after the colour, created if missing) | reset (targets: remove their own style).
        targets: a part or sheet metal document (its body; the default is the active document), body, faces (se_geometry faces), features (se_feature_tree) or assembly occurrences (se_assembly list; the style must exist in the assembly).
        A target without a style of its own (null) shows the one of its feature, body or part. A feature style replaces the face styles of its faces; Solid Edge cannot remove a feature style through the API (apply another one).
        Example: {"action":"apply","targets":["face:12"],"color":"#FF8000"}.
        """)]
    public Task<CallToolResult> Appearance(
        AppearanceAction action,
        [Description("Handles to read, colour or reset (see above). Default: the body of the active part.")] string[]? targets = null,
        [Description("apply: name of an existing style (see list).")] string? style = null,
        [Description("create/apply: colour '#RRGGBB', e.g. '#FF8000'.")] string? color = null,
        [Description("create/apply with color: 0 (transparent) to 1 (opaque, default).")] double? opacity = null,
        [Description("create/delete: style name.")] string? name = null,
        [Description("list: text contained in the style name.")] string? filter = null,
        int offset = 0,
        int limit = 100,
        [Description("Document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_appearance";
        string act = Snake(action);
        return action switch
        {
            AppearanceAction.List => runner.RunAsync(tool, act, c => AppearanceService.List(c, document, filter, offset, limit), cancellationToken),
            AppearanceAction.Create => runner.RunAsync(tool, act, c => AppearanceService.Create(c, document, name, color, opacity), cancellationToken),
            AppearanceAction.Delete => runner.RunAsync(tool, act, c => AppearanceService.Delete(c, document, name), cancellationToken),
            AppearanceAction.Get => runner.RunAsync(tool, act, c => AppearanceService.Get(c, document, targets), cancellationToken),
            AppearanceAction.Apply => runner.RunAsync(tool, act, c => AppearanceService.Apply(c, document, targets, style, color, opacity), cancellationToken),
            AppearanceAction.Reset => runner.RunAsync(tool, act, c => AppearanceService.Reset(c, document, targets), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
