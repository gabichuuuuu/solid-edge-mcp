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
using Leader = SolidEdgeFrameworkSupport.Leader;
using Leaders = SolidEdgeFrameworkSupport.Leaders;
using TextBox = SolidEdgeFrameworkSupport.TextBox;
using TextBoxes = SolidEdgeFrameworkSupport.TextBoxes;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Drawing annotations: text, balloons, callouts, centre marks, centre lines and leaders. Annotations attached to a view
/// pick its edges with model points, as dimensions do, so they stay associative; sheet positions are mm from the lower-left
/// corner of the sheet.
/// </summary>
public static partial class DrawingService
{
    public static readonly IReadOnlyList<string> AnnotationKinds = ["text", "balloon", "center_mark", "centerline", "leader"];

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
            ("centerline", sheet.CenterLines), ("leader", sheet.Leaders),
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

    private static string RequireText(string? text, string name) =>
        string.IsNullOrEmpty(text) ? throw ToolException.InvalidArgument($"'{name}' is required.") : text;
}
