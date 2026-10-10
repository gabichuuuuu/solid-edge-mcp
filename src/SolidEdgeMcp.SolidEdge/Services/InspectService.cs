using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeGeometry;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Consultas del modelo: geometría (cuerpo, caras, aristas), propiedades físicas y medidas. En mm.</summary>
public static class InspectService
{
    public static readonly IReadOnlyList<string> FaceTypes = ["all", "plane", "cylinder", "cone", "sphere", "torus"];
    public static readonly IReadOnlyList<string> EdgeTypes = ["all", "line", "circle", "spline"];

    public static object Summary(SeContext context, string? documentHandle, string? source = null)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            return OccurrenceSummary(context, source);
        }

        SolidEdgeDocument target = context.GetDocument(documentHandle);
        if (target is AssemblyDocument)
        {
            throw new ToolException("wrong_document_type", $"'{SeContext.SafeName(target)}' is an assembly; its summary is per occurrence.",
                "Pass 'source' = an occurrence handle (se_assembly list) for the summary of its part in assembly coordinates.");
        }

        ModelDocument model = ModelDocument.From(target);
        string document = context.DocumentHandle(model.Document);
        if (model.TryModel()?.Body is not Body body)
        {
            return new { document, solid = false, volume = 0.0 };
        }

        Array min = new double[3];
        Array max = new double[3];
        body.GetRange(ref min, ref max);
        double[] lo = Geo.Mm(min);
        double[] hi = Geo.Mm(max);
        return new
        {
            document,
            body = context.Register(body, "body", model.Document),
            solid = body.IsSolid,
            volume = Geo.Mm3(body.Volume),
            boundingBox = new { min = lo, max = hi, size = hi.Zip(lo, (a, b) => Geo.R(a - b)).ToArray() },
            faces = Late.Count(body.Faces[FeatureTopologyQueryTypeConstants.igQueryAll]),
            edges = Late.Count(body.Edges[FeatureTopologyQueryTypeConstants.igQueryAll]),
            vertices = Late.Count(body.Vertices),
            modelingMode = model.ModelingMode,
        };
    }

    /// <summary>
    /// Resumen del cuerpo de la pieza de una ocurrencia. La caja es la de <c>Occurrence.Range</c>, en coordenadas del
    /// conjunto y exacta también con la pieza girada (comprobado en Solid Edge 2026).
    /// </summary>
    private static object OccurrenceSummary(SeContext context, string source)
    {
        if (context.Handles.Resolve(source, "source") is not Occurrence occurrence)
        {
            throw ToolException.InvalidArgument("For 'summary', 'source' must be an assembly occurrence handle (se_assembly list).",
                "Omit 'source' for the summary of a part or sheet metal document.");
        }

        ModelDocument part = AssemblyService.PartOf(occurrence);
        if (part.TryModel()?.Body is not Body body)
        {
            return new { occurrence = source, name = occurrence.Name, solid = false, volume = 0.0 };
        }

        occurrence.Range(out double x0, out double y0, out double z0, out double x1, out double y1, out double z1);
        double[] lo = [Geo.Mm(x0), Geo.Mm(y0), Geo.Mm(z0)];
        double[] hi = [Geo.Mm(x1), Geo.Mm(y1), Geo.Mm(z1)];
        return new
        {
            occurrence = source,
            name = occurrence.Name,
            solid = body.IsSolid,
            volume = Geo.Mm3(body.Volume),
            boundingBox = new { min = lo, max = hi, size = hi.Zip(lo, (a, b) => Geo.R(a - b)).ToArray() },
            faces = Late.Count(body.Faces[FeatureTopologyQueryTypeConstants.igQueryAll]),
            edges = Late.Count(body.Edges[FeatureTopologyQueryTypeConstants.igQueryAll]),
            vertices = Late.Count(body.Vertices),
            coordinates = "assembly",
        };
    }

    /// <summary>
    /// Caras del cuerpo o de una operación (<paramref name="source"/>), con tipo, área, normal o radio.
    /// <paramref name="direction"/> deja solo las caras planas cuya normal exterior apunta en ese sentido. Con una
    /// ocurrencia de conjunto como <paramref name="source"/>, las caras de su pieza en coordenadas del conjunto.
    /// </summary>
    public static object Faces(SeContext context, string? documentHandle, string? source, string type, double[]? direction, int offset, int limit)
    {
        double[]? wanted = CheckDirection(direction);
        FeatureTopologyQueryTypeConstants query = type switch
        {
            "all" => FeatureTopologyQueryTypeConstants.igQueryAll,
            "plane" => FeatureTopologyQueryTypeConstants.igQueryPlane,
            "cylinder" => FeatureTopologyQueryTypeConstants.igQueryCylinder,
            "cone" => FeatureTopologyQueryTypeConstants.igQueryCone,
            "sphere" => FeatureTopologyQueryTypeConstants.igQuerySphere,
            "torus" => FeatureTopologyQueryTypeConstants.igQueryTorus,
            _ => throw ToolException.InvalidArgument($"Unknown face type '{type}'.", $"Use one of: {string.Join(", ", FaceTypes)}."),
        };
        TopologySource topology = Topology(context, documentHandle, source, "Faces", query);
        Placement? placement = topology.Placement;
        Func<object, bool>? filter = wanted is null ? null : item => ((Face)item).Geometry is Plane
            && Cosine(Rotated(placement, FaceNormal((Face)item)), wanted) > ParallelCosine;
        return Page(context, topology.Collection, offset, limit, filter, item => DescribeFace(context, topology, (Face)item));
    }

    /// <summary>
    /// Aristas del cuerpo, de una operación o de una cara, con extremos, longitud y radio.
    /// <paramref name="direction"/> deja solo las aristas rectas paralelas a ese vector (en cualquier sentido).
    /// </summary>
    public static object Edges(SeContext context, string? documentHandle, string? source, string type, double[]? direction, int offset, int limit)
    {
        double[]? wanted = CheckDirection(direction);
        if (!EdgeTypes.Contains(type))
        {
            throw ToolException.InvalidArgument($"Unknown edge type '{type}'.", $"Use one of: {string.Join(", ", EdgeTypes)}.");
        }

        // El tipo se filtra por la geometría: la consulta igQueryEllipse no devuelve las aristas circulares (comprobado).
        TopologySource topology = Topology(context, documentHandle, source, "Edges", FeatureTopologyQueryTypeConstants.igQueryAll);
        Placement? placement = topology.Placement;
        Func<object, bool>? filter = null;
        if (type != "all" || wanted is not null)
        {
            filter = item =>
            {
                var edge = (Edge)item;
                return (type == "all" || CurveType(edge.Geometry) == type)
                    && (wanted is null || (LineDirection(edge) is { } d && Math.Abs(Cosine(Rotated(placement, d), wanted)) > ParallelCosine));
            };
        }

        return Page(context, topology.Collection, offset, limit, filter, item => DescribeEdge(context, topology, (Edge)item));
    }

    /// <summary>Coseno mínimo para considerar dos direcciones paralelas (unos 0,08°).</summary>
    private const double ParallelCosine = 0.999999;

    private static double[]? CheckDirection(double[]? direction)
    {
        if (direction is null)
        {
            return null;
        }

        if (direction.Length != 3 || !direction.All(double.IsFinite) || Geo.Length(direction) < 1e-12)
        {
            throw ToolException.InvalidArgument("'direction' must be a non-zero vector [x, y, z].");
        }

        return direction;
    }

    private static double Cosine(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum() / (Geo.Length(a) * Geo.Length(b));

    private static double[]? LineDirection(Edge edge)
    {
        if (edge.Geometry is not Line line)
        {
            return null;
        }

        Array root = new double[3];
        Array direction = new double[3];
        line.GetLineData(ref root, ref direction);
        return Geo.Doubles(direction).ToArray();
    }

    /// <summary>
    /// Propiedades físicas en mm, mm², mm³, kg y kg·mm². Sin densidad (ni la indicada ni la del
    /// material del documento) solo se dan las geométricas.
    /// </summary>
    public static object Physical(SeContext context, string? documentHandle, double? density)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        Model target = model.RequireModel();
        if (density is not null)
        {
            Units.RequirePositive(density.Value, "density");
        }

        (double documentDensity, double documentAccuracy) = StoredSettings(target);
        double useDensity = density ?? documentDensity;
        bool hasDensity = useDensity > 0;
        double accuracy = documentAccuracy is > 0 and < 1 ? documentAccuracy : 0.99;

        Array cog = new double[3];
        Array cov = new double[3];
        Array global = new double[6];
        Array principal = new double[3];
        Array axes = new double[9];
        Array radii = new double[3];
        target.ComputePhysicalPropertiesWithSpecifiedDensity(hasDensity ? useDensity : 1.0, accuracy,
            out double volume, out double area, out double mass, ref cog, ref cov, ref global, ref principal, ref axes, ref radii,
            out double achieved, out int status);
        if (status != 0)
        {
            throw new ToolException("physical_properties_failed", $"Solid Edge could not compute the physical properties (status {status}).",
                "Check the model for failed features (se_feature_tree) and try again.");
        }

        double[] g = Geo.Doubles(global).Select(v => Math.Round(Units.KgM2ToKgMm2(v), 6)).ToArray();
        return new
        {
            document = context.DocumentHandle(model.Document),
            volume = Geo.Mm3(volume),
            area = Geo.Mm2(area),
            centerOfVolume = Geo.Mm(cov),
            density = hasDensity ? useDensity : (double?)null,
            densitySource = density is not null ? "argument" : hasDensity ? "document" : null,
            mass = hasDensity ? Math.Round(mass, 9) : (double?)null,
            centerOfGravity = hasDensity ? Geo.Mm(cog) : null,
            inertiaAboutOrigin = hasDensity
                ? new { ixx = g[0], iyy = g[1], izz = g[2], ixy = g[3], ixz = g[4], iyz = g[5] }
                : null,
            principalMomentsAboutCog = hasDensity ? Geo.Doubles(principal).Select(v => Math.Round(Units.KgM2ToKgMm2(v), 6)).ToArray() : null,
            principalAxes = hasDensity ? Geo.Plain(axes) : null,
            radiiOfGyration = hasDensity ? Geo.Mm(radii) : null,
            units = new { length = "mm", area = "mm2", volume = "mm3", density = "kg/m3", mass = "kg", inertia = "kg*mm2" },
            accuracy,
            relativeAccuracyAchieved = Geo.R(achieved),
            note = hasDensity ? null : "No density: pass 'density' (kg/m3) or assign a material to get mass and inertia.",
        };
    }

    /// <summary>Distancia mínima entre dos elementos (caras, aristas, vértices, cuerpos, planos).</summary>
    public static object Distance(SeContext context, string? documentHandle, string first, string second)
    {
        object a = context.Handles.Resolve(first, "first");
        object b = context.Handles.Resolve(second, "second");
        Array p1 = new double[3];
        Array p2 = new double[3];
        double distance;
        if (a is OccurrenceElement or Occurrence || b is OccurrenceElement or Occurrence)
        {
            // En un conjunto se mide entre referencias (ocurrencia + elemento), en coordenadas del conjunto.
            AssemblyDocument assembly = context.GetDocument<AssemblyDocument>(context.Handles.GetScope(first), "assembly");
            assembly.MinimumDistance(InAssembly(assembly, a, "first"), InAssembly(assembly, b, "second"), out distance, ref p1, ref p2);
        }
        else
        {
            ModelDocument model = ModelDocument.For(context, documentHandle, first);
            model.MinimumDistance(a, b, out distance, ref p1, ref p2);
        }

        return new { distance = Geo.Mm(distance), pointOnFirst = Geo.Mm(p1), pointOnSecond = Geo.Mm(p2) };
    }

    private static object InAssembly(AssemblyDocument assembly, object value, string argumentName) => value switch
    {
        OccurrenceElement element => element.Reference(assembly),
        Occurrence or AsmRefPlane => value,
        _ => throw ToolException.InvalidArgument(
            $"In an assembly, '{argumentName}' must be an occurrence, an assembly plane or a face/edge handle from se_geometry with 'source' = an occurrence."),
    };

    /// <summary>Ángulo entre normales (caras planas, planos) o direcciones (aristas rectas), en grados.</summary>
    public static object Angle(SeContext context, string first, string second)
    {
        double[] u = Direction(context, first, "first");
        double[] v = Direction(context, second, "second");
        double cos = Math.Clamp(u.Zip(v, (x, y) => x * y).Sum() / (Geo.Length(u) * Geo.Length(v)), -1, 1);
        double angle = Units.RadToDeg(Math.Acos(cos));
        return new { angle = Math.Round(angle, 9), supplement = Math.Round(180 - angle, 9), firstDirection = u.Select(Geo.R), secondDirection = v.Select(Geo.R) };
    }

    internal static (double Density, double Accuracy) StoredSettings(Model model)
    {
        try
        {
            Array cog = new double[3];
            Array cov = new double[3];
            Array global = new double[6];
            Array principal = new double[3];
            Array axes = new double[9];
            Array radii = new double[3];
            model.GetPhysicalProperties(out _, out double density, out double accuracy, out _, out _, out _,
                ref cog, ref cov, ref global, ref principal, ref axes, ref radii, out _);
            return (density, accuracy);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Colección pedida y, si el origen es una ocurrencia de conjunto, su posición (para dar la geometría de la pieza en
    /// coordenadas del conjunto).
    /// </summary>
    private static TopologySource Topology(SeContext context, string? documentHandle,
        string? source, string member, FeatureTopologyQueryTypeConstants query)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            ModelDocument model = ModelDocument.Get(context, documentHandle);
            return new TopologySource(model.Document, BodyCollection(model, member, query), null, null);
        }

        object value = context.Handles.Resolve(source, "source");
        if (value is Occurrence occurrence)
        {
            ModelDocument part = AssemblyService.PartOf(occurrence);
            return new TopologySource(context.DocumentOf(source), BodyCollection(part, member, query), Placement.Of(occurrence), occurrence);
        }

        if (value is OccurrenceElement { Element: Face occurrenceFace } element && member == "Edges")
        {
            return new TopologySource(context.DocumentOf(source), occurrenceFace.Edges, Placement.Of(element.Occurrence), element.Occurrence);
        }

        ModelDocument owner = ModelDocument.For(context, documentHandle, source);
        if (value is Face face && member == "Edges")
        {
            return new TopologySource(owner.Document, face.Edges, null, null);
        }

        try
        {
            return new TopologySource(owner.Document, Late.Get(value, member, query)
                ?? throw ToolException.InvalidArgument($"'{source}' returned no {member.ToLowerInvariant()}."), null, null);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw ToolException.InvalidArgument($"'{source}' has no {member.ToLowerInvariant()}.",
                "Pass a feature handle (se_feature_tree), a face handle (for edges), an assembly occurrence or omit 'source' to use the whole body.");
        }
    }

    /// <summary>
    /// Colección de caras o aristas y dónde se registran sus handles: el documento de la pieza o, desde una ocurrencia,
    /// el conjunto (con la posición de la ocurrencia y handles propios de cada ocurrencia).
    /// </summary>
    private sealed record TopologySource(SolidEdgeDocument Owner, object Collection, Placement? Placement, Occurrence? Occurrence)
    {
        public object Wrap(object element) => Occurrence is null ? element : new OccurrenceElement(Occurrence, element);
    }

    private static object BodyCollection(ModelDocument model, string member, FeatureTopologyQueryTypeConstants query)
    {
        Body body = model.RequireModel().Body as Body
            ?? throw new ToolException("no_model", $"'{model.Document.Name}' has no body yet.", "Create a base feature first.");
        return member == "Faces" ? body.Faces[query] : body.Edges[query];
    }

    private static double[] Rotated(Placement? placement, double[] vector) => placement?.Vector(vector) ?? vector;

    private static double[] Mm(Placement? placement, Array point) => placement is null
        ? Geo.Mm(point)
        : placement.Point(Geo.Doubles(point).ToArray()).Select(Geo.Mm).ToArray();

    private static double[] Plain(Placement? placement, Array vector) => placement is null
        ? Geo.Plain(vector)
        : placement.Vector(Geo.Doubles(vector).ToArray()).Select(Geo.R).ToArray();

    /// <summary>Página de una colección; con filtro, la cuenta y el desplazamiento se refieren a los elementos que lo cumplen.</summary>
    private static object Page(SeContext context, object collection, int offset, int limit, Func<object, bool>? filter, Func<object, object> describe)
    {
        int total = Late.Count(collection);
        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object>();
        int matched = total;
        if (filter is null)
        {
            for (int i = start + 1; i <= total && items.Count < take; i++)
            {
                if (Late.Call(collection, "Item", i) is { } item)
                {
                    items.Add(describe(item));
                }
            }
        }
        else
        {
            // Con filtro hay que recorrer toda la colección para contar las coincidencias.
            matched = 0;
            for (int i = 1; i <= total; i++)
            {
                if (Late.Call(collection, "Item", i) is { } item && filter(item) && matched++ >= start && items.Count < take)
                {
                    items.Add(describe(item));
                }
            }
        }

        return new { count = matched, offset = start, returned = items.Count, more = start + items.Count < matched, items };
    }

    private static Dictionary<string, object?> DescribeFace(SeContext context, TopologySource topology, Face face)
    {
        Placement? placement = topology.Placement;
        object? geometry = face.Geometry;
        var info = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(topology.Wrap(face), "face", topology.Owner),
            ["type"] = SurfaceType(geometry),
            ["area"] = Geo.Mm2(face.Area),
        };
        Array min = new double[3];
        Array max = new double[3];
        face.GetRange(ref min, ref max);
        if (placement is null)
        {
            info["boundingBox"] = new { min = Geo.Mm(min), max = Geo.Mm(max) };
        }
        else
        {
            (double[] lo, double[] hi) = placement.Box(Geo.Doubles(min).ToArray(), Geo.Doubles(max).ToArray());
            info["boundingBox"] = new { min = lo.Select(Geo.Mm).ToArray(), max = hi.Select(Geo.Mm).ToArray() };
        }

        switch (geometry)
        {
            case Plane:
                info["normal"] = Rotated(placement, FaceNormal(face)).Select(Geo.R).ToArray();
                break;
            case Cylinder cylinder:
                Array basePoint = new double[3];
                Array axis = new double[3];
                cylinder.GetCylinderData(ref basePoint, ref axis, out double radius);
                info["radius"] = Geo.Mm(radius);
                info["axisPoint"] = Mm(placement, basePoint);
                info["axisDirection"] = Plain(placement, axis);
                break;
        }

        return info;
    }

    private static Dictionary<string, object?> DescribeEdge(SeContext context, TopologySource topology, Edge edge)
    {
        Placement? placement = topology.Placement;
        object? geometry = edge.Geometry;
        var info = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(topology.Wrap(edge), "edge", topology.Owner),
            ["type"] = CurveType(geometry),
        };
        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        info["start"] = Mm(placement, start);
        info["end"] = Mm(placement, end);
        edge.GetParamExtents(out double p0, out double p1);
        edge.GetLengthAtParam(p0, p1, out double length);
        info["length"] = Geo.Mm(length);
        switch (geometry)
        {
            case Line line:
                Array root = new double[3];
                Array direction = new double[3];
                line.GetLineData(ref root, ref direction);
                info["direction"] = Plain(placement, direction);
                break;
            case Circle circle:
                Array center = new double[3];
                Array axis = new double[3];
                circle.GetCircleData(ref center, ref axis, out double radius);
                info["center"] = Mm(placement, center);
                info["radius"] = Geo.Mm(radius);
                info["axisDirection"] = Plain(placement, axis);
                break;
        }

        return info;
    }

    /// <summary>Normal exterior de la cara en el centro de su rango paramétrico.</summary>
    internal static double[] FaceNormal(Face face)
    {
        Array min = new double[2];
        Array max = new double[2];
        face.GetParamRange(ref min, ref max);
        double[] lo = Geo.Doubles(min).ToArray();
        double[] hi = Geo.Doubles(max).ToArray();
        Array param = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2 };
        Array normal = new double[3];
        face.GetNormal(1, ref param, ref normal);
        return Geo.Plain(normal);
    }

    private static double[] Direction(SeContext context, string handle, string argumentName)
    {
        object value = context.Handles.Resolve(handle, argumentName);
        Placement? placement = null;
        if (value is OccurrenceElement element)
        {
            placement = Placement.Of(element.Occurrence);
            value = element.Element;
        }

        return Rotated(placement, LocalDirection(value, handle));
    }

    private static double[] LocalDirection(object value, string handle)
    {
        switch (value)
        {
            case Face face when face.Geometry is Plane:
                return FaceNormal(face);
            case Edge edge when edge.Geometry is Line line:
                Array root = new double[3];
                Array direction = new double[3];
                line.GetLineData(ref root, ref direction);
                return Geo.Doubles(direction).ToArray();
            case RefPlane plane:
                Array normal = new double[3];
                plane.GetNormal(ref normal);
                return Geo.Doubles(normal).ToArray();
            default:
                throw ToolException.InvalidArgument($"'{handle}' has no single direction.",
                    "Angles are measured between planar faces, reference planes and straight edges.");
        }
    }

    private static string SurfaceType(object? geometry) => geometry switch
    {
        Plane => "plane",
        Cylinder => "cylinder",
        Cone => "cone",
        Sphere => "sphere",
        Torus => "torus",
        BSplineSurface => "spline",
        null => "unknown",
        _ => "other",
    };

    private static string CurveType(object? geometry) => geometry switch
    {
        Line => "line",
        Circle => "circle",
        Ellipse => "ellipse",
        BSplineCurve => "spline",
        null => "unknown",
        _ => "other",
    };
}
