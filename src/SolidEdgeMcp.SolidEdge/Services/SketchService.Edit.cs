// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Sheet = SolidEdgeDraft.Sheet;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Editing of the 2D elements of a sketch or a drawing sheet, as the sketch editing commands do: move, rotate, scale and mirror
/// (the elements or copies of them), trim, extend, split, corner, construction, offset of a chain, patterns and delete. Every
/// result is measured against the geometry it must have. Verified in Solid Edge 2026: Move leaves an element tied by relations
/// where it was and Rotate turns it alone, breaking its relations, while the setters of the geometry keep them; so elements tied
/// by relations are placed point by point, as a set, with the relations that would hold each point lifted meanwhile.
/// </summary>
public static partial class SketchService
{
    /// <summary>Largest distance, in metres, between a measured point and the one expected.</summary>
    private const double EditTolerance = 1e-7;

    /// <summary>Most elements edited, or created by a pattern, in one call.</summary>
    private const int MaxEdited = 1000;


    public static object Move(SeContext context, string sketch, string[]? elements, double[]? delta, bool copy)
    {
        (double dx, double dy) = Geo.Point2(delta, "delta");
        if (Math.Abs(dx) < EditTolerance && Math.Abs(dy) < EditTolerance)
        {
            throw ToolException.InvalidArgument("'delta' is zero: there is nothing to move.");
        }

        return Transform(context, sketch, elements, Moving(dx, dy), copy);
    }

    public static object Rotate(SeContext context, string sketch, string[]? elements, double[]? center, double? angle, bool copy)
    {
        (double X, double Y) c = Geo.Point2(center, "center");
        double degrees = Units.RequireFinite(angle ?? throw ToolException.InvalidArgument(
            "Action 'rotate' requires 'angle' in degrees (counter-clockwise positive)."), "angle");
        if (Math.Abs(Math.IEEERemainder(degrees, 360)) < 1e-9)
        {
            throw ToolException.InvalidArgument("'angle' is a whole number of turns: the elements would not change.");
        }

        return Transform(context, sketch, elements, Rotating(c, Units.DegToRad(degrees)), copy);
    }

    public static object Scale(SeContext context, string sketch, string[]? elements, double[]? center, double? factor, bool copy)
    {
        (double X, double Y) c = Geo.Point2(center, "center");
        double f = Units.RequirePositive(factor ?? throw ToolException.InvalidArgument("Action 'scale' requires 'factor' (> 0)."), "factor");
        if (Math.Abs(f - 1) < 1e-12)
        {
            throw ToolException.InvalidArgument("'factor' is 1: the elements would not change.");
        }

        return Transform(context, sketch, elements, Scaling(c, f), copy);
    }

    public static object Mirror(SeContext context, string sketch, string[]? elements, double[]? line, bool copy)
    {
        List<(double X, double Y)> p = Geo.Points2(line, "line", 2);
        if (p.Count != 2 || Gap(p[0], p[1]) < EditTolerance)
        {
            throw ToolException.InvalidArgument("'line' must be two different points [x1, y1, x2, y2] (mm) of the mirror line.");
        }

        return Transform(context, sketch, elements, Mirroring(p[0], p[1]), copy);
    }

