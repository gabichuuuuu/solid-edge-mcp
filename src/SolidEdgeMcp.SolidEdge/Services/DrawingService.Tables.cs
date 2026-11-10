// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Tables that Solid Edge fills from the model: parts list, family, block, tolerance, bend and hole tables.</summary>
public static partial class DrawingService
{
    /// <summary>
    /// Parts list of an assembly view (with balloons if requested). With <paramref name="family"/> or
    /// <paramref name="members"/>, a family of assemblies parts list: one quantity column per chosen member of the assembly
    /// family. Verified: a family with members NoPin and WithPin gives the columns "Quantity (WithPin)" and "Quantity (NoPin)"
    /// once both are chosen and the list updated (block 1 and 1, pin 1 and 0); one member alone gives its column and rows.
    /// </summary>
    public static object PartsList(SeContext context, string? viewHandle, double[]? position, bool balloons, string? style,
        bool family = false, string[]? members = null, string[]? columns = null)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (LinkOf(view) is not { } link || link.ModelType != ModelLinkTypeConstants.igAssemblyLink)
        {
            throw new ToolException("wrong_view", "A parts list needs a view of an assembly.", "Pass a drawing view created from an .asm file.");
        }

        string[]? properties = columns is null ? null : [.. columns.Select(c => c?.Trim() ?? "")];
        if (properties is not null && (properties.Length == 0 || properties.Any(p => p.Length == 0)))
        {
            throw ToolException.InvalidArgument("'columns' needs one or more property names, none empty.", "E.g. [\"Quantity\", \"File Name (no extension)\", \"Material\"].");
        }

        family |= members is { Length: > 0 };
        PartsList list;
        try
        {
            list = family
                ? draft.PartsLists.AddFamilyOfAssemblyPartsList(view, 0, style ?? "", balloons ? 1 : 0, 1)
                : draft.PartsLists.Add(view, style ?? "", balloons ? 1 : 0, 1);
        }
        catch (NullReferenceException ex) when (family)
        {
            // Verified: a view of an assembly without a family gives E_POINTER, which .NET raises as NullReferenceException.
            throw new ToolException("not_family", "The assembly of the view has no family members.",
                "Make a family of assemblies (se_assembly_family create), save it and place a view of a member; or leave out 'family'.", ex);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("parts_list_failed", $"Solid Edge could not create the parts list: {ex.Message.Trim()}",
                style is null ? "Pass 'style' with the name of a saved parts list setting (e.g. 'Normal')." : "Check the saved setting name in 'style'.", ex);
        }

