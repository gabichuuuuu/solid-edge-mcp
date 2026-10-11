using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Construction curves of a part (they do not change the solid): through 3D points, where two sets of faces meet, a sketch
/// projected onto faces, where two sketches cross, and composite curves from edges. Each one is checked by its status and the
/// edges it makes, and deleted if it fails. Verified: through (0,0,30) and (100,60,30) a straight curve of 116.619 mm; a line
/// x→z/2 on XZ and y→5z/6 on YZ cross from the origin to (100,60,50); a surface at z=10 meets the box 100×60 in a loop of 320.
/// The intersection takes one face, a Faces collection or the body on each side (an array gives E_NOINTERFACE); the projection
/// takes the Sketch object (the parent of the profile), not the profile.
/// </summary>
public static partial class FeatureService
{
    private static readonly string[] CurveTypes = ["points", "intersection", "projected", "cross", "derived"];

    public static object Curves(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object[] curves = AllCurves(model).Select(c => DescribeCurve(context, model, c.Curve, c.Type)).ToArray();
        return new { document = context.DocumentHandle(model.Document), count = curves.Length, curves };
    }

    /// <summary>Smooth curve through model points [x1,y1,z1,...] in mm; closed = periodic (it returns to the first point).</summary>
    public static object CurveThroughPoints(SeContext context, string? documentHandle, double[]? points, bool closed)
    {
        if (points is not { Length: >= 6 } || points.Length % 3 != 0)
        {
            throw ToolException.InvalidArgument("'points' must give at least two points [x1,y1,z1,x2,y2,z2,...] in mm.");
        }

        double[] metres = points.Select(v => Units.MmToM(Units.RequireFinite(v, "points"))).ToArray();
        int n = metres.Length / 3;
        if (closed && n < 3)
        {
            throw ToolException.InvalidArgument("A closed curve needs at least three points.");
        }

        for (int i = 1; i < n; i++)
        {
            if (Distance(metres, i - 1, i) < 1e-7)
            {
                throw ToolException.InvalidArgument($"Points {i} and {i + 1} are the same.");
            }
        }

        ModelDocument model = ModelDocument.Get(context, documentHandle);
        bool switched = model.EnsureOrdered();
        Array pointTypes = Enumerable.Repeat((int)PointTypeConstants.igSpacePoint, n).ToArray();
        Array edges = new object?[n];
        Array keyTypes = new int[n];
        Array positions = metres;
        KeypointEndConditionConstants end = closed
            ? KeypointEndConditionConstants.seKeypointEndConditionPeriodic
            : KeypointEndConditionConstants.seKeypointEndConditionNatural;
        object curve = MakeCurve("curve through points",
            () => ConstructionsOf(model).KeyPointCurves.AddEx(n, ref pointTypes, ref edges, ref keyTypes, ref positions, end, end),
            "Check that the points are distinct and not all on one spot.");
        return CheckedCurve(context, model, curve, "points", switched, c =>
        {
            // An open curve starts and ends at the first and last points.
            if (closed)
            {
                return null;
            }

            double[][] ends = CurveEnds(c);
            double[] first = [metres[0], metres[1], metres[2]];
            double[] last = [metres[^3], metres[^2], metres[^1]];
            return ends.Any(e => Geo.Distance(e, first) < 1e-6) && ends.Any(e => Geo.Distance(e, last) < 1e-6)
                ? null
                : "it does not end at the first and last points";
        });
    }

    /// <summary>Curve where two sets meet; each set is a face, a surface or feature (its faces) or 'body'.</summary>
    public static object IntersectionCurve(SeContext context, string? documentHandle, string? first, string? second)
    {
        string one = first ?? throw ToolException.InvalidArgument("intersection requires 'first' and 'second': a face, surface or feature handle, or 'body'.");
        string two = second ?? throw ToolException.InvalidArgument("intersection requires 'second': a face, surface or feature handle, or 'body'.");
        ModelDocument model = string.Equals(one, "body", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(two, "body", StringComparison.OrdinalIgnoreCase)
                ? throw ToolException.InvalidArgument("'first' and 'second' cannot both be the body.")
                : ModelDocument.For(context, documentHandle, two)
            : ModelDocument.For(context, documentHandle, one);
        object set1 = FaceSet(context, model, one, "first");
        object set2 = FaceSet(context, model, two, "second");
        bool switched = model.EnsureOrdered();
        object curve = MakeCurve("intersection curve", () => ConstructionsOf(model).IntersectionCurves.Add(set1, set2),
            "Give two sets that cross: a face, a surface or feature handle, or 'body'.");
        return CheckedCurve(context, model, curve, "intersection", switched, null);
    }