    /// <summary>Removes the part of <paramref name="element"/> at <paramref name="at"/> up to the 1 or 2 elements that cut it.</summary>
    public static object Trim(SeContext context, string sketch, string element, double[]? at, string[]? bounds)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object target = Editable(context, element, "trim", e => e is Line2d or Circle2d or Arc2d or Ellipse2d or EllipticalArc2d or BSplineCurve2d);
        object[] cuts = Bounds(context, bounds, 2, "trim");
        (double x, double y) = Geo.Point2(at, "at");
        return Cut(context, canvas, sketch, [(element, target)], () => Late.Call(target, "Trim", [x, y, .. cuts]),
            $"Solid Edge did not trim '{element}' at {Mm(x, y)}",
            "'at' must be a point on the part to remove and 'bounds' the elements that cross the element where that part ends (se_sketch info lists the geometry).");
    }

    /// <summary>Extends the end of <paramref name="element"/> nearest to <paramref name="at"/> up to another element.</summary>
    public static object Extend(SeContext context, string sketch, string element, double[]? at, string[]? bounds)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object target = Editable(context, element, "extend", e => e is Line2d or Arc2d or EllipticalArc2d or BSplineCurve2d);
        object reach = Bounds(context, bounds, 1, "extend")[0];
        (double x, double y) = Geo.Point2(at, "at");
        return Cut(context, canvas, sketch, [(element, target)], () => Late.Call(target, "Extend", x, y, reach),
            $"Solid Edge did not extend '{element}' from its end near {Mm(x, y)}",
            "'at' must be near the end to extend and the element in 'bounds' must lie ahead of that end.");
    }

    /// <summary>Splits an element in two at a point on it; the second part is a new element.</summary>
    public static object Split(SeContext context, string sketch, string element, double[]? at)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object target = Editable(context, element, "split", e => e is Line2d or Arc2d or EllipticalArc2d or BSplineCurve2d);
        (double x, double y) = Geo.Point2(at, "at");
        return Cut(context, canvas, sketch, [(element, target)], () => Late.Call(target, "SplitAtPoint", x, y),
            $"Solid Edge did not split '{element}' at {Mm(x, y)}",
            "'at' must lie on the element, away from its ends.");
    }

    /// <summary>Trims or extends two elements to the corner where they meet, keeping the part of each one at its 'keep' point.</summary>
    public static object Corner(SeContext context, string sketch, string[]? elements, double[]? keep)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] e = Elements(context, elements, 2, 2, "corner");
        foreach (string handle in elements!)
        {
            _ = Editable(context, handle, "corner", o => o is Line2d or Arc2d or EllipticalArc2d or BSplineCurve2d);
        }

        List<(double X, double Y)> k = Geo.Points2(keep, "keep", 2);
        if (k.Count != 2)
        {
            throw ToolException.InvalidArgument("'keep' must be [x1, y1, x2, y2]: a point on the part of each element to keep (mm).");
        }

        return Cut(context, canvas, sketch, [(elements[0], e[0]), (elements[1], e[1])],
            () => Late.Call(e[0], "CornerTrim", k[0].X, k[0].Y, e[1], k[1].X, k[1].Y),
            $"Solid Edge did not join '{elements[0]}' and '{elements[1]}' at a corner",
            "The elements must cross or point at each other; 'keep' takes a point on the part of each one to keep.");
    }

    /// <summary>Makes elements construction geometry (drawn, but not part of the profile) or normal geometry again.</summary>
    public static object Construction(SeContext context, string sketch, string[]? elements, bool value)
    {
        Profile profile = Resolve(context, sketch);
        object[] e = EditedElements(context, elements, "construction");
        for (int i = 0; i < e.Length; i++)
        {
            if (profile.IsConstructionElement(e[i]) != value)
            {
                profile.ToggleConstruction(e[i]);
            }

            if (profile.IsConstructionElement(e[i]) != value)
            {
                throw new ToolException("edit_failed", $"Solid Edge did not make '{elements![i]}' {(value ? "construction" : "normal")} geometry.",
                    "Check that the element belongs to this sketch (se_sketch info).");
            }
        }

        return new { elements = elements!.Select(h => new { element = h, construction = value }).ToArray() };
    }

    /// <summary>
    /// Offset copy of the chain of connected elements through <paramref name="at"/>, on the side of <paramref name="side"/>, as the
    /// Offset command does. Verified: Offset2d copies the selected chain with its relations and a driving dimension for the
    /// distance, and the chain stays selected. The chain of lines, arcs and circles is found from their ends; ChainLocate,
    /// which picks with a tolerance in pixels and picked a wrong single element with the Solid Edge window minimized
    /// (verified), is only used for other curves. With <paramref name="symmetric"/>, as the Symmetric Offset command (no member
    /// in the type libraries): a copy on each side (the second towards <paramref name="side"/> mirrored through
    /// <paramref name="at"/>) and, on an open chain, caps joining the copies' ends round each end of the chain.
    /// </summary>
    public static object Offset(SeContext context, string sketch, double[]? at, double[]? side, double? distance, bool symmetric = false,
        string cap = "arc")
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        (double x, double y) = Geo.Point2(at, "at");
        (double sx, double sy) = Geo.Point2(side, "side");
        double d = Units.MmToM(Units.RequirePositive(distance ?? throw ToolException.InvalidArgument("Action 'offset' requires 'distance' (mm)."), "distance"));
        if (cap is not ("arc" or "line" or "none"))
        {
            throw ToolException.InvalidArgument($"Unknown cap '{cap}'.", "Use arc, line or none.");
        }

        if (symmetric && Gap((x, y), (sx, sy)) < 1e-9)
        {
            throw ToolException.InvalidArgument("'side' must be a point off the chain.", "Give a point a little off the chain, straight across from 'at'.");
        }

        (int chain, List<object> elements, List<object> created) = OffsetChain(context, canvas, sketch, x, y, sx, sy, d);
        if (!symmetric)
        {
            return new { chain, distance = Geo.Mm(d), created = Registered(context, sketch, created) };
        }

        List<object> other;
        try
        {
            (_, _, other) = OffsetChain(context, canvas, sketch, x, y, (2 * x) - sx, (2 * y) - sy, d);
        }
        catch (ToolException)
        {
            DeleteAll(created);
            throw;
        }

        List<object> caps = cap == "none" ? [] : Caps(canvas, elements, created, other, d, cap == "arc");
        return new
        {
            chain,
            distance = Geo.Mm(d),
            width = Geo.Mm(2 * d),
            created = Registered(context, sketch, created.Concat(other)),
            caps = Registered(context, sketch, caps),
        };
    }

    /// <summary>Offsets the chain through (x, y) towards (sx, sy) by d (m): how many elements it has, them and the copies.</summary>
    private static (int Chain, List<object> Elements, List<object> Created) OffsetChain(SeContext context, Canvas2d canvas, string sketch,
        double x, double y, double sx, double sy, double d)
    {
        object owner = (object?)canvas.Profile ?? canvas.Sheet!;
        object selection = Late.Get(context.DocumentOf(sketch), "SelectSet")
            ?? throw new ToolException("offset_failed", "The document has no selection to locate the chain with.");
        Late.Call(selection, "RemoveAll");
        try
        {
            if (ChainAt(canvas, x, y) is { } found)
            {
                foreach (object element in found)
                {
                    Late.Call(selection, "Add", element);
                }
            }
            else
            {
                Late.Call(owner, "ChainLocate", x, y);
            }

            List<object> elements = Late.Items(selection).ToList();
            if (elements.Count == 0)
            {
                throw new ToolException("offset_failed", $"There is no element of the sketch at {Mm(x, y)}.",
                    "Pass in 'at' a point on an element of the chain to offset (se_sketch info lists the geometry).");
            }

            int[] before = ElementCounts(canvas);
            Late.Call(owner, "Offset2d", sx, sy, d);
            List<object> created = NewElements(canvas, before).ToList();
            if (created.Count == 0)
            {
                throw new ToolException("offset_failed", $"Solid Edge did not offset the chain at {Mm(x, y)}.",
                    "'side' must be a point off the chain, on the side of the copy; the distance must leave room for it (an inner offset smaller than the shape).");
            }

            return (elements.Count, elements, created);
        }
        finally
        {
            Late.Call(selection, "RemoveAll");
        }
    }

    /// <summary>
    /// Caps of a symmetric offset: at each free end of an open chain, a line or a half circle (centred on the end, bulging away
    /// from the chain) from the end of one copy to the end of the other, tied to both by connect relations.
    /// </summary>
    private static List<object> Caps(Canvas2d canvas, List<object> chain, List<object> first, List<object> second, double d, bool arc)
    {
        var caps = new List<object>();
        List<(object Element, (double X, double Y) Point)> a = FreeEnds(first), b = FreeEnds(second);
        if (a.Count == 0 || b.Count == 0)
        {
            return caps;
        }

        foreach ((object element, (double X, double Y) end) in FreeEnds(chain))
        {
            (object Element, (double X, double Y) Point) ea = a.MinBy(p => Gap(p.Point, end));
            (object Element, (double X, double Y) Point) eb = b.MinBy(p => Gap(p.Point, end));
            if (Math.Abs(Gap(ea.Point, end) - d) > 1e-6 || Math.Abs(Gap(eb.Point, end) - d) > 1e-6)
            {
                continue;
            }

            object made;
            if (arc)
            {
                (double X, double Y) middle = Middle(element);
                (double X, double Y) outward = (end.X - middle.X, end.Y - middle.Y);
                (double X, double Y) bulge = (-(ea.Point.Y - end.Y), ea.Point.X - end.X);
                ((double X, double Y) from, (double X, double Y) to) = (bulge.X * outward.X) + (bulge.Y * outward.Y) >= 0
                    ? (ea.Point, eb.Point)
                    : (eb.Point, ea.Point);
                made = canvas.Arcs2d.AddByCenterStartEnd(end.X, end.Y, from.X, from.Y, to.X, to.Y);
            }
            else
            {
                made = canvas.Lines2d.AddBy2Points(ea.Point.X, ea.Point.Y, eb.Point.X, eb.Point.Y);
            }

            Relations2d relations = canvas.Relations2d;
            foreach ((object Element, (double X, double Y) Point) joint in new[] { ea, eb })
            {
                if (KeypointAt(made, joint.Point) is { } k1 && KeypointAt(joint.Element, joint.Point) is { } k2)
                {
                    relations.AddKeypoint(made, k1, joint.Element, k2);
                }
            }

            caps.Add(made);
        }

        return caps;
    }

    /// <summary>Ends of the elements that no other element of the set shares (none for a closed chain or a circle).</summary>
    private static List<(object Element, (double X, double Y) Point)> FreeEnds(List<object> elements)
    {
        var ends = new List<(object Element, (double X, double Y) Point)>();
        foreach (object element in elements)
        {
            try
            {
                ((double X, double Y) start, (double X, double Y) end) = Termini(element);
                if (Gap(start, end) > EditTolerance)
                {
                    ends.Add((element, start));
                    ends.Add((element, end));
                }
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                // Circles and ellipses have no ends.
            }
        }

        return [.. ends.Where(p => !ends.Any(q => !ReferenceEquals(q.Element, p.Element) && Gap(q.Point, p.Point) <= EditTolerance))];
    }

    /// <summary>Start and end of an open 2D element (m), through IDispatch.</summary>
    private static ((double X, double Y) Start, (double X, double Y) End) Termini(object element)
    {
        object?[] s = Late.CallByRef(element, "GetStartPoint", [0.0, 0.0], 0, 1);
        object?[] e = Late.CallByRef(element, "GetEndPoint", [0.0, 0.0], 0, 1);
        return ((Convert.ToDouble(s[0], CultureInfo.InvariantCulture), Convert.ToDouble(s[1], CultureInfo.InvariantCulture)),
            (Convert.ToDouble(e[0], CultureInfo.InvariantCulture), Convert.ToDouble(e[1], CultureInfo.InvariantCulture)));
    }

    /// <summary>Middle of an arc (m); for other curves the midpoint of its ends.</summary>
    private static (double X, double Y) Middle(object element)
    {
        if (element is Arc2d arc)
        {
            arc.GetCenterPoint(out double cx, out double cy);
            double angle = arc.StartAngle + (arc.SweepAngle / 2);
            return (cx + (arc.Radius * Math.Cos(angle)), cy + (arc.Radius * Math.Sin(angle)));
        }

        ((double X, double Y) start, (double X, double Y) end) = Termini(element);
        return ((start.X + end.X) / 2, (start.Y + end.Y) / 2);
    }

    private static void DeleteAll(IEnumerable<object> elements)
    {
        foreach (object element in elements)
        {
            try
            {
                Late.Call(element, "Delete");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                // The error being reported is the failed offset.
            }
        }
    }

    /// <summary>
    /// Lines, arcs and circles connected end to end with the one through (<paramref name="x"/>, <paramref name="y"/>) (m), or
    /// null when the point is on no line, arc or circle (then ChainLocate is used).
    /// </summary>
    private static List<object>? ChainAt(Canvas2d canvas, double x, double y)
    {
        const double onTolerance = 1e-4, endTolerance = 1e-9;
        var open = new List<(object Element, (double X, double Y) A, (double X, double Y) B)>();
        object? hit = null;
        double best = onTolerance;
        bool closed = false;
        foreach (Line2d line in Late.Items(canvas.Lines2d).OfType<Line2d>())
        {
            line.GetStartPoint(out double x1, out double y1);
            line.GetEndPoint(out double x2, out double y2);
            open.Add((line, (x1, y1), (x2, y2)));
            double dx = x2 - x1, dy = y2 - y1, lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared < 1e-18 ? 0 : Math.Clamp((((x - x1) * dx) + ((y - y1) * dy)) / lengthSquared, 0, 1);
            Take(line, Math.Sqrt(Math.Pow(x - (x1 + (t * dx)), 2) + Math.Pow(y - (y1 + (t * dy)), 2)), false);
        }

        foreach (Arc2d arc in Late.Items(canvas.Arcs2d).OfType<Arc2d>())
        {
            arc.GetCenterPoint(out double cx, out double cy);
            arc.GetStartPoint(out double x1, out double y1);
            arc.GetEndPoint(out double x2, out double y2);
            open.Add((arc, (x1, y1), (x2, y2)));
            double angle = Math.Atan2(y - cy, x - cx), from = arc.StartAngle, sweep = arc.SweepAngle;
            double relative = ((((sweep >= 0 ? angle - from : from - angle) % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI));
            double onCurve = Math.Abs(Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2)) - arc.Radius);
            Take(arc, relative <= Math.Abs(sweep) + 1e-9 ? onCurve : double.MaxValue, false);
        }

        foreach (Circle2d circle in Late.Items(canvas.Circles2d).OfType<Circle2d>())
        {
            circle.GetCenterPoint(out double cx, out double cy);
            Take(circle, Math.Abs(Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2)) - circle.Radius), true);
        }

        if (hit is null)
        {
            return null;
        }

        if (closed)
        {
            return [hit];
        }

        // Breadth-first over the elements that share an end with the chain.
        var chain = new List<object> { hit };
        var ends = new List<(double X, double Y)>();
        (object _, (double X, double Y) a0, (double X, double Y) b0) = open.First(o => ReferenceEquals(o.Element, hit));
        ends.Add(a0);
        ends.Add(b0);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((object element, (double X, double Y) a, (double X, double Y) b) in open)
            {
                if (chain.Any(c => ReferenceEquals(c, element)))
                {
                    continue;
                }

                bool Touches((double X, double Y) p) => ends.Any(e => Math.Abs(e.X - p.X) <= endTolerance && Math.Abs(e.Y - p.Y) <= endTolerance);
                if (Touches(a) || Touches(b))
                {
                    chain.Add(element);
                    ends.Add(a);
                    ends.Add(b);
                    grew = true;
                }
            }
        }

        return chain;

        void Take(object element, double distance, bool isClosed)
        {
            if (distance < best)
            {
                (hit, best, closed) = (element, distance, isClosed);
            }
        }
    }

    /// <summary>Rectangular pattern of elements (sketches only): <paramref name="columns"/> x <paramref name="rows"/> occurrences with the original.</summary>
    public static object RectangularPattern(SeContext context, string sketch, string[]? elements, int? columns, int? rows, double[]? spacing, double? angle)
    {
        Profile profile = Resolve(context, sketch);
        object[] e = EditedElements(context, elements, "pattern_rectangular");
        int nx = columns ?? 1, ny = rows ?? 1;
        if (nx < 1 || ny < 1 || (long)nx * ny < 2 || (long)nx * ny * e.Length > MaxEdited)
        {
            throw ToolException.InvalidArgument($"'count' (columns) and 'rows' must be 1 or more, with at least 2 occurrences and at most {MaxEdited} elements in all.");
        }

        if (spacing is not { Length: 2 })
        {
            throw ToolException.InvalidArgument("'spacing' must be [dx, dy] in mm: the distance between columns and between rows (negative = to the left / down).");
        }

        double dx = Units.MmToM(Units.RequireFinite(spacing[0], "spacing")), dy = Units.MmToM(Units.RequireFinite(spacing[1], "spacing"));
        if ((nx > 1 && Math.Abs(dx) < EditTolerance) || (ny > 1 && Math.Abs(dy) < EditTolerance))
        {
            throw ToolException.InvalidArgument("'spacing' is zero in a direction with more than one occurrence.");
        }

        double a = Units.DegToRad(Units.RequireFinite(angle ?? 0, "angle"));

        // Solid Edge spaces its x count along Angle and its y count 90 degrees to the left of it, with positive spacings:
        // a negative spacing turns that grid (and may swap its axes).
        (double turn, int xCount, int yCount, double xSpace, double ySpace) = (dx >= 0, dy >= 0) switch
        {
            (true, true) => (a, nx, ny, dx, dy),
            (false, false) => (a + Math.PI, nx, ny, -dx, -dy),
            (false, true) => (a + (Math.PI / 2), ny, nx, dy, -dx),
            (true, false) => (a - (Math.PI / 2), ny, nx, -dy, dx),
        };

        // A direction with one occurrence still gets a size: Fit divides it by the count less one.
        xSpace = xSpace > EditTolerance ? xSpace : ySpace;
        ySpace = ySpace > EditTolerance ? ySpace : xSpace;
        (double X, double Y) origin = ShapeOf(e[0]).Points[0];
        Canvas2d canvas = Canvas2d.Of(profile);
        int[] before = ElementCounts(canvas);
        Array geometry = e;
        RectangularPattern2d pattern;
        try
        {
            // Verified: with Fit, Width / (XCount - 1) is the spacing; the origin does not move the copies.
            pattern = profile.RectangularPatterns2d.AddEx(ref geometry, e.Length, origin.X, origin.Y,
                xSpace * Math.Max(xCount - 1, 1), ySpace * Math.Max(yCount - 1, 1), turn,
                PatternOffsetTypeConstants.sePatternFitOffset, xCount, yCount, xSpace, ySpace);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("pattern_failed", $"Solid Edge could not pattern the elements: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the counts and the spacing.", ex);
        }

        (double X, double Y) u = (Math.Cos(a), Math.Sin(a));
        (double X, double Y) v = (-u.Y, u.X);
        (double X, double Y)[] expected =
            (from i in Enumerable.Range(0, nx)
             from j in Enumerable.Range(0, ny)
             where i + j > 0
             select (origin.X + (i * dx * u.X) + (j * dy * v.X), origin.Y + (i * dx * u.Y) + (j * dy * v.Y))).ToArray();
        return Patterned(context, canvas, sketch, pattern, before, e, expected, nx * ny);
    }

    /// <summary>
    /// Circular pattern of elements about <paramref name="center"/> (sketches only): <paramref name="count"/> occurrences with the
    /// original, evenly round the circle or spread over <paramref name="angle"/> degrees from the original. Verified: AddByCircleEx
    /// spreads them evenly round the whole circle (it ignores a fixed spacing), AddByArcEx from the original to the end of the
    /// sweep; Solid Edge 2026 closed on a spacing of 0, so the spacing always matches the count.
    /// </summary>
    public static object CircularPattern(SeContext context, string sketch, string[]? elements, double[]? center, int? count, double? angle)
    {
        Profile profile = Resolve(context, sketch);
        object[] e = EditedElements(context, elements, "pattern_circular");
        int n = count ?? throw ToolException.InvalidArgument("Action 'pattern_circular' requires 'count': the occurrences round the centre, with the original (2 or more).");
        if (n < 2 || (long)n * e.Length > MaxEdited)
        {
            throw ToolException.InvalidArgument($"'count' must be 2 or more, with at most {MaxEdited} elements in all.");
        }

        double degrees = Units.RequireFinite(angle ?? 360, "angle");
        if (Math.Abs(degrees) < 1e-6 || Math.Abs(degrees) > 360 + 1e-9)
        {
            throw ToolException.InvalidArgument("'angle' (the sweep) must be between -360 and 360 degrees, and not 0.");
        }

        bool full = Math.Abs(Math.Abs(degrees) - 360) < 1e-9;
        double sweep = Units.DegToRad(Math.Abs(degrees));
        double step = full ? 2 * Math.PI / n : sweep / (n - 1);
        (double X, double Y) c = Geo.Point2(center, "center");
        Shape shape = ShapeOf(e[0]);

        // The pattern starts at a point of the first element (verified with the centre of a circle).
        (double X, double Y) start = shape.Points.FirstOrDefault(p => Gap(p, c) > EditTolerance, c);
        if (Gap(start, c) <= EditTolerance)
        {
            throw ToolException.InvalidArgument("The first element sits on the pattern centre, so its copies would fall on it.",
                "Pattern elements that lie away from 'center'.");
        }

        Geom2dOrientationConstants orientation = degrees > 0
            ? Geom2dOrientationConstants.igGeom2dOrientCounterClockwise
            : Geom2dOrientationConstants.igGeom2dOrientClockwise;
        Canvas2d canvas = Canvas2d.Of(profile);
        int[] before = ElementCounts(canvas);
        Array geometry = e;
        CircularPattern2d pattern;
        try
        {
            pattern = full
                ? profile.CircularPatterns2d.AddByCircleEx(ref geometry, e.Length, c.X, c.Y, start.X, start.Y, orientation,
                    PatternOffsetTypeConstants.sePatternFitOffset, n, step)
                : profile.CircularPatterns2d.AddByArcEx(ref geometry, e.Length, c.X, c.Y, start.X, start.Y, sweep, orientation,
                    PatternOffsetTypeConstants.sePatternFitOffset, n, step);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("pattern_failed", $"Solid Edge could not pattern the elements: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the count and the sweep.", ex);
        }

        double sign = degrees > 0 ? 1 : -1;
        (double X, double Y)[] expected = Enumerable.Range(1, n - 1).Select(k => Map2.Rotate(c, sign * k * step).Apply(shape.Points[0])).ToArray();
        return Patterned(context, canvas, sketch, pattern, before, e, expected, n);
    }

    /// <summary>Deletes 2D elements (Solid Edge deletes their relations with them).</summary>
    public static object DeleteElements(SeContext context, string sketch, string[]? elements)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] e = EditedElements(context, elements, "delete");
        int before = ElementCounts(canvas).Sum();
        var deleted = new List<string>();
        try
        {
            for (int i = 0; i < e.Length; i++)
            {
                Late.Call(e[i], "Delete");
                context.Handles.Remove(elements![i], "the element was deleted");
                deleted.Add(elements[i]);
            }
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("edit_failed",
                $"Solid Edge deleted {deleted.Count} of {e.Length} elements ({string.Join(", ", deleted)}) and then failed: {ComErrors.Unwrap(ex).Message.Trim()}",
                "List the sketch (se_sketch info) and delete the remaining elements again.", ex);
        }

        int gone = before - ElementCounts(canvas).Sum();
        if (gone < e.Length)
        {
            throw new ToolException("edit_failed", $"Solid Edge removed {gone} of the {e.Length} elements.", "List the sketch with se_sketch info.");
        }

        return new { deleted, elements = Summary(canvas) };
    }

    public static readonly IReadOnlyList<string> Orders = ["front", "back", "forward", "backward"];

    /// <summary>
    /// Bring to Front, Send to Back, Bring Forward and Send Backward on elements of a drawing sheet. Verified: they change
    /// ZOrder (lines at 5, 6, 7; the first brought to front: 7, 5, 6).
    /// </summary>
    public static object Order(SeContext context, string sketch, string[]? elements, string? to)
    {
        Sheet sheet = SheetOnly(ResolveCanvas(context, sketch), "order");
        string method = to switch
        {
            "front" => "BringToFront",
            "back" => "SendToBack",
            "forward" => "BringForward",
            "backward" => "SendBackward",
            _ => throw ToolException.InvalidArgument("Action 'order' requires 'to': front, back, forward or backward."),
        };
        object[] e = EditedElements(context, elements, "order");
        var rows = new List<object>();
        for (int i = 0; i < e.Length; i++)
        {
            int before, after;
            try
            {
                before = ZOrder(e[i]);
                Late.Call(e[i], method);
                after = ZOrder(e[i]);
            }
            catch (Exception ex) when (ComErrors.IsCallFailure(ex))
            {
                throw ToolException.InvalidArgument($"'{elements![i]}' has no drawing order on '{sheet.Name}'.", "Give 2D elements, text or annotations of the sheet.");
            }

            if (to is "front" or "forward" ? after < before : after > before)
            {
                throw new ToolException("edit_failed", $"Solid Edge moved '{elements![i]}' the wrong way ({before} to {after}).", "Check the order with se_sketch info.");
            }

            rows.Add(new { element = elements![i], zOrder = after, previous = before });
        }

        return new { to, elements = rows };
    }

    /// <summary>
    /// Group: the elements become one group of the sheet, selected and moved together. Verified: Groups.Add and
    /// AddToGroup; grouped elements leave the sheet's collections (three lines, two grouped: Lines2d counts one) and come
    /// back with Ungroup.
    /// </summary>
    public static object Group(SeContext context, string sketch, string[]? elements)
    {
        Sheet sheet = SheetOnly(ResolveCanvas(context, sketch), "group");
        object[] e = EditedElements(context, elements, "group");
        if (e.Length < 2)
        {
            throw ToolException.InvalidArgument("A group needs two or more elements.");
        }

        SolidEdgeFrameworkSupport.Group group = ((SolidEdgeFrameworkSupport.Groups)sheet.Groups).Add();
        try
        {
            group.AddToGroup(e.Length, e);
            if (group.Count != e.Length)
            {
                throw new ToolException("edit_failed", $"Solid Edge grouped {group.Count} of the {e.Length} elements.", "Give 2D elements of the same sheet that are not in a group yet.");
            }
        }
        catch (Exception ex) when (ex is ToolException || ComErrors.IsCallFailure(ex))
        {
            TryUngroup(sheet, group);
            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("edit_failed", $"Solid Edge could not group the elements: {ComErrors.Unwrap(ex).Message.Trim()}", "Give 2D elements of the same sheet.", ex);
        }

        return new { group = context.Register(group, "group2d", context.DocumentOf(sketch)), members = group.Count, groups = ((SolidEdgeFrameworkSupport.Groups)sheet.Groups).Count };
    }

    /// <summary>Ungroup (one group) or Ungroup All (every group of the sheet): the members go back to the sheet.</summary>
    public static object Ungroup(SeContext context, string sketch, string? element)
    {
        Sheet sheet = SheetOnly(ResolveCanvas(context, sketch), "ungroup");
        var groups = (SolidEdgeFrameworkSupport.Groups)sheet.Groups;
        SolidEdgeFrameworkSupport.Group[] chosen = element is null
            ? [.. Late.Items(groups).OfType<SolidEdgeFrameworkSupport.Group>()]
            : [context.Handles.Resolve(element, "element") as SolidEdgeFrameworkSupport.Group
                ?? throw ToolException.InvalidArgument($"'{element}' is not a group.", "Use the handle returned by action 'group', or no element to ungroup every group of the sheet.")];
        int members = 0;
        foreach (SolidEdgeFrameworkSupport.Group group in chosen)
        {
            members += group.Count;
            TryUngroup(sheet, group);
        }

        if (element is not null)
        {
            context.Handles.Remove(element, "the group was ungrouped");
        }

        int left = groups.Count;
        return element is null && left > 0
            ? throw new ToolException("edit_failed", $"{left} group(s) are still on '{sheet.Name}'.", "List them with se_sketch info.")
            : new { ungrouped = chosen.Length, members, groups = left };
    }

    private static void TryUngroup(Sheet sheet, SolidEdgeFrameworkSupport.Group group)
    {
        group.Ungroup();
        if (Late.Items(sheet.Groups).Any(g => Late.SameObject(g, group)))
        {
            Late.Call(group, "Delete");
        }
    }

    private static int ZOrder(object element) => Convert.ToInt32(Late.Get(element, "ZOrder"), System.Globalization.CultureInfo.InvariantCulture);

    private static Sheet SheetOnly(Canvas2d canvas, string action) => canvas.Sheet
        ?? throw ToolException.InvalidArgument($"Action '{action}' works on drawing sheets.", "Pass a drawing sheet or drawing document handle in 'sketch'.");

    /// <summary>Moves, rotates, scales or mirrors elements, or copies of them.</summary>
    private static object Transform(SeContext context, string sketch, string[]? handles, Edit edit, bool copy)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] elements = EditedElements(context, handles, edit.Name);
        Shape[] before = elements.Select(ShapeOf).ToArray();
        return copy
            ? Copies(context, canvas, sketch, elements, before, edit)
            : InPlace(context, canvas, sketch, handles!, elements, before, edit);
    }

    private static object InPlace(SeContext context, Canvas2d canvas, string sketch, string[] handles, object[] elements, Shape[] before, Edit edit)
    {
        (Dictionary<object, List<Tie>> ties, List<(Relation2d Relation, Bond Bond)> inner) = Ties(canvas, elements);
        string[] blocking = elements
            .SelectMany((e, i) => ties.TryGetValue(e, out List<Tie>? t) ? Blocking(context, sketch, handles[i], e, t, edit) : [])
            .ToArray();
        if (blocking.Length > 0)
        {
            throw new ToolException("held_by_relations",
                $"Relations would break if the elements were {edit.Past}: {string.Join("; ", blocking.Take(8))}{(blocking.Length > 8 ? "; ..." : "")}.",
                "Add the tied elements to 'elements', or delete those relations (se_sketch_constraint list, delete) and repeat; copies (copy=true) are never held.");
        }

        int[] tied = Enumerable.Range(0, elements.Length).Where(i => ties.ContainsKey(elements[i])).ToArray();
        Bond[] lifted = Lift(canvas, inner);
        var touched = new List<int>();
        try
        {
            for (int i = 0; i < elements.Length; i++)
            {
                if (!ties.ContainsKey(elements[i]))
                {
                    touched.Add(i);
                    edit.Native(elements[i]);
                }
            }

            touched.AddRange(tied);
            PlaceSet(elements, before, tied, edit.Map);
            for (int i = 0; i < elements.Length; i++)
            {
                Shape expected = Mapped(before[i], edit.Map);
                if (!Matches(ShapeOf(elements[i]), expected) && CanPlace(elements[i]) && !ties.ContainsKey(elements[i]))
                {
                    // A free element that Solid Edge did not carry as asked is placed by its points.
                    Place(elements[i], before[i], edit.Map);
                }

                if (!Matches(ShapeOf(elements[i]), expected))
                {
                    throw new ToolException("edit_failed",
                        $"Solid Edge did not {edit.Name} '{handles[i]}' as asked ({Points(ShapeOf(elements[i]))} instead of {Points(expected)}), so the elements were put back.",
                        "List the sketch (se_sketch info); copies (copy=true) are another way.");
                }
            }

            Remake(canvas, lifted, edit.Map);
        }
        catch (Exception ex) when (ex is ToolException || ComErrors.IsCallFailure(ex))
        {
            Restore(elements, before, touched, tied, edit);
            string lost = Remade(canvas, lifted);
            if (ex is ToolException tool)
            {
                throw lost.Length == 0 ? tool : new ToolException(tool.Code, tool.Message + lost, tool.NextStep, tool.InnerException);
            }

            throw new ToolException("edit_failed", $"Solid Edge could not {edit.Name} the elements: {ComErrors.Unwrap(ex).Message.Trim()}; they were put back.{lost}",
                "List the sketch (se_sketch info).", ex);
        }

        return new
        {
            elements = handles.Select((h, i) => new { element = h, geometry = Describe2d(elements[i]) }).ToArray(),
            relations = lifted.Length > 0 ? lifted.GroupBy(b => edit.Map.SwapsAxes ? Swapped(b.Kind) : b.Kind).ToDictionary(g => g.Key, g => g.Count()) : null,
        };
    }

    /// <summary>
    /// Kinds of relation that Solid Edge enforces on each point placed: inside the edited set they are lifted while it is placed and
    /// made again on the result. Verified: a horizontal, vertical, parallel, perpendicular or tangent relation kept a set placed
    /// point by point from its place (a diagonal move, a half turn), while connections, equal, colinear, concentric and symmetric
    /// relations followed it.
    /// </summary>
    private static readonly HashSet<string> Liftable = new(StringComparer.Ordinal)
    {
        "horizontal", "vertical", "parallel", "perpendicular", "tangent", "horizontal_align", "vertical_align", "set",
    };

    /// <summary>A relation between edited elements: its kind and the elements it relates, with their keypoint (negative = none).</summary>
    private sealed record Bond(string Kind, (object Element, int Key)[] Related);

    /// <summary>Deletes the relations inside the edited set that would hold it; on a failure the deleted ones are made again.</summary>
    private static Bond[] Lift(Canvas2d canvas, List<(Relation2d Relation, Bond Bond)> inner)
    {
        var lifted = new List<Bond>();
        try
        {
            foreach ((Relation2d relation, Bond bond) in inner)
            {
                relation.Delete();
                lifted.Add(bond);
            }
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("edit_failed",
                $"Solid Edge did not let the relations of the elements be lifted while they are placed: {ComErrors.Unwrap(ex).Message.Trim()}.{Remade(canvas, [.. lifted])}",
                "List the relations (se_sketch_constraint list).", ex);
        }

        return [.. lifted];
    }

    /// <summary>Makes the lifted relations again, turned by the map (a quarter turn makes a horizontal line vertical); all or none.</summary>
    private static void Remake(Canvas2d canvas, Bond[] bonds, Map2 map)
    {
        var made = new List<Relation2d>();
        foreach (Bond bond in bonds)
        {
            try
            {
                made.Add(Relate(canvas.Relations2d, bond, map.SwapsAxes));
            }
            catch (Exception ex) when (ComErrors.IsCallFailure(ex))
            {
                foreach (Relation2d relation in made)
                {
                    TryDelete(relation);
                }

                throw new ToolException("relation_failed",
                    $"Solid Edge did not make the {(map.SwapsAxes ? Swapped(bond.Kind) : bond.Kind)} relation again on the edited elements: {ComErrors.Unwrap(ex).Message.Trim()}; they were put back.",
                    "List the relations (se_sketch_constraint list) and edit a copy (copy=true) or without that relation.", ex);
            }
        }
    }

    /// <summary>Makes the lifted relations again as they were, after a failed edit; returns what could not be made, for the error.</summary>
    private static string Remade(Canvas2d canvas, Bond[] bonds)
    {
        var lost = new List<string>();
        foreach (Bond bond in bonds)
        {
            try
            {
                Relate(canvas.Relations2d, bond, swap: false);
            }
            catch (Exception ex) when (ComErrors.IsCallFailure(ex))
            {
                lost.Add(bond.Kind);
            }
        }

        return lost.Count == 0 ? ""
            : $" These relations could not be made again and are missing: {string.Join(", ", lost)} (se_sketch_constraint relation).";
    }

    private static Relation2d Relate(Relations2d relations, Bond bond, bool swap)
    {
        (object Element, int Key)[] r = bond.Related;
        return (swap ? Swapped(bond.Kind) : bond.Kind) switch
        {
            "horizontal" => relations.AddHorizontal(r[0].Element),
            "vertical" => relations.AddVertical(r[0].Element),
            "parallel" => relations.AddParallel(r[0].Element, r[1].Element),
            "perpendicular" => relations.AddPerpendicular(r[0].Element, r[1].Element),
            "tangent" => relations.AddTangent(r[0].Element, r[1].Element),
            "horizontal_align" => relations.AddHorizontalAlign(r[0].Element, r[0].Key, r[1].Element, r[1].Key),
            "vertical_align" => relations.AddVerticalAlign(r[0].Element, r[0].Key, r[1].Element, r[1].Key),
            _ => AddSet(relations, r.Select(x => x.Element).ToArray()),
        };
    }

    private static Relation2d AddSet(Relations2d relations, object[] elements)
    {
        Array set = elements;
        return relations.AddSet(set.Length, ref set);
    }

    private static string Swapped(string kind) => kind switch
    {
        "horizontal" => "vertical",
        "vertical" => "horizontal",
        "horizontal_align" => "vertical_align",
        "vertical_align" => "horizontal_align",
        _ => kind,
    };

    /// <summary>
    /// Copies of the elements, moved, rotated, scaled or mirrored; the copies are connected where their originals are, and lines
    /// horizontal or vertical by a relation stay so where they still are. Verified: Duplicate with no offset leaves a copy on the
    /// original with the relations of the element alone (horizontal, vertical, fix), which would hold it; they are deleted, so the
    /// copy is edited like a free element.
    /// </summary>
    private static object Copies(SeContext context, Canvas2d canvas, string sketch, object[] elements, Shape[] before, Edit edit)
    {
        var copies = new List<object>();
        var copied = new List<string[]>();
        var relations = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            for (int i = 0; i < elements.Length; i++)
            {
                object copy = Late.Call(elements[i], "Duplicate", 0.0, 0.0)
                    ?? throw new ToolException("edit_failed", "Solid Edge did not copy the element.", "List the sketch (se_sketch info).");
                copies.Add(copy);
                copied.Add(Unrelate(copy));
                edit.Native(copy);
                Shape expected = Mapped(before[i], edit.Map);
                if (!Matches(ShapeOf(copy), expected) && CanPlace(copy))
                {
                    Place(copy, before[i], edit.Map);
                }

                if (!Matches(ShapeOf(copy), expected) || !Matches(ShapeOf(elements[i]), before[i]))
                {
                    throw new ToolException("edit_failed",
                        $"Solid Edge did not {edit.Name} the copy of element {i + 1} as asked ({Points(ShapeOf(copy))} instead of {Points(expected)}; original at {Points(ShapeOf(elements[i]))}), so the copies were deleted.",
                        "List the sketch (se_sketch info).");
                }
            }

            Count(relations, "connect", Reconnect(canvas, elements, copies, edit.Map));
            for (int i = 0; i < copies.Count; i++)
            {
                if (copied[i].Any(k => k is "horizontal" or "vertical") && Realign(canvas, copies[i]) is { } kind)
                {
                    Count(relations, kind, 1);
                }
            }
        }
        catch (Exception ex) when (ex is ToolException || ComErrors.IsCallFailure(ex))
        {
            foreach (object copy in copies)
            {
                TryDelete(copy);
            }

            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("edit_failed", $"Solid Edge could not {edit.Name} copies of the elements: {ComErrors.Unwrap(ex).Message.Trim()}; the copies were deleted.",
                "List the sketch (se_sketch info).", ex);
        }

        return new { copies = Registered(context, sketch, copies), relations = relations.Count > 0 ? relations : null };
    }

    private static void Count(Dictionary<string, int> counts, string kind, int n)
    {
        if (n > 0)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + n;
        }
    }

    /// <summary>Deletes the relations of an element (those Duplicate gave a new copy) and returns their kinds.</summary>
    private static string[] Unrelate(object element)
    {
        List<Relation2d> own;
        try
        {
            own = Late.Items(Late.Get(element, "Relationships")!).OfType<Relation2d>().ToList();
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return [];
        }

        string[] kinds = own.Select(r => RelationName(r.Type)).ToArray();
        foreach (Relation2d relation in own)
        {
            relation.Delete();
        }

        return kinds;
    }

    /// <summary>Makes a copied line horizontal or vertical by a relation if it lies so; returns the relation made.</summary>
    private static string? Realign(Canvas2d canvas, object copy)
    {
        if (copy is not Line2d line)
        {
            return null;
        }

        line.GetStartPoint(out double x1, out double y1);
        line.GetEndPoint(out double x2, out double y2);
        if (Math.Abs(y2 - y1) <= EditTolerance)
        {
            canvas.Relations2d.AddHorizontal(line);
            return "horizontal";
        }

        if (Math.Abs(x2 - x1) <= EditTolerance)
        {
            canvas.Relations2d.AddVertical(line);
            return "vertical";
        }

        return null;
    }

    /// <summary>Connects the copies where their originals are connected by keypoint relations inside the copied set.</summary>
    private static int Reconnect(Canvas2d canvas, object[] originals, List<object> copies, Map2 map)
    {
        var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < originals.Length; i++)
        {
            index[originals[i]] = i;
        }

        Relations2d relations = canvas.Relations2d;
        var joints = new List<(int A, int KeyA, int B, int KeyB)>();
        for (int r = 1; r <= relations.Count; r++)
        {
            Relation2d relation = relations.Item(r);
            if ((SolidEdgeFramework.ObjectType)relation.Type != SolidEdgeFramework.ObjectType.igKeyPointRelation2d)
            {
                continue;
            }

            relation.GetRelatedObjectCount(out int count);
            if (count != 2)
            {
                continue;
            }

            relation.GetRelatedObject(1, out object a, out int keyA);
            relation.GetRelatedObject(2, out object b, out int keyB);
            if (index.TryGetValue(a, out int ia) && index.TryGetValue(b, out int ib))
            {
                joints.Add((ia, keyA, ib, keyB));
            }
        }

        int made = 0;
        foreach ((int a, int keyA, int b, int keyB) in joints)
        {
            // A mirrored arc swaps its ends, so the copy's keypoint is found where the original one went.
            (double X, double Y) point = map.Apply(KeyPoint(originals[a], keyA));
            if (KeypointAt(copies[a], point) is { } ka && KeypointAt(copies[b], point) is { } kb)
            {
                relations.AddKeypoint(copies[a], ka, copies[b], kb);
                made++;
            }
        }

        return made;
    }

    private static int? KeypointAt(object element, (double X, double Y) point)
    {
        int count = Convert.ToInt32(Late.Get(element, "KeyPointCount"), CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            try
            {
                if (Gap(KeyPoint(element, i), point) <= EditTolerance)
                {
                    return i;
                }
            }
            catch (ToolException)
            {
                // A keypoint that cannot be read is skipped.
            }
        }

        return null;
    }

    /// <summary>
    /// Places the elements of <paramref name="set"/>, tied by relations, at the image of their shapes, point by point: with the
    /// relations that hold each point lifted (see <see cref="Liftable"/>), the connections inside the set follow its points.
    /// </summary>
    private static void PlaceSet(object[] elements, Shape[] before, int[] set, Map2 map)
    {
        foreach (int i in set)
        {
            Place(elements[i], before[i], map);
        }
    }

    /// <summary>Puts edited elements back where they were, after an edit that failed; a failure here leaves the first error reported.</summary>
    private static void Restore(object[] elements, Shape[] before, List<int> touched, int[] tied, Edit edit)
    {
        try
        {
            int[] set = touched.Where(tied.Contains).ToArray();
            if (set.Any(i => !Matches(ShapeOf(elements[i]), before[i])))
            {
                PlaceSet(elements, before, set, Map2.Identity);
            }
        }
        catch (Exception ex) when (ex is ToolException || ComErrors.IsCallFailure(ex))
        {
            // The error that stopped the edit is the one reported.
        }

        foreach (int i in touched.Where(i => !tied.Contains(i)))
        {
            try
            {
                if (Matches(ShapeOf(elements[i]), before[i]))
                {
                    continue;
                }

                if (CanPlace(elements[i]))
                {
                    Place(elements[i], before[i], Map2.Identity);
                }
                else
                {
                    edit.Inverse().Native(elements[i]);
                }
            }
            catch (Exception ex) when (ex is ToolException || ComErrors.IsCallFailure(ex))
            {
                // The error that stopped the edit is the one reported.
            }
        }
    }

    /// <summary>A relation or driving dimension on a selected element, with the elements it ties outside the selection.</summary>
    private sealed record Tie(string Kind, object[] Outside, bool Sized);

    /// <summary>
    /// Relations and driving dimensions of the selected elements, and the relations inside the selection that hold each point
    /// placed (<see cref="Liftable"/>).
    /// </summary>
    private static (Dictionary<object, List<Tie>> Ties, List<(Relation2d Relation, Bond Bond)> Inner) Ties(Canvas2d canvas, object[] elements)
    {
        var selected = new HashSet<object>(elements, ReferenceEqualityComparer.Instance);
        var ties = new Dictionary<object, List<Tie>>(ReferenceEqualityComparer.Instance);
        void Add(List<object> related, Tie tie)
        {
            foreach (object o in related.Where(selected.Contains).Distinct(ReferenceEqualityComparer.Instance))
            {
                if (!ties.TryGetValue(o, out List<Tie>? list))
                {
                    ties[o] = list = [];
                }

                list.Add(tie);
            }
        }

        var inner = new List<(Relation2d, Bond)>();
        Relations2d relations = canvas.Relations2d;
        for (int i = 1; i <= relations.Count; i++)
        {
            Relation2d relation = relations.Item(i);
            relation.GetRelatedObjectCount(out int count);
            var related = new List<(object Element, int Key)>();
            for (int j = 1; j <= count; j++)
            {
                relation.GetRelatedObject(j, out object o, out int key);
                related.Add((o, key));
            }

            List<object> objects = related.Select(r => r.Element).ToList();
            if (objects.Any(selected.Contains))
            {
                string kind = RelationName(relation.Type);
                object[] outside = objects.Where(o => !selected.Contains(o)).ToArray();
                Add(objects, new Tie(kind, outside, Sized: false));
                if (outside.Length == 0 && Liftable.Contains(kind))
                {
                    inner.Add((relation, new Bond(kind, [.. related])));
                }
            }
        }

        Dimensions dimensions = canvas.Dimensions;
        for (int i = 1; i <= dimensions.Count; i++)
        {
            SolidEdgeFrameworkSupport.Dimension dimension = dimensions.Item(i);
            if (!dimension.Constraint)
            {
                // A driven dimension only reports the geometry.
                continue;
            }

            // Verified: GetRelated starts at 0, while a relation's GetRelatedObject starts at 1.
            dimension.GetRelatedCount(out int count);
            var related = new List<object>();
            for (int j = 0; j < count; j++)
            {
                dimension.GetRelated(j, out object o, out _, out _, out _, out _);
                related.Add(o);
            }

            if (related.Any(selected.Contains))
            {
                bool angular = dimension.DimensionType is DimTypeConstants.igDimTypeAngular or DimTypeConstants.igDimTypeArcAngle;
                Add(related, new Tie($"dimension {dimension.DisplayName}", related.Where(o => !selected.Contains(o)).ToArray(), Sized: !angular));
            }
        }

        return (ties, inner);
    }

    /// <summary>Why the relations of an element would break if it were edited with the others, if they would.</summary>
    private static IEnumerable<string> Blocking(SeContext context, string sketch, string handle, object element, List<Tie> ties, Edit edit)
    {
        if (edit.Map.Reflects && element is Arc2d or EllipticalArc2d)
        {
            yield return $"{handle}: a mirrored arc swaps its ends, so its relations would point at the wrong end";
            yield break;
        }

        if (!CanPlace(element))
        {
            yield return $"{handle}: a {KindOf(element)} tied by relations can only be edited as a copy";
            yield break;
        }

        foreach (Tie tie in ties)
        {
            string? reason = tie.Outside.Length > 0
                ? $"{tie.Kind} with {string.Join(", ", tie.Outside.Take(3).Select(o => context.RegisterBeside(o, KindOf(o), sketch)))}, which is not in 'elements'"
                : tie.Kind == "fix" ? "it is fixed"
                : tie.Kind == "link" ? "it is linked to model geometry"
                : !edit.KeepsAxes && !edit.Map.SwapsAxes && tie.Kind is "horizontal" or "vertical" or "horizontal_align" or "vertical_align" ? $"{tie.Kind} relation"
                : tie.Sized && edit.Resizes ? $"{tie.Kind} would change size"
                : null;
            if (reason is not null)
            {
                yield return $"{handle}: {reason}";
            }
        }
    }

    /// <summary>
    /// Measures what a trim, extend, split or corner did: the elements that remain, the new ones and the ones it removed (a
    /// trimmed circle becomes an arc).
    /// </summary>
    private static object Cut(SeContext context, Canvas2d canvas, string sketch, (string Handle, object Element)[] targets, Action call, string failure, string hint)
    {
        int[] before = ElementCounts(canvas);
        string[] was = targets.Select(t => Fingerprint(t.Element)).ToArray();
        try
        {
            call();
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("edit_failed", $"{failure}: {ComErrors.Unwrap(ex).Message.Trim()}", hint, ex);
        }

        List<object> created = NewElements(canvas, before).ToList();
        bool[] removed = targets.Select(t => !Present(canvas, t.Element)).ToArray();
        bool changed = targets.Where((t, i) => !removed[i] && Fingerprint(t.Element) != was[i]).Any();
        if (created.Count == 0 && !removed.Any(r => r) && !changed)
        {
            throw new ToolException("edit_failed", $"{failure}.", hint);
        }

        for (int i = 0; i < targets.Length; i++)
        {
            if (removed[i])
            {
                context.Handles.Remove(targets[i].Handle, "Solid Edge replaced the element when editing it");
            }
        }

        return new
        {
            elements = targets.Where((t, i) => !removed[i]).Select(t => new { element = t.Handle, geometry = Describe2d(t.Element) }).ToArray(),
            created = Registered(context, sketch, created),
            removed = removed.Any(r => r) ? targets.Where((t, i) => removed[i]).Select(t => t.Handle).ToArray() : null,
        };
    }

    /// <summary>Checks the copies a pattern made against the places they must take; a pattern that missed is deleted.</summary>
    private static object Patterned(SeContext context, Canvas2d canvas, string sketch, object pattern, int[] before, object[] originals,
        (double X, double Y)[] expected, int occurrences)
    {
        List<object> created = NewElements(canvas, before).ToList();
        string kind = KindOf(originals[0]);
        List<(double X, double Y)> firsts = created.Where(o => KindOf(o) == kind).Select(o => ShapeOf(o).Points[0]).ToList();
        int wanted = originals.Length * (occurrences - 1);
        if (created.Count != wanted || !expected.All(p => firsts.Any(q => Gap(p, q) <= EditTolerance)))
        {
            TryDelete(pattern);
            foreach (object o in NewElements(canvas, before).ToList())
            {
                TryDelete(o);
            }

            throw new ToolException("pattern_failed",
                $"Solid Edge made {created.Count} element(s) instead of {wanted} at the places asked, so the pattern was deleted.",
                "Check the count, the spacing or sweep and the centre.");
        }

        return new
        {
            pattern = context.RegisterBeside(pattern, "pattern2d", sketch),
            occurrences,
            created = Registered(context, sketch, created),
        };
    }

    private static object[] Registered(SeContext context, string sketch, IEnumerable<object> elements) =>
        elements.Select(o => (object)new { element = context.RegisterBeside(o, KindOf(o), sketch), geometry = Describe2d(o) }).ToArray();

    private static object[] EditedElements(SeContext context, string[]? handles, string action)
    {
        object[] elements = Elements(context, handles, 1, MaxEdited, action);
        if (handles!.Distinct(StringComparer.Ordinal).Count() != handles!.Length)
        {
            throw ToolException.InvalidArgument("'elements' lists the same element twice.");
        }

        return elements;
    }

    private static object Editable(SeContext context, string handle, string action, Func<object, bool> supported)
    {
        object element = context.Handles.Resolve(handle, "element");
        return supported(element)
            ? element
            : throw ToolException.InvalidArgument($"Action '{action}' does not apply to a {KindOf(element)} ('{handle}').",
                action == "split" ? "Circles and ellipses cannot be split: trim them instead." : "Use the handle of a line, arc or curve of the sketch.");
    }

    private static object[] Bounds(SeContext context, string[]? bounds, int max, string action) =>
        bounds is { Length: > 0 } b && b.Length <= max
            ? b.Select(h => context.Handles.Resolve(h, "bounds")).ToArray()
            : throw ToolException.InvalidArgument(max == 1
                ? $"Action '{action}' requires 'bounds' = [the element to reach]."
                : $"Action '{action}' requires 'bounds' = 1 or 2 elements that cut the element where the part to remove ends.");

    private static bool Present(Canvas2d canvas, object element) =>
        ElementCollections.Any(c => Late.Items(c(canvas)).Any(o => ReferenceEquals(o, element)));

    private static string Fingerprint(object element) => JsonSerializer.Serialize(Describe2d(element));

    private static void TryDelete(object element)
    {
        try
        {
            Late.Call(element, "Delete");
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex) || Late.IsMemberFailure(ex))
        {
            // Cleaning up after a failure: the error that stopped the edit is the one reported.
        }
    }

    private static string Mm(double x, double y) =>
        string.Create(CultureInfo.InvariantCulture, $"({Geo.Mm(x)}, {Geo.Mm(y)}) mm");

    private static string Points(Shape shape) =>
        string.Join(" ", shape.Points.Select(p => Mm(p.X, p.Y).Replace(" mm", "", StringComparison.Ordinal))) + " mm";

    private static double Gap((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>
    /// Points of a 2D element that a move, rotation, scale or mirror carries like points (ends, centre, ends of the major axis),
    /// its radius (major and minor for an ellipse) and its angles, in metres and radians.
    /// </summary>
    private sealed record Shape((double X, double Y)[] Points, double Size = 0, double Minor = 0, double Start = 0, double Sweep = 0, double Turn = 0);

    private static Shape ShapeOf(object element)
    {
        switch (element)
        {
            case Line2d line:
            {
                line.GetStartPoint(out double x1, out double y1);
                line.GetEndPoint(out double x2, out double y2);
                return new([(x1, y1), (x2, y2)]);
            }

            case Circle2d circle:
            {
                circle.GetCenterPoint(out double x, out double y);
                return new([(x, y)], circle.Radius);
            }

            case Arc2d arc:
            {
                arc.GetCenterPoint(out double x, out double y);
                arc.GetStartPoint(out double x1, out double y1);
                arc.GetEndPoint(out double x2, out double y2);
                return new([(x, y), (x1, y1), (x2, y2)], arc.Radius, Start: arc.StartAngle, Sweep: arc.SweepAngle);
            }

            case Ellipse2d ellipse:
            {
                ellipse.GetCenterPoint(out double x, out double y);
                double r = ellipse.MajorRadius, turn = ellipse.RotationAngle;
                (double X, double Y) u = (r * Math.Cos(turn), r * Math.Sin(turn));
                return new([(x, y), (x + u.X, y + u.Y), (x - u.X, y - u.Y)], r, ellipse.MinorRadius, Turn: turn);
            }

            case EllipticalArc2d arc:
            {
                arc.GetCenterPoint(out double x, out double y);
                arc.GetStartPoint(out double x1, out double y1);
                arc.GetEndPoint(out double x2, out double y2);
                return new([(x, y), (x1, y1), (x2, y2)], arc.MajorRadius, arc.MinorRadius);
            }

            case Point2d point:
            {
                point.GetPoint(out double x, out double y);
                return new([(x, y)]);
            }

            case BSplineCurve2d spline:
            {
                spline.GetNode(1, out double x1, out double y1);
                spline.GetNode(spline.NodeCount, out double x2, out double y2);
                return new([(x1, y1), (x2, y2)]);
            }

            default:
                throw new ToolException("not_supported",
                    $"se_sketch_edit edits lines, circles, arcs, ellipses, elliptical arcs, splines and points, not a {KindOf(element)}.",
                    "For other objects use se_com_invoke (Move, Rotate, Scale, Mirror; metres and radians).");
        }
    }

    private static Shape Mapped(Shape shape, Map2 map) => shape with
    {
        Points = shape.Points.Select(map.Apply).ToArray(),
        Size = shape.Size * map.Factor,
        Minor = shape.Minor * map.Factor,
    };

    /// <summary>Same points (in any order: a mirror reverses an arc) and sizes.</summary>
    private static bool Matches(Shape actual, Shape expected)
    {
        if (actual.Points.Length != expected.Points.Length
            || Math.Abs(actual.Size - expected.Size) > EditTolerance
            || Math.Abs(actual.Minor - expected.Minor) > EditTolerance)
        {
            return false;
        }

        var left = actual.Points.ToList();
        foreach ((double X, double Y) p in expected.Points)
        {
            int i = left.FindIndex(q => Gap(p, q) <= EditTolerance);
            if (i < 0)
            {
                return false;
            }

            left.RemoveAt(i);
        }

        return true;
    }

    private static bool CanPlace(object element) => element is Line2d or Circle2d or Arc2d or Ellipse2d or Point2d;

    /// <summary>
    /// Puts an element at the image of its shape through its setters. Verified: they do not run the relation solver (the start
    /// point of a line set alone leaves the line connected to it where it was), so the elements tied to it are placed too.
    /// </summary>
    private static void Place(object element, Shape shape, Map2 map)
    {
        (double X, double Y) c = map.Apply(shape.Points[0]);
        double f = map.Factor;
        switch (element)
        {
            case Line2d line:
                (double X, double Y) end = map.Apply(shape.Points[1]);
                line.SetStartPoint(c.X, c.Y);
                line.SetEndPoint(end.X, end.Y);
                break;
            case Circle2d circle:
                circle.SetCenterPoint(c.X, c.Y);
                circle.Radius = shape.Size * f;
                break;
            case Arc2d arc:
                // Verified: SetCenterPoint carries the whole arc, while StartAngle keeps its end where it was, so the sweep is set
                // after it. A mirror reverses the arc: it then starts at the image of its end.
                arc.SetCenterPoint(c.X, c.Y);
                arc.Radius = shape.Size * f;
                arc.StartAngle = TurnOf(map.Reflects ? map.Turn - shape.Start - shape.Sweep : shape.Start + map.Turn);
                arc.SweepAngle = shape.Sweep;
                break;
            case Ellipse2d ellipse:
                ellipse.SetCenterPoint(c.X, c.Y);

                // The major radius stays the larger one at every step.
                if (f >= 1)
                {
                    ellipse.MajorRadius = shape.Size * f;
                    ellipse.MinorRadius = shape.Minor * f;
                }
                else
                {
                    ellipse.MinorRadius = shape.Minor * f;
                    ellipse.MajorRadius = shape.Size * f;
                }

                ellipse.RotationAngle = TurnOf(map.Direction(shape.Turn));
                break;
            case Point2d point:
                point.SetPoint(c.X, c.Y);
                break;
            default:
                throw new ToolException("not_supported", $"A {KindOf(element)} cannot be placed point by point.", "Edit a copy of it (copy=true).");
        }
    }

    private static double TurnOf(double angle) => ((angle % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI);

    /// <summary>A move, rotation, scale or mirror: its map of the plane and the Solid Edge call that does it to a free element.</summary>
    private sealed record Edit(string Name, string Past, Map2 Map, Action<object> Native, Func<Edit> Inverse)
    {
        /// <summary>Do horizontal and vertical lines stay horizontal and vertical?</summary>
        public bool KeepsAxes => Math.Abs(Map.B) < 1e-12 && Math.Abs(Map.C) < 1e-12;

        public bool Resizes => Math.Abs(Map.Factor - 1) > 1e-12;
    }

    private static Edit Moving(double dx, double dy) => new("move", "moved", Map2.Move(dx, dy), e =>
    {
        (double X, double Y) a = ShapeOf(e).Points[0];
        Late.Call(e, "Move", a.X, a.Y, a.X + dx, a.Y + dy);
    }, () => Moving(-dx, -dy));

    /// <summary>Verified: Rotate turns counter-clockwise for a positive angle.</summary>
    private static Edit Rotating((double X, double Y) c, double angle) => new("rotate", "rotated", Map2.Rotate(c, angle),
        e => Late.Call(e, "Rotate", angle, c.X, c.Y), () => Rotating(c, -angle));

    private static Edit Scaling((double X, double Y) c, double factor)
    {
        Map2 map = Map2.Scale(c, factor);
        return new("scale", "scaled", map, e =>
        {
            // Verified: Scale keeps the element's own centre (a circle's centre, a line's midpoint); a move then scales it about 'center'.
            (double X, double Y) a = ShapeOf(e).Points[0];
            Late.Call(e, "Scale", factor);
            (double X, double Y) now = ShapeOf(e).Points[0];
            (double X, double Y) target = map.Apply(a);
            if (Gap(now, target) > EditTolerance)
            {
                Late.Call(e, "Move", now.X, now.Y, target.X, target.Y);
            }
        }, () => Scaling(c, 1 / factor));
    }

    private static Edit Mirroring((double X, double Y) p, (double X, double Y) q) => new("mirror", "mirrored", Map2.Mirror(p, q),
        e => Late.Call(e, "Mirror", p.X, p.Y, q.X, q.Y, false), () => Mirroring(p, q));

    /// <summary>Similarity of the sketch plane, in metres: x' = A x + B y + Tx, y' = C x + D y + Ty.</summary>
    private readonly record struct Map2(double A, double B, double C, double D, double Tx, double Ty)
    {
        public static Map2 Identity => new(1, 0, 0, 1, 0, 0);

        public double Factor => Math.Sqrt(Math.Abs((A * D) - (B * C)));

        public bool Reflects => (A * D) - (B * C) < 0;

        /// <summary>Do horizontal lines become vertical and vertical lines horizontal (a quarter turn)?</summary>
        public bool SwapsAxes => Math.Abs(A) < 1e-12 && Math.Abs(D) < 1e-12;

        /// <summary>Angle of the image of the x axis: the rotation, or twice the angle of the mirror line.</summary>
        public double Turn => Math.Atan2(C, A);

        public (double X, double Y) Apply((double X, double Y) p) => ((A * p.X) + (B * p.Y) + Tx, (C * p.X) + (D * p.Y) + Ty);

        /// <summary>Image of a direction given by its angle.</summary>
        public double Direction(double angle) => Reflects ? Turn - angle : angle + Turn;

        public static Map2 Move(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

        public static Map2 Rotate((double X, double Y) c, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            return new(cos, -sin, sin, cos, c.X - (cos * c.X) + (sin * c.Y), c.Y - (sin * c.X) - (cos * c.Y));
        }

        public static Map2 Scale((double X, double Y) c, double factor) =>
            new(factor, 0, 0, factor, c.X - (factor * c.X), c.Y - (factor * c.Y));

        public static Map2 Mirror((double X, double Y) p, (double X, double Y) q)
        {
            double twice = 2 * Math.Atan2(q.Y - p.Y, q.X - p.X), cos = Math.Cos(twice), sin = Math.Sin(twice);
            return new(cos, sin, sin, -cos, p.X - (cos * p.X) - (sin * p.Y), p.Y - (sin * p.X) + (cos * p.Y));
        }
    }
}