        // Verified with Solid Edge 2026: a saved setting that does not exist gives no error, the list takes the default one.
        if (!string.IsNullOrWhiteSpace(style) && SavedSettings(list) is { } saved && !saved.Contains(style.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            list.Delete();
            throw ToolException.NotFound($"The drawing has no saved parts list setting '{style.Trim()}'.",
                $"Use one of: {string.Join(", ", saved)}; or leave out 'style' for the default one.");
        }

        string[] chosen = family ? ChooseFamilyMembers(view, list, members) : [];
        if (properties is not null)
        {
            ShowColumns(list, properties, chosen);
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        list.AnchorPoint = anchor;
        list.SetOrigin(x, y);
        return DescribePartsList(context, draft, list);
    }

    /// <summary>Chooses the members of a family of assemblies parts list (default all), or deletes the list if that fails.</summary>
    private static string[] ChooseFamilyMembers(DrawingView view, PartsList list, string[]? members)
    {
        try
        {
            Array all = Array.Empty<object>();
            int count = 0;
            if (list.IsFamilyOfAssemblyPartsList())
            {
                list.GetAllFamilyOfAssemblyPartsListMembers(view, out count, ref all);
            }

            string[] names = [.. all.Cast<object?>().Take(count).Select(o => o?.ToString() ?? "")];
            if (names.Length == 0)
            {
                throw new ToolException("not_family", "The assembly of the view has no family members.",
                    "Make a family of assemblies (se_assembly_family create), save it and place a view of a member; or leave out 'family'.");
            }

            string[] chosen = members is { Length: > 0 }
                ? [.. members.Select(m => names.FirstOrDefault(n => string.Equals(n, m?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw ToolException.InvalidArgument($"The assembly family has no member '{m}'.", $"Use one of: {string.Join(", ", names)}."))
                    .Distinct(StringComparer.OrdinalIgnoreCase)]
                : names;
            Array wanted = chosen.Cast<object>().ToArray();
            int number = chosen.Length;
            list.SetSelectedFamilyOfAssemblyPartsListMembers(ref number, ref wanted);
            list.Update();
            Array selected = Array.Empty<object>();
            list.GetSelectedFamilyOfAssemblyPartsListMembers(out int got, ref selected);
            // The array keeps its old length: only the first 'got' entries are the members chosen.
            string[] kept = [.. selected.Cast<object?>().Take(got).Select(o => o?.ToString() ?? "")];
            if (!kept.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(chosen))
            {
                throw new ToolException("parts_list_failed", $"Solid Edge kept the members {string.Join(", ", kept)} instead of {string.Join(", ", chosen)}.");
            }

            return chosen;
        }
        catch (Exception ex) when (ex is ToolException || ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex))
        {
            list.Delete();
            throw ex is ToolException ? ex : new ToolException("parts_list_failed",
                $"Solid Edge could not choose the family members: {ComErrors.Unwrap(ex).Message.Trim()}", "Check the member names (se_assembly_family list).", ex);
        }
    }

    /// <summary>
    /// Shows the given properties as the columns of a parts list, in place of the columns of its saved setting (each
    /// installation has its own), or deletes the list if that fails. A name is a property of the document of each row as
    /// Solid Edge names it (e.g. "Quantity", "File Name (no extension)", "Material"; a custom property followed by "/CP"), or
    /// a property text as given ("%{…}"). Verified with Solid Edge 2026: saved settings write "%{Quantity|G}" and
    /// "%{Name/CP|G}"; without "|G" a column shows the property of the drawing instead. The quantity column of each member of
    /// a family ("Quantity (WithPin)") stays, after the given ones: verified, without them the list has no rows.
    /// </summary>
    private static void ShowColumns(PartsList list, string[] properties, string[] familyMembers)
    {
        try
        {
            TableColumns all = list.Columns;
            int saved = all.Count;
            bool[] quantity = [.. Enumerable.Range(1, saved).Select(i => familyMembers.Any(m => (all.Item(i).Header ?? "").EndsWith($"({m})", StringComparison.OrdinalIgnoreCase)))];
            int firstQuantity = Array.IndexOf(quantity, true);
            int before = firstQuantity > 0 ? firstQuantity : saved;
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                bool text = property.StartsWith("%{", StringComparison.Ordinal);
                TableColumn column = all.Add(before + i, true);
                column.PropertyText = text ? property : $"%{{{property}|G}}";
                column.Header = text ? property.TrimStart('%', '{').Split('|', '}')[0].Replace("/CP", "", StringComparison.Ordinal) : property;
            }

            for (int i = saved; i >= 1; i--)
            {
                if (!quantity[i - 1])
                {
                    all.Item(i > before ? i + properties.Length : i).Delete();
                }
            }

            list.Update();
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            list.Delete();
            throw new ToolException("parts_list_failed", $"Solid Edge could not set the columns of the parts list: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Give property names as Solid Edge shows them in its property text dialog.", ex);
        }
    }

    /// <summary>
    /// Family of parts table of a view of a part with a family (se_part_family): one row per member with the value of each
    /// family variable (verified: members Thick and Thin of a block whose depth is the family variable give two rows,
    /// "Thick | 30.000 mm"). A part without members gives an empty table, which is deleted.
    /// </summary>
    public static object FamilyTable(SeContext context, string? viewHandle, double[]? position)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        FOPTable table;
        try
        {
            table = draft.FOPTables.Add(view);
        }
        catch (NullReferenceException ex)
        {
            // Verified: a view of a part without a family of parts gives E_POINTER, which .NET raises as NullReferenceException.
            throw new ToolException("family_table_failed", "The part of the view has no family of parts.",
                "Give a view of a part with family members (se_part_family add_member, then save the part).", ex);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("family_table_failed", $"Solid Edge could not create the family of parts table: {ex.Message.Trim()}",
                "Give a view of a part that has a family of parts (se_part_family).", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("family_table_failed", "The family of parts table came out empty: the part of the view has no family members.",
                "Add members with se_part_family add_member and save the part, then make the table.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        return TableResult(context, draft, table, "family_table", table.Columns, table.Rows.Count, anchor, x, y);
    }

    /// <summary>
    /// Block table of a sheet: one row per block placed on it (item, name, quantity…), as the saved setting lists them
    /// (verified: a block placed twice gives one row "Frame"). A sheet without blocks gives an empty table, which is deleted.
    /// </summary>
    public static object BlockTable(SeContext context, string? documentHandle, string? sheetRef, double[]? position, string? style)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        if (!Late.SameObject(sheet, draft.ActiveSheet))
        {
            sheet.Activate();
        }

        BlockTable? table;
        try
        {
            // Verified: with no block on the sheet Solid Edge returns nothing; an unknown saved setting falls back to the default.
            table = draft.BlockTables.AddByActiveSheet(style ?? "", 0);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("block_table_failed", $"Solid Edge could not create the block table: {ex.Message.Trim()}",
                style is null ? "Place blocks on the sheet first (se_drawing place_block)." : "Check the saved setting name in 'style'.", ex);
        }

        if (table is null || table.Rows.Count == 0)
        {
            table?.Delete();
            throw new ToolException("block_table_failed", "The block table came out empty: no block is placed on the sheet.",
                "Place blocks with se_drawing place_block, then make the table.");
        }

        (TableAnchorPoint anchor, double x, double y) = position is null
            ? (TableAnchorPoint.igLowerRight, AboveTitleBlock(sheet).X, AboveTitleBlock(sheet).Y)
            : (TableAnchorPoint.igUpperLeft, Geo.Point2(position, "position").X, Geo.Point2(position, "position").Y);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        return TableResult(context, draft, table, "block_table", table.Columns, table.Rows.Count, anchor, x, y);
    }

    /// <summary>
    /// The tables already on a drawing (parts lists, bend, tolerance, family of parts, block and hole tables) with their shown columns
    /// and cells, so that a drawing opened from a file can be read.
    /// </summary>
    public static object Tables(SeContext context, string? documentHandle)
    {
        DraftDocument draft = DraftFor(context, documentHandle, null);
        var tables = new List<object>();
        void Add(string kind, object table, TableColumns columns, int count)
        {
            (List<string?> headers, List<string?[]> rows) = TableContent(context, table, columns, count);
            double[]? origin = null;
            try
            {
                object?[] args = Late.CallByRef(table, "GetOrigin", [0.0, 0.0], 0, 1);
                origin = [Geo.Mm(Convert.ToDouble(args[0], System.Globalization.CultureInfo.InvariantCulture)), Geo.Mm(Convert.ToDouble(args[1], System.Globalization.CultureInfo.InvariantCulture))];
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                // Bend and family of parts tables have no GetOrigin.
            }

            tables.Add(new { handle = context.Register(table, kind, Document(draft)), kind, origin, columns = headers, count, rows });
        }

        foreach (PartsList list in Late.Items(draft.PartsLists).OfType<PartsList>())
        {
            Add("parts_list", list, list.Columns, list.Rows.Count);
        }

        foreach (DraftBendTable table in Late.Items(draft.DraftBendTables).OfType<DraftBendTable>())
        {
            Add("bend_table", table, table.Columns, table.Rows.Count);
        }

        foreach (ToleranceTable table in Late.Items(draft.ToleranceTables).OfType<ToleranceTable>())
        {
            Add("tolerance_table", table, table.Columns, table.Rows.Count);
        }

        foreach (FOPTable table in Late.Items(draft.FOPTables).OfType<FOPTable>())
        {
            Add("family_table", table, table.Columns, table.Rows.Count);
        }

        foreach (BlockTable table in Late.Items(draft.BlockTables).OfType<BlockTable>())
        {
            Add("block_table", table, table.Columns, table.Rows.Count);
        }

        foreach (HoleTable2 table in Late.Items(draft.HoleTables2).OfType<HoleTable2>())
        {
            Add("hole_table", table, table.Columns, table.Rows.Count);
        }

        return new { document = Document(draft).Name, count = tables.Count, tables };
    }

    private static object TableResult(SeContext context, DraftDocument draft, object table, string kind, TableColumns columns, int count,
        TableAnchorPoint anchor, double x, double y)
    {
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, columns, count);
        return new
        {
            handle = context.Register(table, kind, Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            columns = headers,
            count,
            rows,
        };
    }

    /// <summary>
    /// Tolerance table of a view: one row per dimension of the view with a fit (nominal, class, deviations), as the ISO saved
    /// setting lists them (verified: a 60 mm dimension with H7 gives one row 60 | H7). A view whose dimensions have no fit gives
    /// an empty table, which is deleted.
    /// </summary>
    public static object ToleranceTable(SeContext context, string? viewHandle, double[]? position, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        ToleranceTable table;
        try
        {
            table = draft.ToleranceTables.AddByDrawingView(view, style ?? "", 1);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("tolerance_table_failed", $"Solid Edge could not create the tolerance table: {ex.Message.Trim()}",
                style is null ? "Give a view with dimensions that have a fit (se_dimension tolerance fit)." : "Check the saved setting name in 'style' (e.g. ISO).", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("tolerance_table_failed", "The tolerance table came out empty: no dimension of the view has a fit.",
                "Give dimensions of the view a fit with se_dimension tolerance (fit, e.g. hole H7), then make the table.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, table.Columns, table.Rows.Count);
        return new
        {
            handle = context.Register(table, "tolerance_table", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            columns = headers,
            count = table.Rows.Count,
            rows,
        };
    }

    /// <summary>
    /// Bend table of a view of a sheet metal part (its flat pattern or the bent part; verified: both list the same bends): one
    /// row per bend (sequence, feature, radius, angle, direction, included angle), placed like a parts list. A view without
    /// bends gives an empty table, which is deleted.
    /// </summary>
    public static object BendTable(SeContext context, string? viewHandle, double[]? position, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (LinkOf(view) is not { } link || link.ModelType != ModelLinkTypeConstants.igPartLink)
        {
            throw new ToolException("wrong_view", "A bend table needs a view of a sheet metal part.", "Use the flat pattern view (se_drawing base_view flat=true) of the part.");
        }

        DraftBendTable table;
        try
        {
            table = draft.DraftBendTables.Add(view, style ?? "", 0, 1);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("bend_table_failed", $"Solid Edge could not create the bend table: {ex.Message.Trim()}",
                style is null ? "Give the flat pattern view (base_view flat) of a sheet metal part with bends." : "Check the saved setting name in 'style'.", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("bend_table_failed", "The bend table came out empty: the view shows no bends.",
                "Give the flat pattern view (base_view flat) of a sheet metal part with bends.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, table.Columns, table.Rows.Count);
        return new
        {
            handle = context.Register(table, "bend_table", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            columns = headers,
            count = table.Rows.Count,
            rows,
        };
    }

    /// <summary>
    /// Hole table of a view (HoleTables2.Add by user selection, verified in Solid Edge 2026): an origin at a vertex or circle
    /// centre of the view (AddNewXYoriginEx with its sheet reference and key point) and one row per circle (AddHole with the
    /// circle's reference and origin 1; origin 0 makes Solid Edge throw a server fault, and attaching the whole view gives
    /// E_UNEXPECTED). Each row gives the hole label, X and Y from the origin and the size: the top view of a plate with holes of
    /// 6.6 at (15, 15) and (85, 15) and 12 at (50, 40), with the origin at its corner (100, 0), lists 1.1 | -85 | 15 | 6.6,
    /// 1.2 | -15 | 15 | 6.6 and 1.3 | -50 | 40 | 12 (mm). The table stays after saving and reopening.
    /// </summary>
    public static object HoleTable(SeContext context, string? viewHandle, double[]? points, double[]? position, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = points is null ? [] : Geo.Points3(points, "points", 1);
        List<Graphic> graphics = Graphics(view);
        Pick origin;
        if (model.Count > 0)
        {
            origin = PickAt(view, graphics, model[0], false);
            if (!origin.KeyPoint)
            {
                throw ToolException.InvalidArgument("The origin of a hole table (the first point) must be a vertex or a circle centre of the view.",
                    "Give the model point of a corner or a hole centre (se_geometry lists edges with their ends and circles with their centres).");
            }
        }
        else
        {
            // As a person would place it: the lower left corner of the outline.
            (Graphic Graphic, (double X, double Y) Point)[] ends = [.. graphics.Where(g => g.Shape == Shape.Line).SelectMany(g => new[] { (g, g.Start), (g, g.End) })];
            if (ends.Length == 0)
            {
                throw new ToolException("hole_table_failed", "The view has no straight edges to put the origin of the hole table on.",
                    "Give the origin in 'points' (a vertex or a circle centre of the model).");
            }

            (Graphic corner, (double X, double Y) at) = ends.MinBy(e => e.Point.X + e.Point.Y);
            origin = new Pick(corner, true, ToSheet(view, at));
        }

        List<Graphic> shown = [.. graphics.Where(g => g.Shape == Shape.Circle)];
        bool stale = model.Count <= 1 && shown.Count > 0 && !view.IsUpToDate;
        List<Graphic>? holes = model.Count > 1 || stale ? null : HoleCircles(view, shown);
        List<Graphic> circles = model.Count > 1
            ? [.. model.Skip(1).Select(p => Nearest(view, graphics, ModelToView(view, p), p, g => g.Shape == Shape.Circle, "circle"))]
            : holes ?? shown;
        if (circles.Count == 0)
        {
            throw ToolException.NotFound(shown.Count == 0 ? "The view shows no circles (holes)." : $"None of the {shown.Count} circles of the view is on a hole (a concave cylindrical or conical face of the part along the view): they are the outline, bosses or edges of chamfers and rounds.",
                shown.Count == 0 ? "Use a view that looks along the holes (e.g. the top view of a plate)." : "Give the holes in 'points' (after the origin) if they should be listed.");
        }

        string? note = stale
            ? "The view is out of date with its model, so every circle it shows was listed; update it (se_drawing update) to list only the holes."
            : holes is not null && holes.Count < shown.Count
            ? $"{shown.Count - holes.Count} circle(s) of the view were left out: not on a hole (the outline, a boss, the edge of a chamfer or a round) or a second circle of a hole listed by its narrowest one (a counterbore, countersink or taper); give the holes in 'points' to choose them."
            : null;

        if (circles.Distinct().Count() != circles.Count)
        {
            throw ToolException.InvalidArgument("Two points pick the same circle of the view.", "Give one point per hole.");
        }

        HoleTable2 table;
        try
        {
            table = ((HoleTables2)draft.HoleTables2).Add(style ?? "", HoleTableType.igByUserSelection);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("hole_table_failed", $"Solid Edge could not create the hole table: {ex.Message.Trim()}",
                style is null ? null : "Check the saved setting name in 'style'.", ex);
        }

        try
        {
            table.AddNewXYoriginEx(origin.Graphic.Reference, KeyPointOf(view, origin));
            foreach (Graphic circle in circles)
            {
                table.AddHole(circle.Reference, 1);
            }
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex))
        {
            table.Delete();
            throw new ToolException("hole_table_failed", $"Solid Edge rejected the hole table: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Give a vertex or circle centre of the view as the origin and circles of the same view as the holes.", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("hole_table_failed", "The hole table came out empty.", "Give circles of the view as the holes.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.Pages.Item(1).SetOrigin(x, y);
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, table.Columns, table.Rows.Count);
        return new
        {
            handle = context.Register(table, "hole_table", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            holeOrigin = new[] { Geo.Mm(origin.Sheet.X), Geo.Mm(origin.Sheet.Y) },
            columns = headers,
            count = table.Rows.Count,
            rows,
            note,
        };
    }

    /// <summary>
    /// The circles of a part view that are holes, one per hole: centred on the axis of a cylindrical or conical face of the
    /// model whose outward normal points to the axis (the material is outside it), the axis along the view direction, with a
    /// radius the face has; of the circles of one hole, the narrowest. The outline of a round part, a boss and the edge of a
    /// chamfer are left out (verified: every circle of the top view of a disc of 80 with a 1 mm chamfer made rows of 78 and
    /// 80), and so are the second circles of a counterbore, a countersink and a tapered hole (verified: a counterbore of 11
    /// over 6.6 made two rows). Null when the model cannot be read this way (an assembly, a flat pattern view, a body that is
    /// a mesh): every circle is then kept.
    /// </summary>
    private static List<Graphic>? HoleCircles(DrawingView view, List<Graphic> circles)
    {
        if (circles.Count == 0 || circles.Any(c => Late.TryGet(c.Value, "ModelMember") is { } member && Late.TryGet(member, "IsFlat") is true)
            || LinkOf(view) is not { ModelType: ModelLinkTypeConstants.igPartLink } link
            || Late.TryGet(link, "ModelDocument") is not SolidEdgeDocument document
            || ModelDocument.TryGet(document)?.TryBodies() is not { Count: > 0 } bodies)
        {
            return null;
        }

        const double tolerance = 1e-5;
        var walls = new List<((double X, double Y) Centre, double Least, double Most)>();
        foreach ((_, SolidEdgeGeometry.Body body) in bodies)
        {
            IEnumerable<SolidEdgeGeometry.Face> faces = new[] { SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryCylinder, SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryCone }
                .SelectMany(query => Late.Items(body.Faces[query]).OfType<SolidEdgeGeometry.Face>());
            foreach (SolidEdgeGeometry.Face face in faces)
            {
                Array axisPoint = new double[3];
                Array axisDirection = new double[3];
                switch (face.Geometry)
                {
                    case SolidEdgeGeometry.Cylinder cylinder:
                        cylinder.GetCylinderData(ref axisPoint, ref axisDirection, out _);
                        break;
                    case SolidEdgeGeometry.Cone cone:
                        cone.GetConeData(ref axisPoint, ref axisDirection, out _, out _, out _);
                        break;
                    default:
                        continue;
                }

                double[] a = [.. Geo.Doubles(axisPoint)];
                double[] d = [.. Geo.Doubles(axisDirection)];
                (double X, double Y) centre = ModelToView(view, (a[0], a[1], a[2]));
                (double X, double Y) further = ModelToView(view, (a[0] + d[0], a[1] + d[1], a[2] + d[2]));
                if (Distance(centre, further) >= tolerance)
                {
                    continue;
                }

                // The radius at the middle and at the corners of the face's parameter range: a cone's two ends, a cylinder's one.
                Array min = new double[2];
                Array max = new double[2];
                face.GetParamRange(ref min, ref max);
                double[] lo = [.. Geo.Doubles(min)];
                double[] hi = [.. Geo.Doubles(max)];
                double Radial(double[] p, out double[] off)
                {
                    double along = (p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1] + (p[2] - a[2]) * d[2];
                    off = [p[0] - a[0] - along * d[0], p[1] - a[1] - along * d[1], p[2] - a[2] - along * d[2]];
                    return Math.Sqrt(off.Sum(x => x * x));
                }

                Array param = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2 };
                Array point = new double[3];
                Array normal = new double[3];
                face.GetPointAtParam(1, ref param, ref point);
                face.GetNormal(1, ref param, ref normal);
                double[] n = [.. Geo.Doubles(normal)];
                Radial([.. Geo.Doubles(point)], out double[] radial);
                if (Enumerable.Range(0, 3).Sum(i => n[i] * radial[i]) >= 0)
                {
                    continue;
                }

                var radii = new List<double>();
                double[][] corners = [[lo[0], lo[1]], [lo[0], hi[1]], [hi[0], lo[1]], [hi[0], hi[1]]];
                foreach (double[] corner in corners)
                {
                    Array at = corner;
                    face.GetPointAtParam(1, ref at, ref point);
                    radii.Add(Radial([.. Geo.Doubles(point)], out _));
                }

                walls.Add((centre, radii.Min(), radii.Max()));
            }
        }

        return [.. circles
            .Where(c => walls.Any(w => Distance(c.Centre, w.Centre) < tolerance && c.Radius > w.Least - tolerance && c.Radius < w.Most + tolerance))
            .GroupBy(c => walls.First(w => Distance(c.Centre, w.Centre) < tolerance).Centre)
            .Select(hole => hole.MinBy(c => c.Radius)!)];
    }

    /// <summary>Key point index of a picked vertex or centre for the Solid Edge calls that take an element and a key point.</summary>
    private static KeypointIndexConstants KeyPointOf(DrawingView view, Pick pick)
    {
        double Off((double X, double Y) p) => Distance(ToSheet(view, p), pick.Sheet);
        return pick.Graphic.Shape switch
        {
            Shape.Line => Off(pick.Graphic.Start) <= Off(pick.Graphic.End) ? KeypointIndexConstants.igLineStart : KeypointIndexConstants.igLineEnd,
            Shape.Circle => KeypointIndexConstants.igCircleCenter,
            _ => new[] { (KeypointIndexConstants.igArcCenter, Off(pick.Graphic.Centre)), (KeypointIndexConstants.igArcStart, Off(pick.Graphic.Start)),
                (KeypointIndexConstants.igArcEnd, Off(pick.Graphic.End)) }.MinBy(k => k.Item2).Item1,
        };
    }

    /// <summary>
    /// Corner of a table: its upper left corner at the position, or, as a person would place it (Solid Edge leaves it outside
    /// the frame), its lower right corner above the title block.
    /// </summary>
    private static (TableAnchorPoint Anchor, double X, double Y) TableCorner(DrawingView view, double[]? position)
    {
        if (position is not null)
        {
            (double x, double y) = Geo.Point2(position, "position");
            return (TableAnchorPoint.igUpperLeft, x, y);
        }

        (double right, double top) = AboveTitleBlock(SheetOf(view));
        return (TableAnchorPoint.igLowerRight, right, top);
    }

    /// <summary>
    /// Lower right corner of the gap above the title block, deduced from the lines of the sheet background: the right edge of the frame
    /// (the longest vertical line in the right half) and the highest horizontal line in the lower half that reaches that edge
    /// without crossing the whole frame. Without a background, at a margin from the lower right corner of the sheet.
    /// </summary>
    private static (double X, double Y) AboveTitleBlock(Sheet sheet)
    {
        double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
        var lines = new List<(double X1, double Y1, double X2, double Y2)>();
        try
        {
            if (sheet.Background is Sheet back)
            {
                foreach (SolidEdgeFrameworkSupport.Line2d line in Late.Items(back.Lines2d).OfType<SolidEdgeFrameworkSupport.Line2d>())
                {
                    line.GetStartPoint(out double x1, out double y1);
                    line.GetEndPoint(out double x2, out double y2);
                    lines.Add((x1, y1, x2, y2));
                }
            }
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Background without readable lines: the sheet corner is used.
        }

        var vertical = lines.Where(l => Math.Abs(l.X1 - l.X2) < 1e-6 && l.X1 > width / 2).ToList();
        if (vertical.Count == 0)
        {
            return (width - Margin, Margin);
        }

        double right = vertical.MaxBy(l => Math.Abs(l.Y2 - l.Y1)).X1;
        var horizontal = lines.Where(l => Math.Abs(l.Y1 - l.Y2) < 1e-6 && l.Y1 < height / 2).ToList();
        double frameWidth = horizontal.Count == 0 ? width : horizontal.Max(l => Math.Abs(l.X2 - l.X1));
        double top = horizontal.Where(l => Math.Abs(Math.Max(l.X1, l.X2) - right) < 1e-4 && Math.Abs(l.X2 - l.X1) < 0.8 * frameWidth)
            .Select(l => l.Y1).DefaultIfEmpty(horizontal.Select(l => l.Y1).DefaultIfEmpty(Margin).Min()).Max();
        return (right, top);
    }

    private static object DescribePartsList(SeContext context, DraftDocument draft, PartsList list)
    {
        list.GetOrigin(out double x, out double y);
        int count = list.Rows.Count;
        (List<string?> headers, List<string?[]> rows) = TableContent(context, list, list.Columns, count);
        return new
        {
            handle = context.Register(list, "parts_list", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            columns = headers,
            count,
            rows,
            savedSettings = SavedSettings(list),
        };
    }

    /// <summary>
    /// Names of the saved settings of the parts list ('style' of parts_list), which each template and installation defines:
    /// the columns and their properties come from the one used. Null when Solid Edge gives none.
    /// </summary>
    private static string[]? SavedSettings(PartsList list)
    {
        try
        {
            Array names = Array.Empty<object>();
            list.GetListOfSavedSettings(out int count, ref names);
            string[] found = [.. names.Cast<object?>().Take(count).Select(o => o?.ToString() ?? "").Where(n => n.Length > 0)];
            return found.Length > 0 ? found : null;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Headers and every row of the shown columns of a table of a drawing: a parts list, a hole, bend, tolerance, family or
    /// block table of se_drawing, or a table of se_sheet_content.
    /// </summary>
    internal static (List<string?> Headers, List<string?[]> Rows) TableCells(SeContext context, string handle)
    {
        object item = context.Handles.Resolve(handle, "table");
        if (item is not (SolidEdgeDraft.PartsList or DraftBendTable or SolidEdgeDraft.ToleranceTable or FOPTable or SolidEdgeDraft.BlockTable or HoleTable2 or Table)
            || Late.Get(item, "Columns") is not TableColumns columns || Late.Get(item, "Rows") is not { } rows)
        {
            throw ToolException.InvalidArgument($"'{handle}' is not a table of a drawing.", "List the tables with se_drawing tables or se_sheet_content list.");
        }

        return TableContent(context, item, columns, Late.Count(rows), int.MaxValue);
    }

    /// <summary>Headers and cell values of the shown columns of a table (parts list, bend table), the first rows up to the list limit or 'max'.</summary>
    private static (List<string?> Headers, List<string?[]> Rows) TableContent(SeContext context, object table, TableColumns columns, int count, int? max = null)
    {
        var shown = new List<int>();
        var headers = new List<string?>();
        for (int c = 1, total = columns.Count; c <= total; c++)
        {
            TableColumn column = columns.Item(c);
            if (!column.Show)
            {
                continue;
            }

            shown.Add(c);
            headers.Add(Late.TryGetString(column, "HeaderRowValue") ?? column.Header);
        }

        var rows = new List<string?[]>();
        for (int r = 1; r <= count && r <= (max ?? context.MaxListItems); r++)
        {
            int row = r;
            rows.Add(shown.Select(c => (Late.TryGet(table, "Cell", row, c) as TableCell)?.value).ToArray());
        }

        return (headers, rows);
    }
}
