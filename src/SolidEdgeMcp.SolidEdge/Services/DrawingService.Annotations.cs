using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Balloon = SolidEdgeFrameworkSupport.Balloon;
using Balloons = SolidEdgeFrameworkSupport.Balloons;
using CenterLine = SolidEdgeFrameworkSupport.CenterLine;
using CenterLines = SolidEdgeFrameworkSupport.CenterLines;
using CenterMark = SolidEdgeFrameworkSupport.CenterMark;
using CenterMarks = SolidEdgeFrameworkSupport.CenterMarks;
using DatumFrame = SolidEdgeFrameworkSupport.DatumFrame;
using DatumFrames = SolidEdgeFrameworkSupport.DatumFrames;
using FeatureControlFrame = SolidEdgeFrameworkSupport.FeatureControlFrame;
using FeatureControlFrames = SolidEdgeFrameworkSupport.FeatureControlFrames;
using FinishType = SolidEdgeFrameworkSupport.DimSurfTextureSymTypeConstants;
using Leader = SolidEdgeFrameworkSupport.Leader;
using Leaders = SolidEdgeFrameworkSupport.Leaders;
using SurfaceFinishSymbol = SolidEdgeFrameworkSupport.SurfaceFinishSymbol;
using SurfaceFinishSymbols = SolidEdgeFrameworkSupport.SurfaceFinishSymbols;
using TextBox = SolidEdgeFrameworkSupport.TextBox;
using TextBoxes = SolidEdgeFrameworkSupport.TextBoxes;
using WeldSymbol = SolidEdgeFrameworkSupport.WeldSymbol;
using WeldSymbols = SolidEdgeFrameworkSupport.WeldSymbols;
using WeldType = SolidEdgeFrameworkSupport.DimWeldTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Drawing annotations: text, balloons, callouts, centre marks, centre lines, leaders, surface finish symbols, datum frames,
/// feature control frames and weld symbols. Annotations attached to a view pick its edges with model points, as dimensions do, so they
/// stay associative; sheet positions are mm from the lower-left corner of the sheet.
/// </summary>
public static partial class DrawingService
{
    public static readonly IReadOnlyList<string> AnnotationKinds =
        ["text", "balloon", "center_mark", "centerline", "leader", "surface_finish", "datum", "feature_control_frame", "weld"];

    public static readonly IReadOnlyList<string> FinishTypes = ["basic", "machined", "no_material_removal"];

    /// <summary>
    /// Codes of the geometric characteristics in the text of a feature control frame, verified by drawing them: %ST is the
    /// statistical tolerance, not straightness, and %PA/%PE are not codes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Characteristics = new Dictionary<string, string>
    {
        ["straightness"] = "%SR", ["flatness"] = "%FL", ["circularity"] = "%CI", ["cylindricity"] = "%CY",
        ["line_profile"] = "%PL", ["surface_profile"] = "%PS", ["angularity"] = "%AN", ["perpendicularity"] = "%PP",
        ["parallelism"] = "%PR", ["position"] = "%PO", ["concentricity"] = "%CO", ["symmetry"] = "%SY",
        ["circular_runout"] = "%CR", ["total_runout"] = "%TR",
    };

    /// <summary>Separator between the cells of a feature control frame.</summary>
    private const string FrameCell = "%VB";

    /// <summary>Default offset (m of sheet) of a balloon or leader end from the point it points at.</summary>
    private const double AnnotationOffset = 0.015;

    public static object Annotations(SeContext context, string? documentHandle, string? sheetRef, string? kind, int offset, int limit)
    {
        if (kind is not null && !AnnotationKinds.Contains(kind))
        {
            throw ToolException.InvalidArgument($"Unknown annotation kind '{kind}'.", $"Use one of: {string.Join(", ", AnnotationKinds)}.");
        }

        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        var rows = new List<object>();
        foreach ((string name, object item) in AnnotationsOf(sheet))
        {
            if (kind is null || kind == name)
            {
                rows.Add(DescribeAnnotation(context, draft, name, item));
            }
        }

        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        List<object> page = rows.Skip(start).Take(take).ToList();
        return new { sheet = sheet.Name, count = rows.Count, offset = start, returned = page.Count, more = start + page.Count < rows.Count, annotations = page };
    }

