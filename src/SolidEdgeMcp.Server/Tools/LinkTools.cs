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

/// <summary>Links between files: references of a document, replacing them, where a file is used and the document search.</summary>
[McpServerToolType]
public sealed class LinkTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_links", Title = "File links and where used", Destructive = true, OpenWorld = false)]
    [Description("""
        Links between Solid Edge files, as File > Info > Links, Replace and Where Used do.
        list (what a document refers to: an assembly's component files with quantities (recursive: also inside subassemblies), a drawing's models with
        upToDate and views, a part's copied parts; exists flags missing files) |
        replace (from = the referenced file, full path or file name; to = the new file: every occurrence of it in an assembly, the model of a drawing
        (views updated) or the source of copied parts; checked by reading the links back) |
        break (part: unlink copied parts of 'from'; the geometry stays and no longer updates, it cannot be undone) |
        where_used (path: the documents in 'folders' (default the file's folder; subfolders) that refer to it; indirect = also their users, up to the top) |
        top_level (files, or the .asm files of 'folder': the assemblies no other one of the set uses) |
        search (folder: Solid Edge documents with title, subject and date of change; filter = file patterns separated by ';'; text = part of the title, subject or file name; contents=true: text inside the documents, through the Windows Search index) |
        pack (Pack and Go: path = a closed document and every file it refers to, at every level, copied into 'folder' and linked to one another; checked by reading the links of each copy; overwrite) |
        missing (assembly: the component files it cannot find; recheck = look again once they are back, as Repair Missing Files) |
        cad_direct (saved assembly; path = a file of another CAD program (STEP, IGES, Parasolid…) placed as a component that stays linked to it; its part lives inside the assembly file, so each occurrence comes with its box in mm) | cad_update (read the CAD Direct files again: all, or from = one source; gives the boxes again).
        The document is changed in memory: save it with se_documents. Only folders allowed by the path policy are searched.
        Example: {"action":"replace","from":"bracket.par","to":"D:\\CAD\\bracket_v2.par"}; {"action":"where_used","path":"D:\\CAD\\bracket.par","subfolders":true}.
        """)]
    public Task<CallToolResult> Links(
        LinkAction action,
        [Description("replace, break: the referenced file to change, full path or file name (action 'list'); cad_update: the source file to read again.")] string? from = null,
        [Description("replace: the new file (allowed folders).")] string? to = null,
        [Description("where_used: the file whose users are searched; pack: the document to pack; cad_direct: the file of the other CAD program.")] string? path = null,
        [Description("where_used: folders to search (default: the file's folder).")] string[]? folders = null,
        [Description("top_level: assembly files to check.")] string[]? files = null,
        [Description("search, top_level: folder to search; pack: the folder the copies go to (not the document's own).")] string? folder = null,
        [Description("where_used, top_level, search: include subfolders.")] bool subfolders = false,
        [Description("where_used: also the documents that use the users, up to the top.")] bool indirect = false,
        [Description("list: include the components inside subassemblies.")] bool recursive = false,
        [Description("search: file patterns separated by ';' (default all Solid Edge documents), e.g. '*.par;*.psm'.")] string? filter = null,
        [Description("search: keep the documents whose title, subject or file name contains this text.")] string? text = null,
        [Description("search: look for 'text' inside the documents (texts, notes) instead, through the Windows Search index.")] bool contents = false,
        [Description("pack: replace files of the same name in the folder.")] bool overwrite = false,
        [Description("missing: look for the missing files again.")] bool recheck = false,
        int offset = 0,
        int limit = 100,
        [Description("list, replace, break: document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_links";
        string act = Snake(action);
        return action switch
        {
            LinkAction.List => runner.RunAsync(tool, act, c => LinkService.List(c, document, recursive), cancellationToken),
            LinkAction.Replace => runner.RunAsync(tool, act, c => LinkService.Replace(c, document, from, ToolRunner.Require(to, "to", "replace")),
                cancellationToken, precheck: to is null ? null : () => guard.Check(to, "to")),
            LinkAction.Break => runner.RunAsync(tool, act, c => LinkService.Break(c, document, from), cancellationToken),
            LinkAction.WhereUsed => runner.RunAsync(tool, act, c => LinkService.WhereUsed(c, ToolRunner.Require(path, "path", "where_used"), folders, subfolders, indirect),
                cancellationToken, precheck: path is null ? null : () => guard.Check(path)),
            LinkAction.TopLevel => runner.RunAsync(tool, act, c => LinkService.TopLevel(c, files, folder, subfolders), cancellationToken),
            LinkAction.Search => runner.RunAsync(tool, act, c => LinkService.Search(c, ToolRunner.Require(folder, "folder", "search"), filter, subfolders, text, offset, limit, contents),
                cancellationToken, precheck: folder is null ? null : () => guard.Check(folder, "folder")),
            LinkAction.Pack => runner.RunAsync(tool, act, c => LinkService.Pack(c, path, folder, overwrite), cancellationToken, timeout: TimeSpan.FromMinutes(10),
                precheck: path is null || folder is null ? null : () => { guard.Check(path); guard.Check(folder, "folder"); }),
            LinkAction.Missing => runner.RunAsync(tool, act, c => LinkService.Missing(c, document, recheck), cancellationToken),
            LinkAction.CadDirect => runner.RunAsync(tool, act, c => LinkService.CadDirect(c, document, path), cancellationToken, timeout: TimeSpan.FromMinutes(5),
                precheck: path is null ? null : () => guard.Check(path)),
            LinkAction.CadUpdate => runner.RunAsync(tool, act, c => LinkService.CadUpdate(c, document, from), cancellationToken, timeout: TimeSpan.FromMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
