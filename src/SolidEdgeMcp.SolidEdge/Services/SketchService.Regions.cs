// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Closed regions of a drawing sheet or a sketch, as the Fill and Area commands do: fills (Boundaries2d) and area properties
/// (AreaPropertiesCollection). Solid Edge looks for the region round a point among the given elements.
/// </summary>
public static partial class SketchService
{
    /// <summary>Look of a fill: fill style name, colour #RRGGBB, angle in degrees and spacing in mm.</summary>
    public sealed record FillLook(string? Style, string? Color, double? Angle, double? Spacing)
    {
        public bool IsEmpty => Style is null && Color is null && Angle is null && Spacing is null;
    }

    /// <summary>Ends closer than 1 nm meet: never looser than Solid Edge, since a gap that it sees would bring it down.</summary>
    private const double RegionJoin = 1e-9;

    /// <summary>The point keeps 0.01 mm from every element, far more than the polyline error of the curves (at most 0.3 µm).</summary>
    private const double RegionMargin = 1e-5;

    private const double RegionSagitta = 1e-7;

    public static object Fill(SeContext context, string sketch, string[]? elements, double[]? point, double[]? points, FillLook look)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        Boundary2d fill;
        if (points is not null)
        {
            if (elements is not null || point is not null)
            {
                throw ToolException.InvalidArgument("Pass either 'points' (a polygon) or 'elements' and 'point', not both.");
            }

            List<(double X, double Y)> corners = Geo.Points2(points, "points", 3);
            double twice = corners.Select((a, i) => (a.X * corners[(i + 1) % corners.Count].Y) - (corners[(i + 1) % corners.Count].X * a.Y)).Sum();
            if (Math.Abs(twice) / 2 < 1e-12)
            {
                throw ToolException.InvalidArgument("The polygon of 'points' encloses no area.", "Give at least three corners that are not on one line.");
            }

            Array flat = corners.SelectMany(p => new[] { p.X, p.Y }).ToArray();
            fill = canvas.Boundaries2d.AddByPoints(corners.Count, ref flat);
            if (!(fill.Area > 0))
            {
                Late.TryDelete(fill);
                throw ToolException.InvalidArgument("The polygon of 'points' encloses no area.", "Give at least three corners that are not on one line.");
            }
        }
        else
        {
            (object[] objects, _, (double X, double Y) p) = RegionInput(context, canvas, elements, point, "fill");
            fill = AddFill(canvas, objects, p);
        }

        try
        {
            ApplyLook(context, sketch, fill, look);
        }
        catch
        {
            Late.TryDelete(fill);
            throw;
        }

