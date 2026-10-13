// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum CommandAction
{
    Find,
    Run,
    Check,
}

public enum SelectionAction
{
    Get,
    Set,
    Add,
    Clear,
}

/// <summary>Export, UI commands and selection.</summary>
[McpServerToolType]
public sealed class AppTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_export", Title = "Export to neutral formats", Destructive = false, OpenWorld = false)]
    [Description("""
        Writes a copy of a document in another format; the format comes from the extension of 'path' (absolute, in an allowed folder). The document itself is not renamed.
        Part, sheet metal, assembly: .stp/.step, .igs/.iges, .x_t, .x_b, .stl, .jt, .sat, .3mf, .pdf (a page with a picture of the view; a 3D PDF is se_publish pdf3d). Draft: .pdf (the sheets chosen by the PDF options: the active one by default), .dxf, .dwg, .igs, .tif. PDF results give their pages and page sizes in mm.
        flatPattern=true (sheet metal): flat pattern as .dxf/.dwg. Images of the view: se_view capture with 'path'.
        """)]
    public Task<CallToolResult> Export(
        [Description("Absolute output path; its extension selects the format.")] string path,
        [Description("Document handle; default: active document.")] string? document = null,
        [Description("Replace the file if it exists.")] bool overwrite = false,
        [Description("Export the sheet metal flat pattern instead of the model (.dxf or .dwg).")] bool flatPattern = false,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_export", flatPattern ? "flat_pattern" : "export",
            c => ExportService.Export(c, document, path, overwrite, flatPattern), cancellationToken);

    [McpServerTool(Name = "se_command", Title = "Run a UI command", Destructive = true, OpenWorld = false)]
    [Description("""
        Solid Edge UI commands, as if the person clicked them. find (query, optional environment: Part, SheetMetal, Assembly, Detail...) lists names and ids without needing Solid Edge |
        run (command = name such as 'PartViewISOView', 'Environment.Name', or numeric id). Commands that need picks or a dialog wait for the person: prefer the specific se_* tools when they exist |
        check (command, optional environment, default the active one): whether this installation knows the command, its licence includes it and it can run now, without running it.
        """)]
    public Task<CallToolResult> Command(
        CommandAction action,
        [Description("For find: text contained in the command name.")] string? query = null,
        [Description("For find and check: environment (Part, SheetMetal, Assembly, Detail, Profile...).")] string? environment = null,
        [Description("For run and check: command name or numeric id.")] string? command = null,
        int offset = 0,
        [Description("For find: maximum number of results (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default) => action switch
        {
            CommandAction.Find => runner.RunLocalAsync("se_command", "find",
                () => Task.FromResult<object?>(CommandService.Find(ToolRunner.Require(query, "query", "find"), environment, offset, limit))),
            CommandAction.Run => runner.RunAsync("se_command", "run",
                c => CommandService.Run(c, ToolRunner.Require(command, "command", "run")), cancellationToken),
            CommandAction.Check => runner.RunAsync("se_command", "check",
                c => CommandService.Check(c, ToolRunner.Require(command, "command", "check"), environment), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    [McpServerTool(Name = "se_selection", Title = "Selection", Destructive = false, OpenWorld = false)]
    [Description("""
        Active selection in Solid Edge. get: what is selected (e.g. by the person), with handles usable in other tools |
        set: select the given handles (faces, edges, features, occurrences...) replacing the selection | add: add them | clear.
        """)]
    public Task<CallToolResult> Selection(
        SelectionAction action,
        [Description("For set/add: object handles.")] string[]? handles = null,
        int offset = 0,
        [Description("For get: maximum number of items (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_selection";
        string name = action.ToString();
        return action switch
        {
            SelectionAction.Get => runner.RunAsync(tool, name, c => SelectionService.Get(c, offset, limit), cancellationToken),
            SelectionAction.Set => runner.RunAsync(tool, name, c => SelectionService.Set(c, handles, append: false), cancellationToken),
            SelectionAction.Add => runner.RunAsync(tool, name, c => SelectionService.Set(c, handles, append: true), cancellationToken),
            SelectionAction.Clear => runner.RunAsync(tool, name, SelectionService.Clear, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
