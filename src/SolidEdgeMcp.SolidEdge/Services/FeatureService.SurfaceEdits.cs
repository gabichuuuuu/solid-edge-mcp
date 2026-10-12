// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using Vertex = SolidEdgeGeometry.Vertex;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Edits and new surfaces from existing geometry: faces copied as a surface, a surface trimmed where surfaces or curves cross
/// it, a surface extended from its edges, surfaces closed into a solid construction body, and BlueSurf through section edges.
/// Verified in Solid Edge 2026:
/// <list type="bullet">
/// <item>CopySurfaces.Add(n, faces) copies faces of the body or of surfaces as one construction surface (the top and front of
/// a 100 x 60 x 20 block: 6000 + 2000 mm²).</item>
/// <item>TrimSurfaces belongs to the construction model of the surface: AddByPointOnFace(3, point, 3, normal, 1, [face], n,
/// tools) removes the region of the face that holds the point. Tools are faces of other surfaces or curve edges (a curve
/// that ends inside the face is carried across it); reference planes give E_INVALIDARG, and Add(n, tools, side) gives
/// E_FAIL or does nothing.</item>
/// <item>ExtendSurfaces.AddEx(n, edges, distance, type) on the construction model: natural, linear and reflective add
/// length x distance to a plane; the constant radius arc fails on a plane.</item>
/// <item>IntersectSurfaces.AddByAutoTrim(n, bodies, false, true, true, tolerance) takes the construction bodies (the surface
/// features give E_INVALIDARG) and leaves one closed solid construction body (a tube 100 x 60 cut by planes at z = 5 and
/// z = 15: 60000 mm³); invert = true failed.</item>
/// <item>BlueSurfs.Add takes edges as sections and their vertices as origins, and edges as guides (two lines 100 long, 60
/// apart and 20 higher: 100 x 63.2456 mm²). Sketches as sections gave E_NOINTERFACE with points as origins and a failed
/// feature with their lines.</item>
/// </list>
/// </summary>
public static partial class FeatureService
{
    /// <summary>Faces of the body or of surfaces copied as one construction surface; its area is checked against theirs.</summary>
    public static object CopySurface(SeContext context, string? documentHandle, string[]? faces)
    {
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("copy needs 'faces': face handles of the body or of surfaces (se_geometry faces).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, faces[0]);
        Face[] resolved = faces.Select(h => context.Handles.Resolve<Face>(h, "faces")).ToArray();
        double expected = resolved.Sum(f => f.Area);
        bool switched = model.EnsureOrdered();
        Array array = resolved.Cast<object>().ToArray();
        object surface = Create("copied surface",
            () => ConstructionsOf(model).CopySurfaces.Add(array.Length, ref array, System.Reflection.Missing.Value, System.Reflection.Missing.Value));
        object result = CheckedSurface(context, model, surface, "copy", switched, [], []);
        double area = FacesOf(surface).Sum(f => f.Area);
        if (Math.Abs(area - expected) > Math.Max(1e-9, expected * 1e-4))
        {
            TryDeleteSurface(surface);
            throw new ToolException("feature_failed",
                $"The copied surface has {Geo.Mm2(area)} mm² instead of {Geo.Mm2(expected)} mm² (the faces given); it was deleted.",
                "Pass faces of the body or of surfaces (se_geometry faces).");
        }

        return result;
    }

    /// <summary>
    /// Removes the region of a surface that holds <paramref name="point"/> (mm), bounded by the tools: faces of surfaces,
    /// surfaces, curves or edges that cross it.
    /// </summary>
    public static object TrimSurface(SeContext context, string? documentHandle, string[]? surfaces, string[]? tools, double[]? point)
    {
        if (surfaces is not [string targetHandle])
        {
            throw ToolException.InvalidArgument("trim takes one surface in 'surfaces': a surface handle (action 'list') or a face of it.");
        }

