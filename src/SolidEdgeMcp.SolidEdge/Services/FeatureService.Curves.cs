// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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
    private static readonly string[] CurveTypes = ["points", "intersection", "projected", "cross", "derived", "contour", "isocline"];

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

    /// <summary>
    /// Curve drawn on faces through points that lie on them (model mm); open, or closed back to the first point. It must pass
    /// through every point and, if open, end at the first and last. Solid Edge rejects a contour curve on several faces (E_FAIL,
    /// even tangent ones), so across faces it makes one contour curve per face, from point to point, and joins them into one
    /// composite derived curve; consecutive points must share a face, so a point on the edge between two faces marks the crossing.
    /// </summary>
    public static object ContourCurve(SeContext context, string? documentHandle, string[]? faces, double[]? points, bool closed)
    {
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("contour needs 'faces': the faces the curve is drawn on (se_geometry faces).");
        }

        List<(double X, double Y, double Z)> list = Geo.Points3(points, "points", closed ? 3 : 2);
        ModelDocument model = ModelDocument.For(context, documentHandle, faces[0]);
        Face[] faceList = faces.Select(h => context.Handles.Resolve<Face>(h, "faces")).ToArray();
        double[][] nodes = list.Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
        // The faces each point lies on.
        bool[][] on = nodes.Select(p => faceList.Select(f => DistanceToFace(f, p) <= 1e-5).ToArray()).ToArray();
        int far = Array.FindIndex(on, o => !o.Contains(true));
        if (far >= 0)
        {
            double distance = faceList.Min(f => DistanceToFace(f, nodes[far]));
            throw ToolException.InvalidArgument($"Point {far + 1} is {Geo.Mm(distance)} mm away from the faces; the points must lie on them.");
        }

        // Runs of consecutive points on one face, in order along the curve (closed: back to the first point).
        int count = closed ? nodes.Length + 1 : nodes.Length;
        var runs = new List<(int Face, List<int> Points)>();
        for (int i = 0; i + 1 < count; i++)
        {
            int a = i, b = (i + 1) % nodes.Length;
            int[] common = [.. Enumerable.Range(0, faceList.Length).Where(k => on[a][k] && on[b][k])];
            if (common.Length == 0)
            {
                throw ToolException.InvalidArgument($"Points {a + 1} and {b + 1} do not lie on the same face.",
                    "Where the curve passes to another face, add a point on the edge between the two faces.");
            }

            int face = runs.Count > 0 && common.Contains(runs[^1].Face) ? runs[^1].Face : common[0];
            if (runs.Count == 0 || runs[^1].Face != face)
            {
                runs.Add((face, [a]));
            }

            runs[^1].Points.Add(b);
        }

        bool switched = model.EnsureOrdered();
        var errors = new List<string>();
        if (runs.Count == 1)
        {
            object curve = Contour(model, faceList[runs[0].Face], nodes, closed, errors) ?? throw ContourFailed(context, model, errors);
            return CheckedCurve(context, model, curve, "contour", switched, null);
        }

        var parts = new List<object>();
        try
        {
            foreach ((int face, List<int> run) in runs)
            {
                parts.Add(Contour(model, faceList[face], [.. run.Select(k => nodes[k])], false, errors) ?? throw ContourFailed(context, model, errors));
            }

            Array set = parts.SelectMany(CurveEdges).Cast<object>().ToArray();
            double expected = parts.Sum(p => CurveEdges(p).Sum(EdgeLength));
            object joined = MakeCurve("contour curve across the faces",
                () => ConstructionsOf(model).DerivedCurves.Add(set.Length, ref set, DerivedCurveTypeConstants.igDCComposite),
                "Give the points in order along the curve.");
            object result = CheckedCurve(context, model, joined, "contour", switched, c =>
                Math.Abs(CurveEdges(c).Sum(EdgeLength) - expected) > 1e-6 * Math.Max(1, expected)
                    ? "its length differs from the curves on each face" : ContourProblem(c, nodes, closed));
            // The curves on each face stay as the parents of the joined curve, hidden.
            foreach (object part in parts)
            {
                try
                {
                    Late.Set(part, "Visible", false);
                }
                catch (Exception ex) when (Late.IsMemberFailure(ex))
                {
                    // Only their display.
                }
            }

            return result;
        }
        catch
        {
            parts.ForEach(TryDeleteCurve);
            context.ModelChanged(model.Document, "the model changed (contour curve attempts)");
            throw;
        }
    }

    /// <summary>A contour curve on one face through the points (model m), checked; null if Solid Edge does not make it.</summary>
    private static object? Contour(ModelDocument model, Face face, double[][] nodes, bool closed, List<string> errors)
    {
        foreach (bool spareAtEnd in new[] { true, false })
        {
            object? curve = MakeContour(model, face, nodes, closed, spareAtEnd, errors);
            if (curve is null)
            {
                continue;
            }

            string? problem = StatusOf(curve) is not ("ok" or "warning") ? $"status {StatusOf(curve)}" : ContourProblem(curve, nodes, closed);
            if (problem is null)
            {
                return curve;
            }

            errors.Add(problem);
            TryDeleteCurve(curve);
        }

        return null;
    }

    private static ToolException ContourFailed(SeContext context, ModelDocument model, List<string> errors)
    {
        context.ModelChanged(model.Document, "the model changed (contour curve attempts)");
        return new ToolException("feature_failed", "Solid Edge did not make the contour curve: " + string.Join("; ", errors.Distinct()),
            "Give points inside the faces, in order along the curve; a closed curve on one face must not leave it.");
    }

    /// <summary>
    /// ContourCurves.Add reads point k of PointSet at index k instead of 3k (point k = PointSet[k..k+2]), so only one point can be
    /// given there. The curve is made with that point (the first, or the last when spareAtEnd is false) and spare ones, the other
    /// points are inserted with InsertPoint (1-based, before a given point) and the spare points are removed. Null if it fails.
    /// </summary>
    private static object? MakeContour(ModelDocument model, Face face, double[][] nodes, bool closed, bool spareAtEnd, List<string> errors)
    {
        int seeds = closed ? 3 : 2;
        int n = nodes.Length;
        double[] given = spareAtEnd ? nodes[0] : nodes[^1];
        int at = spareAtEnd ? 0 : seeds - 1;
        // The spare points are read from the same values: the filler is chosen so that no two seed points coincide.
        double[] values = new[] { given[2], given[0], given[1], 0, 0.01 }.Select(fill =>
        {
            var v = new double[3 * seeds];
            Array.Fill(v, fill);
            Array.Copy(given, 0, v, at, 3);
            return v;
        }).First(v => Enumerable.Range(0, seeds).All(i => Enumerable.Range(i + 1, seeds - i - 1)
            .All(j => Geo.Distance(v[i..(i + 3)], v[j..(j + 3)]) > 1e-6)));
        Array faceArray = new[] { face };
        Array pointArray = values;
        Array ownerArray = Enumerable.Repeat((object)face, seeds).ToArray();
        ContourCurve curve;
        try
        {
            curve = ConstructionsOf(model).ContourCurves.Add(faceArray.Length, ref faceArray, seeds, ref pointArray, ref ownerArray, !closed);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            errors.Add("rejected: " + ComErrors.Unwrap(ex).Message.Trim());
            return null;
        }

        try
        {
            // Spare at the end: P0, spare... -> P0..Pn-1, spare...; at the start: spare..., Pn-1 -> spare..., P0..Pn-1.
            for (int i = 0; i < n - 1; i++)
            {
                int node = spareAtEnd ? i + 1 : i;
                Array point = nodes[node].ToArray();
                curve.InsertPoint(face, ref point, spareAtEnd ? i + 2 : seeds + i);
            }

            for (int k = 0; k < seeds - 1; k++)
            {
                curve.RemovePoint(spareAtEnd ? n + 1 : 1);
            }

            return curve;
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            errors.Add("points not accepted: " + ComErrors.Unwrap(ex).Message.Trim());
            TryDeleteCurve(curve);
            return null;
        }
    }

    /// <summary>Why the contour curve is wrong: a point off the curve, or (open) ends that are not the first and last points.</summary>
    private static string? ContourProblem(object curve, double[][] nodes, bool closed)
    {
        Edge[] edges = CurveEdges(curve);
        if (edges.Length == 0)
        {
            return "it has no edges";
        }

        int off = Array.FindIndex(nodes, p => edges.Min(e => DistanceToEdge(e, p)) > 1e-5);
        if (off >= 0)
        {
            return $"it does not pass through point {off + 1}";
        }

        double[][] ends = CurveEnds(curve);
        return closed ? (ends.Length == 0 ? null : $"it is not closed: it leaves the faces, ends at {string.Join(" and ", ends.Select(e => "(" + string.Join(", ", e.Select(Geo.Mm)) + ")"))} mm")
            : ends.Any(e => Geo.Distance(e, nodes[0]) < 1e-6) && ends.Any(e => Geo.Distance(e, nodes[^1]) < 1e-6) ? null
            : "it does not end at the first and last points";
    }

    private static void TryDeleteCurve(object curve)
    {
        try
        {
            Late.Call(curve, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The error being reported is the failed curve.
        }
    }

    /// <summary>A sketch wrapped onto cylindrical or conical faces (tangent to them); its length is checked against the sketch's.</summary>
    public static object WrapCurve(SeContext context, string? documentHandle, string? sketch, string[]? faces)
    {
        string handle = sketch ?? throw ToolException.InvalidArgument("wrap requires 'sketch': drawn on a plane tangent to the faces and closed with se_sketch 'close' (open: true for open elements).");
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("wrap needs 'faces': the cylindrical or conical faces to wrap onto (se_geometry faces).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, handle);
        Profile profile = SketchService.Resolve(context, handle);
        double expected = SketchLength(profile);
        bool switched = model.EnsureOrdered();
        Array faceArray = faces.Select(h => (object)context.Handles.Resolve<Face>(h, "faces")).ToArray();
        Array tools = new object[] { profile };
        object curve = MakeCurve("wrapped sketch", () => ConstructionsOf(model).WrapSketchs.Add(faceArray.Length, ref faceArray, 1, ref tools),
            "Draw the sketch on a plane tangent to the faces.");
        object result = CheckedCurve(context, model, curve, "wrap", switched, c =>
        {
            double length = CurveEdges(c).Sum(EdgeLength);
            return Math.Abs(length - expected) > Math.Max(1e-6, expected * 1e-3)
                ? $"its length is {Geo.Mm(length)} mm instead of the sketch's {Geo.Mm(expected)} mm"
                : null;
        });
        Hide(profile);
        return result;
    }

    /// <summary>Distance (m) from a model point to a face: the point is mapped to the face's parameters and back.</summary>
    private static double DistanceToFace(Face face, double[] point)
    {
        try
        {
            Array points = point.ToArray();
            Array guess = new double[2];
            Array deviations = new double[1];
            Array parameters = new double[2];
            Array flags = new int[1];
            face.GetParamAtPoint(1, ref points, ref guess, ref deviations, ref parameters, ref flags);
            Array back = new double[3];
            face.GetPointAtParam(1, ref parameters, ref back);
            return Geo.Distance(point, [.. Geo.Doubles(back)]);
        }
        catch (COMException)
        {
            return double.MaxValue;
        }
    }

    /// <summary>
    /// Distance (m) from a model point to an edge, mapped to the edge's parameter and back; several starting parameters, since
    /// on a winding spline the mapping can stop at a local minimum.
    /// </summary>
    internal static double DistanceToEdge(Edge edge, double[] point)
    {
        try
        {
            edge.GetParamExtents(out double low, out double high);
            return Enumerable.Range(0, 9).Min(k =>
            {
                Array points = point.ToArray();
                Array guess = new[] { low + (high - low) * k / 8 };
                Array deviations = new double[1];
                Array parameters = new double[1];
                Array flags = new int[1];
                edge.GetParamAtPoint(1, ref points, ref guess, ref deviations, ref parameters, ref flags);
                Array back = new double[3];
                edge.GetPointAtParam(1, ref parameters, ref back);
                return Geo.Distance(point, [.. Geo.Doubles(back)]);
            });
        }
        catch (COMException)
        {
            return double.MaxValue;
        }
    }

    /// <summary>Total length (m) of the elements of a sketch.</summary>
    private static double SketchLength(Profile profile) =>
        new object[] { profile.Lines2d, profile.Arcs2d, profile.Circles2d, profile.Ellipses2d, profile.BSplineCurves2d }
            .SelectMany(c => Late.Items(c))
            .Sum(e => Late.TryGet(e, "Length") is double length ? length : 0);

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
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw ComErrors.IsObjectRemoved(com)
                ? new ToolException("feature_failed", $"Solid Edge could not build the {what} and removed it.", hint, ex)
                : new ToolException("feature_failed", $"Solid Edge rejected the {what}: {com.Message.Trim()}", hint, ex);
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
            TryDeleteCurve(curve);
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
            constructions.CrossCurves, constructions.DerivedCurves, constructions.ContourCurves, constructions.IsoclineCurves];
        // WrapSketchs.Count stays 0 after Add: the wrapped sketches are taken from the feature tree.
        IEnumerable<object> wraps = Late.Items(model.EdgebarFeatures).Where(f => f is WrapSketch);
        return collections.SelectMany((c, i) => Late.Items(c).Select(s => (s, CurveTypes[i]))).Concat(wraps.Select(w => (w, "wrap")));
    }

    private static object? FindCurve(ModelDocument model, string name) =>
        AllCurves(model).Select(c => c.Curve).FirstOrDefault(c => string.Equals(Late.TryGetString(c, "Name"), name, StringComparison.OrdinalIgnoreCase));
}
