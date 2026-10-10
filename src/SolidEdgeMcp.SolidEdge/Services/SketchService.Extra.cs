using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Elementos de boceto adicionales (elipse, spline, punto, redondeo, aristas del modelo) y restricciones 2D:
/// relaciones geométricas y cotas que gobiernan el boceto, como hace una persona con «Cota inteligente».
/// </summary>
public static partial class SketchService
{
    public static readonly IReadOnlyList<string> RelationTypes =
        ["horizontal", "vertical", "fix", "parallel", "perpendicular", "equal", "tangent", "concentric", "colinear", "connect", "symmetric"];

    public static readonly IReadOnlyList<string> DimensionTypes = ["length", "radius", "diameter", "distance", "angle"];

    /// <summary>Elipse por centro, semieje mayor, semieje menor y ángulo del eje mayor (grados desde x).</summary>
    public static object Ellipse(SeContext context, string sketch, double[]? center, double? radius, double? minorRadius, double? angle)
    {
        Profile profile = Resolve(context, sketch);
        (double x, double y) = Geo.Point2(center, "center");
        double major = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'ellipse' requires 'radius' (semi-major axis, mm)."), "radius"));
        double minor = Units.MmToM(Units.RequirePositive(minorRadius ?? throw ToolException.InvalidArgument("Action 'ellipse' requires 'minorRadius' (semi-minor axis, mm)."), "minorRadius"));
        if (minor > major * (1 + 1e-12))
        {
            throw ToolException.InvalidArgument("'minorRadius' must not exceed 'radius' (the semi-major axis).", "Swap them and add 90 to 'angle'.");
        }

        double a = Units.DegToRad(Units.RequireFinite(angle ?? 0, "angle"));
        // El eje mayor se da como vector desde el centro (comprobado: no es un punto absoluto).
        Ellipse2d ellipse = profile.Ellipses2d.AddByCenter(x, y, major * Math.Cos(a), major * Math.Sin(a), minor / major,
            Geom2dOrientationConstants.igGeom2dOrientCounterClockwise);
        return new { element = context.RegisterBeside(ellipse, "ellipse2d", sketch), geometry = Describe2d(ellipse) };
    }

    /// <summary>Spline cúbica que pasa por los puntos; cerrada (tangente continua) si <paramref name="closed"/>.</summary>
    public static object Spline(SeContext context, string sketch, double[]? points, bool closed)
    {
        Profile profile = Resolve(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", 3);
        Array flat = p.SelectMany(q => new[] { q.X, q.Y }).ToArray();
        int order = Math.Min(4, p.Count);
        BSplineCurve2d curve = closed
            ? profile.BSplineCurves2d.AddByPointsWithCloseOption(order, p.Count, ref flat, true)
            : profile.BSplineCurves2d.AddByPoints(order, p.Count, ref flat);
        return new { element = context.RegisterBeside(curve, "bspline2d", sketch), geometry = Describe2d(curve) };
    }

    public static object Point(SeContext context, string sketch, double[]? points)
    {
        Profile profile = Resolve(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", 1);
        if (p.Count != 1)
        {
            throw ToolException.InvalidArgument("'points' must be one point [x, y] for action 'point'.");
        }

        Point2d point = profile.Points2d.Add(p[0].X, p[0].Y);
        return new { element = context.RegisterBeside(point, "point2d", sketch), geometry = Describe2d(point) };
    }

    /// <summary>
    /// Redondeo entre dos elementos que recorta ambos. <paramref name="side"/> es un punto dentro de la esquina que
    /// se redondea; con dos líneas, por defecto, sobre la bisectriz de su esquina común.
    /// </summary>
    public static object Fillet(SeContext context, string sketch, string[]? elements, double? radius, double[]? side)
    {
        Profile profile = Resolve(context, sketch);
        object[] e = Elements(context, elements, 2, 2, "fillet");
        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'fillet' requires 'radius' (mm)."), "radius"));
        (double X, double Y) pick = side is not null ? Geo.Point2(side, "points")
            : e is [Line2d a, Line2d b] ? InsideCorner(a, b, r)
            : throw ToolException.InvalidArgument("A fillet between curves needs 'points' = [x, y], a point inside the corner to round.");
        try
        {
            Arc2d arc = profile.Arcs2d.AddAsFillet(e[0], e[1], r, pick.X, pick.Y);
            return new
            {
                element = context.RegisterBeside(arc, "arc2d", sketch),
                geometry = Describe2d(arc),
                trimmed = e.Select(Describe2d).ToArray(),
            };
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("fillet_failed", $"Solid Edge could not round the corner: {ex.Message}",
                "The two elements must meet (or cross) and both be long enough for the radius; pass 'points' inside the corner to round.", ex);
        }
    }