        if (tools is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("trim needs 'tools': surfaces, faces of surfaces, curves (se_curve) or edges that cross the surface.");
        }

        double[] p = PointArgument(point, "point", "trim needs 'point' [x, y, z] in mm: a point on the region of the surface to remove.");
        ModelDocument model = ModelDocument.For(context, documentHandle, targetHandle);
        object target = context.Handles.Resolve(targetHandle, "surfaces");
        (ConstructionModel owner, Body body) = ConstructionOf(model, target, targetHandle, "surfaces");
        Face face = target as Face ?? FaceAtPoint(BodyFaces(body), p, targetHandle);
        (double off, double[] normal) = OnFace(face, p);
        if (off > 1e-5)
        {
            throw ToolException.InvalidArgument($"'point' is {Geo.Mm(off)} mm away from the surface.",
                "Give a point that lies on the region of the surface to remove.");
        }

        object[] toolItems = tools.SelectMany(h => CuttingElements(context, model, h, "tools")).ToArray();
        double before = BodyFaces(body).Sum(f => f.Area);
        bool switched = model.EnsureOrdered();
        Array points = p;
        Array direction = normal;
        Array targets = new object[] { face };
        Array toolArray = toolItems;
        object trim = Create("trim", () => owner.TrimSurfaces.AddByPointOnFace(3, ref points, 3, ref direction, 1, ref targets, toolArray.Length, ref toolArray));
        return SurfaceEditResult(context, model, owner, trim, "trim", before, switched, removed: true,
            "The tools must cross the surface (reference planes are not accepted: use a surface or a curve) and the point must lie on the region to remove.");
    }

    /// <summary>Extends a surface from free edges by a distance (mm): natural, linear or reflective.</summary>
    public static object ExtendSurface(SeContext context, string? documentHandle, string[]? edges, double? distance, string extension)
    {
        (ModelDocument model, object[] resolved) = ResolveEdges(context, documentHandle, edges);
        double d = Units.MmToM(Units.RequirePositive(distance ?? throw ToolException.InvalidArgument("extend requires 'distance' (mm)."), "distance"));
        ExtendSurfaceExtentTypeConstants type = extension switch
        {
            "natural" => ExtendSurfaceExtentTypeConstants.igESNatural,
            "linear" => ExtendSurfaceExtentTypeConstants.igESLinear,
            "reflective" => ExtendSurfaceExtentTypeConstants.igESReflective,
            _ => throw ToolException.InvalidArgument($"Unknown extension '{extension}'.", "Use natural, linear or reflective."),
        };
        (ConstructionModel owner, Body body) = ConstructionOf(model, resolved[0], edges![0], "edges");
        int tag = body.Tag;
        for (int i = 1; i < resolved.Length; i++)
        {
            if (Late.Get(resolved[i], "Body") is not Body other || other.Tag != tag)
            {
                throw ToolException.InvalidArgument($"'{edges[i]}' is not an edge of the same surface as '{edges[0]}'.", "Extend one surface at a time.");
            }
        }

        double before = BodyFaces(body).Sum(f => f.Area);
        bool switched = model.EnsureOrdered();
        Array array = resolved;
        object extend = Create("extension", () => owner.ExtendSurfaces.AddEx(array.Length, ref array, d, type));
        return SurfaceEditResult(context, model, owner, extend, "extension", before, switched, removed: false,
            "Pass free edges of a construction surface (se_geometry edges with source = the surface); the arc extension does not work on planes.");
    }

