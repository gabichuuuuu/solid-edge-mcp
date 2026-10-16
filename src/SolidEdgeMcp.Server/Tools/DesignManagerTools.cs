// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum DesignManagerAction
{
    Links,
    Rename,
    Copy,
    Replace,
    Move,
    Revise,
    Release,
    Status,
    Relink,
    BrokenLinks,
    Duplicates,
}

public enum DocumentStatus
{
    Available,
    InWork,
    InReview,
    Released,
    Baselined,
    Obsolete,
}

/// <summary>Design Manager: files and their links managed on closed files, through Design Manager's own automation.</summary>
[McpServerToolType]
public sealed class DesignManagerTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_design_manager", Title = "Design Manager", Destructive = true, OpenWorld = false)]
    [Description("""
        Design Manager (the file manager installed with Solid Edge) on closed files: a hidden instance is started for each call and closed after it.
        links (path: every document it refers to, at every level, with exists, status, revision and document number) |
        rename | copy | replace | move | revise | release (path = the top document of the set, e.g. an assembly's drawing; files = files of its set; to = for each, the new name or path (rename, copy), an existing file (replace), a folder or path (move) or the name of the revision (revise: written as 'name-1.par' with Revision 1); release needs no 'to'. The documents of the set are updated: after a copy or a revision they use the new file. Copy also takes 'path' itself in 'files': its copy uses the copies and the original is left as it was. For rename and move, documents outside the set in 'folders' (default the folders of the files and of 'path') that use the files are pointed at the new ones too) |
        status (files: closed files given a document status, forward as Solid Edge's workflow does: a released file is revised, not taken back; released, baselined and obsolete files open read-only) |
        relink (files = linked files, to = the existing files to use instead, in one document ('path') or in every document of 'folders'; a folder in both moves every link under the first to the same name under the second, as Redefine Links) |
        broken_links (folders: documents whose linked files do not exist) |
        duplicates (folders: files with the same document ID, copied outside Solid Edge, and the same name in different folders).
        Every change is checked by reading the links and the files back; the documents involved must not be open in Solid Edge. Links of an open document: se_links; properties of a closed file: se_properties with 'path'.
        Example: {"action":"rename","path":"D:\\CAD\\frame.dft","files":["bracket.par"],"to":["bracket_v2"]}.
        """)]
    public Task<CallToolResult> DesignManager(
        DesignManagerAction action,
        [Description("links, rename...release: the top document of the set (allowed folders); relink: one document.")] string? path = null,
        [Description("The files acted on: full paths or names in the folder of 'path'; relink: linked files or a folder.")] string[]? files = null,
        [Description("rename, copy, revise: new names (the extension is kept) or paths; replace: existing files; move: folders; relink: existing files or a folder. One per file.")] string[]? to = null,
        [Description("relink, broken_links, duplicates: the folders to look through; rename, move: where to look for other documents that use the files.")] string[]? folders = null,
        [Description("Include the subfolders of 'folders'.")] bool subfolders = false,
        [Description("status: the document status to give 'files'.")] DocumentStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_design_manager";
        string act = Snake(action);
        TimeSpan scan = TimeSpan.FromMinutes(5);
        TimeSpan write = TimeSpan.FromMinutes(10);
        return action switch
        {
            DesignManagerAction.Links => runner.RunAsync(tool, act, c => DesignManagerService.Links(c, path), cancellationToken, timeout: scan),
            DesignManagerAction.Status => runner.RunAsync(tool, act,
                c => FilePropertyService.SetStatus(c, files, Snake(ToolRunner.RequireValue(status, "status", act))), cancellationToken),
            DesignManagerAction.Relink => runner.RunAsync(tool, act, c => DesignManagerService.Relink(c, path, folders, subfolders, files, to), cancellationToken, timeout: write),
            DesignManagerAction.BrokenLinks => runner.RunAsync(tool, act, c => DesignManagerService.BrokenLinks(c, folders, subfolders), cancellationToken, timeout: scan),
            DesignManagerAction.Duplicates => runner.RunAsync(tool, act, c => DesignManagerService.Duplicates(c, folders, subfolders), cancellationToken, timeout: scan),
            _ => runner.RunAsync(tool, act, c => DesignManagerService.Act(c, act, path, files, to, folders, subfolders), cancellationToken, timeout: write),
        };
    }
}