        return DescribeFill(context, sketch, fill);
    }

    public static object Fills(SeContext context, string sketch)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        Boundaries2d fills = canvas.Boundaries2d;
        List<object> items = [.. Late.Items(fills, context.MaxListItems).OfType<Boundary2d>().Select(f => DescribeFill(context, sketch, f))];
        return new { sketch, count = fills.Count, fills = items, truncated = items.Count < fills.Count ? true : (bool?)null };
    }

    public static object Restyle(SeContext context, string fill, FillLook look)
    {
        Boundary2d boundary = context.Handles.Resolve<Boundary2d>(fill, "fill");
        if (look.IsEmpty)
        {
            throw ToolException.InvalidArgument("Action 'style' needs at least one of 'style', 'color', 'angle' or 'spacing'.");
        }

        ApplyLook(context, fill, boundary, look);
        return DescribeFill(context, fill, boundary);
    }

    public static object DeleteFill(SeContext context, string fill)
    {
        Boundary2d boundary = context.Handles.Resolve<Boundary2d>(fill, "fill");
        boundary.Delete();
        context.Handles.Remove(fill, "the fill was deleted");
        return new { deleted = fill };
    }

    /// <summary>
    /// Area properties of the region round the point, minus the holes. Nothing stays in the document: the fill that checks the
    /// region and the area properties objects are deleted after they are read.
    /// </summary>
    public static object Area(SeContext context, string sketch, string[]? elements, double[]? point, string[]? holes)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        (object[] objects, _, (double X, double Y) p) = RegionInput(context, canvas, elements, point, "area");
        Boundary2d check = AddFill(canvas, objects, p);
        double xMin, yMin, xMax, yMax;
        try
        {
            check.Range(out xMin, out yMin, out xMax, out yMax);
        }
        finally
        {
            Late.TryDelete(check);
        }

        Region2d.Moments outer = Measure(canvas, objects, p);
        var cut = new List<Region2d.Moments>();
        foreach ((object[] loop, (double X, double Y)[] corners, (double X, double Y) inside) in Holes(context, canvas, holes))
        {
            if (corners.Any(c => c.X < xMin - RegionJoin || c.X > xMax + RegionJoin || c.Y < yMin - RegionJoin || c.Y > yMax + RegionJoin))
            {
                throw ToolException.InvalidArgument("A hole reaches outside the region.",
                    "Pass as 'holes' only closed elements or chains that lie inside the region without touching its edge.");
            }

            cut.Add(Measure(canvas, loop, inside));
        }

        Region2d.Moments m = cut.Count == 0 ? outer : outer.Minus(cut);
        (double max, double min, double angle) = m.Principal();
        return new
        {
            sketch,
            area = Geo.Mm2(m.Area),
            perimeter = Geo.Mm(m.Perimeter),
            centroid = new[] { Geo.Mm(m.Cx), Geo.Mm(m.Cy) },
            inertia = new { ix = Geo.Mm4(m.Ix), iy = Geo.Mm4(m.Iy), ixy = Geo.Mm4(m.Ixy), polar = Geo.Mm4(m.Ix + m.Iy) },
            principal = new { max = Geo.Mm4(max), min = Geo.Mm4(min), angle = Geo.R(Units.RadToDeg(angle)) },
            radiusOfGyration = new { x = Geo.Mm(Math.Sqrt(m.Ix / m.Area)), y = Geo.Mm(Math.Sqrt(m.Iy / m.Area)) },
            sectionModulus = new
            {
                x = Geo.Mm3(m.Ix / Math.Max(yMax - m.Cy, m.Cy - yMin)),
                y = Geo.Mm3(m.Iy / Math.Max(xMax - m.Cx, m.Cx - xMin)),
            },
            range = new[] { Geo.Mm(xMin), Geo.Mm(yMin), Geo.Mm(xMax), Geo.Mm(yMax) },
            holes = cut.Count,
            units = "mm, mm², mm³, mm⁴. Second moments about the centroid on axes parallel to x and y; ixy = ∫(x−cx)(y−cy)dA; "
                + "principal angle = direction of the axis of the maximum, degrees from x; section moduli = I / farthest fibre.",
        };
    }

    /// <summary>
    /// The elements of the canvas that bound the region and the point inside it (metres), proved before Solid Edge looks for
    /// the region: verified in Solid Edge 2026, a point outside every closed region fails with RPC_E_SERVERFAULT.
    /// </summary>
    private static (object[] Objects, Region2d.Curve[] Curves, (double X, double Y) Point) RegionInput(SeContext context, Canvas2d canvas,
        string[]? elements, double[]? point, string action)
    {
        object[] objects = Elements(context, elements, 1, MaxEdited, $"action '{action}'");
        Region2d.Curve[] curves = Curves(canvas, elements!, objects, "elements");
        (double X, double Y) p;
        if (point is null)
        {
            List<int[]> loops = Region2d.Loops(curves, RegionJoin);
            if (loops.Count != 1)
            {
                throw loops.Count == 0
                    ? NoLoop()
                    : ToolException.InvalidArgument("The elements form more than one closed loop: pass 'point' inside the region to choose it.");
            }

            p = Region2d.InteriorPoint([.. loops[0].Select(i => curves[i])], RegionJoin, RegionMargin)
                ?? throw ToolException.InvalidArgument("No point was found inside the loop of the elements.", "Pass 'point' inside the region.");
        }
        else
        {
            p = Geo.Point2(point, "point");
        }

        switch (Region2d.Locate(curves, p, RegionJoin, RegionMargin))
        {
            case Region2d.Place.Inside:
                return (objects, curves, p);
            case Region2d.Place.OnElement:
                throw ToolException.InvalidArgument("'point' lies on one of the elements or closer than 0.01 mm to one.",
                    "Pick a point clearly inside the region.");
            case Region2d.Place.NoLoop:
                throw NoLoop();
            default:
                throw new ToolException("point_outside", $"'point' [{Geo.Mm(p.X)}, {Geo.Mm(p.Y)}] is outside every closed loop of the elements.",
                    "Pick a point inside the region (se_sketch info gives the geometry of the elements).");
        }
    }

    private static ToolException NoLoop() => new("no_closed_region",
        "The elements form no closed loop. A region must be bounded by closed elements (circle, ellipse, closed spline) or by "
        + "chains of elements whose ends meet; elements that only cross each other (like '#') are not accepted.",
        "Join the ends (se_sketch_edit corner, trim or extend) or draw the boundary as a polyline, and include all its elements.");

    /// <summary>
    /// Adds the fill and checks that Solid Edge filled the region round the point. Verified in Solid Edge 2026: when a closed
    /// loop of the elements lies inside that region (an island), it fills the loop instead, and GetFloodPoint then returns a
    /// point of the loop rather than the given one.
    /// </summary>
    private static Boundary2d AddFill(Canvas2d canvas, object[] objects, (double X, double Y) p)
    {
        Array array = objects;
        Boundary2d fill = canvas.Boundaries2d.AddByObjects(objects.Length, ref array, p.X, p.Y);
        fill.GetFloodPoint(out double x, out double y);
        if (Math.Abs(x - p.X) > RegionJoin || Math.Abs(y - p.Y) > RegionJoin)
        {
            Late.TryDelete(fill);
            throw new ToolException("region_with_island",
                "A closed loop of the elements lies inside the region round 'point' (an island): Solid Edge fills that loop instead, so the fill was removed.",
                "Leave the inner loop out of 'elements'. For a fill with a hole, draw a line across the hole and fill each side; for its area, pass the hole as 'holes' in action 'area'.");
        }

        if (fill.State != Boundary2dStateConstants.igBoundary2dUpToDate || !(fill.Area > 0))
        {
            Late.TryDelete(fill);
            throw new ToolException("region_failed", "Solid Edge could not compute the region round 'point'.",
                "Check that the elements close the region (se_sketch info) and pick another point inside it.");
        }

        return fill;
    }

    private static Region2d.Moments Measure(Canvas2d canvas, object[] objects, (double X, double Y) p)
    {
        Array array = objects;
        AreaProperties properties = canvas.AreaPropertiesCollection.Add(objects.Length, ref array, p.X, p.Y);
        try
        {
            properties.GetAreaProperties(out double area, out double cx, out double cy, out double ix, out double iy, out double ixy,
                out _, out _, out _, out _, out _, out _, out _, out double perimeter);
            return new(area, perimeter, cx, cy, ix, iy, ixy);
        }
        finally
        {
            Late.TryDelete(properties);
        }
    }

    /// <summary>Each hole: a closed element or one chain whose ends meet, with its corners and a point inside it.</summary>
    private static List<(object[] Objects, (double X, double Y)[] Corners, (double X, double Y) Inside)> Holes(SeContext context, Canvas2d canvas, string[]? holes)
    {
        var result = new List<(object[], (double X, double Y)[], (double X, double Y))>();
        if (holes is not { Length: > 0 })
        {
            return result;
        }

        object[] objects = Elements(context, holes, 1, MaxEdited, "'holes'");
        Region2d.Curve[] curves = Curves(canvas, holes, objects, "holes");
        List<int[]> loops = Region2d.Loops(curves, RegionJoin);
        int[] uses = new int[curves.Length];
        foreach (int i in loops.SelectMany(l => l))
        {
            uses[i]++;
        }

        if (uses.Any(u => u != 1))
        {
            throw ToolException.InvalidArgument("Each hole must be a closed element or one chain of elements whose ends meet, with no other elements.",
                "Pass in 'holes' only the elements of the hole boundaries.");
        }

        foreach (int[] loop in loops)
        {
            Region2d.Curve[] ring = [.. loop.Select(i => curves[i])];
            (double X, double Y) inside = Region2d.InteriorPoint(ring, RegionJoin, RegionMargin)
                ?? throw ToolException.InvalidArgument("No point was found inside one of the holes.", "Check that each hole encloses an area.");
            result.Add(([.. loop.Select(i => objects[i])], [.. ring.SelectMany(c => c.Points)], inside));
        }

        return result;
    }

    /// <summary>The elements as polylines (metres); they must belong to the canvas.</summary>
    private static Region2d.Curve[] Curves(Canvas2d canvas, string[] handles, object[] objects, string argument)
    {
        var curves = new Region2d.Curve[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            if (!Present(canvas, objects[i]))
            {
                throw ToolException.InvalidArgument($"'{handles[i]}' in '{argument}' is not an element of {canvas.Name}.",
                    "Pass elements of the same sketch or sheet (se_sketch info lists them).");
            }

            curves[i] = CurveOf(objects[i], handles[i]);
        }

        return curves;
    }

    /// <summary>A line, arc, circle, ellipse, elliptical arc or spline of a sketch as a polyline (<see cref="CurveOf"/>).</summary>
    internal static Region2d.Curve Outline(object element) => CurveOf(element, KindOf(element));

    /// <summary>A 2D element as a polyline whose chords stay within 0.1 µm of it; the ends are kept exactly.</summary>
    private static Region2d.Curve CurveOf(object element, string handle)
    {
        switch (element)
        {
            case Line2d line:
            {
                line.GetStartPoint(out double x1, out double y1);
                line.GetEndPoint(out double x2, out double y2);
                return new([(x1, y1), (x2, y2)], false);
            }

            case Circle2d circle:
            {
                circle.GetCenterPoint(out double x, out double y);
                double r = circle.Radius;
                return new(Round((x, y), (r, 0), (0, r), 0, 2 * Math.PI, Steps(r, 2 * Math.PI)), true);
            }

            case Arc2d arc:
            {
                arc.GetCenterPoint(out double x, out double y);
                arc.GetStartPoint(out double x1, out double y1);
                arc.GetEndPoint(out double x2, out double y2);
                double r = arc.Radius;
                arc.Range(out double a, out double b, out double c, out double d);
                return new(Side((x, y), (r, 0), (0, r), (x1, y1), (x2, y2), (a, b, c, d), r), false);
            }

            case Ellipse2d ellipse:
            {
                ellipse.GetCenterPoint(out double x, out double y);
                ellipse.GetMajorAxis(out double ux, out double uy);
                (double X, double Y) u = (ux, uy), v = Minor(u, ellipse.MinorRadius);
                return new(Round((x, y), u, v, 0, 2 * Math.PI, Steps(Math.Max(ellipse.MajorRadius, ellipse.MinorRadius), 2 * Math.PI)), true);
            }

            case EllipticalArc2d arc:
            {
                arc.GetCenterPoint(out double x, out double y);
                arc.GetMajorAxis(out double ux, out double uy);
                arc.GetStartPoint(out double x1, out double y1);
                arc.GetEndPoint(out double x2, out double y2);
                arc.Range(out double a, out double b, out double c, out double d);
                (double X, double Y) u = (ux, uy), v = Minor(u, arc.MinorRadius);
                return new(Side((x, y), u, v, (x1, y1), (x2, y2), (a, b, c, d), Math.Max(arc.MajorRadius, arc.MinorRadius)), false);
            }

            case BSplineCurve2d spline:
            {
                spline.GetParameterRange(out double t0, out double t1);
                const int samples = 512;
                var points = new (double X, double Y)[samples + 1];
                for (int i = 0; i <= samples; i++)
                {
                    spline.Evaluate(t0 + ((t1 - t0) * i / samples), out object point, out _, out _, out _);
                    double[] xy = [.. Geo.Doubles((Array)point)];
                    points[i] = (xy[0], xy[1]);
                }

                bool closed = spline.Form is not (Geom2dFormConstants.igGeom2dFormOpen or Geom2dFormConstants.igGeom2dFormUnknown);
                if (!closed)
                {
                    spline.GetNode(1, out double x1, out double y1);
                    spline.GetNode(spline.NodeCount, out double x2, out double y2);
                    (points[0], points[^1]) = ((x1, y1), (x2, y2));
                }

                return new(points, closed);
            }

            default:
                throw ToolException.InvalidArgument($"'{handle}' is a {KindOf(element)}: a region is bounded by lines, arcs, circles, ellipses, elliptical arcs and splines.");
        }
    }

    /// <summary>Points of centre + cos t·u + sin t·v for t from <paramref name="start"/> over <paramref name="sweep"/>.</summary>
    private static (double X, double Y)[] Round((double X, double Y) c, (double X, double Y) u, (double X, double Y) v, double start, double sweep, int steps)
    {
        var points = new (double X, double Y)[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double t = start + (sweep * i / steps);
            points[i] = (c.X + (Math.Cos(t) * u.X) + (Math.Sin(t) * v.X), c.Y + (Math.Cos(t) * u.Y) + (Math.Sin(t) * v.Y));
        }

        return points;
    }

    /// <summary>
    /// The arc of the circle or ellipse (centre + cos t·u + sin t·v) from start to end whose range matches the element's
    /// Range: this does not depend on how Solid Edge orients arcs.
    /// </summary>
    private static (double X, double Y)[] Side((double X, double Y) c, (double X, double Y) u, (double X, double Y) v, (double X, double Y) s,
        (double X, double Y) e, (double XMin, double YMin, double XMax, double YMax) range, double radius)
    {
        double t0 = Parameter(c, u, v, s), t1 = Parameter(c, u, v, e);
        double forward = (((t1 - t0) % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI);
        if (forward < 1e-12)
        {
            forward = 2 * Math.PI;
        }

        (double X, double Y)[] Arc(double sweep)
        {
            (double X, double Y)[] points = Round(c, u, v, t0, sweep, Math.Max(4, (int)Math.Ceiling(Steps(radius, 2 * Math.PI) * Math.Abs(sweep) / (2 * Math.PI))));
            (points[0], points[^1]) = (s, e);
            return points;
        }

        static double Miss((double X, double Y)[] points, (double XMin, double YMin, double XMax, double YMax) r) =>
            Math.Abs(points.Min(p => p.X) - r.XMin) + Math.Abs(points.Min(p => p.Y) - r.YMin)
            + Math.Abs(points.Max(p => p.X) - r.XMax) + Math.Abs(points.Max(p => p.Y) - r.YMax);

        (double X, double Y)[] ccw = Arc(forward), cw = Arc(forward - (2 * Math.PI));
        return forward >= 2 * Math.PI || Miss(ccw, range) <= Miss(cw, range) ? ccw : cw;
    }

    private static double Parameter((double X, double Y) c, (double X, double Y) u, (double X, double Y) v, (double X, double Y) p)
    {
        double dx = p.X - c.X, dy = p.Y - c.Y;
        return Math.Atan2(((dx * v.X) + (dy * v.Y)) / ((v.X * v.X) + (v.Y * v.Y)), ((dx * u.X) + (dy * u.Y)) / ((u.X * u.X) + (u.Y * u.Y)));
    }

    /// <summary>The minor semi-axis: the major one turned a quarter counter-clockwise, with the minor radius.</summary>
    private static (double X, double Y) Minor((double X, double Y) u, double minor)
    {
        double length = Math.Sqrt((u.X * u.X) + (u.Y * u.Y));
        return (-u.Y * minor / length, u.X * minor / length);
    }

    /// <summary>Chords over a full turn so that the gap to a circle of this radius stays under 0.1 µm (16 to 4096).</summary>
    private static int Steps(double radius, double sweep)
    {
        double ratio = 1 - Math.Min(1, RegionSagitta / Math.Max(radius, 1e-12));
        int full = (int)Math.Ceiling(Math.PI / Math.Max(Math.Acos(ratio), 1e-9));
        return (int)Math.Ceiling(Math.Clamp(full, 16, 4096) * sweep / (2 * Math.PI));
    }

    private static void ApplyLook(SeContext context, string related, Boundary2d fill, FillLook look)
    {
        BoundaryStyle2d style = fill.Style;
        if (look.Style is { } wanted)
        {
            object styles = Late.Get(context.DocumentOf(related), "FillStyles")!;
            string[] names = [.. Late.Items(styles).Select(s => Late.TryGetString(s, "Name")).OfType<string>()];
            style.FillName = NameMatch.Resolve(names, wanted.Trim())
                ?? throw ToolException.NotFound($"There is no fill style '{wanted}' in this document.{NameMatch.Hint(names, wanted)}",
                    "se_styles list with type 'fill' lists them.");
        }

        if (look.Color is { } color)
        {
            style.FillColor = Rgb.ColorRef(color, "color");
        }

        if (look.Angle is { } angle)
        {
            style.Rotation = Units.DegToRad(Units.RequireFinite(angle, "angle"));
        }

        if (look.Spacing is { } spacing)
        {
            style.spacing = Units.MmToM(Units.RequirePositive(spacing, "spacing"));
        }
    }

    private static object DescribeFill(SeContext context, string related, Boundary2d fill)
    {
        fill.GetCentroid(out double x, out double y);
        BoundaryStyle2d style = fill.Style;
        int color = style.FillColor;
        return new
        {
            fill = context.RegisterBeside(fill, "fill", related),
            area = Geo.Mm2(fill.Area),
            perimeter = Geo.Mm(fill.Perimeter),
            centroid = new[] { Geo.Mm(x), Geo.Mm(y) },
            style = style.FillName,
            color = color >= 0 ? Rgb.HexOfColorRef(color) : "default",
            angle = Geo.R(Units.RadToDeg(style.Rotation)),
            spacing = Geo.Mm(style.spacing),
            elements = fill.BoundingObjects.Count,
        };
    }
}
