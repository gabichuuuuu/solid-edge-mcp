// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum InternalComponentAction
{
    List,
    Create,
    Delete,
    Publish,
}

public enum InternalComponentKind
{
    Part,
    SheetMetal,
    Assembly,
}

/// <summary>Internal components of an assembly: kept inside the assembly file until they are published as files.</summary>
[McpServerToolType]
public sealed class InternalComponentTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_internal_component", Title = "Internal components", Destructive = true, OpenWorld = false)]
    [Description("""
        Internal components of an assembly (Create Internal Component, Publish Internal Components): parts, sheet metal parts and subassemblies kept
        inside the assembly file, with no file of their own, until they are published.
        list (each component with its occurrence) | create (name without extension; kind part | sheet_metal | assembly; ground: grounded in place, default true):
        an empty component placed as an occurrence; model it in Solid Edge or after publishing it (se_documents open) |
        delete (name: the component and its occurrence) | publish (folder: every internal component written as a file there, its occurrence then refers
        to that file; overwrite). Save the assembly with se_documents to keep the components.
        Example: {"action":"create","name":"Bracket","kind":"part"}; {"action":"publish","folder":"D:\\CAD\\Parts"}.
        """)]
    public Task<CallToolResult> InternalComponent(
        InternalComponentAction action,
        [Description("create: the new component's name (no extension); delete: a name from list.")] string? name = null,
        [Description("create: part (default), sheet_metal or assembly.")] InternalComponentKind kind = InternalComponentKind.Part,
        [Description("create: ground the new component in place.")] bool ground = true,
        [Description("publish: absolute folder for the files (allowed folders).")] string? folder = null,
        [Description("publish: replace files of the same name in the folder.")] bool overwrite = false,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_internal_component";
        string act = Snake(action);
        return action switch
        {
            InternalComponentAction.List => runner.RunAsync(tool, act, c => InternalComponentService.List(c, document), cancellationToken),
            InternalComponentAction.Create => runner.RunAsync(tool, act, c => InternalComponentService.Create(c, document, name, Snake(kind), ground), cancellationToken),
            InternalComponentAction.Delete => runner.RunAsync(tool, act, c => InternalComponentService.Delete(c, document, name), cancellationToken),
            InternalComponentAction.Publish => runner.RunAsync(tool, act, c => InternalComponentService.Publish(c, document, folder, overwrite), cancellationToken,
                precheck: folder is null ? null : () => guard.Check(folder, "folder")),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
