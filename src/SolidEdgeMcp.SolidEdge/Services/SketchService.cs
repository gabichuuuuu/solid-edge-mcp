using SolidEdgeDraft;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Ordered-modelling sketches: they are created on a plane, drawn in mm with 2D coordinates of the
/// plane and closed by validating the profile before being used in an operation. They are
/// standalone sketches (like the Sketch command of the UI): each operation uses a linked copy
/// of its profile, so the sketch survives if the operation fails and is deleted. The drawing actions
/// and the 2D constraints also work on a drawing sheet (see <see cref="Canvas2d"/>).
/// </summary>
public static partial class SketchService
{
    private const int LineStart = 0;
    private const int LineEnd = 1;

    public static object Create(SeContext context, string? documentHandle, string plane)
    {
        SolidEdgeFramework.SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (document is SolidEdgeAssembly.AssemblyDocument assembly)
        {
            return CreateInAssembly(context, assembly, plane);
        }

        ModelDocument model = ModelDocument.From(document);
        bool switched = model.EnsureOrdered();
        RefPlane refPlane = model.ResolvePlane(context, plane);
        Profile profile = model.Sketches.Add().Profiles.Add(refPlane);
        string sketch = context.Register(profile, "sketch", model.Document);
        return new
        {
            sketch,
            plane = context.Register(refPlane, "refplane", model.Document),
            frame = Frame(profile),
            note = switched ? "The document was switched to ordered modeling." : null,
        };
    }

    /// <summary>Base plane '1'-'3' of an assembly or an assembly reference plane handle.</summary>
    internal static object AssemblyPlane(SeContext context, SolidEdgeAssembly.AssemblyDocument assembly, string plane) =>
        int.TryParse(plane, out int index)
            ? index is >= 1 and <= 3
                ? assembly.AsmRefPlanes.Item(index)
                : throw ToolException.InvalidArgument($"The assembly has base planes '1'-'3', not '{plane}'.")
            : context.Handles.Resolve(plane, "plane") is SolidEdgeAssembly.AsmRefPlane given
                ? given
                : throw new ToolException("handle_wrong_type", $"'{plane}' is not an assembly reference plane.",
                    "In an assembly, sketch on '1'-'3' or on an assembly reference plane.");

    /// <summary>
    /// Sketch of an assembly, on one of its reference planes ('1'-'3' or a handle), for assembly features (se_assembly_feature).
    /// </summary>
    private static object CreateInAssembly(SeContext context, SolidEdgeAssembly.AssemblyDocument assembly, string plane)
    {
        object refPlane = AssemblyPlane(context, assembly, plane);
        var document = (SolidEdgeFramework.SolidEdgeDocument)assembly;
        Profile profile = assembly.ProfileSets.Add().Profiles.Add(refPlane);
        return new
        {
            sketch = context.Register(profile, "sketch", document),
            plane = context.Register(refPlane, "asmrefplane", document),
            frame = Frame(profile),
        };
    }

    public static object Line(SeContext context, string sketch, double[]? points)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", 2);
        if (p.Count != 2)
        {
            throw ToolException.InvalidArgument("'points' must be [x1, y1, x2, y2] for a line.", "Use action 'polyline' for more points.");
        }

