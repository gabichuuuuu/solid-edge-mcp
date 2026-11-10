// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

[McpServerToolType]
public sealed class DocumentTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_documents", Title = "Solid Edge documents", Destructive = true, OpenWorld = false)]
    [Description("""
        Documents. list | info | activate | new (kind, optional template) | open (path; a file of another CAD system: import) | import (path of a STEP, IGES, Parasolid, JT, SAT, STL (read in mm), OBJ, 3MF, CATIA V5 (.catpart, .catproduct), CATIA V4 (.model; of an assembly: kind assembly), DXF or DWG file: a new unsaved document translated from it; kind and template optional, default part, assembly for .catproduct or draft for DXF/DWG; returns the volume) | save | save_as (path, overwrite) | save_copy_as (path, overwrite) | save_mirror (part: path of a new .par, its mirror image about 'plane'; link = keep it linked to this part; the new part opens) | close (saveChanges required if there are unsaved changes) | templates (this installation's default template of each kind and the templates in its folders; optional kind) | status (value = available | in_work | in_review | released | baselined | obsolete: set and read back; none: read) | undo | redo (steps; only what Solid Edge recorded: holes, deletes, suppressions... but not features made from sketches, and creating a sketch clears the history; nothing to undo is an error) | recompute | modeling_mode (part or sheet metal: mode = synchronous | ordered sets it, as the ribbon's Synchronous and Ordered buttons; none: read) | thumbnail (path: the picture saved in a Solid Edge file, as Windows Explorer shows it, read without opening the file; 128 x 128).
        Without 'document' the active document is used. Paths must be absolute and inside the allowed folders (se_server_info). save_as keeps the native format (.par .psm .asm .dft .pwd); use se_export for other formats.
        Example: {"action":"new","kind":"part"} then {"action":"save_as","path":"D:\\CAD\\block.par"}.
        """)]
    public Task<CallToolResult> Documents(
        DocumentAction action,
        [Description("Document handle (e.g. 'doc:1'); default: active document.")] string? document = null,
        [Description("For 'new', 'templates' and 'import': part | sheet_metal | assembly | draft | weldment (without a .pwd template, as Solid Edge 2026 installs none, an assembly from the weldment assembly template set in Solid Edge's options).")] DocumentKind? kind = null,
        [Description("For 'new' and 'import': template file (optional; allowed folders or the Solid Edge template folder).")] string? template = null,
        [Description("For 'open', 'import', 'save_as', 'save_copy_as', 'thumbnail': absolute file path.")] string? path = null,
        [Description("For 'save_as'/'save_copy_as': replace an existing file.")] bool overwrite = false,
        [Description("For 'close': true saves, false discards unsaved changes.")] bool? saveChanges = null,
        [Description("For 'undo'/'redo': number of steps (default 1).")] int steps = 1,
        [Description("For 'status': the new life-cycle status; omitted = read it.")] DocumentLifeStatus? value = null,
        [Description("For 'save_mirror': reference plane of the part ('1'-'3', 'xy'/'yz'/'xz' or a handle).")] string? plane = null,
        [Description("For 'save_mirror': the new part stays linked to this one (default false: its features are copied).")] bool link = false,
        [Description("For 'modeling_mode': synchronous | ordered; omitted = read it.")] DocumentModelingMode? mode = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_documents";
        string name = action.ToString();
        Action? precheck = path is null ? null : () => guard.Check(path);
        return action switch
        {
            DocumentAction.List => runner.RunAsync(tool, name, DocumentService.List, cancellationToken),
            DocumentAction.Templates => runner.RunAsync(tool, name, c => DocumentService.Templates(c, kind is { } k ? ToKind(k) : null), cancellationToken),
            DocumentAction.Info => runner.RunAsync(tool, name, c => DocumentService.Info(c, document), cancellationToken),
            DocumentAction.Activate => runner.RunAsync(tool, name, c => DocumentService.Activate(c, document), cancellationToken),
            DocumentAction.New => runner.RunAsync(tool, name, c => DocumentService.New(c,
                ToKind(ToolRunner.RequireValue(kind, "kind", "new")), template), cancellationToken),
            DocumentAction.Open => runner.RunAsync(tool, name, c => DocumentService.Open(c, ToolRunner.Require(path, "path", "open")), cancellationToken, precheck: precheck),
            DocumentAction.Import => runner.RunAsync(tool, name, c => DocumentService.Import(c, ToolRunner.Require(path, "path", "import"),
                kind is { } ik ? ToKind(ik) : null, template), cancellationToken, precheck: precheck),
            DocumentAction.Save => runner.RunAsync(tool, name, c => DocumentService.Save(c, document), cancellationToken),
            DocumentAction.SaveAs => runner.RunAsync(tool, name, c => DocumentService.SaveAs(c, document,
                ToolRunner.Require(path, "path", "save_as"), overwrite, copy: false), cancellationToken, precheck: precheck),
            DocumentAction.SaveCopyAs => runner.RunAsync(tool, name, c => DocumentService.SaveAs(c, document,
                ToolRunner.Require(path, "path", "save_copy_as"), overwrite, copy: true), cancellationToken, precheck: precheck),
            DocumentAction.SaveMirror => runner.RunAsync(tool, name, c => DocumentService.SaveMirror(c, document,
                ToolRunner.Require(path, "path", "save_mirror"), ToolRunner.Require(plane, "plane", "save_mirror"), link, overwrite), cancellationToken, precheck: precheck),
            DocumentAction.Close => runner.RunAsync(tool, name, c => DocumentService.Close(c, document, saveChanges), cancellationToken),
            DocumentAction.Undo => runner.RunAsync(tool, name, c => DocumentService.Edit(c, document, "undo", steps), cancellationToken),
            DocumentAction.Redo => runner.RunAsync(tool, name, c => DocumentService.Edit(c, document, "redo", steps), cancellationToken),
            DocumentAction.Status => runner.RunAsync(tool, name, c => DocumentService.Status(c, document, value), cancellationToken),
            DocumentAction.Recompute => runner.RunAsync(tool, name, c => DocumentService.Edit(c, document, "recompute", 1), cancellationToken),
            DocumentAction.ModelingMode => runner.RunAsync(tool, name, c => DocumentService.ModelingMode(c, document, mode), cancellationToken),
            DocumentAction.Thumbnail => runner.RunAsync(tool, name, c =>
            {
                ThumbnailService.Thumbnail thumbnail = ThumbnailService.Read(c, ToolRunner.Require(path, "path", "thumbnail"));
                return new ToolImage(thumbnail.Png, "image/png", new { file = thumbnail.File, width = thumbnail.Width, height = thumbnail.Height });
            }, cancellationToken, precheck: precheck),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static string ToKind(DocumentKind kind) => kind switch
    {
        DocumentKind.Part => "part",
        DocumentKind.SheetMetal => "sheet_metal",
        DocumentKind.Assembly => "assembly",
        DocumentKind.Draft => "draft",
        DocumentKind.Weldment => "weldment",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
