// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum SheetContentAction
{
    List,
    Table,
    Cell,
    Rows,
    Image,
    Frame,
    Symbol,
    Delete,
}

/// <summary>Tables of their own, pictures, smart frames and symbols on drawing sheets.</summary>
[McpServerToolType]
public sealed class SheetContentTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_sheet_content", Title = "Tables, images, frames and symbols on drawing sheets", Destructive = true, OpenWorld = false)]
    [Description("""
        Tables of your own (a revision list, notes in columns), pictures (a logo), smart frames (reserved areas) and symbols (saved drawings) on the sheets of a drawing. Positions in mm from the lower-left corner of the sheet.
        list (the tables of the drawing with their cells, the images, frames and symbols of the sheet) |
        table (columns = header texts; rows = cells split by '|', e.g. 'A|First issue|2026-10-12'; widths = column widths in mm; position = upper-left corner, default 20 mm inside the upper-left corner of the sheet) |
        cell (table + row + column from 1 + text) | rows (table + rows: added at the end) |
        image (path = a picture file in an allowed folder: png, jpg, bmp, gif, tif, emf, wmf; position = lower-left corner; width or height in mm, the proportions are kept; link = linked, default embedded) |
        frame (an empty smart frame: position = lower-left corner, width and height in mm; style = a smart frame style of the drawing whose frame keeps its size, se_styles type frame; default the default style) |
        symbol (path = a saved .dft, e.g. from a symbol library; position = lower-left corner; link = linked, default embedded; geometry = copy its 2D elements instead) |
        delete (item = a table, image, frame or symbol handle).
        Everything written is read back (cells, headers, position, size); a table, image or frame that does not come out as asked is removed.
        Parts lists and bend tables: se_drawing.
        Example: {"action":"table","columns":["REV","DESCRIPTION","DATE"],"rows":["A|First issue|2026-10-12"],"widths":[15,80,30],"position":[20,280]}.
        """)]
    public Task<CallToolResult> Content(
        SheetContentAction action,
        [Description("table: header text of each column.")] string[]? columns = null,
        [Description("table, rows: rows of cells split by '|', one cell per column.")] string[]? rows = null,
        [Description("table: width of each column in mm.")] double[]? widths = null,
        [Description("table: upper-left corner [x, y] in mm; image, frame, symbol: lower-left corner.")] double[]? position = null,
        [Description("cell, rows: table handle from 'list' or 'table'.")] string? table = null,
        [Description("cell: row number from 1.")] int? row = null,
        [Description("cell: column number from 1.")] int? column = null,
        [Description("cell: the text.")] string? text = null,
        [Description("image: absolute path of the picture; symbol: of the .dft (allowed folders).")] string? path = null,
        [Description("image: width in mm (the height follows); frame: width in mm.")] double? width = null,
        [Description("image: height in mm (when no width is given); frame: height in mm.")] double? height = null,
        [Description("image, symbol: link the file instead of embedding it.")] bool link = false,
        [Description("delete: table, image, frame or symbol handle.")] string? item = null,
        [Description("symbol: copy the 2D elements of the drawing onto the sheet instead of placing it as a symbol.")] bool geometry = false,
        [Description("frame: smart frame style name (default: the default style).")] string? style = null,
        [Description("Sheet handle or name (default: the active sheet).")] string? sheet = null,
        [Description("Draft document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sheet_content";
        string act = Snake(action);
        return action switch
        {
            SheetContentAction.List => runner.RunAsync(tool, act, c => DrawingService.Contents(c, document, sheet), cancellationToken),
            SheetContentAction.Table => runner.RunAsync(tool, act, c => DrawingService.AddTable(c, document, sheet, columns, rows, widths, position), cancellationToken),
            SheetContentAction.Cell => runner.RunAsync(tool, act, c => DrawingService.SetCell(c, table, row, column, text), cancellationToken),
            SheetContentAction.Rows => runner.RunAsync(tool, act, c => DrawingService.AddRows(c, table, rows), cancellationToken),
            SheetContentAction.Image => runner.RunAsync(tool, act, c => DrawingService.AddImage(c, document, sheet, path, position, width, height, link), cancellationToken,
                precheck: path is null ? null : () => guard.Check(path)),
            SheetContentAction.Frame => runner.RunAsync(tool, act, c => DrawingService.AddFrame(c, document, sheet, style, position, width, height), cancellationToken),
            SheetContentAction.Symbol => runner.RunAsync(tool, act, c => DrawingService.AddSymbol(c, document, sheet, path, position, link, geometry), cancellationToken,
                precheck: path is null ? null : () => guard.Check(path)),
            SheetContentAction.Delete => runner.RunAsync(tool, act, c => DrawingService.DeleteContent(c, item), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