    public static object AddText(SeContext context, string? documentHandle, string? sheetRef, string? text, double[]? position)
    {
        string content = RequireText(text, "text");
        (double x, double y) = Geo.Point2(position ?? throw ToolException.InvalidArgument("'position' [x, y] in mm on the sheet is required for a text box."), "position");
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        TextBox box = ((TextBoxes)sheet.TextBoxes).Add(x, y, 0);
        return Checked(context, draft, "text", box, () =>
        {
            box.Text = content;
            if (box.Text != content)
            {
                throw new ToolException("annotation_failed", "Solid Edge did not keep the text of the text box.", "Avoid control characters in 'text'.");
            }
        });
    }

    /// <summary>Balloon (or callout) with a leader to the edge, vertex or circle picked by a model point.</summary>
    public static object AddBalloon(SeContext context, string? viewHandle, double[]? points, double[]? position, string? text, bool callout)
    {
        string content = RequireText(text, "text");
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y) at = position is null ? (pick.Sheet.X + AnnotationOffset, pick.Sheet.Y + AnnotationOffset) : Geo.Point2(position, "position");
        Balloon balloon = ((Balloons)SheetOf(view).Balloons).Add(at.X, at.Y, 0);
        return Checked(context, draft, "balloon", balloon, () =>
        {
            balloon.SetTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
            balloon.Leader = true;
            if (callout)
            {
                balloon.Callout = 1;
            }

            balloon.BalloonText = content;
            // Verified: SetTerminator moves the balloon onto the terminator; a vertex at the requested position puts the
            // balloon back there, with the leader running from the terminator.
            balloon.AddVertex(at.X, at.Y, 0);
            if (!balloon.IsTerminatorAttachedToEntity)
            {
                throw new ToolException("annotation_failed", "The balloon's leader is not attached to the view.",
                    "Give a model point on an edge, vertex or circle seen in the view.");
            }

            RequireAround(balloon, at, "balloon");
        });
    }

    /// <summary>Centre mark of the circle or arc picked by its centre or a point on it.</summary>
    public static object AddCenterMark(SeContext context, string? viewHandle, double[]? points)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Graphic curve = PickCurve(view, Graphics(view), Geo.Points3(points, "points", 1)[0]);
        (double X, double Y) centre = ToSheet(view, curve.Centre);
        CenterMark mark = ((CenterMarks)SheetOf(view).CenterMarks).AddByCenter(curve.Reference, centre.X, centre.Y, 0, true);
        return Checked(context, draft, "center_mark", mark, () => RequireAround(mark, centre, "centre mark"));
    }

    /// <summary>
    /// Centre line between two circles or arcs (centre to centre) or between two straight edges (their mid line, e.g. the
    /// axis of a shaft seen from the side).
    /// </summary>
    public static object AddCenterLine(SeContext context, string? viewHandle, double[]? points)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 2);
        if (model.Count != 2)
        {
            throw ToolException.InvalidArgument("A centre line needs two model points: one on each circle (or centre) or one on each straight edge.");
        }

        List<Graphic> graphics = Graphics(view);
        Graphic? curve1 = TryPickCurve(view, graphics, model[0]);
        Graphic? curve2 = TryPickCurve(view, graphics, model[1]);
        var lines = (CenterLines)SheetOf(view).CenterLines;
        CenterLine line;
        (double X, double Y) expected;
        if (curve1 is not null && curve2 is not null)
        {
            if (ReferenceEquals(curve1.Value, curve2.Value))
            {
                throw ToolException.InvalidArgument("Both points pick the same circle.", "Use action 'center_mark' for one circle.");
            }

            (double X, double Y) c1 = ToSheet(view, curve1.Centre), c2 = ToSheet(view, curve2.Centre);
            line = lines.AddByStartEnd(curve1.Reference, c1.X, c1.Y, 0, true, curve2.Reference, c2.X, c2.Y, 0, true);
            expected = Mid(c1, c2);
        }
        else
        {
            Graphic edge1 = Nearest(graphics, ModelToView(view, model[0]), model[0], g => g.Shape == Shape.Line, "straight edge");
            Graphic edge2 = Nearest(graphics, ModelToView(view, model[1]), model[1], g => g.Shape == Shape.Line, "straight edge");
            if (ReferenceEquals(edge1.Value, edge2.Value))
            {
                throw ToolException.InvalidArgument("Both points pick the same edge.", "Give a point on each of the two edges.");
            }

            line = lines.AddBetweenTwoLines(edge1.Reference, edge2.Reference);
            expected = Mid(Mid(ToSheet(view, edge1.Start), ToSheet(view, edge1.End)), Mid(ToSheet(view, edge2.Start), ToSheet(view, edge2.End)));
        }

        return Checked(context, draft, "centerline", line, () => RequireAround(line, expected, "centre line"));
    }

    /// <summary>Leader from the picked edge, vertex or circle to a point of the sheet.</summary>
    public static object AddLeader(SeContext context, string? viewHandle, double[]? points, double[]? position)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y) end = position is null ? (pick.Sheet.X + AnnotationOffset, pick.Sheet.Y + AnnotationOffset) : Geo.Point2(position, "position");
        Leader leader = ((Leaders)SheetOf(view).Leaders).AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint, end.X, end.Y, 0);
        return Checked(context, draft, "leader", leader, () =>
        {
            if (!leader.IsTerminatorAttachedToEntity)
            {
                throw new ToolException("annotation_failed", "The leader is not attached to the view.", "Give a model point on an edge, vertex or circle seen in the view.");
            }
        });
    }

    /// <summary>
    /// Surface finish symbol on the edge picked by a model point; with a position, at the end of a leader from the edge.
    /// </summary>
    public static object AddSurfaceFinish(SeContext context, string? viewHandle, double[]? points, double[]? position, string? roughness, string finish)
    {
        FinishType type = finish switch
        {
            "basic" => FinishType.igDimSurfaceFinishBasic,
            "machined" => FinishType.igDimSurfaceFinishMachined,
            "no_material_removal" => FinishType.igDimSurfaceFinishNoMaterialRemoval,
            _ => throw ToolException.InvalidArgument($"Unknown finish '{finish}'.", $"Use one of: {string.Join(", ", FinishTypes)}."),
        };
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y)? at = position is null ? null : Geo.Point2(position, "position");
        SurfaceFinishSymbol symbol = ((SurfaceFinishSymbols)SheetOf(view).SurfaceFinishSymbols)
            .AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "surface_finish", symbol, () =>
        {
            symbol.SurfaceFinishSymbol = type;
            if (!string.IsNullOrEmpty(roughness))
            {
                symbol.RoughnessValue = roughness;
                if (symbol.RoughnessValue != roughness)
                {
                    throw new ToolException("annotation_failed", "Solid Edge did not keep the roughness of the symbol.", "Avoid control characters in 'text'.");
                }
            }

            OnLeader(symbol, at, "surface finish symbol");
        });
    }

    /// <summary>Datum frame (letter) on the edge picked by a model point; with a position, at the end of a leader.</summary>
    public static object AddDatum(SeContext context, string? viewHandle, double[]? points, double[]? position, string? letter)
    {
        string datum = RequireText(letter, "text");
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y)? at = position is null ? null : Geo.Point2(position, "position");
        DatumFrame frame = ((DatumFrames)SheetOf(view).DatumFrames).AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "datum", frame, () =>
        {
            frame.Datum = datum;
            if (frame.Datum != datum)
            {
                throw new ToolException("annotation_failed", "Solid Edge did not keep the datum letter.", "Use a short label such as 'A'.");
            }

            OnLeader(frame, at, "datum frame");
        });
    }

    /// <summary>
    /// Feature control frame: on a leader from the edge picked by a model point of a view, or free at a sheet position. The
    /// frame is the characteristic, the tolerance and the datums, or the whole text in Solid Edge codes; up to three more
    /// rows below it, each with its own characteristic or, in a composite frame, sharing the characteristic of the first.
    /// </summary>
    public static object AddFeatureControlFrame(SeContext context, string? documentHandle, string? sheetRef, string? viewHandle, double[]? points,
        double[]? position, string? characteristic, string? tolerance, string[]? datums, string? text, string[]? rows = null, bool composite = false)
    {
        string content = string.IsNullOrEmpty(text) ? FrameText(characteristic, tolerance, datums) : text;
        string[] extra = RowTexts(rows, composite);
        DraftDocument draft;
        FeatureControlFrame frame;
        (double X, double Y) at;
        if (viewHandle is null)
        {
            if (points is not null)
            {
                throw ToolException.InvalidArgument("'points' picks an edge of a view: give 'view' too, or only 'position' for a free frame.");
            }

            at = Geo.Point2(position ?? throw ToolException.InvalidArgument("'position' [x, y] in mm on the sheet is required for a free frame (or give 'view' and 'points')."), "position");
            draft = DraftFor(context, documentHandle, sheetRef);
            Sheet sheet = ResolveSheet(context, draft, sheetRef);
            frame = ((FeatureControlFrames)sheet.FeatureControlFrames).Add(at.X, at.Y, 0);
            return Checked(context, draft, "feature_control_frame", frame, () =>
            {
                SetFrame(frame, content);
                SetRows(frame, extra, composite);
                RequireAround(frame, at, "feature control frame");
            });
        }

        DrawingView view = ResolveView(context, viewHandle);
        draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        at = position is null ? (pick.Sheet.X + AnnotationOffset, pick.Sheet.Y + AnnotationOffset) : Geo.Point2(position, "position");
        frame = ((FeatureControlFrames)SheetOf(view).FeatureControlFrames).AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "feature_control_frame", frame, () =>
        {
            SetFrame(frame, content);
            SetRows(frame, extra, composite);
            OnLeader(frame, at, "feature control frame");
        });
    }

    /// <summary>Frame text: characteristic | tolerance | datums. ⌀ or Ø in the tolerance becomes the diameter code.</summary>
    private static string FrameText(string? characteristic, string? tolerance, string[]? datums)
    {
        if (characteristic is null || !Characteristics.TryGetValue(characteristic, out string? code))
        {
            throw ToolException.InvalidArgument(characteristic is null ? "'characteristic' is required (or the whole frame in 'text')." : $"Unknown characteristic '{characteristic}'.",
                $"Use one of: {string.Join(", ", Characteristics.Keys)}.");
        }

        string value = RequireText(tolerance, "tolerance").Replace("⌀", "%DI", StringComparison.Ordinal).Replace("Ø", "%DI", StringComparison.Ordinal);
        var cells = new List<string> { code, value };
        foreach (string datum in datums ?? [])
        {
            cells.Add(string.IsNullOrWhiteSpace(datum) ? throw ToolException.InvalidArgument("'datums' cannot contain empty labels.") : datum);
        }

        return string.Join(FrameCell, cells);
    }

    /// <summary>
    /// Texts of the rows below the first: cells split by '|', the first one a characteristic name (not in a composite frame,
    /// whose rows share the characteristic of the first row), ⌀ or Ø as the diameter code; a row with %VB is taken as codes.
    /// </summary>
    private static string[] RowTexts(string[]? rows, bool composite)
    {
        if (rows is null or [])
        {
            return composite ? throw ToolException.InvalidArgument("'composite' needs 'rows': the lower rows, e.g. [\"⌀0.05|A\"].") : [];
        }

        if (rows.Length > 3)
        {
            throw ToolException.InvalidArgument("A feature control frame has at most 4 rows: give up to 3 in 'rows'.");
        }

        return rows.Select(row =>
        {
            if (row.Contains(FrameCell, StringComparison.Ordinal))
            {
                return row;
            }

            string[] cells = row.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Any(string.IsNullOrEmpty))
            {
                throw ToolException.InvalidArgument($"Row '{row}' has an empty cell.", "Write the cells split by '|', e.g. 'perpendicularity|0.02|A'.");
            }

            bool named = Characteristics.TryGetValue(cells[0], out string? code);
            if (named == composite)
            {
                throw ToolException.InvalidArgument(composite
                    ? $"Row '{row}': the rows of a composite frame share the characteristic of the first row; start with the tolerance, e.g. '⌀0.05|A'."
                    : $"Row '{row}' must start with its characteristic, e.g. 'perpendicularity|0.02|A' (or set composite for rows that share the first one).",
                    $"Characteristics: {string.Join(", ", Characteristics.Keys)}.");
            }

            IEnumerable<string> values = cells.Skip(named ? 1 : 0).Select(c => c.Replace("⌀", "%DI", StringComparison.Ordinal).Replace("Ø", "%DI", StringComparison.Ordinal));
            return string.Join(FrameCell, named ? values.Prepend(code!) : values);
        }).ToArray();
    }

    /// <summary>Lower rows of the frame; checked by reading them back and by the frame growing one row height each.</summary>
    private static void SetRows(FeatureControlFrame frame, string[] rows, bool composite)
    {
        if (rows.Length == 0)
        {
            return;
        }

        double[] before = RangeOf(frame);
        frame.CompositeFrame = composite;
        frame.SecondaryFrame = rows[0];
        if (rows.Length > 1)
        {
            frame.TertiaryFrame = rows[1];
        }

        if (rows.Length > 2)
        {
            frame.QuaternaryFrame = rows[2];
        }

        double[] after = RangeOf(frame);
        if (!RowsOf(frame).SequenceEqual(rows) || frame.CompositeFrame != composite
            || after[3] - after[1] < (rows.Length + 1) * (before[3] - before[1]) * 0.9)
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the rows of the feature control frame.", "Check the codes in 'rows'.");
        }
    }

    private static IEnumerable<string> RowsOf(FeatureControlFrame frame) =>
        new[] { frame.SecondaryFrame, frame.TertiaryFrame, frame.QuaternaryFrame }.Where(r => !string.IsNullOrEmpty(r));

    private static void SetFrame(FeatureControlFrame frame, string content)
    {
        frame.PrimaryFrame = content;
        if (frame.PrimaryFrame != content)
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the text of the feature control frame.", "Check the codes in 'text'.");
        }
    }

    /// <summary>
    /// Weld types and their constants for the arrow side (below the reference line, "Bottom" in the API); the other side
    /// ("Top") is the same type 21 lower.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, WeldType> WeldTypes = new Dictionary<string, WeldType>
    {
        ["fillet"] = WeldType.igDimWeldBottomFillet,
        ["square"] = WeldType.igDimWeldBottomSquare,
        ["v_groove"] = WeldType.igDimWeldBottomVGroove,
        ["bevel"] = WeldType.igDimWeldBottomBevel,
        ["u_groove"] = WeldType.igDimWeldBottomUGroove,
        ["j_groove"] = WeldType.igDimWeldBottomJGroove,
        ["spot"] = WeldType.igDimWeldBottomSpot,
        ["seam"] = WeldType.igDimWeldBottomSeam,
        ["plug"] = WeldType.igDimWeldBottomSlot,
        ["backing"] = WeldType.igDimWeldBottomBacking,
        ["surfacing"] = WeldType.igDimWeldBottomSurfacing,
        ["flare_v"] = WeldType.igDimWeldBottomFlareV,
        ["flare_bevel"] = WeldType.igDimWeldBottomFlareBevel,
        ["edge"] = WeldType.igDimWeldBottomEdgeWeld,
    };

    public static readonly IReadOnlyList<string> WeldSides = ["arrow", "other", "both"];

    private const int WeldOtherSide = WeldType.igDimWeldBottomFillet - WeldType.igDimWeldTopFillet;

    /// <summary>How far (m) a weld symbol may reach left of or below its leader: the all-around circle and the arrow-side text.</summary>
    private const double WeldReach = 0.015;

    /// <summary>
    /// Weld symbol on the edge picked by a model point: the weld type on the arrow side, the other side or both, the size
    /// or note (e.g. 'a5'), and the all-around and field-weld flags; the leader ends at the position (default 15 mm up-right),
    /// where the reference line starts.
    /// </summary>
    public static object AddWeld(SeContext context, string? viewHandle, double[]? points, double[]? position, string weld,
        string side, string? size, bool allAround, bool field)
    {
        WeldType arrow = WeldTypes.TryGetValue(weld, out WeldType type) ? type
            : throw ToolException.InvalidArgument($"Unknown weld '{weld}'.", $"Use one of: {string.Join(", ", WeldTypes.Keys)}.");
        bool onArrow = side is "arrow" or "both";
        bool onOther = side is "other" or "both";
        if (!onArrow && !onOther)
        {
            throw ToolException.InvalidArgument($"Unknown side '{side}'.", $"Use one of: {string.Join(", ", WeldSides)}.");
        }

        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y) at = position is null ? (pick.Sheet.X + AnnotationOffset, pick.Sheet.Y + AnnotationOffset) : Geo.Point2(position, "position");
        WeldSymbol symbol = ((WeldSymbols)SheetOf(view).WeldSymbols).Add(at.X, at.Y, 0);
        return Checked(context, draft, "weld", symbol, () =>
        {
            symbol.SetTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
            symbol.Leader = true;
            symbol.AddVertex(at.X, at.Y, 0);
            WeldType other = arrow - WeldOtherSide;
            if (onArrow)
            {
                symbol.BottomType = arrow;
                symbol.BottomNote1 = size ?? "";
            }

            if (onOther)
            {
                symbol.TopType = other;
                symbol.TopNote1 = size ?? "";
            }

            symbol.WeldAllAround = allAround;
            symbol.WeldInField = field;
            // Verified: without the break line (the horizontal shoulder of the leader) Solid Edge draws the body of the symbol
            // at the sheet origin whatever the position; setting it places the reference line at the end of the leader.
            symbol.BreakLine = true;
            if ((onArrow && symbol.BottomType != arrow) || (onOther && symbol.TopType != other)
                || (!string.IsNullOrEmpty(size) && (onArrow ? symbol.BottomNote1 : symbol.TopNote1) != size))
            {
                throw new ToolException("annotation_failed", "Solid Edge did not keep the weld type or size of the symbol.", "Avoid control characters in 'text'.");
            }

            if (!symbol.IsTerminatorAttachedToEntity)
            {
                throw new ToolException("annotation_failed", "The weld symbol's leader is not attached to the view.",
                    "Give a model point on an edge seen in the view.");
            }

            RequireAround(symbol, at, "weld symbol");
            double[] box = RangeOf(symbol);
            if (box[0] < Math.Min(pick.Sheet.X, at.X) - WeldReach || box[1] < Math.Min(pick.Sheet.Y, at.Y) - WeldReach)
            {
                throw new ToolException("annotation_failed",
                    $"The weld symbol was drawn from ({Geo.Mm(box[0])}, {Geo.Mm(box[1])}) mm, away from its leader.", "Give another 'position'.");
            }
        });
    }

    /// <summary>
    /// With a position, draws the symbol on a leader ending there (verified: Leader plus one vertex, as with balloons); then
    /// checks that it is attached to the view and drawn around its position.
    /// </summary>
    private static void OnLeader(object symbol, (double X, double Y)? at, string what)
    {
        if (at is { } end)
        {
            Late.Set(symbol, "Leader", true);
            Late.Call(symbol, "AddVertex", end.X, end.Y, 0.0);
        }

        if (!Convert.ToBoolean(Late.Get(symbol, "IsTerminatorAttachedToEntity"), System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new ToolException("annotation_failed", $"The {what} is not attached to the view.", "Give a model point on an edge, vertex or circle seen in the view.");
        }

        if (at is { } point)
        {
            RequireAround(symbol, point, what);
        }
    }

    public static object DeleteAnnotation(SeContext context, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw ToolException.InvalidArgument("'annotation' is required: a handle from se_annotation 'list' or from the action that created it.");
        }

        object annotation = context.Handles.Resolve(handle, "annotation");
        string kind = KindOf(annotation) ?? throw ToolException.InvalidArgument($"'{handle}' is not a drawing annotation.");
        Late.Call(annotation, "Delete");
        context.Handles.Remove(handle, "the annotation was deleted");
        return new { deleted = handle, kind };
    }

    /// <summary>Runs the checks of a new annotation and deletes it if they fail, so a failed call leaves nothing behind.</summary>
    private static object Checked(SeContext context, DraftDocument draft, string kind, object annotation, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex) when (ex is ToolException or COMException && !ComErrors.IsDisconnected(ex))
        {
            TryDelete(annotation);
            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("annotation_failed", $"Solid Edge rejected the {kind}: {ex.Message.Trim()}", "Check the view, the picked element and the position.", ex);
        }

        return new { annotation = DescribeAnnotation(context, draft, kind, annotation) };
    }

    private static Pick PickOne(DrawingView view, double[]? points)
    {
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 1);
        if (model.Count != 1)
        {
            throw ToolException.InvalidArgument("'points' must be one model point [x, y, z] on the edge, vertex or circle to point at.");
        }

        List<Graphic> graphics = Graphics(view);
        if (graphics.Count == 0)
        {
            throw new ToolException("nothing_to_annotate", "The view has no edges.", "Update the view (se_drawing update) and try again.");
        }

        return PickAt(view, graphics, model[0], false);
    }

    private static Graphic PickCurve(DrawingView view, List<Graphic> graphics, (double X, double Y, double Z) model) =>
        TryPickCurve(view, graphics, model)
        ?? Nearest(graphics, ModelToView(view, model), model, g => g.Shape is Shape.Circle or Shape.Arc, "circle or arc");

    /// <summary>Circle or arc whose centre is the point or which passes through it; null if there is none.</summary>
    private static Graphic? TryPickCurve(DrawingView view, List<Graphic> graphics, (double X, double Y, double Z) model)
    {
        (double X, double Y) p = ModelToView(view, model);
        List<Graphic> curves = graphics.Where(g => g.Shape is Shape.Circle or Shape.Arc).ToList();
        return curves.FirstOrDefault(g => Distance(g.Centre, p) <= KeyTolerance)
            ?? curves.Where(g => DistanceTo(g, p) <= PickTolerance).OrderBy(g => DistanceTo(g, p)).FirstOrDefault();
    }

    /// <summary>The annotation's extent must contain the expected sheet point (within 0.5 mm).</summary>
    private static void RequireAround(object annotation, (double X, double Y) expected, string what)
    {
        double[] box = RangeOf(annotation);
        const double slack = 0.0005;
        if (expected.X < box[0] - slack || expected.X > box[2] + slack || expected.Y < box[1] - slack || expected.Y > box[3] + slack)
        {
            throw new ToolException("annotation_failed",
                $"The {what} was drawn at ({Geo.Mm(box[0])}..{Geo.Mm(box[2])}, {Geo.Mm(box[1])}..{Geo.Mm(box[3])}) mm, away from ({Geo.Mm(expected.X)}, {Geo.Mm(expected.Y)}) mm.",
                "Check the picked circles or edges in the view.");
        }
    }

    /// <summary>Sheet point (mm) where the leader of a balloon or leader is attached, or null if it cannot be read.</summary>
    private static double[]? TerminatorOf(object annotation)
    {
        try
        {
            double x, y;
            switch (annotation)
            {
                case Balloon balloon:
                    balloon.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                case Leader leader:
                    leader.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                case SurfaceFinishSymbol symbol:
                    symbol.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                case DatumFrame datum:
                    datum.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                case WeldSymbol weld:
                    weld.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                case FeatureControlFrame frame when frame.IsTerminatorAttachedToEntity:
                    frame.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                default:
                    return null;
            }

            return [Geo.Mm(x), Geo.Mm(y)];
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return null;
        }
    }

    private static double[] RangeOf(object annotation)
    {
        object?[] values = Late.CallByRef(annotation, "Range", [0.0, 0.0, 0.0, 0.0], 0, 1, 2, 3);
        return values.Select(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static IEnumerable<(string Kind, object Item)> AnnotationsOf(Sheet sheet)
    {
        foreach ((string kind, object? collection) in new (string, object?)[]
        {
            ("text", sheet.TextBoxes), ("balloon", sheet.Balloons), ("center_mark", sheet.CenterMarks),
            ("centerline", sheet.CenterLines), ("leader", sheet.Leaders), ("surface_finish", sheet.SurfaceFinishSymbols),
            ("datum", sheet.DatumFrames), ("feature_control_frame", sheet.FeatureControlFrames),
            ("weld", sheet.WeldSymbols),
        })
        {
            if (collection is null)
            {
                continue;
            }

            foreach (object item in Late.Items(collection))
            {
                yield return (kind, item);
            }
        }
    }

    private static string? KindOf(object annotation) => annotation switch
    {
        TextBox => "text",
        Balloon => "balloon",
        CenterMark => "center_mark",
        CenterLine => "centerline",
        Leader => "leader",
        SurfaceFinishSymbol => "surface_finish",
        DatumFrame => "datum",
        FeatureControlFrame => "feature_control_frame",
        WeldSymbol => "weld",
        _ => null,
    };

    private static object DescribeAnnotation(SeContext context, DraftDocument draft, string kind, object annotation)
    {
        var info = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(annotation, kind, Document(draft)),
            ["kind"] = kind,
        };
        switch (annotation)
        {
            case TextBox box:
                info["text"] = box.Text;
                break;
            case Balloon balloon:
                info["text"] = balloon.BalloonDisplayedText;
                info["callout"] = balloon.Callout != 0;
                info["attached"] = balloon.IsTerminatorAttachedToEntity;
                info["terminator"] = TerminatorOf(balloon);
                break;
            case Leader leader:
                info["attached"] = leader.IsTerminatorAttachedToEntity;
                info["terminator"] = TerminatorOf(leader);
                break;
            case SurfaceFinishSymbol symbol:
                info["text"] = symbol.RoughnessValue;
                info["finish"] = FinishOf(symbol);
                info["attached"] = symbol.IsTerminatorAttachedToEntity;
                info["terminator"] = TerminatorOf(symbol);
                break;
            case DatumFrame datum:
                info["text"] = datum.Datum;
                info["attached"] = datum.IsTerminatorAttachedToEntity;
                info["terminator"] = TerminatorOf(datum);
                break;
            case FeatureControlFrame frame:
                string text = frame.PrimaryFrame;
                info["text"] = text;
                info["characteristic"] = Characteristics.FirstOrDefault(c => text.StartsWith(c.Value + FrameCell, StringComparison.Ordinal) || text == c.Value).Key;
                string[] rows = [.. RowsOf(frame)];
                if (rows.Length > 0)
                {
                    info["rows"] = rows;
                    info["composite"] = frame.CompositeFrame;
                }
                info["attached"] = frame.IsTerminatorAttachedToEntity;
                info["terminator"] = TerminatorOf(frame);
                break;
            case WeldSymbol weld:
                bool arrowSide = weld.BottomType != WeldType.igDimWeldTypeNone;
                bool otherSide = weld.TopType != WeldType.igDimWeldTypeNone;
                WeldType shown = arrowSide ? weld.BottomType : weld.TopType + WeldOtherSide;
                info["weld"] = WeldTypes.FirstOrDefault(w => w.Value == shown).Key;
                info["side"] = arrowSide && otherSide ? "both" : arrowSide ? "arrow" : otherSide ? "other" : null;
                info["text"] = arrowSide ? weld.BottomNote1 : weld.TopNote1;
                info["allAround"] = weld.WeldAllAround;
                info["field"] = weld.WeldInField;
                info["attached"] = weld.IsTerminatorAttachedToEntity;
                info["terminator"] = TerminatorOf(weld);
                break;
        }

        try
        {
            double[] box = RangeOf(annotation);
            info["at"] = new[] { Geo.Mm((box[0] + box[2]) / 2), Geo.Mm((box[1] + box[3]) / 2) };
            info["size"] = new[] { Geo.Mm(box[2] - box[0]), Geo.Mm(box[3] - box[1]) };
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Without a graphic extent (not drawn yet).
        }

        return info;
    }

    /// <summary>Symbol type; Solid Edge rejects reading it (E_INVALIDARG) until it has been set.</summary>
    private static string? FinishOf(SurfaceFinishSymbol symbol)
    {
        try
        {
            return symbol.SurfaceFinishSymbol switch
            {
                FinishType.igDimSurfaceFinishBasic => "basic",
                FinishType.igDimSurfaceFinishMachined => "machined",
                FinishType.igDimSurfaceFinishNoMaterialRemoval => "no_material_removal",
                FinishType other => other.ToString(),
            };
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }
    }

    private static string RequireText(string? text, string name) =>
        string.IsNullOrEmpty(text) ? throw ToolException.InvalidArgument($"'{name}' is required.") : text;
}