    /// <summary>Incluye aristas del modelo en el boceto (proyectadas y enlazadas, como «Incluir» de la interfaz).</summary>
    public static object Project(SeContext context, string sketch, string[]? edges)
    {
        Profile profile = Resolve(context, sketch);
        if (edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("Action 'project' requires 'elements' with model edge handles.", "Get them with se_geometry action 'edges'.");
        }

        var created = new List<object>();
        foreach (string handle in edges)
        {
            object edge = context.Handles.Resolve<SolidEdgeGeometry.Edge>(handle, "elements");
            try
            {
                profile.IncludeEdge(edge, out object geometry);
                created.Add(new { edge = handle, element = context.RegisterBeside(geometry, KindOf(geometry), sketch), geometry = Describe2d(geometry) });
            }
            catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
            {
                throw new ToolException("project_failed", $"Solid Edge could not include edge '{handle}' in the sketch: {ex.Message}",
                    "The edge must belong to this part; edges lying off the sketch plane are projected onto it.", ex);
            }
        }

        return new { elements = created };
    }

    /// <summary>Relación geométrica entre elementos del boceto; Solid Edge recoloca la geometría para cumplirla.</summary>
    public static object Relate(SeContext context, string sketch, string type, string[]? elements, int[]? keypoints)
    {
        Profile profile = Resolve(context, sketch);
        var relations = (Relations2d)profile.Relations2d;
        (int count, string usage) = type switch
        {
            "horizontal" or "vertical" or "fix" => (1, "one line (fix: any element)"),
            "connect" => (2, "two elements plus 'keypoints' [i1, i2]"),
            "symmetric" => (3, "the symmetry line followed by two elements"),
            _ when RelationTypes.Contains(type) => (2, "two elements"),
            _ => throw ToolException.InvalidArgument($"Unknown relation '{type}'.", $"Use one of: {string.Join(", ", RelationTypes)}."),
        };
        object[] e = Elements(context, elements, count, count, $"relation '{type}' ({usage})");
        int[] k = type == "connect" ? Keypoints(keypoints, 2) : [];
        try
        {
            Relation2d relation = type switch
            {
                "horizontal" => relations.AddHorizontal(e[0]),
                "vertical" => relations.AddVertical(e[0]),
                "fix" => relations.AddFix(e[0]),
                "parallel" => relations.AddParallel(e[0], e[1]),
                "perpendicular" => relations.AddPerpendicular(e[0], e[1]),
                "equal" => relations.AddEqual(e[0], e[1]),
                "tangent" => relations.AddTangent(e[0], e[1]),
                "concentric" => relations.AddConcentric(e[0], e[1]),
                "colinear" => relations.AddColinear(e[0], e[1]),
                "connect" => relations.AddKeypoint(e[0], k[0], e[1], k[1]),
                _ => relations.AddSymmetric(e[0], e[1], e[2]),
            };
            return new
            {
                relation = context.RegisterBeside(relation, "relation2d", sketch),
                type,
                elements = e.Select(Describe2d).ToArray(),
            };
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("relation_failed", $"Solid Edge rejected the {type} relation: {ex.Message}",
                "Check the element types (e.g. parallel needs lines, concentric circles or arcs) and that it does not conflict with existing relations or dimensions.", ex);
        }
    }