    /// <summary>Sketch projected along its plane normal (normal) or against it (reverse) onto a face, surface or the body.</summary>
    public static object ProjectedCurve(SeContext context, string? documentHandle, string? sketch, string? onto, string direction)
    {
        string handle = sketch ?? throw ToolException.InvalidArgument("project requires 'sketch' (closed with se_sketch 'close', open: true for an open chain).");
        ModelDocument model = ModelDocument.For(context, documentHandle, handle);
        Profile profile = SketchService.Resolve(context, handle);
        object target = FaceSet(context, model, onto ?? throw ToolException.InvalidArgument("project requires 'onto': the face, surface or feature handle, or 'body', to project onto."), "onto");
        FeaturePropertyConstants side = direction switch
        {
            "normal" => FeaturePropertyConstants.igRight,
            "reverse" => FeaturePropertyConstants.igLeft,
            _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}' for a projection.", "Use normal or reverse."),
        };
        object plane = Late.Get(profile, "Plane") ?? throw new ToolException("sketch_not_usable", "The sketch has no plane.", "Use a sketch made with se_sketch create.");
        object sketchObject = Late.Get(profile, "Parent") ?? throw new ToolException("sketch_not_usable", "The sketch has no parent sketch.", "Use a sketch made with se_sketch create.");
        bool switched = model.EnsureOrdered();
        object curve = MakeCurve("projected curve",
            () => ConstructionsOf(model).ProjectCurves.Add(sketchObject, target, plane, side, FeaturePropertyConstants.igProjectOptionProject),
            "Check that the target lies along the sketch normal ('direction' normal or reverse).");
        object result = CheckedCurve(context, model, curve, "projected", switched, null);
        Hide(profile);
        return result;
    }

    /// <summary>3D curve where two sketches on different planes cross (each extruded along its own normal).</summary>
    public static object CrossCurve(SeContext context, string? documentHandle, string[]? sketches)
    {
        if (sketches is not [string a, string b])
        {
            throw ToolException.InvalidArgument("cross takes two sketches in 'sketches', on different planes.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, a);
        Profile first = SketchService.Resolve(context, a);
        Profile second = SketchService.Resolve(context, b);
        bool switched = model.EnsureOrdered();
        Array set1 = new object[] { first };
        Array set2 = new object[] { second };
        object curve = MakeCurve("cross curve", () => ConstructionsOf(model).CrossCurves.Add(1, ref set1, 1, ref set2),
            "Use two sketches on planes that are not parallel, whose extrusions meet.");
        object result = CheckedCurve(context, model, curve, "cross", switched, null);
        Hide(first);
        Hide(second);
        return result;
    }

    /// <summary>One curve made of a chain of edges; its length is the sum of theirs.</summary>
    public static object DerivedCurve(SeContext context, string? documentHandle, string[]? edges)
    {
        (ModelDocument model, object[] resolved) = ResolveEdges(context, documentHandle, edges);
        double expected = resolved.Cast<Edge>().Sum(EdgeLength);
        bool switched = model.EnsureOrdered();
        Array set = resolved;
        object curve = MakeCurve("derived curve", () => ConstructionsOf(model).DerivedCurves.Add(set.Length, ref set, DerivedCurveTypeConstants.igDCComposite),
            "Give a connected chain of edges (se_geometry edges).");
        return CheckedCurve(context, model, curve, "derived", switched,
            c => Math.Abs(CurveEdges(c).Sum(EdgeLength) - expected) <= 1e-6 * Math.Max(1, expected) ? null : "its length differs from the edges'");
    }

    public static object DeleteCurve(SeContext context, string? documentHandle, string? name)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object curve = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the curve name (action 'list').")
            : FindCurve(model, name) ?? throw ToolException.InvalidArgument($"The document has no curve '{name}'.", "List them with action 'list'.");
        Late.Call(curve, "Delete");
        context.ModelChanged(model.Document, "a curve was deleted");
        return FindCurve(model, name) is null
            ? new { deleted = name, count = AllCurves(model).Count() }
            : throw new ToolException("feature_failed", $"Solid Edge did not delete the curve '{name}'.", "Check it with action 'list'.");
    }

    /// <summary>Resolves a curve handle to its edges, e.g. for a sweep path.</summary>
    public static Edge[] CurveEdges(object curve)
    {
        try
        {
            return Late.Get(curve, "Edges", TopologyQuery.igQueryAll) is { } edges ? [.. Late.Items(edges).OfType<Edge>()] : [];
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return [];
        }
    }

    private static object MakeCurve(string what, Func<object> create, string hint)
    {
        try
        {
            return create();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsDisconnected(com))
        {
            throw new ToolException("feature_failed", $"Solid Edge rejected the {what}: {com.Message.Trim()}", hint, ex);
        }
    }

