using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Bocetos de modelado ordenado: se crean sobre un plano, se dibujan en mm con coordenadas 2D del
/// plano y se cierran validando el perfil antes de usarlos en una operación. Son bocetos
/// independientes (como el comando Boceto de la interfaz): cada operación usa una copia enlazada
/// de su perfil, así que el boceto sobrevive si la operación falla y se borra.
/// </summary>
public static class SketchService
{
    private const int LineStart = 0;
    private const int LineEnd = 1;

    public static object Create(SeContext context, string? documentHandle, string plane)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
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

    public static object Line(SeContext context, string sketch, double[]? points)
    {
        Profile profile = Resolve(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", 2);
        if (p.Count != 2)
        {
            throw ToolException.InvalidArgument("'points' must be [x1, y1, x2, y2] for a line.", "Use action 'polyline' for more points.");
        }

        Line2d line = profile.Lines2d.AddBy2Points(p[0].X, p[0].Y, p[1].X, p[1].Y);
        return new { element = context.RegisterBeside(line, "line2d", sketch) };
    }

    /// <summary>Rectángulo por dos esquinas opuestas: cuatro líneas unidas, horizontales y verticales.</summary>
    public static object Rectangle(SeContext context, string sketch, double[]? corners)
    {
        Profile profile = Resolve(context, sketch);
        List<(double X, double Y)> c = Geo.Points2(corners, "points", 2);
        if (c.Count != 2 || Math.Abs(c[0].X - c[1].X) < 1e-9 || Math.Abs(c[0].Y - c[1].Y) < 1e-9)
        {
            throw ToolException.InvalidArgument("'points' must be two opposite corners [x1, y1, x2, y2] (mm) with different x and y.");
        }

        (double X, double Y)[] v = [c[0], (c[1].X, c[0].Y), c[1], (c[0].X, c[1].Y)];
        Line2d[] lines = Chain(profile, v, closed: true);
        var relations = (Relations2d)profile.Relations2d;
        relations.AddHorizontal(lines[0]);
        relations.AddHorizontal(lines[2]);
        relations.AddVertical(lines[1]);
        relations.AddVertical(lines[3]);
        return new { elements = lines.Select(l => context.RegisterBeside(l, "line2d", sketch)).ToArray() };
    }

    public static object Polyline(SeContext context, string sketch, double[]? points, bool closed)
    {
        Profile profile = Resolve(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", closed ? 3 : 2);
        Line2d[] lines = Chain(profile, p, closed);
        return new { elements = lines.Select(l => context.RegisterBeside(l, "line2d", sketch)).ToArray() };
    }

    public static object Circle(SeContext context, string sketch, double[]? center, double? radius)
    {
        Profile profile = Resolve(context, sketch);
        (double x, double y) = Geo.Point2(center, "center");
        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'circle' requires 'radius' (mm)."), "radius"));
        Circle2d circle = profile.Circles2d.AddByCenterRadius(x, y, r);
        return new { element = context.RegisterBeside(circle, "circle2d", sketch) };
    }

    /// <summary>Arco por centro, inicio y fin (sentido antihorario de inicio a fin).</summary>
    public static object Arc(SeContext context, string sketch, double[]? center, double[]? start, double[]? end)
    {
        Profile profile = Resolve(context, sketch);
        (double xc, double yc) = Geo.Point2(center, "center");
        (double xs, double ys) = Geo.Point2(start, "start");
        (double xe, double ye) = Geo.Point2(end, "end");
        Arc2d arc = profile.Arcs2d.AddByCenterStartEnd(xc, yc, xs, ys, xe, ye);
        return new { element = context.RegisterBeside(arc, "arc2d", sketch) };
    }

    /// <summary>Línea del boceto como eje de revolución; devuelve el handle del eje.</summary>
    public static object Axis(SeContext context, string sketch, string element)
    {
        Profile profile = Resolve(context, sketch);
        Line2d line = context.Handles.Resolve<Line2d>(element, "element");
        object axis = profile.SetAxisOfRevolution(line);
        return new { axis = context.RegisterBeside(axis, "refaxis", sketch) };
    }

    /// <summary>
    /// Termina el boceto y lo valida: cerrado (se admiten islas interiores) o, con <paramref name="open"/>, una sola cadena abierta
    /// sin autointersecciones (brida de contorno, pliegue, trayectoria de barrido...).
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

        return new { sketch, closed = !open, elements = Summary(profile) };
    }

    public static object Info(SeContext context, string sketch)
    {
        Profile profile = Resolve(context, sketch);
        return new { sketch, name = NameOf(profile), frame = Frame(profile), elements = Summary(profile) };
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

    /// <summary>Perfil de dibujo del boceto. Admite también el handle de un boceto del árbol (se_feature_tree).</summary>
    public static Profile Resolve(SeContext context, string sketch) => context.Handles.Resolve(sketch, "sketch") switch
    {
        Profile profile => profile,
        Sketch owner => owner.Profiles.Count > 0
            ? owner.Profiles.Item(1)
            : throw new ToolException("empty_sketch", $"The sketch '{sketch}' has no profile.", "Create a new sketch with se_sketch action 'create'."),
        _ => throw new ToolException("handle_wrong_type", $"Handle '{sketch}' in 'sketch' does not refer to a sketch.",
            "Pass the handle returned by se_sketch 'create' or a sketch from se_feature_tree."),
    };

    /// <summary>
    /// Lazos del boceto: grupos de líneas y arcos unidos por sus extremos, más cada círculo o elipse. Devuelve
    /// <c>null</c> si hay curvas sin extremos consultables (B-splines), para no dar un número dudoso.
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

        // Extremos 2k y 2k+1 son de la misma curva; dos extremos coincidentes (1e-7 m) unen sus curvas.
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

    /// <summary>Nombre del boceto en el árbol (Sketch_N) o, si no tiene, el del perfil.</summary>
    private static string NameOf(Profile profile) => profile.Parent is Sketch owner ? owner.Name : profile.Name;

    private static Line2d[] Chain(Profile profile, IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        Lines2d lines2d = profile.Lines2d;
        var lines = new List<Line2d>();
        for (int i = 0; i + 1 < points.Count; i++)
        {
            lines.Add(lines2d.AddBy2Points(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
        }

        if (closed)
        {
            lines.Add(lines2d.AddBy2Points(points[^1].X, points[^1].Y, points[0].X, points[0].Y));
        }

        var relations = (Relations2d)profile.Relations2d;
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

    /// <summary>Sistema de coordenadas del boceto en el espacio del modelo (origen en mm, ejes unitarios).</summary>
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

    private static object Summary(Profile profile) => new
    {
        lines = profile.Lines2d.Count,
        circles = profile.Circles2d.Count,
        arcs = profile.Arcs2d.Count,
    };
}
