// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Balloon = SolidEdgeFrameworkSupport.Balloon;
using Balloons = SolidEdgeFrameworkSupport.Balloons;
using CenterLine = SolidEdgeFrameworkSupport.CenterLine;
using CenterLines = SolidEdgeFrameworkSupport.CenterLines;
using CenterMark = SolidEdgeFrameworkSupport.CenterMark;
using CenterMarks = SolidEdgeFrameworkSupport.CenterMarks;
using CornerAnnotation = SolidEdgeFrameworkSupport.CornerAnnotation;
using CornerAnnotations = SolidEdgeFrameworkSupport.CornerAnnotations;
using GostWeldSymbol = SolidEdgeFrameworkSupport.GostWeldSymbol;
using GostWeldSymbols = SolidEdgeFrameworkSupport.GostWeldSymbols;
using DatumFrame = SolidEdgeFrameworkSupport.DatumFrame;
using DatumFrames = SolidEdgeFrameworkSupport.DatumFrames;
using FeatureControlFrame = SolidEdgeFrameworkSupport.FeatureControlFrame;
using FeatureControlFrames = SolidEdgeFrameworkSupport.FeatureControlFrames;
using FinishType = SolidEdgeFrameworkSupport.DimSurfTextureSymTypeConstants;
using Leader = SolidEdgeFrameworkSupport.Leader;
using Leaders = SolidEdgeFrameworkSupport.Leaders;
using SurfaceFinishSymbol = SolidEdgeFrameworkSupport.SurfaceFinishSymbol;
using SurfaceFinishSymbols = SolidEdgeFrameworkSupport.SurfaceFinishSymbols;
using BoltHoleCircle = SolidEdgeFrameworkSupport.BoltHoleCircle;
using BoltHoleCircles = SolidEdgeFrameworkSupport.BoltHoleCircles;
using DatumPoint = SolidEdgeFrameworkSupport.DatumPoint;
using DatumPoints = SolidEdgeFrameworkSupport.DatumPoints;
using DatumTarget = SolidEdgeFrameworkSupport.DatumTarget;
using DatumTargets = SolidEdgeFrameworkSupport.DatumTargets;
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
    internal const string FrameCell = "%VB";

    /// <summary>Default offset (m of sheet) of a balloon or leader end from the point it points at.</summary>
    internal const double AnnotationOffset = 0.015;

    public static object Annotations(SeContext context, string? documentHandle, string? sheetRef, AnnotationKind? kind, int offset, int limit)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        var rows = new List<object>();
        foreach ((string name, object item) in AnnotationsOf(sheet))
        {
            if (kind is null || ArgumentNames.Snake(kind.Value) == name)
            {
                rows.Add(DescribeAnnotation(context, draft, name, item));
            }
        }

        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        List<object> page = rows.Skip(start).Take(take).ToList();
        return new { sheet = sheet.Name, count = rows.Count, offset = start, returned = page.Count, more = start + page.Count < rows.Count, annotations = page };
    }

    /// <summary>
    /// The saved notes of the installation's note library (texts kept for reuse in text boxes and callouts), read only:
    /// Application.AddNote has no counterpart to remove a note, so the server does not add any.
    /// </summary>
    public static object SavedNotes(SeContext context)
    {
        string[] names = SavedNoteNames(context);
        return new { count = names.Length, notes = names.Select(n => new { name = n, text = SavedNote(context, n) }).ToList() };
    }

    /// <summary>Verified: with an empty note library GetSavedNoteList fails with E_POINTER (NullReferenceException in .NET).</summary>
    private static string[] SavedNoteNames(SeContext context)
    {
        try
        {
            context.Application.GetSavedNoteList(out Array list);
            return list is null ? [] : [.. list.Cast<object?>().Select(n => n?.ToString() ?? "").Where(n => n.Length > 0)];
        }
        catch (NullReferenceException)
        {
            return [];
        }
    }

    private static string SavedNote(SeContext context, string name)
    {
        string[] names = SavedNoteNames(context);
        string match = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"There is no saved note '{name}'.", names.Length == 0 ? "The note library is empty." : $"Saved notes: {string.Join(", ", names)}.");
        return context.Application.GetSavedNote(match)?.ToString() ?? "";
    }

    public static object AddText(SeContext context, string? documentHandle, string? sheetRef, string? text, double[]? position, string? note = null)
    {
        if (note is not null && text is not null)
        {
            throw ToolException.InvalidArgument("Give 'text' or 'note' (a saved note), not both.");
        }

        // Verified: a text box keeps its lines only when they are separated by '\r' ('\n' and "\r\n" are not kept).
        string content = (note is not null ? SavedNote(context, note) : RequireText(text, "text")).Replace("\r\n", "\r").Replace('\n', '\r');
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

    /// <summary>
    /// Technical requirements: a text box with a title and the requirements numbered below it. Verified:
    /// TechnicalRequirements.Add draws only the title (the requirements are not shown), so they are written as text.
    /// </summary>
    public static object AddRequirements(SeContext context, string? documentHandle, string? sheetRef, string? title, string[]? items, double[]? position)
    {
        if (items is not { Length: > 0 } || items.Any(string.IsNullOrWhiteSpace))
        {
            throw ToolException.InvalidArgument("'items' must list the requirements, one text each.");
        }

        if (items.Any(i => i.Contains('\n') || i.Contains('\r')))
        {
            throw ToolException.InvalidArgument("Each requirement is one line: split a long one into several items.");
        }

        string heading = string.IsNullOrWhiteSpace(title) ? "TECHNICAL REQUIREMENTS" : title.Trim();
        string content = string.Join("\r", items.Select((item, i) => $"{i + 1}. {item.Trim()}").Prepend(heading));
        return AddText(context, documentHandle, sheetRef, content, position);
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
            Graphic edge1 = Nearest(view, graphics, ModelToView(view, model[0]), model[0], g => g.Shape == Shape.Line, "straight edge");
            Graphic edge2 = Nearest(view, graphics, ModelToView(view, model[1]), model[1], g => g.Shape == Shape.Line, "straight edge");
            if (ReferenceEquals(edge1.Value, edge2.Value))
            {
                throw ToolException.InvalidArgument("Both points pick the same edge.", "Give a point on each of the two edges.");
            }

            line = lines.AddBetweenTwoLines(edge1.Reference, edge2.Reference);
            expected = Mid(Mid(ToSheet(view, edge1.Start), ToSheet(view, edge1.End)), Mid(ToSheet(view, edge2.Start), ToSheet(view, edge2.End)));
        }

        return Checked(context, draft, "centerline", line, () => RequireAround(line, expected, "centre line"));
    }

    /// <summary>
    /// Automatic Centerlines: the centre lines of the cylinders and the centre marks of the circles and arcs a view shows.
    /// Verified: with its default range checks on (radii 0 to 0) RetrieveCenterLinesCenterMarks adds no centre marks, so they
    /// are turned off; what it adds goes to the active sheet, so the view's sheet is activated first; IsRetrieve = false
    /// removes the ones of the view.
    /// </summary>
    public static object AutoCenterlines(SeContext context, string? viewHandle, bool connect, bool remove)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Sheet sheet = SheetOf(view);
        if (!Late.SameObject(draft.ActiveSheet, sheet))
        {
            sheet.Activate();
        }

        var before = new HashSet<object>(CentersOf(sheet).Select(c => c.Item), ReferenceEqualityComparer.Instance);
        try
        {
            if (remove)
            {
                view.RetrieveCenterLinesCenterMarks(false);
            }
            else
            {
                view.RetrieveCenterLinesCenterMarks(true, "", true, true, false, connect, true, true, true, true, true, true, false, false, 0, 0, 0, 0);
            }
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("retrieve_failed", $"Solid Edge could not {(remove ? "remove" : "add")} the centre lines and marks: {ex.Message.Trim()}", null, ex);
        }

        List<(string Kind, object Item)> after = [.. CentersOf(sheet)];
        if (remove)
        {
            var left = new HashSet<object>(after.Select(c => c.Item), ReferenceEqualityComparer.Instance);
            return new { view = context.Register(view, "drawing_view", Document(draft)), removed = before.Count(b => !left.Contains(b)), left = after.Count };
        }

        List<object> added = after.Where(c => !before.Contains(c.Item)).Select(c => DescribeAnnotation(context, draft, c.Kind, c.Item)).ToList();
        return new
        {
            view = context.Register(view, "drawing_view", Document(draft)),
            added = added.Count,
            annotations = added,
            note = added.Count == 0 ? "Nothing was added: the view shows no circles, arcs or cylinders without centre lines or marks." : null,
        };
    }

    private static IEnumerable<(string Kind, object Item)> CentersOf(Sheet sheet) =>
        Late.Items(sheet.CenterLines).Select(i => ("centerline", i)).Concat(Late.Items(sheet.CenterMarks).Select(i => ("center_mark", i)));

    /// <summary>Watermark: a text box Solid Edge draws faded behind the drawing (TextBoxes.AddWatermark).</summary>
    public static object AddWatermark(SeContext context, string? documentHandle, string? sheetRef, string? text, double[]? position)
    {
        string content = RequireText(text, "text").Replace("\r\n", "\r").Replace('\n', '\r');
        (double x, double y) = Geo.Point2(position ?? throw ToolException.InvalidArgument("'position' [x, y] in mm on the sheet is required for a watermark."), "position");
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        TextBox box = ((TextBoxes)sheet.TextBoxes).AddWatermark(x, y, 0);
        return Checked(context, draft, "text", box, () =>
        {
            box.Text = content;
            if (box.Text != content || !box.Watermark)
            {
                throw new ToolException("annotation_failed", "Solid Edge did not keep the text or the watermark flag of the text box.", "Avoid control characters in 'text'.");
            }
        });
    }

    /// <summary>
    /// Line Up Text: moves text boxes so that their left, right or centre (x) or bottom, top or middle (y) matches a source
    /// text box, which stays where it is (Sheet.LineupTextEx). Each target is measured afterwards.
    /// </summary>
    public static object LineUpText(SeContext context, string? sourceHandle, string[]? targetHandles, TextAlignment align)
    {
        int index = align switch
        {
            TextAlignment.Left => 0,
            TextAlignment.Right => 1,
            TextAlignment.Center => 2,
            TextAlignment.Bottom => 3,
            TextAlignment.Top => 4,
            TextAlignment.Middle => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(align)),
        };

        TextBox source = TextBoxOf(context, sourceHandle ?? throw ToolException.InvalidArgument("'item' is required: the text box the others line up with."), "item");
        if (targetHandles is not { Length: > 0 } || targetHandles.Distinct(StringComparer.Ordinal).Count() != targetHandles.Length || targetHandles.Contains(sourceHandle))
        {
            throw ToolException.InvalidArgument("'targets' must list the other text boxes to move, each once, without the source ('item').");
        }

        TextBox[] targets = [.. targetHandles.Select(h => TextBoxOf(context, h, "targets"))];
        DraftDocument draft = DraftFor(context, null, sourceHandle);
        Sheet sheet = WorkingSheets(draft).Concat(BackgroundSheets(draft)).FirstOrDefault(s => Late.Items(s.TextBoxes).Any(t => Late.SameObject(t, source)))
            ?? throw new ToolException("not_found", $"'{sourceHandle}' is not on a sheet of the drawing.");
        Array moved = targets.Cast<object>().ToArray();
        try
        {
            sheet.LineupTextEx(source, ref moved, (LineupTextAlignOptionConstants)index, 0.0);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("annotation_failed", $"Solid Edge could not line up the text: {ex.Message.Trim()}", "Give text boxes of the same sheet.", ex);
        }

        double reference = Edge(RangeOf(source), index);
        string[] off = [.. targetHandles.Where((_, i) => Math.Abs(Edge(RangeOf(targets[i]), index) - reference) > 5e-5)];
        if (off.Length > 0)
        {
            throw new ToolException("annotation_failed", $"Solid Edge did not line up {string.Join(", ", off)} with '{sourceHandle}'.", "Give text boxes of the same sheet.");
        }

        return new
        {
            source = DescribeAnnotation(context, draft, "text", source),
            align = ArgumentNames.Snake(align),
            moved = targets.Select(t => DescribeAnnotation(context, draft, "text", t)).ToArray(),
        };
    }

    /// <summary>The coordinate (m) that an alignment matches: x of the left, right or centre, y of the bottom, top or middle.</summary>
    private static double Edge(double[] box, int alignment) => alignment switch
    {
        0 => box[0],
        1 => box[2],
        2 => (box[0] + box[2]) / 2,
        3 => box[1],
        4 => box[3],
        _ => (box[1] + box[3]) / 2,
    };

    private static TextBox TextBoxOf(SeContext context, string handle, string parameter) =>
        context.Handles.Resolve(handle, parameter) as TextBox
        ?? throw ToolException.InvalidArgument($"'{handle}' in '{parameter}' is not a text box.", "Use text handles from se_annotation list (kind text).");

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
    public static object AddSurfaceFinish(SeContext context, string? viewHandle, double[]? points, double[]? position, string? roughness, SurfaceFinishType finish)
    {
        FinishType type = ParseFinish(finish);
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y)? at = position is null ? null : Geo.Point2(position, "position");
        SurfaceFinishSymbol symbol = ((SurfaceFinishSymbols)SheetOf(view).SurfaceFinishSymbols)
            .AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "surface_finish", symbol, () =>
        {
            SetFinish(symbol, type, roughness);
            OnLeader(symbol, at, "surface finish symbol");
        });
    }

    internal static FinishType ParseFinish(SurfaceFinishType finish) => finish switch
    {
        SurfaceFinishType.Basic => FinishType.igDimSurfaceFinishBasic,
        SurfaceFinishType.Machined => FinishType.igDimSurfaceFinishMachined,
        SurfaceFinishType.NoMaterialRemoval => FinishType.igDimSurfaceFinishNoMaterialRemoval,
        _ => throw new ArgumentOutOfRangeException(nameof(finish)),
    };

    internal static void SetFinish(SurfaceFinishSymbol symbol, FinishType type, string? roughness)
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
    }

    /// <summary>
    /// Corner annotation (edge condition, ISO 13715) on the edge picked by a model point: text = the upper deviation,
    /// or "upper|lower" (e.g. "+0.3" for a burr allowed, "-0.1|-0.3" for an undercut range), shown in the ISO 13715 symbol;
    /// with a position, on a leader. Verified: CornerAnnotations.Add(x, y, z) alone makes an annotation with an empty range, so it is attached
    /// to the edge with AddByTerminator; it is drawn (at the picked vertex) once UseISODIN2000CornerAnnot is set.
    /// </summary>
    public static object AddCorner(SeContext context, string? viewHandle, double[]? points, double[]? position, string? text)
    {
        string[] deviations = CornerDeviations(text);
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y)? at = position is null ? null : Geo.Point2(position, "position");
        CornerAnnotation corner = ((CornerAnnotations)SheetOf(view).CornerAnnotations)
            .AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "corner", corner, () =>
        {
            SetDeviations(corner, deviations);
            OnLeader(corner, at, "corner annotation");
        });
    }

    internal static string[] CornerDeviations(string? text)
    {
        string[] deviations = string.IsNullOrWhiteSpace(text)
            ? throw ToolException.InvalidArgument("corner requires 'text': the edge deviation in mm, e.g. \"+0.3\", or \"upper|lower\" for a range, e.g. \"-0.1|-0.3\".")
            : [.. text.Split('|').Select(p => p.Trim())];
        return deviations.Length > 2 || deviations.Any(d => !double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            ? throw ToolException.InvalidArgument($"'text' for a corner annotation is one deviation or two separated by '|', as signed numbers in mm (e.g. \"+0.3\" or \"-0.1|-0.3\"); got '{text}'.")
            : deviations;
    }

    internal static void SetDeviations(CornerAnnotation corner, string[] deviations)
    {
        // Verified: without the ISO/DIN 2000 symbol, setting a deviation fails with E_FAIL and the annotation is not drawn.
        corner.UseISODIN2000CornerAnnot = true;
        corner.UpperToleranceForEnhancedSymbol = deviations[0];
        if (deviations.Length > 1)
        {
            corner.LowerToleranceForEnhancedSymbol = deviations[1];
        }

        if (corner.UpperToleranceForEnhancedSymbol != deviations[0] || deviations.Length > 1 && corner.LowerToleranceForEnhancedSymbol != deviations[1])
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the deviations of the corner annotation.", "Give signed numbers, e.g. \"+0.3\".");
        }
    }

    /// <summary>
    /// Bolt hole circle through the centres of three holes picked by model points (their centres or points on them); the
    /// circle Solid Edge draws is checked against the circle through the three centres.
    /// </summary>
    public static object AddBoltCircle(SeContext context, string? viewHandle, double[]? points)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 3);
        if (model.Count != 3)
        {
            throw ToolException.InvalidArgument("A bolt hole circle needs three model points: the centres of (or points on) three holes of the circle.");
        }

        List<Graphic> graphics = Graphics(view);
        Graphic[] holes = [.. model.Select(p => PickCurve(view, graphics, p))];
        if (holes.Select(h => h.Value).Distinct(ReferenceEqualityComparer.Instance).Count() != 3)
        {
            throw ToolException.InvalidArgument("The three points pick the same hole twice.", "Give one point for each of three different holes.");
        }

        (double X, double Y)[] c = [.. holes.Select(h => ToSheet(view, h.Centre))];
        double d = 2 * ((c[0].X * (c[1].Y - c[2].Y)) + (c[1].X * (c[2].Y - c[0].Y)) + (c[2].X * (c[0].Y - c[1].Y)));
        if (Math.Abs(d) < 1e-12)
        {
            throw ToolException.InvalidArgument("The three hole centres are in a line: they do not make a circle.");
        }

        double s0 = (c[0].X * c[0].X) + (c[0].Y * c[0].Y), s1 = (c[1].X * c[1].X) + (c[1].Y * c[1].Y), s2 = (c[2].X * c[2].X) + (c[2].Y * c[2].Y);
        (double X, double Y) centre = (((s0 * (c[1].Y - c[2].Y)) + (s1 * (c[2].Y - c[0].Y)) + (s2 * (c[0].Y - c[1].Y))) / d,
            ((s0 * (c[2].X - c[1].X)) + (s1 * (c[0].X - c[2].X)) + (s2 * (c[1].X - c[0].X))) / d);
        double radius = Distance(centre, c[0]);
        // KeypointIndexConstants.igCircleCenter is 0 (also the centre of an arc).
        BoltHoleCircle circle = ((BoltHoleCircles)SheetOf(view).BoltHoleCircles).AddBoltHoleCircleBy3Points(holes[0].Reference, 0, holes[1].Reference, 0, holes[2].Reference, 0);
        return Checked(context, draft, "bolt_circle", circle, () =>
        {
            circle.Circle2d(out double cx, out double cy, out double r);
            if (Distance((cx, cy), centre) > 1e-6 || Math.Abs(r - radius) > 1e-6)
            {
                throw new ToolException("annotation_failed",
                    $"The bolt hole circle has its centre at ({Geo.Mm(cx)}, {Geo.Mm(cy)}) mm and a radius of {Geo.Mm(r)} mm instead of ({Geo.Mm(centre.X)}, {Geo.Mm(centre.Y)}) and {Geo.Mm(radius)} mm.",
                    "Pick three holes of the same circle.");
            }
        });
    }

    /// <summary>
    /// Datum target ('A1': reference letter and number) on the edge or vertex picked by a model point; with a position, its
    /// symbol at the end of a leader.
    /// </summary>
    public static object AddDatumTarget(SeContext context, string? viewHandle, double[]? points, double[]? position, string? label)
    {
        (string Letter, string Number) name = DatumTargetLabel(label);
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y)? at = position is null ? null : Geo.Point2(position, "position");
        DatumTarget target = ((DatumTargets)SheetOf(view).DatumTargets).AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "datum_target", target, () =>
        {
            SetLabel(target, name);
            OnLeader(target, at, "datum target");
        });
    }

    internal static (string Letter, string Number) DatumTargetLabel(string? label)
    {
        string name = RequireText(label, "text");
        int split = name.TakeWhile(char.IsLetter).Count();
        return split == 0 || split == name.Length || !name[split..].All(char.IsDigit)
            ? throw ToolException.InvalidArgument($"'{name}' is not a datum target label.", "Give the datum letter and the target number, e.g. 'A1'.")
            : (name[..split], name[split..]);
    }

    internal static void SetLabel(DatumTarget target, (string Letter, string Number) name)
    {
        target.DatumReference = name.Letter;
        target.DatumNumber = name.Number;
        if (target.DatumReference != name.Letter || target.DatumNumber != name.Number)
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the datum target label.", "Use a label such as 'A1'.");
        }
    }

    /// <summary>Datum point (a cross) on the vertex, circle centre or edge point picked by a model point.</summary>
    public static object AddDatumPoint(SeContext context, string? viewHandle, double[]? points)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        DatumPoint point = ((DatumPoints)SheetOf(view).DatumPoints).AddByElement(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        // GetCenter reads (0, 0) (verified in Solid Edge 2026): the symbol's extent is checked instead.
        return Checked(context, draft, "datum_point", point, () => RequireAround(point, pick.Sheet, "datum point"));
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
            SetDatum(frame, datum);
            OnLeader(frame, at, "datum frame");
        });
    }

    internal static void SetDatum(DatumFrame frame, string datum)
    {
        frame.Datum = datum;
        if (frame.Datum != datum)
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the datum letter.", "Use a short label such as 'A'.");
        }
    }

    /// <summary>
    /// Feature control frame: on a leader from the edge picked by a model point of a view, or free at a sheet position. The
    /// frame is the characteristic, the tolerance and the datums, or the whole text in Solid Edge codes; up to three more
    /// rows below it, each with its own characteristic or, in a composite frame, sharing the characteristic of the first.
    /// </summary>
    public static object AddFeatureControlFrame(SeContext context, string? documentHandle, string? sheetRef, string? viewHandle, double[]? points,
        double[]? position, GeometricCharacteristic? characteristic, string? tolerance, string[]? datums, string? text, string[]? rows = null, bool composite = false)
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
    internal static string FrameText(GeometricCharacteristic? characteristic, string? tolerance, string[]? datums)
    {
        if (characteristic is not { } named)
        {
            throw ToolException.InvalidArgument("'characteristic' is required (or the whole frame in 'text').",
                $"Use one of: {string.Join(", ", Characteristics.Keys)}.");
        }

        string code = Characteristics[ArgumentNames.Snake(named)];

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
    internal static string[] RowTexts(string[]? rows, bool composite)
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
    internal static void SetRows(FeatureControlFrame frame, string[] rows, bool composite)
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

    internal static IEnumerable<string> RowsOf(FeatureControlFrame frame) =>
        new[] { frame.SecondaryFrame, frame.TertiaryFrame, frame.QuaternaryFrame }.Where(r => !string.IsNullOrEmpty(r));

    internal static void SetFrame(FeatureControlFrame frame, string content)
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
    public static readonly IReadOnlyDictionary<Arguments.WeldType, WeldType> WeldTypes = new Dictionary<Arguments.WeldType, WeldType>
    {
        [Arguments.WeldType.Fillet] = WeldType.igDimWeldBottomFillet,
        [Arguments.WeldType.Square] = WeldType.igDimWeldBottomSquare,
        [Arguments.WeldType.VGroove] = WeldType.igDimWeldBottomVGroove,
        [Arguments.WeldType.Bevel] = WeldType.igDimWeldBottomBevel,
        [Arguments.WeldType.UGroove] = WeldType.igDimWeldBottomUGroove,
        [Arguments.WeldType.JGroove] = WeldType.igDimWeldBottomJGroove,
        [Arguments.WeldType.Spot] = WeldType.igDimWeldBottomSpot,
        [Arguments.WeldType.Seam] = WeldType.igDimWeldBottomSeam,
        [Arguments.WeldType.Plug] = WeldType.igDimWeldBottomSlot,
        [Arguments.WeldType.Backing] = WeldType.igDimWeldBottomBacking,
        [Arguments.WeldType.Surfacing] = WeldType.igDimWeldBottomSurfacing,
        [Arguments.WeldType.FlareV] = WeldType.igDimWeldBottomFlareV,
        [Arguments.WeldType.FlareBevel] = WeldType.igDimWeldBottomFlareBevel,
        [Arguments.WeldType.Edge] = WeldType.igDimWeldBottomEdgeWeld,
    };

    /// <summary>The name of a weld type read from a symbol, or <c>null</c> for one the tools do not place.</summary>
    internal static string? WeldName(WeldType arrow) =>
        WeldTypes.Where(w => w.Value == arrow).Select(w => ArgumentNames.Snake(w.Key)).FirstOrDefault();

    internal const int WeldOtherSide = WeldType.igDimWeldBottomFillet - WeldType.igDimWeldTopFillet;

    /// <summary>How far (m) a weld symbol may reach left of or below its leader: the all-around circle and the arrow-side text.</summary>
    private const double WeldReach = 0.015;

    /// <summary>
    /// Weld symbol on the edge picked by a model point: the weld type on the arrow side, the other side or both, the size
    /// or note (e.g. 'a5'), and the all-around and field-weld flags; the leader ends at the position (default 15 mm up-right),
    /// where the reference line starts.
    /// </summary>
    public static object AddWeld(SeContext context, string? viewHandle, double[]? points, double[]? position, Arguments.WeldType weld,
        WeldSide side, string? size, bool allAround, bool field)
    {
        (WeldType arrow, bool onArrow, bool onOther) = ParseWeld(weld, side);
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
            SetWeld(symbol, (arrow, onArrow, onOther), size, allAround, field);
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

    internal static (WeldType Arrow, bool OnArrow, bool OnOther) ParseWeld(Arguments.WeldType weld, WeldSide side) =>
        (WeldTypes[weld], side is WeldSide.Arrow or WeldSide.Both, side is WeldSide.Other or WeldSide.Both);

    /// <summary>Weld type and size on the chosen sides, the flags and the break line; checked by reading them back.</summary>
    internal static void SetWeld(WeldSymbol symbol, (WeldType Arrow, bool OnArrow, bool OnOther) weld, string? size, bool allAround, bool field)
    {
        WeldType other = weld.Arrow - WeldOtherSide;
        if (weld.OnArrow)
        {
            symbol.BottomType = weld.Arrow;
            symbol.BottomNote1 = size ?? "";
        }

        if (weld.OnOther)
        {
            symbol.TopType = other;
            symbol.TopNote1 = size ?? "";
        }

        symbol.WeldAllAround = allAround;
        symbol.WeldInField = field;
        // Verified: without the break line (the horizontal shoulder of the leader) Solid Edge draws the body of the symbol
        // at the sheet origin whatever the position; setting it places the reference line at the end of the leader.
        symbol.BreakLine = true;
        if ((weld.OnArrow && symbol.BottomType != weld.Arrow) || (weld.OnOther && symbol.TopType != other)
            || (!string.IsNullOrEmpty(size) && (weld.OnArrow ? symbol.BottomNote1 : symbol.TopNote1) != size))
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the weld type or size of the symbol.", "Avoid control characters in 'text'.");
        }
    }

    /// <summary>
    /// GOST weld symbol (GOST 2.312) on a leader from the edge picked by a model point: text = the note above the leader
    /// shelf, or "above|below" (the seam on the arrow side reads above, the other side below); allAround, field; position =
    /// the end of the leader (default 15 mm up-right).
    /// </summary>
    public static object AddGostWeld(SeContext context, string? viewHandle, double[]? points, double[]? position, string? text, bool allAround, bool field)
    {
        string[] notes = GostNotes(text);
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Pick pick = PickOne(view, points);
        (double X, double Y) at = position is null ? (pick.Sheet.X + AnnotationOffset, pick.Sheet.Y + AnnotationOffset) : Geo.Point2(position, "position");
        GostWeldSymbol symbol = ((GostWeldSymbols)SheetOf(view).GostWeldSymbols).AddByTerminator(pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint);
        return Checked(context, draft, "gost_weld", symbol, () =>
        {
            symbol.WeldAllAround = allAround;
            symbol.WeldInField = field;
            OnLeader(symbol, at, "GOST weld symbol");
            SetShelf(symbol, notes, left: at.X < pick.Sheet.X);
        });
    }

    internal static string[] GostNotes(string? text)
    {
        string[] notes = string.IsNullOrWhiteSpace(text)
            ? throw ToolException.InvalidArgument("gost_weld requires 'text': the weld note, e.g. \"GOST 5264-80-T1-4\", or \"above|below\".")
            : [.. text.Split('|').Select(p => p.Trim())];
        return notes.Length > 2
            ? throw ToolException.InvalidArgument("'text' for a GOST weld symbol is one note or two separated by '|' (above and below the shelf).")
            : notes;
    }

    /// <summary>
    /// The shelf of the leader is its break line, and the notes go above and below it (the leader-line texts would be written
    /// along the slanted leader).
    /// </summary>
    internal static void SetShelf(GostWeldSymbol symbol, string[] notes, bool left)
    {
        symbol.BreakLine = true;
        // Verified: true draws the shelf to the left of the bend.
        symbol.BreakLineDirection = left;
        symbol.BreakLineDistance = Math.Max(0.02, notes.Max(n => n.Length) * 0.0025);
        symbol.AboveBreakLineText = notes[0];
        if (notes.Length > 1)
        {
            symbol.BelowBreakLineText = notes[1];
        }

        if (symbol.AboveBreakLineText != notes[0] || notes.Length > 1 && symbol.BelowBreakLineText != notes[1])
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the notes of the GOST weld symbol.");
        }
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
        catch (Exception ex) when (ex is ToolException or COMException && !ComErrors.IsGone(ex))
        {
            Late.TryDelete(annotation);
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
        ?? Nearest(view, graphics, ModelToView(view, model), model, g => g.Shape is Shape.Circle or Shape.Arc, "circle or arc");

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
                case DatumTarget target:
                    target.GetTerminator(out _, out x, out y, out _, out _);
                    break;
                default:
                    return null;
            }

            return [Geo.Mm(x), Geo.Mm(y)];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
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
            ("weld", sheet.WeldSymbols), ("bolt_circle", sheet.BoltHoleCircles), ("datum_target", sheet.DatumTargets),
            ("datum_point", sheet.DatumPoints), ("corner", sheet.CornerAnnotations), ("gost_weld", sheet.GostWeldSymbols),
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

    internal static string? KindOf(object annotation) => annotation switch
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
        BoltHoleCircle => "bolt_circle",
        DatumTarget => "datum_target",
        DatumPoint => "datum_point",
        CornerAnnotation => "corner",
        GostWeldSymbol => "gost_weld",
        _ => null,
    };

    private static object DescribeAnnotation(SeContext context, DraftDocument draft, string kind, object annotation)
    {
        var info = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(annotation, kind, Document(draft)),
            ["kind"] = kind,
        };
        DescribeContent(info, annotation);
        if (info.ContainsKey("attached"))
        {
            info["terminator"] = TerminatorOf(annotation);
        }

        switch (annotation)
        {
            case BoltHoleCircle circle:
                circle.Circle2d(out double cx, out double cy, out double radius);
                info["centre"] = new[] { Geo.Mm(cx), Geo.Mm(cy) };
                info["radius"] = Geo.Mm(radius);
                break;
            case DatumPoint point:
                double[] extent = RangeOf(point);
                info["at"] = new[] { Geo.Mm((extent[0] + extent[2]) / 2), Geo.Mm((extent[1] + extent[3]) / 2) };
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

    /// <summary>What an annotation says (text, symbol, frame, weld) and whether its leader is attached; shared with PMI.</summary>
    internal static void DescribeContent(Dictionary<string, object?> info, object annotation)
    {
        switch (annotation)
        {
            case TextBox box:
                info["text"] = box.Text;
                if (box.Watermark)
                {
                    info["watermark"] = true;
                }

                break;
            case Balloon balloon:
                info["text"] = balloon.BalloonDisplayedText;
                info["callout"] = balloon.Callout != 0;
                info["attached"] = balloon.IsTerminatorAttachedToEntity;
                break;
            case Leader leader:
                info["attached"] = leader.IsTerminatorAttachedToEntity;
                break;
            case SurfaceFinishSymbol symbol:
                info["text"] = symbol.RoughnessValue;
                info["finish"] = FinishOf(symbol);
                info["attached"] = symbol.IsTerminatorAttachedToEntity;
                break;
            case DatumFrame datum:
                info["text"] = datum.Datum;
                info["attached"] = datum.IsTerminatorAttachedToEntity;
                break;
            case DatumTarget target:
                info["text"] = target.DatumReference + target.DatumNumber;
                info["attached"] = target.IsTerminatorAttachedToEntity;
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
                break;
            case WeldSymbol weld:
                bool arrowSide = weld.BottomType != WeldType.igDimWeldTypeNone;
                bool otherSide = weld.TopType != WeldType.igDimWeldTypeNone;
                WeldType shown = arrowSide ? weld.BottomType : weld.TopType + WeldOtherSide;
                info["weld"] = WeldName(shown);
                info["side"] = arrowSide && otherSide ? "both" : arrowSide ? "arrow" : otherSide ? "other" : null;
                info["text"] = arrowSide ? weld.BottomNote1 : weld.TopNote1;
                info["allAround"] = weld.WeldAllAround;
                info["field"] = weld.WeldInField;
                info["attached"] = weld.IsTerminatorAttachedToEntity;
                break;
        }
    }

    /// <summary>Symbol type; Solid Edge rejects reading it (E_INVALIDARG) until it has been set.</summary>
    internal static string? FinishOf(SurfaceFinishSymbol symbol)
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

    internal static string RequireText(string? text, string name) =>
        string.IsNullOrEmpty(text) ? throw ToolException.InvalidArgument($"'{name}' is required.") : text;
}