    /// <summary>
    /// Cota de boceto que gobierna la geometría (como las de la interfaz). Con <paramref name="value"/> fija esa medida
    /// (mm o grados); sin él bloquea la actual. Su variable se puede cambiar después con se_variables.
    /// </summary>
    public static object Dimension(SeContext context, string sketch, string type, string[]? elements, int[]? keypoints, double? value)
    {
        Profile profile = Resolve(context, sketch);
        var dimensions = (Dimensions)profile.Dimensions;
        int count = type is "distance" or "angle" ? 2 : 1;
        object[] e = Elements(context, elements, count, count, $"dimension '{type}'");
        int[]? k = keypoints is null ? null : Keypoints(keypoints, count);
        double? target = value is null ? null
            : type == "angle" ? Units.DegToRad(Units.RequirePositive(value.Value, "value"))
            : Units.MmToM(Units.RequirePositive(value.Value, "value"));

        Dimension dimension;
        try
        {
            dimension = type switch
            {
                "length" => dimensions.AddLength(e[0] as Line2d ?? throw ToolException.InvalidArgument("A 'length' dimension takes one line.", "For arcs and circles use 'radius' or 'diameter'.")),
                "radius" => dimensions.AddRadius(e[0]),
                "diameter" => dimensions.AddCircularDiameter(e[0]),
                "distance" => Distance(dimensions, e, k),
                "angle" => Angle(dimensions, e),
                _ => throw ToolException.InvalidArgument($"Unknown dimension '{type}'.", $"Use one of: {string.Join(", ", DimensionTypes)}."),
            };
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("dimension_failed", $"Solid Edge could not dimension the elements: {ex.Message}",
                "Check the element types: length (line), radius/diameter (circle or arc), distance (two elements or keypoints), angle (two lines).", ex);
        }

        double measured = dimension.Value;
        dimension.Constraint = true;
        if (target is { } wanted)
        {
            dimension.Value = wanted;
            if (Math.Abs(dimension.Value - wanted) > 1e-9)
            {
                double got = dimension.Value;
                Late.Call(dimension, "Delete");
                throw new ToolException("dimension_failed",
                    $"Solid Edge kept the {type} at {Format(type, got)} instead of {Format(type, wanted)}; the dimension was deleted.",
                    "The sketch is probably over-constrained: remove a conflicting relation or dimension (se_sketch_constraint 'delete').");
            }
        }

        return new
        {
            dimension = context.RegisterBeside(dimension, "dimension", sketch),
            variable = dimension.DisplayName,
            type,
            measured = Num(type, measured),
            value = Num(type, dimension.Value),
            unit = type == "angle" ? "deg" : "mm",
            elements = e.Select(Describe2d).ToArray(),
        };
    }

    /// <summary>Relaciones y cotas del boceto.</summary>
    public static object Constraints(SeContext context, string sketch)
    {
        Profile profile = Resolve(context, sketch);
        var dimensions = (Dimensions)profile.Dimensions;
        var items = new List<object>();
        for (int i = 1; i <= dimensions.Count; i++)
        {
            Dimension d = dimensions.Item(i);
            string kind = d.DimensionType switch
            {
                DimTypeConstants.igDimTypeAngular or DimTypeConstants.igDimTypeArcAngle => "angle",
                DimTypeConstants.igDimTypeRadial => "radius",
                DimTypeConstants.igDimTypeRDiameter or DimTypeConstants.igDimTypeCDiameter or DimTypeConstants.igDimTypeSDiameter => "diameter",
                _ => "linear",
            };
            items.Add(new
            {
                dimension = context.RegisterBeside(d, "dimension", sketch),
                variable = d.DisplayName,
                type = kind,
                value = Num(kind, d.Value),
                unit = kind == "angle" ? "deg" : "mm",
                driving = d.Constraint,
            });
        }

        return new { sketch, relations = ((Relations2d)profile.Relations2d).Count, dimensions = items };
    }

    /// <summary>Borra una relación o cota del boceto (handle de 'relation' o 'dimension').</summary>
    public static object DeleteConstraint(SeContext context, string handle)
    {
        object item = context.Handles.Resolve(handle, "constraint");
        if (item is not (Relation2d or SolidEdgeFrameworkSupport.Dimension))
        {
            throw new ToolException("handle_wrong_type", $"'{handle}' is not a sketch relation or dimension.",
                "Pass a handle returned by se_sketch_constraint 'relation', 'dimension' or 'list'.");
        }

