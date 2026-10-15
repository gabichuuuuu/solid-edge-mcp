// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Image2d = SolidEdgeFrameworkSupport.Image2d;
using SmartFrame2d = SolidEdgeFrameworkSupport.SmartFrame2d;
using SmartFrame2dStyle = SolidEdgeFrameworkSupport.SmartFrame2dStyle;
using SmartFrames2d = SolidEdgeFrameworkSupport.SmartFrames2d;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Tables of their own (a revision list, notes in columns) and pictures placed on drawing sheets. Verified in Solid Edge
/// 2026: Tables.Add makes an empty table (no rows or columns) on the active sheet; Columns.Add(i, after) and Rows.Add(i,
/// after) insert after the i-th (also into an empty table with i = 1); cells and headers read back what was written; with
/// the upper-left anchor SetOrigin places that corner and Pages(1) gives its origin, width and height. Images2d.AddImage
/// keeps the picture's proportions when its width or height changes, SetOrigin places its lower-left corner, and an image
/// has no Delete: it is deleted through the selection.
/// </summary>
public static partial class DrawingService
{
    private static readonly string[] ImageExtensions = [".bmp", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff", ".emf", ".wmf"];

    /// <summary>Tables of the drawing (with the sheet each is on), images, smart frames, symbols and connectors of the sheet.</summary>
    public static object Contents(SeContext context, string? documentHandle, string? sheetRef)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        object[] tables = [.. Late.Items(draft.Tables).OfType<Table>().Select(t => DescribeTable(context, draft, t))];
        object[] images = [.. Late.Items(sheet.Images2d).OfType<Image2d>().Select(i => DescribeImage(context, draft, i))];
        object[] frames = [.. Late.Items(sheet.SmartFrames2d).OfType<SmartFrame2d>().Select(f => DescribeFrame(context, draft, f))];
        object[] symbols = [.. Late.Items(sheet.Symbols).OfType<SolidEdgeFramework.Symbol2d>().Select(s => DescribeSymbol(context, draft, s))];
        object[] connectors = [.. Late.Items(sheet.Connectors).OfType<SolidEdgeFrameworkSupport.Connector>().Select(c => DescribeConnector(context, draft, c))];
        return new { sheet = sheet.Name, tables, images, frames, symbols, connectors };
    }

