// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum PrintAction
{
    Printers,
    Print,
    Mesh,
}

public enum PrintPaper
{
    Printer,
    A2,
    A3,
    A4,
    A5,
    A6,
    Letter,
    Legal,
    Tabloid,
    Ledger,
}

public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape,
}

/// <summary>Printing of drawing sheets (Application menu > Print).</summary>
[McpServerToolType]
public sealed class PrintTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_print", Title = "Print drawings / 3D print mesh", Destructive = false, OpenWorld = true)]
    [Description("""
        Prints the sheets of a drawing as the Print command of Solid Edge does: on a printer of this Windows user, or to files.
        printers: the Windows printers (default, connection local | network | remote_desktop, port, driver, asksForFile, state) and the current print settings of Solid Edge |
        print (printer = a name from 'printers', required; sheets = working sheet names, default all; folder = print to files there instead of on paper: one file per sheet, '<document>_<sheet>' with the extension of what the printer writes, .pdf, .ps or .prn;
        copies; paper = printer (its own paper, default) | a2 | a3 | a4 | a5 | a6 | letter | legal | tabloid | ledger; orientation = auto | portrait | landscape;
        scale = scale of the sheet on the paper (1 = full size), default: fit to the paper; black = colors as black; overwrite).
        Without 'folder' the sheets go to the printer and use paper. A printer that asks for a file name (asksForFile, e.g. Microsoft Print to PDF) prints only with 'folder'.
        The settings of Solid Edge's Print command are put back afterwards. Solid Edge records the print date in the drawing (it is then marked as modified), and Windows may make the printer used its default printer: the result says so.
        mesh (3D printing; part, sheet metal, weldment or assembly): the mesh Solid Edge's 3D Print command makes: triangles, points, mesh area (mm2) and volume (mm3) against the model volume, angle tolerance. The file itself: se_export .stl or .3mf.
        Example: {"action":"print","printer":"Microsoft Print to PDF","folder":"C:\\Work\\prints","paper":"a3"}; {"action":"mesh"}.
        """)]
    public Task<CallToolResult> Print(
        PrintAction action,
        [Description("print: printer name from 'printers'.")] string? printer = null,
        [Description("print: working sheet names (default: all of them).")] string[]? sheets = null,
        [Description("print: absolute folder (in an allowed folder) to print to files instead of on paper.")] string? folder = null,
        [Description("print: number of copies, 1-99 (printed to a file they are pages of it).")] int copies = 1,
        PrintPaper paper = PrintPaper.Printer,
        PrintOrientation orientation = PrintOrientation.Auto,
        [Description("print: scale of the sheet on the paper (1 = full size, 0.5 = half size); default: fit to the paper.")] double? scale = null,
        [Description("print: print the colors as black.")] bool black = false,
        [Description("print with folder: replace files that already exist.")] bool overwrite = false,
        [Description("Document handle (print: a drawing; mesh: a part, sheet metal, weldment or assembly); default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_print";
        string act = Snake(action);
        return action switch
        {
            PrintAction.Printers => runner.RunAsync(tool, act, PrintService.Printers, cancellationToken),
            PrintAction.Print => runner.RunAsync(tool, act, c => PrintService.Print(c, document, sheets, printer, folder, copies, Snake(paper),
                Snake(orientation), scale, black, overwrite), cancellationToken, timeout: TimeSpan.FromMinutes(5)),
            PrintAction.Mesh => runner.RunAsync(tool, act, c => Print3dService.Mesh(c, document), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