        Line2d line = canvas.Lines2d.AddBy2Points(p[0].X, p[0].Y, p[1].X, p[1].Y);
        return new { element = context.RegisterBeside(line, "line2d", sketch) };
    }

    /// <summary>Rectangle from two opposite corners: four connected lines, horizontal and vertical.</summary>
    public static object Rectangle(SeContext context, string sketch, double[]? corners)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        List<(double X, double Y)> c = Geo.Points2(corners, "points", 2);
        if (c.Count != 2 || Math.Abs(c[0].X - c[1].X) < 1e-9 || Math.Abs(c[0].Y - c[1].Y) < 1e-9)
        {
            throw ToolException.InvalidArgument("'points' must be two opposite corners [x1, y1, x2, y2] (mm) with different x and y.");
        }

        (double X, double Y)[] v = [c[0], (c[1].X, c[0].Y), c[1], (c[0].X, c[1].Y)];
        Line2d[] lines = Chain(canvas, v, closed: true);
        Relations2d relations = canvas.Relations2d;
        relations.AddHorizontal(lines[0]);
        relations.AddHorizontal(lines[2]);
        relations.AddVertical(lines[1]);
        relations.AddVertical(lines[3]);
        return new { elements = lines.Select(l => context.RegisterBeside(l, "line2d", sketch)).ToArray() };
    }

    public static object Polyline(SeContext context, string sketch, double[]? points, bool closed)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", closed ? 3 : 2);
        Line2d[] lines = Chain(canvas, p, closed);
        return new { elements = lines.Select(l => context.RegisterBeside(l, "line2d", sketch)).ToArray() };
    }

    public static object Circle(SeContext context, string sketch, double[]? center, double? radius)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        (double x, double y) = Geo.Point2(center, "center");
        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'circle' requires 'radius' (mm)."), "radius"));
        Circle2d circle = canvas.Circles2d.AddByCenterRadius(x, y, r);
        return new { element = context.RegisterBeside(circle, "circle2d", sketch) };
    }

    /// <summary>Arc from center, start and end (counter-clockwise from start to end).</summary>
    public static object Arc(SeContext context, string sketch, double[]? center, double[]? start, double[]? end)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        (double xc, double yc) = Geo.Point2(center, "center");
        (double xs, double ys) = Geo.Point2(start, "start");
        (double xe, double ye) = Geo.Point2(end, "end");
        Arc2d arc = canvas.Arcs2d.AddByCenterStartEnd(xc, yc, xs, ys, xe, ye);
        return new { element = context.RegisterBeside(arc, "arc2d", sketch) };
    }

    /// <summary>
    /// Line of the sketch as the revolution axis; returns the axis handle. Solid Edge removes from the profile the line
    /// that it turns into the axis: if it is a side of the profile, the profile stays open and the revolve fails (verified), so
    /// a copy of the line is used as the axis and the side is kept.
    /// </summary>
    public static object Axis(SeContext context, string sketch, string element)
    {
        Profile profile = Resolve(context, sketch);
        Line2d line = context.Handles.Resolve<Line2d>(element, "element");
        string? copy = null;
        if (IsConnected(profile, line))
        {
            ((double X, double Y) start, (double X, double Y) end) = Ends(line);
            line = profile.Lines2d.AddBy2Points(start.X, start.Y, end.X, end.Y);
            copy = context.RegisterBeside(line, "line2d", sketch);
        }

        object axis = profile.SetAxisOfRevolution(line);
        return new
        {
            axis = context.RegisterBeside(axis, "refaxis", sketch),
            axisLine = copy,
            note = copy is null ? null : "The line is a side of the profile: a copy of it is the axis, so the profile stays closed.",
        };
    }

    /// <summary>Indicates whether any other segment or arc of the sketch starts or ends at an endpoint of the line.</summary>
    private static bool IsConnected(Profile profile, Line2d line)
    {
        const double tolerance = 1e-9;
        ((double X, double Y) a, (double X, double Y) b) = Ends(line);
        bool Touches(double x, double y) =>
            Math.Abs(x - a.X) + Math.Abs(y - a.Y) < tolerance || Math.Abs(x - b.X) + Math.Abs(y - b.Y) < tolerance;

        Lines2d lines = profile.Lines2d;
        for (int i = 1; i <= lines.Count; i++)
        {
            ((double X, double Y) s, (double X, double Y) e) = Ends(lines.Item(i));
            bool same = (Touches(s.X, s.Y) && Touches(e.X, e.Y)) && Math.Abs(s.X - e.X) + Math.Abs(s.Y - e.Y) > tolerance;
            if (!same && (Touches(s.X, s.Y) || Touches(e.X, e.Y)))
            {
                return true;
            }
        }

        Arcs2d arcs = profile.Arcs2d;
        for (int i = 1; i <= arcs.Count; i++)
        {
            Arc2d arc = arcs.Item(i);
            arc.GetStartPoint(out double x1, out double y1);
            arc.GetEndPoint(out double x2, out double y2);
            if (Touches(x1, y1) || Touches(x2, y2))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finishes the sketch and validates it: closed (inner islands are allowed) or, with <paramref name="open"/>, a single open chain
    /// without self-intersections (contour flange, fold, sweep path...).
    /// </summary>
    public static object Close(SeContext context, string sketch, bool open = false)
    {
        Profile profile = Resolve(context, sketch);
        var criteria = open
            ? (ProfileValidationType)((int)ProfileValidationType.igProfileSingle | (int)ProfileValidationType.igProfileNoSelfIntersect)
            : (ProfileValidationType)((int)ProfileValidationType.igProfileClosed | (int)ProfileValidationType.igProfileAllowNested);
        int status = profile.End(criteria);
        if (status != 0)
        {
            throw new ToolException(open ? "sketch_not_valid" : "sketch_not_closed",
                open
                    ? $"The sketch is not a single connected chain (Solid Edge validation code {status})."
                    : $"The sketch is not a valid closed profile (Solid Edge validation code {status}).",
                open
                    ? "Draw one chain whose consecutive elements share endpoints ('polyline' with closed=false connects them), without self-intersections."
                    : "Make every loop closed (endpoints must coincide), without self-intersections, then call 'close' again. 'rectangle' and 'polyline' connect their lines automatically.");
        }

        return new { sketch, closed = !open, elements = Summary(Canvas2d.Of(profile)) };
    }

    public static object Info(SeContext context, string sketch)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        (List<object> items, bool truncated) = ElementList(context, canvas, sketch);
        return new
        {
            sketch,
            name = canvas.Name,
            sheet = canvas.Sheet is null ? (bool?)null : true,
            frame = canvas.Profile is { } profile ? Frame(profile) : null,
            elements = Summary(canvas),
            items,
            truncated = truncated ? true : (bool?)null,
        };
    }

    public static object Delete(SeContext context, string sketch)
    {
        Profile profile = Resolve(context, sketch);
        string name = NameOf(profile);
        if (profile.Parent is Sketch owner)
        {
            owner.Delete();
        }
        else
        {
            profile.Delete();
        }

        context.Handles.Remove(sketch, "the sketch was deleted");
        return new { deleted = sketch, name };
    }

    /// <summary>Drawing profile of the sketch. Also accepts the handle of a sketch in the tree (se_feature_tree).</summary>
    public static Profile Resolve(SeContext context, string sketch) => context.Handles.Resolve(sketch, "sketch") switch
    {
        Profile profile => profile,
        Sheet or DraftDocument => throw new ToolException("not_supported", $"'{sketch}' is a drawing sheet: this action applies only to model sketches.",
            "On a sheet you can draw (line, rectangle, polyline, circle, arc, ellipse, spline, point, fillet), list (info) and constrain the geometry."),
        Sketch owner => owner.Profiles.Count > 0
            ? owner.Profiles.Item(1)
            : throw new ToolException("empty_sketch", $"The sketch '{sketch}' has no profile.", "Create a new sketch with se_sketch action 'create'."),
        _ => throw new ToolException("handle_wrong_type", $"Handle '{sketch}' in 'sketch' does not refer to a sketch.",
            "Pass the handle returned by se_sketch 'create' or a sketch from se_feature_tree."),
    };

    /// <summary>
    /// Loops of the sketch: groups of lines and arcs joined at their endpoints, plus each circle or ellipse. Returns
    /// <c>null</c> if there are curves without queryable endpoints (B-splines), so as not to give a doubtful number.
    /// </summary>
    internal static int? LoopCount(Profile profile)
    {
        if (profile.BSplineCurves2d.Count > 0)
        {
            return null;
        }

        var ends = new List<(double X, double Y)>();
        foreach (Line2d line in profile.Lines2d)
        {
            line.GetStartPoint(out double x1, out double y1);
            line.GetEndPoint(out double x2, out double y2);
            ends.Add((x1, y1));
            ends.Add((x2, y2));
        }

        foreach (Arc2d arc in profile.Arcs2d)
        {
            arc.GetStartPoint(out double x1, out double y1);
            arc.GetEndPoint(out double x2, out double y2);
            ends.Add((x1, y1));
            ends.Add((x2, y2));
        }

        foreach (EllipticalArc2d arc in profile.EllipticalArcs2d)
        {
            arc.GetStartPoint(out double x1, out double y1);
            arc.GetEndPoint(out double x2, out double y2);
            ends.Add((x1, y1));
            ends.Add((x2, y2));
        }

        // Endpoints 2k and 2k+1 belong to the same curve; two coinciding endpoints (1e-7 m) join their curves.
        int[] parent = Enumerable.Range(0, ends.Count / 2).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (int i = 0; i < ends.Count; i++)
        {
            for (int j = i + 1; j < ends.Count; j++)
            {
                if (Math.Abs(ends[i].X - ends[j].X) < 1e-7 && Math.Abs(ends[i].Y - ends[j].Y) < 1e-7)
                {
                    parent[Root(i / 2)] = Root(j / 2);
                }
            }
        }

        int chains = Enumerable.Range(0, parent.Length).Select(Root).Distinct().Count();
        return chains + profile.Circles2d.Count + profile.Ellipses2d.Count;
    }

    /// <summary>Name of the sketch in the tree (Sketch_N) or, if it has none, that of the profile.</summary>
    private static string NameOf(Profile profile) => profile.Parent is Sketch owner ? owner.Name : profile.Name;

    private static Line2d[] Chain(Canvas2d canvas, IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        Lines2d lines2d = canvas.Lines2d;
        var lines = new List<Line2d>();
        for (int i = 0; i + 1 < points.Count; i++)
        {
            lines.Add(lines2d.AddBy2Points(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
        }

        if (closed)
        {
            lines.Add(lines2d.AddBy2Points(points[^1].X, points[^1].Y, points[0].X, points[0].Y));
        }

        Relations2d relations = canvas.Relations2d;
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            relations.AddKeypoint(lines[i], LineEnd, lines[i + 1], LineStart);
        }

        if (closed)
        {
            relations.AddKeypoint(lines[^1], LineEnd, lines[0], LineStart);
        }

        return lines.ToArray();
    }

    /// <summary>Coordinate system of the sketch in model space (origin in mm, unit axes).</summary>
    private static object Frame(Profile profile)
    {
        double[] o = To3d(profile, 0, 0);
        double[] x = To3d(profile, 1, 0);
        double[] y = To3d(profile, 0, 1);
        double[] xAxis = [x[0] - o[0], x[1] - o[1], x[2] - o[2]];
        double[] yAxis = [y[0] - o[0], y[1] - o[1], y[2] - o[2]];
        double[] normal = Geo.Cross(xAxis, yAxis);
        return new
        {
            origin = o.Select(Geo.Mm).ToArray(),
            xAxis = xAxis.Select(Geo.R).ToArray(),
            yAxis = yAxis.Select(Geo.R).ToArray(),
            normal = normal.Select(Geo.R).ToArray(),
        };
    }

    private static double[] To3d(Profile profile, double x, double y)
    {
        profile.Convert2DCoordinate(x, y, out double x3, out double y3, out double z3);
        return [x3, y3, z3];
    }

    private static object Summary(Canvas2d canvas) => new
    {
        lines = canvas.Lines2d.Count,
        circles = canvas.Circles2d.Count,
        arcs = canvas.Arcs2d.Count,
        ellipses = NonZero(canvas.Ellipses2d.Count),
        splines = NonZero(canvas.BSplineCurves2d.Count),
        points = NonZero(canvas.Points2d.Count),
        relations = NonZero(canvas.Relations2d.Count),
        dimensions = NonZero(canvas.Dimensions.Count),
    };

    private static int? NonZero(int count) => count == 0 ? null : count;
}