    private static object CheckedCurve(SeContext context, ModelDocument model, object curve, string type, bool switched, Func<object, string?>? check)
    {
        context.ModelChanged(model.Document, "the model changed (new curve)");
        string? status = StatusOf(curve);
        Edge[] edges = CurveEdges(curve);
        string? problem = status is not ("ok" or "warning") ? $"status {status}"
            : edges.Length == 0 || edges.Sum(EdgeLength) <= 0 ? "it has no edges"
            : check?.Invoke(curve);
        if (problem is not null)
        {
            try
            {
                Late.Call(curve, "Delete");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                // The error being reported is the failed curve.
            }

            throw new ToolException("feature_failed", $"Solid Edge did not make the {type} curve ({problem}); it was deleted.",
                "Check the input: the sets must cross, the projection must hit the target, the edges must form a chain.");
        }

        return new { curve = DescribeCurve(context, model, curve, type), warnings = Warnings(switched, status == "warning") };
    }

    private static object DescribeCurve(SeContext context, ModelDocument model, object curve, string type)
    {
        Edge[] edges = CurveEdges(curve);
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (Edge edge in edges)
        {
            Array min = new double[3];
            Array max = new double[3];
            edge.GetRange(ref min, ref max);
            double[] a = [.. Geo.Doubles(min)];
            double[] b = [.. Geo.Doubles(max)];
            for (int k = 0; k < 3; k++)
            {
                lo[k] = Math.Min(lo[k], a[k]);
                hi[k] = Math.Max(hi[k], b[k]);
            }
        }

        double[][] ends = edges.Length == 0 ? [] : CurveEnds(curve);
        return new
        {
            handle = context.Register(curve, "curve", model.Document),
            name = Late.TryGetString(curve, "Name"),
            type,
            status = StatusOf(curve),
            edges = edges.Length,
            length = Geo.Mm(edges.Sum(EdgeLength)),
            closed = edges.Length > 0 && ends.Length == 0,
            ends = ends.Select(e => e.Select(Geo.Mm).ToArray()).ToArray(),
            boundingBox = edges.Length == 0 ? null : new { min = lo.Select(Geo.Mm).ToArray(), max = hi.Select(Geo.Mm).ToArray() },
        };
    }

    /// <summary>End points of the chain (points used by one edge only); none if the curve is closed.</summary>
    private static double[][] CurveEnds(object curve)
    {
        var points = new List<double[]>();
        foreach (Edge edge in CurveEdges(curve))
        {
            Array start = new double[3];
            Array end = new double[3];
            edge.GetEndPoints(ref start, ref end);
            points.Add([.. Geo.Doubles(start)]);
            points.Add([.. Geo.Doubles(end)]);
        }

        return points.Where(p => points.Count(q => Geo.Distance(p, q) < 1e-7) == 1).ToArray();
    }

    private static double Distance(double[] points, int i, int j) =>
        Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow(points[3 * i + k] - points[3 * j + k], 2)));

    /// <summary>A face, the faces of a surface or feature (as the Faces collection Solid Edge expects), or the body.</summary>
    private static object FaceSet(SeContext context, ModelDocument model, string handle, string name)
    {
        if (string.Equals(handle, "body", StringComparison.OrdinalIgnoreCase))
        {
            return (Body)model.RequireModel().Body;
        }

        object resolved = context.Handles.Resolve(handle, name);
        if (resolved is Face or Body)
        {
            return resolved;
        }

        object? faces = null;
        try
        {
            faces = Late.Get(resolved, "Faces", TopologyQuery.igQueryAll);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Reported below.
        }

        return faces is not null && Late.Items(faces).Any()
            ? faces
            : throw new ToolException("handle_wrong_type", $"'{handle}' is not a face, a surface or feature with faces, or the body.",
                $"Pass in '{name}' a face handle (se_geometry faces), a surface (se_surface) or feature handle, or 'body'.");
    }

    private static IEnumerable<(object Curve, string Type)> AllCurves(ModelDocument model)
    {
        Constructions constructions = ConstructionsOf(model);
        object[] collections = [constructions.KeyPointCurves, constructions.IntersectionCurves, constructions.ProjectCurves,
            constructions.CrossCurves, constructions.DerivedCurves];
        return collections.SelectMany((c, i) => Late.Items(c).Select(s => (s, CurveTypes[i])));
    }

    private static object? FindCurve(ModelDocument model, string name) =>
        AllCurves(model).Select(c => c.Curve).FirstOrDefault(c => string.Equals(Late.TryGetString(c, "Name"), name, StringComparison.OrdinalIgnoreCase));
}