    /// <summary>
    /// A table of its own: column headers, rows of cells ('a|b|c'), optional column widths (mm), its upper-left corner at
    /// <paramref name="position"/> (mm; default 20 mm inside the upper-left corner of the sheet). Everything written is read
    /// back; if something does not stay, the table is deleted.
    /// </summary>
    public static object AddTable(SeContext context, string? documentHandle, string? sheetRef, string[]? columns, string[]? rows, double[]? widths,
        double[]? position)
    {
        if (columns is not { Length: > 0 } || columns.Any(string.IsNullOrWhiteSpace))
        {
            throw ToolException.InvalidArgument("'columns' is required: the header text of each column, e.g. [\"REV\",\"DESCRIPTION\",\"DATE\"].");
        }

        string[][] cells = [.. (rows ?? []).Select(r => r.Split('|'))];
        if (cells.FirstOrDefault(r => r.Length > columns.Length) is { } wide)
        {
            throw ToolException.InvalidArgument($"A row has {wide.Length} cells and the table {columns.Length} columns.", "Split the cells of each row with '|', one per column.");
        }

        if (widths is not null && (widths.Length != columns.Length || widths.Any(w => !double.IsFinite(w) || w <= 0)))
        {
            throw ToolException.InvalidArgument("'widths' must give one width > 0 in mm per column.");
        }

        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        (double x, double y) = position is null ? (0.02, sheet.SheetSetup.SheetHeight - 0.02) : Geo.Point2(position, "position");
        if (!ReferenceEquals(draft.ActiveSheet, sheet) && draft.ActiveSheet.Name != sheet.Name)
        {
            sheet.Activate();
        }

        Table table = draft.Tables.Add();
        try
        {
            table.AnchorPoint = TableAnchorPoint.igUpperLeft;
            for (int c = 0; c < columns.Length; c++)
            {
                TableColumn column = table.Columns.Add(Math.Max(c, 1), true);
                column.Header = columns[c];
                if (widths is not null)
                {
                    column.Width = Units.MmToM(widths[c]);
                }
            }

            for (int r = 0; r < cells.Length; r++)
            {
                table.Rows.Add(Math.Max(r, 1), true);
            }

            for (int r = 0; r < cells.Length; r++)
            {
                for (int c = 0; c < cells[r].Length; c++)
                {
                    CellOf(table, r + 1, c + 1).value = cells[r][c];
                }
            }

            table.SetOrigin(x, y);
            // Without Update the sheet keeps showing the table as it was first drawn (verified in a capture).
            table.Update();
            CheckTable(table, columns, cells, (x, y));
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex)))
        {
            TryDeleteTable(table);
            throw ex is ToolException ? ex
                : new ToolException("table_failed", $"Solid Edge rejected the table: {ComErrors.Unwrap(ex).Message.Trim()}", "Check the columns and rows.", ex);
        }

        return new { table = DescribeTable(context, draft, table) };
    }

    /// <summary>Writes one cell (row and column from 1) and reads it back.</summary>
    public static object SetCell(SeContext context, string? tableHandle, int? row, int? column, string? text)
    {
        Table table = ResolveTable(context, tableHandle);
        int rowCount = table.Rows.Count, columnCount = table.Columns.Count;
        int r = row ?? throw ToolException.InvalidArgument("Action 'cell' requires 'row' (from 1).");
        int c = column ?? throw ToolException.InvalidArgument("Action 'cell' requires 'column' (from 1).");
        if (r < 1 || r > rowCount || c < 1 || c > columnCount)
        {
            throw ToolException.InvalidArgument($"The table has {rowCount} rows and {columnCount} columns: there is no cell ({r}, {c}).", "Add rows with action 'rows'.");
        }

        TableCell cell = CellOf(table, r, c);
        cell.value = text ?? "";
        table.Update();
        if (cell.value != (text ?? ""))
        {
            throw new ToolException("table_failed", $"Solid Edge kept '{cell.value}' in the cell instead of '{text}'.");
        }

        return new { table = DescribeTable(context, (DraftDocument)context.DocumentOf(tableHandle!), table) };
    }

    /// <summary>Adds rows ('a|b|c') at the end of a table and reads them back.</summary>
    public static object AddRows(SeContext context, string? tableHandle, string[]? rows)
    {
        Table table = ResolveTable(context, tableHandle);
        if (rows is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("Action 'rows' requires 'rows': the new rows, cells split by '|'.");
        }

        int columnCount = table.Columns.Count, first = table.Rows.Count;
        string[][] cells = [.. rows.Select(r => r.Split('|'))];
        if (cells.FirstOrDefault(r => r.Length > columnCount) is { } wide)
        {
            throw ToolException.InvalidArgument($"A row has {wide.Length} cells and the table {columnCount} columns.");
        }

        for (int r = 0; r < cells.Length; r++)
        {
            table.Rows.Add(Math.Max(first + r, 1), true);
            for (int c = 0; c < cells[r].Length; c++)
            {
                CellOf(table, first + r + 1, c + 1).value = cells[r][c];
            }
        }

        table.Update();
        if (table.Rows.Count != first + cells.Length)
        {
            throw new ToolException("table_failed", $"The table has {table.Rows.Count} rows instead of {first + cells.Length}.");
        }

        return new { table = DescribeTable(context, (DraftDocument)context.DocumentOf(tableHandle!), table) };
    }

    /// <summary>
    /// A picture file (in an allowed folder) on the sheet, its lower-left corner at <paramref name="position"/> (mm), sized
    /// by width or height (mm, the proportions are kept); embedded unless <paramref name="link"/>.
    /// </summary>
    public static object AddImage(SeContext context, string? documentHandle, string? sheetRef, string? path, double[]? position, double? width,
        double? height, bool link)
    {
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("Action 'image' requires 'path': a picture file."), "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"There is no file '{file}'.");
        }

        if (!ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' is not a picture.", $"Use one of: {string.Join(", ", ImageExtensions)}.");
        }

        if (width is { } w && (!double.IsFinite(w) || w <= 0) || height is { } h && (!double.IsFinite(h) || h <= 0))
        {
            throw ToolException.InvalidArgument("'width' and 'height' must be > 0 (mm).");
        }

        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        int before = Late.Count(sheet.Images2d);
        Image2d image = sheet.Images2d.AddImage(link, file);
        try
        {
            if (position is not null)
            {
                (double x, double y) = Geo.Point2(position, "position");
                image.SetOrigin(x, y);
                image.GetOrigin(out double ox, out double oy);
                if (Math.Abs(ox - x) > 1e-6 || Math.Abs(oy - y) > 1e-6)
                {
                    throw new ToolException("image_failed", $"Solid Edge placed the image at ({Geo.Mm(ox)}, {Geo.Mm(oy)}) mm.");
                }
            }

            if (width is { } wanted)
            {
                image.Width = Units.MmToM(wanted);
            }
            else if (height is { } tall)
            {
                image.Height = Units.MmToM(tall);
            }

            if (Late.Count(sheet.Images2d) != before + 1 || (width is { } ww && Math.Abs(image.Width - Units.MmToM(ww)) > 1e-6)
                || (width is null && height is { } hh && Math.Abs(image.Height - Units.MmToM(hh)) > 1e-6))
            {
                throw new ToolException("image_failed", "Solid Edge did not keep the image or its size.");
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex)))
        {
            DeleteBySelection(draft, image);
            throw ex is ToolException ? ex : new ToolException("image_failed", $"Solid Edge rejected the image: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        return new { image = DescribeImage(context, draft, image) };
    }

    /// <summary>
    /// An empty smart frame (a reserved area, e.g. for a logo or a stamp) of a style of the drawing (default: the default
    /// style), its lower-left corner at <paramref name="position"/> and its size in mm. Verified: AddBy2Points draws it
    /// with the range given; linking a part file into it (CreateLink) closed Solid Edge, so no content is placed.
    /// </summary>
    public static object AddFrame(SeContext context, string? documentHandle, string? sheetRef, string? style, double[]? position, double? width,
        double? height)
    {
        (double x, double y) = Geo.Point2(position ?? throw ToolException.InvalidArgument("Action 'frame' requires 'position': the lower-left corner [x, y] in mm."), "position");
        if (width is not { } w || height is not { } h || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
        {
            throw ToolException.InvalidArgument("Action 'frame' requires 'width' and 'height' > 0 in mm.");
        }

        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        SmartFrame2dStyle[] styles = [.. Late.Items(draft.SmartFrame2dStyles).OfType<SmartFrame2dStyle>()];
        SmartFrame2dStyle? wanted = string.IsNullOrWhiteSpace(style) ? null
            : styles.FirstOrDefault(s => string.Equals(s.Name, style, StringComparison.OrdinalIgnoreCase))
                ?? throw ToolException.NotFound($"The drawing has no smart frame style '{style}'.", $"Its styles: {string.Join(", ", styles.Select(s => s.Name))}.");
        if (wanted is { SizeMode: SolidEdgeFrameworkSupport.SizeModeConstants.igFrameChangesSize })
        {
            // Verified with the "Reference" style: the frame takes the size of its content, so an empty one has no extent.
            throw ToolException.InvalidArgument($"The smart frame style '{wanted.Name}' sizes the frame to its content, so an empty frame has no extent.",
                "Leave 'style' out for the default style, or use a style whose frame keeps its size.");
        }

        var frames = (SmartFrames2d)sheet.SmartFrames2d;
        int before = frames.Count;
        double x2 = x + Units.MmToM(w), y2 = y + Units.MmToM(h);
        // Verified: with a style name the frame gets no range and is not drawn; with "" it takes the default style.
        SmartFrame2d frame = frames.AddBy2Points("", x, y, x2, y2);
        try
        {
            if (wanted is not null)
            {
                frame.Style = wanted;
                if (!string.Equals(frame.Style?.Name, wanted.Name, StringComparison.Ordinal))
                {
                    throw new ToolException("frame_failed", $"Solid Edge kept the style '{frame.Style?.Name}' instead of '{wanted.Name}'.");
                }
            }

            frame.Range(out double x0, out double y0, out double x1, out double y1);
            if (frames.Count != before + 1 || Math.Abs(x0 - x) > 1e-6 || Math.Abs(y0 - y) > 1e-6 || Math.Abs(x1 - x2) > 1e-6 || Math.Abs(y1 - y2) > 1e-6)
            {
                throw new ToolException("frame_failed", $"Solid Edge drew the frame from ({Geo.Mm(x0)}, {Geo.Mm(y0)}) to ({Geo.Mm(x1)}, {Geo.Mm(y1)}) mm.");
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex)))
        {
            frame.Delete();
            throw ex is ToolException ? ex : new ToolException("frame_failed", $"Solid Edge rejected the frame: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        return new { frame = DescribeFrame(context, draft, frame) };
    }

    /// <summary>
    /// A symbol: a saved drawing (.dft, e.g. from a symbol library) placed on the sheet with its lower-left corner at
    /// <paramref name="position"/> (mm), embedded or linked; with <paramref name="geometry"/>, its 2D elements are copied onto the
    /// sheet instead. Verified: Symbols.Add places the whole sheet of the drawing (its background too) with the lower-left
    /// corner of its range at the point; InsertSymbolAsGeometry adds plain elements (Symbols does not count them).
    /// </summary>
    public static object AddSymbol(SeContext context, string? documentHandle, string? sheetRef, string? path, double[]? position, bool link, bool geometry)
    {
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("Action 'symbol' requires 'path': a saved drawing (.dft)."), "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"There is no file '{file}'.");
        }

        if (!string.Equals(Path.GetExtension(file), ".dft", StringComparison.OrdinalIgnoreCase))
        {
            // Verified: a part file linked into a smart frame closed Solid Edge, so only drawings are placed.
            throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' is not a drawing: a symbol is a saved .dft.",
                "Show a model with se_drawing base_view, or a picture with action 'image'.");
        }

        if (link && geometry)
        {
            throw ToolException.InvalidArgument("'geometry' copies the elements of the drawing, so it cannot be linked.");
        }

        (double x, double y) = Geo.Point2(position ?? throw ToolException.InvalidArgument("Action 'symbol' requires 'position': the lower-left corner [x, y] in mm."), "position");
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        var symbols = (SolidEdgeFramework.Symbols)sheet.Symbols;
        if (geometry)
        {
            int objects = Late.Count(sheet.DrawingObjects);
            symbols.InsertSymbolAsGeometry(file, x, y);
            int added = Late.Count(sheet.DrawingObjects) - objects;
            return added > 0
                ? new { geometry = new { file = Path.GetFileName(file), elements = added, position = new[] { Geo.Mm(x), Geo.Mm(y) } } }
                : throw new ToolException("symbol_failed", $"Solid Edge copied nothing from '{Path.GetFileName(file)}'.", "Check that its working sheet has 2D elements.");
        }

        int before = symbols.Count;
        var symbol = (SolidEdgeFramework.Symbol2d)symbols.Add((int)(link ? SolidEdgeFramework.OLEInsertionTypeConstant.igOLELinked : SolidEdgeFramework.OLEInsertionTypeConstant.igOLEEmbedded), file, x, y);
        try
        {
            symbol.Range(out double x0, out double y0, out _, out _);
            if (symbols.Count != before + 1 || Math.Abs(x0 - x) > 1e-6 || Math.Abs(y0 - y) > 1e-6)
            {
                throw new ToolException("symbol_failed", $"Solid Edge placed the symbol at ({Geo.Mm(x0)}, {Geo.Mm(y0)}) mm.");
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex)))
        {
            symbol.Delete();
            throw ex is ToolException ? ex : new ToolException("symbol_failed", $"Solid Edge rejected the symbol: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        return new { symbol = DescribeSymbol(context, draft, symbol) };
    }

    private static object DescribeSymbol(SeContext context, DraftDocument draft, SolidEdgeFramework.Symbol2d symbol)
    {
        symbol.Range(out double x0, out double y0, out double x1, out double y1);
        bool linked = symbol.OLEType == SolidEdgeFramework.OLEInsertionTypeConstant.igOLELinked;
        return new
        {
            handle = context.Register(symbol, "symbol", Document(draft)),
            link = linked,
            file = linked ? symbol.SourceDoc : null,
            position = new[] { Geo.Mm(x0), Geo.Mm(y0) },
            size = new[] { Geo.Mm(x1 - x0), Geo.Mm(y1 - y0) },
        };
    }

    /// <summary>Deletes a table (also a parts list, bend, hole or other table of se_drawing), an image, a smart frame, a symbol or a connector of a sheet.</summary>
    public static object DeleteContent(SeContext context, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw ToolException.InvalidArgument("'item' is required: a table, image, frame, symbol or connector handle from action 'list'.");
        }

        object item = context.Handles.Resolve(handle, "item");
        var draft = (DraftDocument)context.DocumentOf(handle);
        switch (item)
        {
            case Table table:
                int tables = Late.Count(draft.Tables);
                table.Delete();
                if (Late.Count(draft.Tables) != tables - 1)
                {
                    throw new ToolException("table_failed", "Solid Edge did not delete the table.");
                }

                break;
            case Image2d image:
                // The Parent of an image is its sheet (verified).
                Sheet sheet = Late.Get(image, "Parent") as Sheet ?? throw new ToolException("image_failed", "The image has no sheet.");
                int count = Late.Count(sheet.Images2d);
                DeleteBySelection(draft, image);
                if (Late.Count(sheet.Images2d) != count - 1)
                {
                    throw new ToolException("image_failed", "Solid Edge did not delete the image.");
                }

                break;
            case SmartFrame2d frame:
                var frames = (SmartFrames2d)((Sheet)Late.Get(frame, "Parent")!).SmartFrames2d;
                int framed = frames.Count;
                frame.Delete();
                if (frames.Count != framed - 1)
                {
                    throw new ToolException("frame_failed", "Solid Edge did not delete the frame.");
                }

                break;
            case SolidEdgeFramework.Symbol2d symbol:
                var symbols = (SolidEdgeFramework.Symbols)((Sheet)Late.Get(symbol, "Parent")!).Symbols;
                int placed = symbols.Count;
                symbol.Delete();
                if (symbols.Count != placed - 1)
                {
                    throw new ToolException("symbol_failed", "Solid Edge did not delete the symbol.");
                }

                break;
            case SolidEdgeFrameworkSupport.Connector connector:
                var connectors = (SolidEdgeFrameworkSupport.Connectors)((Sheet)Late.Get(connector, "Parent")!).Connectors;
                int drawn = connectors.Count;
                connector.Delete();
                if (connectors.Count != drawn - 1)
                {
                    throw new ToolException("connector_failed", "Solid Edge did not delete the connector.");
                }

                break;
            case SolidEdgeDraft.PartsList or DraftBendTable or SolidEdgeDraft.ToleranceTable or FOPTable or SolidEdgeDraft.BlockTable or HoleTable2:
                // The tables made by se_drawing (listed by its action 'tables'), each in its own collection of the drawing.
                object collection = item switch
                {
                    SolidEdgeDraft.PartsList => draft.PartsLists,
                    DraftBendTable => draft.DraftBendTables,
                    SolidEdgeDraft.ToleranceTable => draft.ToleranceTables,
                    FOPTable => draft.FOPTables,
                    SolidEdgeDraft.BlockTable => draft.BlockTables,
                    _ => draft.HoleTables2,
                };
                int listed = Late.Count(collection);
                Late.Call(item, "Delete");
                if (Late.Count(collection) != listed - 1)
                {
                    throw new ToolException("table_failed", "Solid Edge did not delete the table.");
                }

                break;
            default:
                throw ToolException.InvalidArgument($"'{handle}' is not a table, an image, a smart frame, a symbol or a connector of a sheet.");
        }

        context.Handles.Remove(handle, "it was deleted");
        return new { deleted = handle };
    }

    private static void CheckTable(Table table, string[] columns, string[][] cells, (double X, double Y) at)
    {
        if (table.Columns.Count != columns.Length || table.Rows.Count != cells.Length)
        {
            throw new ToolException("table_failed",
                $"The table has {table.Columns.Count} columns and {table.Rows.Count} rows instead of {columns.Length} and {cells.Length}.");
        }

        for (int c = 0; c < columns.Length; c++)
        {
            if (table.Columns.Item(c + 1).Header != columns[c])
            {
                throw new ToolException("table_failed", $"Solid Edge kept the header '{table.Columns.Item(c + 1).Header}' instead of '{columns[c]}'.");
            }
        }

        for (int r = 0; r < cells.Length; r++)
        {
            for (int c = 0; c < cells[r].Length; c++)
            {
                if (CellOf(table, r + 1, c + 1).value != cells[r][c])
                {
                    throw new ToolException("table_failed", $"Solid Edge did not keep '{cells[r][c]}' in row {r + 1}, column {c + 1}.");
                }
            }
        }

        table.Pages.Item(1).GetOrigin(out double x, out double y);
        if (Math.Abs(x - at.X) > 1e-6 || Math.Abs(y - at.Y) > 1e-6)
        {
            throw new ToolException("table_failed", $"Solid Edge placed the table at ({Geo.Mm(x)}, {Geo.Mm(y)}) mm instead of ({Geo.Mm(at.X)}, {Geo.Mm(at.Y)}) mm.");
        }
    }

    private static TableCell CellOf(Table table, int row, int column) =>
        Late.Get(table, "Cell", row, column) as TableCell ?? throw new ToolException("table_failed", $"The table has no cell ({row}, {column}).");

    private static Table ResolveTable(SeContext context, string? handle) =>
        string.IsNullOrWhiteSpace(handle)
            ? throw ToolException.InvalidArgument("'table' is required: a table handle from action 'list' or 'table'.")
            : context.Handles.Resolve<Table>(handle, "table");

    private static void TryDeleteTable(Table table)
    {
        try
        {
            table.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }

    /// <summary>Image2d and CuttingPlane have no Delete (verified): the item is selected and the selection deleted.</summary>
    private static void DeleteBySelection(DraftDocument draft, object item)
    {
        object selection = Late.Get(draft, "SelectSet") ?? throw new ToolException("image_failed", "The drawing has no selection to delete with.");
        Late.Call(selection, "RemoveAll");
        Late.Call(selection, "Add", item);
        Late.Call(selection, "Delete");
    }

    private static object DescribeTable(SeContext context, DraftDocument draft, Table table)
    {
        int rows = table.Rows.Count, columns = table.Columns.Count;
        TablePage? page = table.Pages.Count > 0 ? table.Pages.Item(1) : null;
        double x = 0, y = 0;
        page?.GetOrigin(out x, out y);
        return new
        {
            handle = context.Register(table, "table", Document(draft)),
            name = table.Name,
            sheet = page?.Sheet,
            position = page is null ? null : new[] { Geo.Mm(x), Geo.Mm(y) },
            size = page is null ? null : new[] { Geo.Mm(page.Width), Geo.Mm(page.Height) },
            columns = Enumerable.Range(1, columns).Select(c => table.Columns.Item(c).Header).ToArray(),
            // In the form the rows are given: cells split by '|'.
            rows = Enumerable.Range(1, rows).Select(r => string.Join('|', Enumerable.Range(1, columns).Select(c => CellOf(table, r, c).value))).ToArray(),
        };
    }

    private static object DescribeImage(SeContext context, DraftDocument draft, Image2d image)
    {
        image.GetOrigin(out double x, out double y);
        return new
        {
            handle = context.Register(image, "image", Document(draft)),
            file = image.ImageFileName,
            link = image.IsLink,
            position = new[] { Geo.Mm(x), Geo.Mm(y) },
            size = new[] { Geo.Mm(image.Width), Geo.Mm(image.Height) },
        };
    }

    private static object DescribeFrame(SeContext context, DraftDocument draft, SmartFrame2d frame)
    {
        frame.Range(out double x0, out double y0, out double x1, out double y1);
        return new
        {
            handle = context.Register(frame, "frame", Document(draft)),
            style = frame.Style?.Name,
            position = new[] { Geo.Mm(x0), Geo.Mm(y0) },
            size = new[] { Geo.Mm(x1 - x0), Geo.Mm(y1 - y0) },
        };
    }
}