        Late.Call(item, "Delete");
        context.Handles.Remove(handle, "the constraint was deleted");
        return new { deleted = handle };
    }

    /// <summary>Geometría de un elemento 2D en mm (y grados), en coordenadas del boceto.</summary>
    internal static object Describe2d(object element)
    {
        static double[] P(double x, double y) => [Geo.Mm(x), Geo.Mm(y)];
        switch (element)
        {
            case Line2d line:
            {
                line.GetStartPoint(out double x1, out double y1);
                line.GetEndPoint(out double x2, out double y2);
                return new { type = "line", start = P(x1, y1), end = P(x2, y2), length = Geo.Mm(line.Length) };
            }

            case Circle2d circle:
            {
                circle.GetCenterPoint(out double x, out double y);
                return new { type = "circle", center = P(x, y), radius = Geo.Mm(circle.Radius) };
            }

            case Arc2d arc:
            {
                arc.GetCenterPoint(out double x, out double y);
                arc.GetStartPoint(out double x1, out double y1);
                arc.GetEndPoint(out double x2, out double y2);
                return new { type = "arc", center = P(x, y), start = P(x1, y1), end = P(x2, y2), radius = Geo.Mm(arc.Radius) };
            }

            case Ellipse2d ellipse:
            {
                ellipse.GetCenterPoint(out double x, out double y);
                return new
                {
                    type = "ellipse",
                    center = P(x, y),
                    radius = Geo.Mm(ellipse.MajorRadius),
                    minorRadius = Geo.Mm(ellipse.MinorRadius),
                    angle = Geo.R(Units.RadToDeg(ellipse.RotationAngle)),
                };
            }

            case Point2d point:
            {
                point.GetPoint(out double x, out double y);
                return new { type = "point", point = P(x, y) };
            }

            case BSplineCurve2d spline:
            {
                (double X, double Y) s = KeyPoint(spline, 0);
                (double X, double Y) e = KeyPoint(spline, 1);
                return new { type = "spline", start = P(s.X, s.Y), end = P(e.X, e.Y), length = Geo.Mm(spline.Length) };
            }

            default:
                return new { type = KindOf(element) };
        }
    }

    private static string KindOf(object element) => element switch
    {
        Line2d => "line2d",
        Circle2d => "circle2d",
        Arc2d => "arc2d",
        Ellipse2d => "ellipse2d",
        Point2d => "point2d",
        BSplineCurve2d => "bspline2d",
        _ => InteropCatalog.GetTypeName(element)?.Split('.')[^1].ToLowerInvariant() ?? "element2d",
    };

    private static object[] Elements(SeContext context, string[]? handles, int min, int max, string what)
    {
        if (handles is null || handles.Length < min || handles.Length > max)
        {
            string count = min == max ? $"{min}" : $"{min} to {max}";
            throw ToolException.InvalidArgument($"'elements' must list {count} sketch element handle(s) for {what}.",
                "Use the handles returned when drawing (line2d:…, circle2d:…, arc2d:…).");
        }

        return handles.Select(h => context.Handles.Resolve(h, "elements")).ToArray();
    }

    private static int[] Keypoints(int[]? keypoints, int count) =>
        keypoints is { } k && k.Length == count && k.All(i => i >= 0)
            ? k
            : throw ToolException.InvalidArgument($"'keypoints' must give {count} keypoint index(es), one per element.",
                "Lines: 0 start, 1 end, 2 middle. Circles: 0 centre. Arcs: 0 centre, 1 start, 2 end. Ellipses: 0 centre.");

    /// <summary>
    /// Distancia entre dos elementos. Sin puntos clave se toma la línea por su punto medio (distancia entre rectas o de
    /// punto a recta) y los círculos, arcos, elipses y puntos por su centro.
    /// </summary>
    private static Dimension Distance(Dimensions dimensions, object[] e, int[]? k)
    {
        (double X, double Y, bool Key)[] picks = e.Select((element, i) => k is null ? DefaultPick(element) : (KeyPoint(element, k[i]).X, KeyPoint(element, k[i]).Y, true)).ToArray();
        return dimensions.AddDistanceBetweenObjects(e[0], picks[0].X, picks[0].Y, 0, picks[0].Key, e[1], picks[1].X, picks[1].Y, 0, picks[1].Key);
    }