    /// <summary>Surfaces that enclose a volume closed into one solid construction body (the pieces outside are trimmed away).</summary>
    public static object IntersectSurfaces(SeContext context, string? documentHandle, string[]? surfaces, double? tolerance)
    {
        if (surfaces is not { Length: >= 2 })
        {
            throw ToolException.InvalidArgument("intersect needs at least two surfaces in 'surfaces' that together enclose a volume.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, surfaces[0]);
        var bodies = new List<Body>();
        ConstructionModel? owner = null;
        foreach (string handle in surfaces)
        {
            (ConstructionModel construction, Body body) = ConstructionOf(model, context.Handles.Resolve(handle, "surfaces"), handle, "surfaces");
            if (body.IsSolid)
            {
                throw ToolException.InvalidArgument($"'{handle}' is already a closed body.", "Pass open surfaces (se_surface list).");
            }

            if (bodies.All(b => b.Tag != body.Tag))
            {
                bodies.Add(body);
                owner ??= construction;
            }
        }

        if (bodies.Count < 2)
        {
            throw ToolException.InvalidArgument("The surfaces given are one surface.", "Pass at least two different surfaces.");
        }

        double tol = tolerance is { } t ? Units.MmToM(Units.RequirePositive(t, "tolerance")) : 1e-5;
        HashSet<int> solidsBefore = [.. ConstructionBodies(model).Where(c => c.Body.IsSolid).Select(c => c.Body.Tag)];
        bool switched = model.EnsureOrdered();
        Array array = bodies.Cast<object>().ToArray();
        object feature = Create("intersection of surfaces", () => owner!.IntersectSurfaces.AddByAutoTrim(array.Length, ref array, false, true, true, tol));
        context.ModelChanged(model.Document, "the model changed (surfaces intersected)");
        string? status = StatusOf(feature);
        (string? Name, Body Body)[] made = [.. ConstructionBodies(model).Where(c => c.Body.IsSolid && !solidsBefore.Contains(c.Body.Tag))];
        if (status is not ("ok" or "warning") || made.Length != 1 || made[0].Body.Volume <= 0)
        {
            TryDeleteSurface(feature);
            throw new ToolException("feature_failed",
                $"Solid Edge did not close the surfaces into a solid ({(status is "ok" or "warning" ? $"{made.Length} new closed bodies" : $"status {status}")}); the operation was deleted.",
                "The surfaces must cross each other and together enclose a volume (open ends closed by other surfaces).");
        }

        return new
        {
            feature = Describe(context, model, feature),
            body = DescribeConstruction(context, model, made[0].Name, made[0].Body),
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>
    /// BlueSurf through section edges in order (one edge per section), optionally along guide edges. Each section is joined
    /// from the end nearest the previous section's.
    /// </summary>
    public static object BlueSurf(SeContext context, string? documentHandle, string[]? edges, string[]? guides, bool endCaps)
    {
        if (edges is not { Length: >= 2 })
        {
            throw ToolException.InvalidArgument("bluesurf needs at least two sections in 'edges': one edge per section, in order (se_geometry edges of curves, surfaces or the body).");
        }

        (ModelDocument model, object[] sections) = ResolveEdges(context, documentHandle, edges);
        object[] guideEdges = guides is { Length: > 0 } ? [.. guides.Select(h => (object)context.Handles.Resolve<Edge>(h, "guides"))] : [];
        object[] origins = new object[sections.Length];
        double[]? previous = null;
        for (int i = 0; i < sections.Length; i++)
        {
            Edge edge = (Edge)sections[i];
            Vertex start = edge.StartVertex as Vertex ?? throw ToolException.InvalidArgument(
                $"Section '{edges[i]}' is a closed edge (a full circle) with no vertex to start from.", "Pass open edges as sections.");
            Vertex end = edge.EndVertex as Vertex ?? start;
            origins[i] = previous is null || Geo.Distance(VertexPoint(start), previous) <= Geo.Distance(VertexPoint(end), previous) ? start : end;
            previous = VertexPoint((Vertex)origins[i]);
        }

        bool switched = model.EnsureOrdered();
        Array sectionArray = sections;
        Array originArray = origins;
        Array guideArray = guideEdges;
        const FeaturePropertyConstants none = FeaturePropertyConstants.igNone;
        object surface = Create("BlueSurf", () => ConstructionsOf(model).BlueSurfs.Add(sectionArray.Length, ref sectionArray, ref originArray,
            none, 0, none, 0, guideArray.Length, ref guideArray, none, 0, none, 0, endCaps, false));
        return CheckedSurface(context, model, surface, "bluesurf", switched, [], []);
    }

    /// <summary>
    /// A constant radius blend between two construction surfaces, on the side of each face where <paramref name="point"/> (mm)
    /// lies; the surfaces are trimmed back to it. Rounds.AddSurfaceBlend on the construction model of the first surface:
    /// igLeft puts the blend on the side opposite the face normal and igRight on the normal side (two planes crossing at the
    /// origin with normals -Y and +X: the four combinations give a quarter cylinder of R10 in each quadrant, 471.24 mm² for a
    /// height of 30).
    /// </summary>
    public static object BlendSurfaces(SeContext context, string? documentHandle, string[]? faces, double? radius, double[]? point)
    {
        if (faces is not [string firstHandle, string secondHandle])
        {
            throw ToolException.InvalidArgument("blend takes two faces in 'faces': one face of each surface (se_geometry faces with source = the surface).");
        }

        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("blend requires 'radius' (mm)."), "radius"));
        double[] p = PointArgument(point, "point", "blend needs 'point' [x, y, z] in mm: a point in the corner between the two faces where the blend goes.");
        ModelDocument model = ModelDocument.For(context, documentHandle, firstHandle);
        Face first = context.Handles.Resolve<Face>(firstHandle, "faces");
        Face second = context.Handles.Resolve<Face>(secondHandle, "faces");
        (ConstructionModel owner, Body body) = ConstructionOf(model, first, firstHandle, "faces");
        (_, Body other) = ConstructionOf(model, second, secondHandle, "faces");
        if (other.Tag == body.Tag)
        {
            throw ToolException.InvalidArgument("Both faces belong to the same surface: blend joins two surfaces.", "Pass one face of each surface.");
        }

        FeaturePropertyConstants firstSide = SideOf(first, p, firstHandle);
        FeaturePropertyConstants secondSide = SideOf(second, p, secondHandle);
        bool switched = model.EnsureOrdered();
        object blend = Create("surface blend", () => owner.Rounds.AddSurfaceBlend(first, firstSide, second, secondSide, r, true, true));
        context.ModelChanged(model.Document, "the model changed (surface blend)");
        string? status = StatusOf(blend);
        Face[] blendFaces = FacesOf(blend);
        if (status is not ("ok" or "warning") || blendFaces.Length == 0)
        {
            TryDeleteSurface(blend);
            throw new ToolException("feature_failed", $"Solid Edge did not make the surface blend (status {status ?? "unknown"}); it was deleted.",
                "The radius must fit between the faces, and the point must lie in the corner they make, off both faces.");
        }

        return new
        {
            feature = Describe(context, model, blend),
            blend = new
            {
                faces = blendFaces.Select(f => new { handle = context.Register(f, "face", model.Document), area = Geo.Mm2(f.Area) }).ToArray(),
                area = Geo.Mm2(blendFaces.Sum(f => f.Area)),
                radius = Geo.Mm(r),
            },
            sides = new[] { firstSide == FeaturePropertyConstants.igRight ? "normal" : "reverse", secondSide == FeaturePropertyConstants.igRight ? "normal" : "reverse" },
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>igRight when the point lies on the side the face normal points to, igLeft on the other side.</summary>
    private static FeaturePropertyConstants SideOf(Face face, double[] point, string handle)
    {
        (double[] nearest, double[] normal) = NearestOnFace(face, point);
        double[] offset = [point[0] - nearest[0], point[1] - nearest[1], point[2] - nearest[2]];
        double along = offset[0] * normal[0] + offset[1] * normal[1] + offset[2] * normal[2];
        return Math.Abs(along) <= 1e-6
            ? throw ToolException.InvalidArgument($"'point' lies on (or beside) the face '{handle}'.", "Give a point off both faces, in the corner where the blend goes.")
            : along > 0 ? FeaturePropertyConstants.igRight : FeaturePropertyConstants.igLeft;
    }

    /// <summary>The construction model of a construction surface or curve, of a face or edge of one, or of a construction body.</summary>
    private static (ConstructionModel Model, Body Body) ConstructionOf(ModelDocument model, object item, string handle, string argumentName)
    {
        Body? body = item switch
        {
            Body b => b,
            Face or Edge => Late.Get(item, "Body") as Body,
            _ => null,
        };
        if (body is null)
        {
            if (Late.TryGet(item, "Parent") is ConstructionModel parent && Late.TryGet(parent, "Body") is Body parentBody)
            {
                return (parent, parentBody);
            }

            throw new ToolException("handle_wrong_type", $"'{handle}' in '{argumentName}' is not a construction surface or curve, a face or edge of one, or a construction body.",
                "Use se_surface list, se_curve list or se_body list.");
        }

        int tag = body.Tag;
        Constructions constructions = ConstructionsOf(model);
        int count = constructions.Count;
        for (int i = 1; i <= count; i++)
        {
            object candidate = constructions.Item(i);
            if (Late.TryGet(candidate, "Body") is Body candidateBody && candidateBody.Tag == tag)
            {
                return ((ConstructionModel)candidate, candidateBody);
            }
        }

        throw ToolException.InvalidArgument($"'{handle}' in '{argumentName}' belongs to the design body, not to a construction surface.",
            "Make a surface from faces of the body with se_surface copy.");
    }

    /// <summary>Faces, edges and reference planes that cut: a face or edge as given, the faces of a surface, the edges of a curve.</summary>
    private static IEnumerable<object> CuttingElements(SeContext context, ModelDocument model, string handle, string argumentName, bool planes = false)
    {
        if (OrientedPlanes.TryAxis(handle, out _) || int.TryParse(handle, out _))
        {
            return planes
                ? [model.ResolvePlaneSource(context, handle, argumentName)]
                : throw ToolException.InvalidArgument($"'{handle}' is a reference plane: Solid Edge does not trim surfaces with planes.",
                    "Make a surface on the plane (se_surface extrude of a line) or use a curve.");
        }

        object resolved = context.Handles.Resolve(handle, argumentName);
        switch (resolved)
        {
            case RefPlane when planes:
            case Face:
            case Edge:
                return [resolved];
            case RefPlane:
                throw ToolException.InvalidArgument($"'{handle}' is a reference plane: Solid Edge does not trim surfaces with planes.",
                    "Make a surface on the plane (se_surface extrude of a line) or use a curve.");
            case Body body:
                Face[] bodyFaces = BodyFaces(body);
                return bodyFaces.Length > 0 ? bodyFaces : Late.Items(body.Edges[TopologyQuery.igQueryAll]).OfType<Edge>().ToArray();
        }

        Face[] faces = FacesOf(resolved);
        if (faces.Length > 0)
        {
            return faces;
        }

        Edge[] edges = CurveEdges(resolved);
        return edges.Length > 0
            ? edges
            : throw new ToolException("handle_wrong_type", $"'{handle}' in '{argumentName}' is not a surface, a curve, a face or an edge.",
                "Use se_surface list, se_curve list or se_geometry.");
    }

    private static Face[] BodyFaces(Body body) => [.. Late.Items(body.Faces[TopologyQuery.igQueryAll]).OfType<Face>()];

    /// <summary>The face of the surface that holds the point (within 0.01 mm and inside the face's box).</summary>
    private static Face FaceAtPoint(Face[] faces, double[] point, string handle)
    {
        Face? best = null;
        double nearest = double.MaxValue;
        foreach (Face face in faces)
        {
            Array min = new double[3];
            Array max = new double[3];
            face.GetRange(ref min, ref max);
            double[] lo = [.. Geo.Doubles(min)];
            double[] hi = [.. Geo.Doubles(max)];
            if (Enumerable.Range(0, 3).Any(k => point[k] < lo[k] - 1e-5 || point[k] > hi[k] + 1e-5))
            {
                continue;
            }

            double distance = DistanceToFace(face, point);
            if (distance < nearest)
            {
                nearest = distance;
                best = face;
            }
        }

        return best is not null && nearest <= 1e-5
            ? best
            : throw ToolException.InvalidArgument($"'point' is not on the surface '{handle}'.", "Give a point that lies on the region of the surface to remove.");
    }

    /// <summary>Distance (m) from the point to the face and the face normal at the nearest point.</summary>
    private static (double Distance, double[] Normal) OnFace(Face face, double[] point)
    {
        (double[] nearest, double[] normal) = NearestOnFace(face, point);
        return (Geo.Distance(point, nearest), normal);
    }

    /// <summary>The nearest point of the face's surface (m) and the face normal there.</summary>
    private static (double[] Point, double[] Normal) NearestOnFace(Face face, double[] point)
    {
        Array points = point.ToArray();
        Array guess = new double[2];
        Array deviations = new double[1];
        Array parameters = new double[2];
        Array flags = new int[1];
        face.GetParamAtPoint(1, ref points, ref guess, ref deviations, ref parameters, ref flags);
        Array back = new double[3];
        face.GetPointAtParam(1, ref parameters, ref back);
        Array normal = new double[3];
        face.GetNormal(1, ref parameters, ref normal);
        return ([.. Geo.Doubles(back)], [.. Geo.Doubles(normal)]);
    }

    private static double[] VertexPoint(Vertex vertex)
    {
        Array point = new double[3];
        vertex.GetPointData(ref point);
        return [.. Geo.Doubles(point)];
    }

    /// <summary>A point [x, y, z] in mm, returned in metres.</summary>
    private static double[] PointArgument(double[]? point, string name, string missing) =>
        point is null ? throw ToolException.InvalidArgument(missing)
        : point.Length != 3 ? throw ToolException.InvalidArgument($"'{name}' must be one point [x, y, z] in mm.")
        : [.. point.Select(v => Units.MmToM(Units.RequireFinite(v, name)))];

    /// <summary>
    /// Checks an edit of a construction surface by the area of its body (less after a trim, more after an extension); one that
    /// fails or changes nothing is deleted.
    /// </summary>
    private static object SurfaceEditResult(SeContext context, ModelDocument model, ConstructionModel owner, object feature, string what,
        double before, bool switched, bool removed, string hint)
    {
        context.ModelChanged(model.Document, $"the model changed (surface {what})");
        string? status = StatusOf(feature);
        Body? body = Late.TryGet(owner, "Body") as Body;
        double after = body is null ? before : BodyFaces(body).Sum(f => f.Area);
        double change = after - before;
        bool changed = removed ? change < -Math.Max(1e-12, before * 1e-9) : change > Math.Max(1e-12, before * 1e-9);
        if (status is not ("ok" or "warning") || body is null || !changed)
        {
            TryDeleteSurface(feature);
            throw new ToolException("feature_failed",
                $"Solid Edge did not make the {what} ({(status is "ok" or "warning" ? "the surface did not change" : $"status {status}")}); it was deleted.", hint);
        }

        (double[] lo, double[] hi) = InspectService.Range([body]);
        return new
        {
            feature = Describe(context, model, feature),
            surface = new
            {
                body = context.Register(body, "body", model.Document),
                name = Late.TryGetString(owner, "BodyName"),
                faces = BodyFaces(body).Length,
                area = Geo.Mm2(after),
                boundingBox = new { min = lo, max = hi },
            },
            areaBefore = Geo.Mm2(before),
            areaChange = Geo.Mm2(change),
            warnings = Warnings(switched, status == "warning"),
        };
    }
}
