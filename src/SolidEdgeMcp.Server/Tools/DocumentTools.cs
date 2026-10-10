using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum DocumentAction
{
    List,
    Info,
    Activate,
    New,
    Open,
    Save,
    SaveAs,
    SaveCopyAs,
    Close,
    Undo,
    Redo,
    Recompute,
}

public enum DocumentKind
{
    Part,
    SheetMetal,
    Assembly,
    Draft,
    Weldment,
}

[McpServerToolType]
public sealed class DocumentTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_documents", Title = "Solid Edge documents", Destructive = true, OpenWorld = false)]
    [Description("""
        Documents. list | info | activate | new (kind, optional template) | open (path) | save | save_as (path, overwrite) | save_copy_as (path, overwrite) | close (saveChanges required if there are unsaved changes) | undo | redo (steps; only what Solid Edge recorded: holes, deletes, suppressions... but not features made from sketches, and creating a sketch clears the history; nothing to undo is an error) | recompute.
        Without 'document' the active document is used. Paths must be absolute and inside the allowed folders (se_server_info). save_as keeps the native format (.par .psm .asm .dft .pwd); use se_export for other formats.
        Example: {"action":"new","kind":"part"} then {"action":"save_as","path":"D:\\CAD\\block.par"}.
        """)]
    public Task<CallToolResult> Documents(
        DocumentAction action,
        [Description("Document handle (e.g. 'doc:1'); default: active document.")] string? document = null,
        [Description("For 'new': part | sheet_metal | assembly | draft | weldment (without a .pwd template, as Solid Edge 2026 installs none, an assembly from the weldment assembly template).")] DocumentKind? kind = null,
        [Description("For 'new': template file (optional; allowed folders or the Solid Edge template folder).")] string? template = null,
        [Description("For 'open', 'save_as', 'save_copy_as': absolute file path.")] string? path = null,
        [Description("For 'save_as'/'save_copy_as': replace an existing file.")] bool overwrite = false,
        [Description("For 'close': true saves, false discards unsaved changes.")] bool? saveChanges = null,
        [Description("For 'undo'/'redo': number of steps (default 1).")] int steps = 1,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_documents";
        string name = action.ToString();
        Action? precheck = path is null ? null : () => guard.Check(path);
        return action switch
        {
            DocumentAction.List => runner.RunAsync(tool, name, DocumentService.List, cancellationToken),
            DocumentAction.Info => runner.RunAsync(tool, name, c => DocumentService.Info(c, document), cancellationToken),
            DocumentAction.Activate => runner.RunAsync(tool, name, c => DocumentService.Activate(c, document), cancellationToken),
            DocumentAction.New => runner.RunAsync(tool, name, c => DocumentService.New(c,
                ToKind(ToolRunner.RequireValue(kind, "kind", "new")), template), cancellationToken),
            DocumentAction.Open => runner.RunAsync(tool, name, c => DocumentService.Open(c, ToolRunner.Require(path, "path", "open")), cancellationToken, precheck: precheck),
            DocumentAction.Save => runner.RunAsync(tool, name, c => DocumentService.Save(c, document), cancellationToken),
            DocumentAction.SaveAs => runner.RunAsync(tool, name, c => DocumentService.SaveAs(c, document,
                ToolRunner.Require(path, "path", "save_as"), overwrite, copy: false), cancellationToken, precheck: precheck),
            DocumentAction.SaveCopyAs => runner.RunAsync(tool, name, c => DocumentService.SaveAs(c, document,
                ToolRunner.Require(path, "path", "save_copy_as"), overwrite, copy: true), cancellationToken, precheck: precheck),
            DocumentAction.Close => runner.RunAsync(tool, name, c => DocumentService.Close(c, document, saveChanges), cancellationToken),
            DocumentAction.Undo => runner.RunAsync(tool, name, c => DocumentService.Edit(c, document, "undo", steps), cancellationToken),
            DocumentAction.Redo => runner.RunAsync(tool, name, c => DocumentService.Edit(c, document, "redo", steps), cancellationToken),
            DocumentAction.Recompute => runner.RunAsync(tool, name, c => DocumentService.Edit(c, document, "recompute", 1), cancellationToken),
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