    private static Dimension Angle(Dimensions dimensions, object[] e)
    {
        if (e is not [Line2d a, Line2d b])
        {
            throw ToolException.InvalidArgument("An 'angle' dimension takes two lines.");
        }

        (double X, double Y, bool _) pa = DefaultPick(a);
        (double X, double Y, bool _) pb = DefaultPick(b);
        return dimensions.AddAngleBetweenObjects(a, pa.X, pa.Y, 0, false, b, pb.X, pb.Y, 0, false);
    }

    private static (double X, double Y, bool Key) DefaultPick(object element)
    {
        switch (element)
        {
            case Line2d line:
                line.GetStartPoint(out double x1, out double y1);
                line.GetEndPoint(out double x2, out double y2);
                return ((x1 + x2) / 2, (y1 + y2) / 2, false);
            case Point2d point:
                point.GetPoint(out double px, out double py);
                return (px, py, true);
            default:
                (double X, double Y) c = KeyPoint(element, 0);
                return (c.X, c.Y, true);
        }
    }

    /// <summary>Punto clave de un elemento 2D (índices de KeypointIndexConstants), por IDispatch: vale para cualquier tipo.</summary>
    private static (double X, double Y) KeyPoint(object element, int index)
    {
        try
        {
            object?[] r = Late.CallByRef(element, "GetKeyPoint", [index, 0.0, 0.0, 0.0, 0, 0], 1, 2, 3, 4, 5);
            return (Convert.ToDouble(r[1], CultureInfo.InvariantCulture), Convert.ToDouble(r[2], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw ToolException.InvalidArgument($"The element has no keypoint {index}.",
                "Lines: 0 start, 1 end, 2 middle. Circles: 0 centre. Arcs: 0 centre, 1 start, 2 end.");
        }
    }

    /// <summary>Punto dentro de la esquina común de dos líneas, sobre la bisectriz, a una distancia del radio.</summary>
    private static (double X, double Y) InsideCorner(Line2d a, Line2d b, double radius)
    {
        a.GetStartPoint(out double a1x, out double a1y);
        a.GetEndPoint(out double a2x, out double a2y);
        b.GetStartPoint(out double b1x, out double b1y);
        b.GetEndPoint(out double b2x, out double b2y);
        (double X, double Y)[] ea = [(a1x, a1y), (a2x, a2y)];
        (double X, double Y)[] eb = [(b1x, b1y), (b2x, b2y)];
        (int i, int j) = (from p in new[] { 0, 1 } from q in new[] { 0, 1 }
                          orderby Math.Pow(ea[p].X - eb[q].X, 2) + Math.Pow(ea[p].Y - eb[q].Y, 2)
                          select (p, q)).First();
        (double X, double Y) corner = ea[i];
        (double X, double Y) ua = Unit(ea[1 - i].X - corner.X, ea[1 - i].Y - corner.Y);
        (double X, double Y) ub = Unit(eb[1 - j].X - corner.X, eb[1 - j].Y - corner.Y);
        (double X, double Y) bisector = Unit(ua.X + ub.X, ua.Y + ub.Y);
        return (corner.X + bisector.X * radius, corner.Y + bisector.Y * radius);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt(x * x + y * y);
        return length < 1e-15
            ? throw ToolException.InvalidArgument("The lines are parallel or degenerate: there is no corner to round.")
            : (x / length, y / length);
    }

    private static double Num(string type, double value) => type == "angle" ? Geo.R(Units.RadToDeg(value)) : Geo.Mm(value);

    private static string Format(string type, double value) => type == "angle"
        ? $"{Geo.R(Units.RadToDeg(value)).ToString(CultureInfo.InvariantCulture)} deg"
        : $"{Geo.Mm(value).ToString(CultureInfo.InvariantCulture)} mm";
}
