using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeGeometry;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Model queries: geometry (body, faces, edges), physical properties and measurements. In mm.</summary>
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
        if (model.TryModel() is null)
        {
            return new { document, solid = false, volume = 0.0 };
        }

        // A body converted to a mesh cannot be read: no_brep_body says so.
        model.RequireBody();
        IReadOnlyList<(Model Model, Body Body)> bodies = model.Bodies();
        if (bodies.Count == 0)
        {
            return new { document, solid = false, volume = 0.0 };
        }

        (double[] lo, double[] hi) = Range(bodies.Select(b => b.Body));
        return new
        {
            document,
            body = context.Register(bodies[0].Body, "body", model.Document),
            solid = bodies.All(b => b.Body.IsSolid),
            volume = Geo.Mm3(bodies.Sum(b => b.Body.Volume)),
            boundingBox = new { min = lo, max = hi, size = hi.Zip(lo, (a, b) => Geo.R(a - b)).ToArray() },
            faces = bodies.Sum(b => Late.Count(b.Body.Faces[FeatureTopologyQueryTypeConstants.igQueryAll])),
            edges = bodies.Sum(b => Late.Count(b.Body.Edges[FeatureTopologyQueryTypeConstants.igQueryAll])),
            vertices = bodies.Sum(b => Late.Count(b.Body.Vertices)),
            modelingMode = model.ModelingMode,
            bodies = bodies.Count == 1 ? null : bodies.Select(b => DescribeBody(context, model.Document, b.Model, b.Body)).ToArray(),
        };
    }

    /// <summary>The design bodies of a part: handle, name, volume and box of each.</summary>
    internal static object[] DescribeBodies(SeContext context, ModelDocument model) =>
        model.Bodies().Select(b => DescribeBody(context, model.Document, b.Model, b.Body)).ToArray();

    /// <summary>One design body of a part with several (after a split): handle, name, volume and box.</summary>
    private static object DescribeBody(SeContext context, SolidEdgeDocument owner, Model model, Body body)
    {
        (double[] lo, double[] hi) = Range([body]);
        return new
        {
            handle = context.Register(body, "body", owner),
            name = Late.TryGet(model, "BodyName") as string,
            volume = Geo.Mm3(body.Volume),
            boundingBox = new { min = lo, max = hi, size = hi.Zip(lo, (a, b) => Geo.R(a - b)).ToArray() },
        };
    }

    /// <summary>Box around one or more bodies, in mm.</summary>
    private static (double[] Min, double[] Max) Range(IEnumerable<Body> bodies)
    {
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (Body body in bodies)
        {
            Array min = new double[3];
            Array max = new double[3];
            body.GetRange(ref min, ref max);
            double[] a = Geo.Mm(min);
            double[] b = Geo.Mm(max);
            for (int i = 0; i < 3; i++)
            {
                lo[i] = Math.Min(lo[i], a[i]);
                hi[i] = Math.Max(hi[i], b[i]);
            }
        }

        return (lo, hi);
    }

    /// <summary>
    /// Summary of the body of an occurrence's part. The box is that of <c>Occurrence.Range</c>, in the coordinates of the
    /// assembly, and exact also with the part rotated (verified in Solid Edge 2026).
    /// </summary>
    private static object OccurrenceSummary(SeContext context, string source)
    {
        if (context.Handles.Resolve(source, "source") is not Occurrence occurrence)
        {
            throw ToolException.InvalidArgument("For 'summary', 'source' must be an assembly occurrence handle (se_assembly list).",
                "Omit 'source' for the summary of a part or sheet metal document.");
        }

        IReadOnlyList<(Model? Model, Body Body)> bodies = OverrideBody(occurrence) is { } own
            ? [(null, own)]
            : [.. AssemblyService.PartOf(occurrence).Bodies().Select(b => ((Model?)b.Model, b.Body))];
        if (bodies.Count == 0)
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
            solid = bodies.All(b => b.Body.IsSolid),
            volume = Geo.Mm3(bodies.Sum(b => b.Body.Volume)),
            boundingBox = new { min = lo, max = hi, size = hi.Zip(lo, (a, b) => Geo.R(a - b)).ToArray() },
            faces = bodies.Sum(b => Late.Count(b.Body.Faces[FeatureTopologyQueryTypeConstants.igQueryAll])),
            edges = bodies.Sum(b => Late.Count(b.Body.Edges[FeatureTopologyQueryTypeConstants.igQueryAll])),
            vertices = bodies.Sum(b => Late.Count(b.Body.Vertices)),
            bodies = bodies.Count == 1 ? (int?)null : bodies.Count,
            coordinates = "assembly",
        };
    }

    /// <summary>
    /// Faces of the body or of a feature (<paramref name="source"/>), with type, area, normal or radius.
    /// <paramref name="direction"/> keeps only the planar faces whose outward normal points in that direction. With an
    /// assembly occurrence as <paramref name="source"/>, the faces of its part in assembly coordinates.
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
    /// Edges of the body, of a feature or of a face, with endpoints, length and radius.
    /// <paramref name="direction"/> keeps only the straight edges parallel to that vector (in either direction).
    /// </summary>
    public static object Edges(SeContext context, string? documentHandle, string? source, string type, double[]? direction, int offset, int limit)
    {
        double[]? wanted = CheckDirection(direction);
        if (!EdgeTypes.Contains(type))
        {
            throw ToolException.InvalidArgument($"Unknown edge type '{type}'.", $"Use one of: {string.Join(", ", EdgeTypes)}.");
        }

        // The type is filtered by geometry: the igQueryEllipse query does not return circular edges (verified).
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

    /// <summary>Minimum cosine to consider two directions parallel (about 0.08°).</summary>
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
    /// Physical properties in mm, mm², mm³, kg and kg·mm². Without density (neither the given one nor the
    /// document material) only the geometric ones are returned.
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

        // A split leaves one model per body. Model.ComputePhysicalProperties... then measures only one of them, whichever
        // model it is called on (also after ActivateBody), while Body.ComputePhysicalProperties on any body measures all of
        // them together and returns status 1 (verified: 84000 + 36000 mm³ gave 120000, centre and inertia of the whole box).
        // So with several bodies the totals come from the first body, checked against the sum of the body volumes. A body
        // converted to a mesh also adds a model, while the first one's body can no longer be read (verified): the model measures it.
        double use = hasDensity ? useDensity : 1.0;
        IReadOnlyList<(Model Model, Body Body)> bodies = model.Models.Count > 1 && model.TryBodies() is { } readable ? readable : [];
        bool several = bodies.Count > 1;
        PhysicalResult r = several ? Compute(bodies[0].Body, use, accuracy, 1) : Compute(target, use, accuracy, 0);
        if (several)
        {
            double sum = bodies.Sum(b => b.Body.Volume);
            if (Math.Abs(r.Volume - sum) > 1e-6 * Math.Max(sum, 1e-9))
            {
                throw new ToolException("physical_properties_failed",
                    $"Solid Edge measured {Geo.Mm3(r.Volume)} mm3 for a part whose {bodies.Count} bodies add up to {Geo.Mm3(sum)} mm3.",
                    "Unite the bodies (se_part_feature unite) to measure them as one, or use se_geometry summary for the volume of each.");
            }
        }

        double[] g = r.Global.Select(v => Math.Round(Units.KgM2ToKgMm2(v), 6)).ToArray();
        return new
        {
            document = context.DocumentHandle(model.Document),
            volume = Geo.Mm3(r.Volume),
            area = Geo.Mm2(r.Area),
            centerOfVolume = r.CenterOfVolume.Select(Geo.Mm).ToArray(),
            density = hasDensity ? useDensity : (double?)null,
            densitySource = density is not null ? "argument" : hasDensity ? "document" : null,
            mass = hasDensity ? Math.Round(r.Mass, 9) : (double?)null,
            centerOfGravity = hasDensity ? r.CenterOfGravity.Select(Geo.Mm).ToArray() : null,
            inertiaAboutOrigin = hasDensity
                ? new { ixx = g[0], iyy = g[1], izz = g[2], ixy = g[3], ixz = g[4], iyz = g[5] }
                : null,
            principalMomentsAboutCog = hasDensity ? r.Principal.Select(v => Math.Round(Units.KgM2ToKgMm2(v), 6)).ToArray() : null,
            principalAxes = hasDensity ? r.Axes.Select(Geo.R).ToArray() : null,
            radiiOfGyration = hasDensity ? r.Radii.Select(Geo.Mm).ToArray() : null,
            bodies = several
                ? bodies.Select(b => new
                {
                    name = Late.TryGet(b.Model, "BodyName") as string,
                    volume = Geo.Mm3(b.Body.Volume),
                    mass = hasDensity ? Math.Round(b.Body.Volume * useDensity, 9) : (double?)null,
                }).ToArray()
                : null,
            units = new { length = "mm", area = "mm2", volume = "mm3", density = "kg/m3", mass = "kg", inertia = "kg*mm2" },
            accuracy,
            relativeAccuracyAchieved = Geo.R(r.Achieved),
            note = !hasDensity ? "No density: pass 'density' (kg/m3) or assign a material to get mass and inertia."
                : several ? $"{bodies.Count} bodies: the properties are those of all of them together ('bodies' gives the volume of each)." : null,
        };
    }
    private sealed record PhysicalResult(double Volume, double Area, double Mass, double[] CenterOfGravity, double[] CenterOfVolume,
        double[] Global, double[] Principal, double[] Axes, double[] Radii, double Achieved);

    /// <summary>
    /// Physical properties of a model or of a body (both have ComputePhysicalProperties with the same arguments);
    /// <paramref name="acceptedStatus"/> is the status that still means a result (1 for a body of a multi-body part).
    /// </summary>
    private static PhysicalResult Compute(object target, double density, double accuracy, int acceptedStatus)
    {
        Array cog = new double[3];
        Array cov = new double[3];
        Array global = new double[6];
        Array principal = new double[3];
        Array axes = new double[9];
        Array radii = new double[3];
        double volume, area, mass, achieved;
        int status;
        if (target is Body body)
        {
            body.ComputePhysicalProperties(density, accuracy, out volume, out area, out mass, ref cog, ref cov, ref global, ref principal,
                ref axes, ref radii, out achieved, out status);
        }
        else
        {
            ((Model)target).ComputePhysicalPropertiesWithSpecifiedDensity(density, accuracy, out volume, out area, out mass, ref cog, ref cov,
                ref global, ref principal, ref axes, ref radii, out achieved, out status);
        }

        if (status != 0 && status != acceptedStatus)
        {
            throw new ToolException("physical_properties_failed", $"Solid Edge could not compute the physical properties (status {status}).",
                "Check the model for failed features (se_feature_tree) and try again.");
        }

        return new PhysicalResult(volume, area, mass, [.. Geo.Doubles(cog)], [.. Geo.Doubles(cov)], [.. Geo.Doubles(global)],
            [.. Geo.Doubles(principal)], [.. Geo.Doubles(axes)], [.. Geo.Doubles(radii)], achieved);
    }

    /// <summary>Minimum distance between two elements (faces, edges, vertices, bodies, planes).</summary>
    public static object Distance(SeContext context, string? documentHandle, string first, string second)
    {
        object a = context.Handles.Resolve(first, "first");
        object b = context.Handles.Resolve(second, "second");
        Array p1 = new double[3];
        Array p2 = new double[3];
        double distance;
        if (a is OccurrenceElement or Occurrence || b is OccurrenceElement or Occurrence)
        {
            // In an assembly it is measured between references (occurrence + element), in assembly coordinates.
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

    /// <summary>Angle between normals (planar faces, planes) or directions (straight edges), in degrees.</summary>
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
    /// Requested collection and, if the origin is an assembly occurrence, its position (to give the geometry of the part in
    /// assembly coordinates).
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
            object collection = OverrideBody(occurrence) is { } own
                ? Late.Get(own, member, query) ?? throw ToolException.InvalidArgument($"'{source}' returned no {member.ToLowerInvariant()}.")
                : BodyCollection(AssemblyService.PartOf(occurrence), member, query);
            return new TopologySource(context.DocumentOf(source), collection, GeometryPlacement(occurrence), occurrence);
        }

        if (value is OccurrenceElement { Element: Face occurrenceFace } element && member == "Edges")
        {
            return new TopologySource(context.DocumentOf(source), occurrenceFace.Edges, GeometryPlacement(element.Occurrence), element.Occurrence);
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
    /// Body an occurrence shows when it is not its part's: a structural frame member is an occurrence of the section part
    /// with a body of its own, cut to length and mitred (Occurrence.HasBodyOverride and Occurrence.Body; verified: 6670 mm³
    /// for a 60 mm tube, while the section part is a short piece). Null for ordinary occurrences.
    /// </summary>
    internal static Body? OverrideBody(Occurrence occurrence)
    {
        try
        {
            return occurrence.HasBodyOverride ? occurrence.Body as Body : null;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Placement that takes an occurrence's geometry to the assembly: none when its own body (frame members) is already in
    /// assembly coordinates, which is told by comparing the body's range with the occurrence's.
    /// </summary>
    internal static Placement? GeometryPlacement(Occurrence occurrence)
    {
        if (OverrideBody(occurrence) is not { } body)
        {
            return Placement.Of(occurrence);
        }

        occurrence.Range(out double x0, out double y0, out double z0, out double x1, out double y1, out double z1);
        Array min = new double[3];
        Array max = new double[3];
        body.GetRange(ref min, ref max);
        double[] lo = [.. Geo.Doubles(min)];
        double[] hi = [.. Geo.Doubles(max)];
        double[] expected = [x0, y0, z0, x1, y1, z1];
        bool assembly = lo.Concat(hi).Zip(expected, (a, b) => Math.Abs(a - b)).All(d => d < 1e-6);
        return assembly ? null : Placement.Of(occurrence);
    }

    /// <summary>
    /// Collection of faces or edges and where their handles are registered: the part document or, from an occurrence,
    /// the assembly (with the occurrence's position and handles specific to each occurrence).
    /// </summary>
    private sealed record TopologySource(SolidEdgeDocument Owner, object Collection, Placement? Placement, Occurrence? Occurrence)
    {
        public object Wrap(object element) => Occurrence is null ? element : new OccurrenceElement(Occurrence, element);
    }

    /// <summary>Faces or edges of the body; with several bodies (after a split), those of all of them in body order.</summary>
    private static object BodyCollection(ModelDocument model, string member, FeatureTopologyQueryTypeConstants query)
    {
        Body body = model.RequireBody() as Body
            ?? throw new ToolException("no_model", $"'{model.Document.Name}' has no body yet.", "Create a base feature first.");
        IReadOnlyList<(Model Model, Body Body)> bodies = model.Models.Count > 1 ? model.Bodies() : [];
        if (bodies.Count <= 1)
        {
            return member == "Faces" ? body.Faces[query] : body.Edges[query];
        }

        return bodies.SelectMany(b => Late.Items(member == "Faces" ? b.Body.Faces[query] : b.Body.Edges[query])).ToList();
    }

    private static double[] Rotated(Placement? placement, double[] vector) => placement?.Vector(vector) ?? vector;

    private static double[] Mm(Placement? placement, Array point) => placement is null
        ? Geo.Mm(point)
        : placement.Point(Geo.Doubles(point).ToArray()).Select(Geo.Mm).ToArray();

    private static double[] Plain(Placement? placement, Array vector) => placement is null
        ? Geo.Plain(vector)
        : placement.Vector(Geo.Doubles(vector).ToArray()).Select(Geo.R).ToArray();

    /// <summary>Page of a collection; with a filter, the count and the offset refer to the elements that match it.</summary>
    private static object Page(SeContext context, object collection, int offset, int limit, Func<object, bool>? filter, Func<object, object> describe)
    {
        // A list gathers the elements of several bodies; anything else is a Solid Edge collection.
        var list = collection as IReadOnlyList<object>;
        int total = list?.Count ?? Late.Count(collection);
        object? ItemAt(int i) => list is null ? Late.Call(collection, "Item", i) : list[i - 1];
        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object>();
        int matched = total;
        if (filter is null)
        {
            for (int i = start + 1; i <= total && items.Count < take; i++)
            {
                if (ItemAt(i) is { } item)
                {
                    items.Add(describe(item));
                }
            }
        }
        else
        {
            // With a filter, the whole collection must be traversed to count the matches.
            matched = 0;
            for (int i = 1; i <= total; i++)
            {
                if (ItemAt(i) is { } item && filter(item) && matched++ >= start && items.Count < take)
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

    /// <summary>Outward normal of the face at the centre of its parametric range.</summary>
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
            placement = GeometryPlacement(element.Occurrence);
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
