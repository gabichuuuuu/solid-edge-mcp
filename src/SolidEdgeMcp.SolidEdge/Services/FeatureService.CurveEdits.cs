// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using Vertex = SolidEdgeGeometry.Vertex;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Isocline curves and split curves. Verified in Solid Edge 2026:
/// <list type="bullet">
/// <item>Constructions.IsoclineCurves.Add(n, faces, plane, side, angle) draws where the face normal makes the angle with the
/// plane: on a sphere of radius 20, 30° to XY gives circles of radius 17.3205 at z = -10 (igIsoclineleft) and z = 10
/// (igIsoclineRight, the side the plane normal points to). On a face whose edge is already that line it fails.</item>
/// <item>SplitCurves.Add(n, curves, n, tools) on the construction model of the curve splits an edge where reference planes or
/// crossing curve edges meet it (a curve 100 long split at x = 40: 40 + 60; one edge and two planes: 30 + 40 + 30). A tool
/// that misses the edge fails the whole split, and so does a vertex (E_INVALIDARG). Several edges in one call, one of them
/// not crossed, close Solid Edge, and so does the curve feature itself in the curves array (RPC_E_SERVERFAULT): each crossed
/// edge is split on its own, with only the tools that cross it. After a split the edges of the tool curves are stale (the
/// next split with them gives E_INVALIDARG), so each split takes them from their curves again.</item>
/// </list>
/// </summary>
public static partial class FeatureService
{
    /// <summary>Distance (m) under which a tool meets an edge, and away from its ends that a crossing must lie.</summary>
    private const double CrossingTolerance = 1e-6;

    /// <summary>Isocline: where the normal of the faces makes <paramref name="angle"/> degrees with the plane.</summary>
    public static object IsoclineCurve(SeContext context, string? documentHandle, string[]? faces, string? plane, double? angle, string direction)
    {
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("isocline needs 'faces' (se_geometry faces).");
        }

        double degrees = Units.RequireFinite(angle ?? throw ToolException.InvalidArgument("isocline requires 'angle': degrees between the face normal and the plane (0 = the silhouette)."), "angle");
        if (degrees < 0 || degrees >= 90)
        {
            throw ToolException.InvalidArgument("'angle' must be at least 0 and less than 90 degrees.");
        }

        IsoclineDirectionConstants side = direction switch
        {
            "normal" => IsoclineDirectionConstants.igIsoclineRight,
            "reverse" => IsoclineDirectionConstants.igIsoclineleft,
            _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}' for an isocline.",
                "Use normal (the face normal tilts towards the side the plane normal points to) or reverse."),
        };
        ModelDocument model = ModelDocument.For(context, documentHandle, faces[0]);
        Face[] resolved = faces.Select(h => context.Handles.Resolve<Face>(h, "faces")).ToArray();
        RefPlane refPlane = model.ResolvePlane(context, plane ?? throw ToolException.InvalidArgument("isocline requires 'plane': '1'-'3', 'xy', 'yz', 'xz' or a plane handle."));
        bool switched = model.EnsureOrdered();
        Array array = resolved.Cast<object>().ToArray();
        object curve = MakeCurve("isocline curve",
            () => ConstructionsOf(model).IsoclineCurves.Add(array.Length, ref array, refPlane, side, Units.DegToRad(degrees)),
            "The faces must reach that angle to the plane inside them (not only along their edges); try the other direction.");
        return CheckedCurve(context, model, curve, "isocline", switched, null);
    }

    /// <summary>
    /// Splits a curve where planes or crossing curves meet it; its length stays and it gets more edges. Each crossed edge gets its
    /// own split feature, with the tools that cross it.
    /// </summary>
    public static object SplitCurve(SeContext context, string? documentHandle, string? curve, string[]? tools)
    {
        string handle = curve ?? throw ToolException.InvalidArgument("split requires 'curve': a curve handle (action 'list').");
        if (tools is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("split needs 'tools': reference planes ('1'-'3', 'xy', 'yz', 'xz' or handles) or curves that cross the curve.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, handle);
        object target = context.Handles.Resolve(handle, "curve");
        Edge[] edges = target is Body or Face or Edge or Vertex ? [] : CurveEdges(target);
        if (edges.Length == 0 || FacesOf(target).Length > 0)
        {
            throw new ToolException("handle_wrong_type", $"'{handle}' is not a curve.", "Use a curve handle from se_curve list.");
        }

        (ConstructionModel owner, Body curveBody) = ConstructionOf(model, edges[0], handle, "curve");
        (object Source, object[] Items)[] toolSet = [.. tools.Select(h => SplitTool(context, model, h))];
        if (toolSet.SelectMany(t => t.Items).OfType<Edge>().Any(t => Late.TryGet(t, "Body") is Body b && b.Tag == curveBody.Tag))
        {
            throw ToolException.InvalidArgument("A curve cannot be split by its own edges.", "Pass reference planes or other curves that cross it.");
        }

        List<(double[] Point, object Tool)> crossings = [.. toolSet.SelectMany(t => t.Items.SelectMany(item => edges.SelectMany(e => Crossings(e, item))).Select(p => (p, t.Source)))];
        if (crossings.Count == 0)
        {
            throw ToolException.InvalidArgument("The tools do not cross the curve (away from its ends).",
                "Pass reference planes or curves that cross the curve; se_curve list shows where it runs.");
        }

        double length = edges.Sum(EdgeLength);
        int before = edges.Length;
        bool switched = model.EnsureOrdered();
        var made = new List<object>();
        const string hint = "The tools must cross the curve: reference planes or curves.";
        try
        {
            while (crossings.Count > 0)
            {
                double[] next = crossings[0].Point;
                Edge edge = CurveEdges(target).FirstOrDefault(e => HoldsInside(e, next))
                    ?? throw new ToolException("feature_failed", "The curve changed while it was split.", hint);
                List<(double[] Point, object Tool)> here = [.. crossings.Where(c => HoldsInside(edge, c.Point))];
                Array curveArray = new object[] { edge };
                Array toolArray = here.GroupBy(c => c.Tool, ReferenceEqualityComparer.Instance).SelectMany(g => ToolItemsAt(g.Key!, [.. g.Select(c => c.Point)])).ToArray();
                object split = MakeCurve("split", () => owner.SplitCurves.Add(1, ref curveArray, toolArray.Length, ref toolArray), hint);
                made.Add(split);
                context.ModelChanged(model.Document, "the model changed (curve split)");
                if (StatusOf(split) is not ("ok" or "warning") and var status)
                {
                    throw new ToolException("feature_failed", $"Solid Edge did not split the curve (status {status}).", hint);
                }

                crossings.RemoveAll(here.Contains);
            }

            Edge[] after = CurveEdges(target);
            if (after.Length <= before || Math.Abs(after.Sum(EdgeLength) - length) > 1e-6 * Math.Max(1, length))
            {
                throw new ToolException("feature_failed", $"Solid Edge did not split the curve ({after.Length} edges).", hint);
            }

            string type = AllCurves(model).FirstOrDefault(c => Late.SameObject(c.Curve, target)).Type ?? "curve";
            return new
            {
                features = made.Select(f => Describe(context, model, f)).ToArray(),
                curve = DescribeCurve(context, model, target, type),
                pieces = after.Select(e => Geo.Mm(EdgeLength(e))).ToArray(),
                warnings = Warnings(switched, made.Any(f => StatusOf(f) == "warning")),
            };
        }
        catch (Exception ex) when (made.Count > 0 && !ComErrors.IsGone(ex))
        {
            made.Reverse();
            made.ForEach(TryDeleteCurve);
            context.ModelChanged(model.Document, "the model changed (curve split deleted)");
            throw ex is ToolException failure
                ? new ToolException(failure.Code, $"{failure.Message} The split was deleted.", failure.NextStep, failure)
                : new ToolException("feature_failed", $"Solid Edge failed while splitting the curve ({ComErrors.Unwrap(ex).Message.Trim()}); the split was deleted.", hint, ex);
        }
    }

    /// <summary>
    /// A tool as given and what it splits with now: a reference plane; a curve and its edges; an edge of a curve, kept as its
    /// curve; another edge. The edges of a curve go stale once a split changes the model (the next split with one of them
    /// gives E_INVALIDARG), so each split takes them again from the curve (<see cref="ToolItemsAt"/>).
    /// </summary>
    private static (object Source, object[] Items) SplitTool(SeContext context, ModelDocument model, string handle)
    {
        if (OrientedPlanes.TryAxis(handle, out _) || int.TryParse(handle, out _))
        {
            RefPlane plane = model.ResolvePlane(context, handle, "tools");
            return (plane, [plane]);
        }

        object resolved = context.Handles.Resolve(handle, "tools");
        if (resolved is RefPlane)
        {
            return (resolved, [resolved]);
        }

        if (resolved is Edge edge)
        {
            int tag = Late.TryGet(edge, "Body") is Body body ? body.Tag : 0;
            object? curve = AllCurves(model).Select(c => c.Curve).FirstOrDefault(c => CurveEdges(c).FirstOrDefault() is { } first
                && Late.TryGet(first, "Body") is Body curveBody && curveBody.Tag == tag);
            return (curve ?? edge, [edge]);
        }

        Edge[] edges = resolved is Body or Face or Vertex || FacesOf(resolved).Length > 0 ? [] : CurveEdges(resolved);
        return edges.Length > 0
            ? (resolved, edges)
            : throw new ToolException("handle_wrong_type", $"'{handle}' in 'tools' is not a reference plane, a curve or an edge.",
                "Split a curve with reference planes or with curves that cross it.");
    }

    /// <summary>What a tool splits with now at the crossings: the plane or edge itself, or the edges of the curve through them.</summary>
    private static IEnumerable<object> ToolItemsAt(object tool, double[][] points)
    {
        if (tool is RefPlane or Edge)
        {
            return [tool];
        }

        Edge[] through = [.. CurveEdges(tool).Where(e => points.Any(p => OnEdge(e, p)))];
        return through.Length > 0 ? through : throw new ToolException("feature_failed", "A tool curve changed while the curve was split.",
            "Split the curve again with the tools.");
    }

    /// <summary>Whether the point (m) lies on the edge, its ends included.</summary>
    private static bool OnEdge(Edge edge, double[] point)
    {
        (double[] start, double[] end) = EdgeEnds(edge);
        return Geo.Distance(point, start) <= CrossingTolerance || Geo.Distance(point, end) <= CrossingTolerance || HoldsInside(edge, point);
    }

    /// <summary>Points (m) inside <paramref name="edge"/>, away from its ends, where a reference plane or another edge crosses it.</summary>
    private static List<double[]> Crossings(Edge edge, object tool)
    {
        const int Samples = 65;
        (double[] parameters, double[][] points) = EdgeSamples(edge, Samples);
        var found = new List<double[]>();
        if (tool is RefPlane plane)
        {
            (double[] root, double[] normal) = PlaneFrame(plane);
            double Side(double[] p) => Dot(Minus(p, root), normal);
            double[] side = [.. points.Select(Side)];
            for (int i = 0; i + 1 < Samples; i++)
            {
                if (Math.Abs(side[i]) > CrossingTolerance && Math.Abs(side[i + 1]) > CrossingTolerance && side[i] * side[i + 1] < 0)
                {
                    found.Add(PlaneCrossing(edge, parameters[i], parameters[i + 1], side[i], Side));
                }
                else if (i > 0 && Math.Abs(side[i]) <= CrossingTolerance && side[i - 1] * side[i + 1] < 0)
                {
                    found.Add(points[i]);
                }
            }
        }
        else if (tool is Edge other)
        {
            (double[] otherParameters, double[][] otherPoints) = EdgeSamples(other, Samples);
            for (int i = 0; i + 1 < Samples; i++)
            {
                for (int j = 0; j + 1 < Samples; j++)
                {
                    (double distance, double s, double t) = SegmentDistance(points[i], points[i + 1], otherPoints[j], otherPoints[j + 1]);
                    if (distance <= CrossingTolerance && Parallel(Minus(points[i + 1], points[i]), Minus(otherPoints[j + 1], otherPoints[j])))
                    {
                        throw ToolException.InvalidArgument("A tool runs along the curve instead of crossing it.",
                            "Pass reference planes or curves that cross the curve.");
                    }

                    double gap = 0.1 * Math.Max(Geo.Distance(points[i], points[i + 1]), Geo.Distance(otherPoints[j], otherPoints[j + 1]));
                    if (distance <= Math.Max(gap, CrossingTolerance)
                        && Meet(edge, parameters[i] + ((parameters[i + 1] - parameters[i]) * s), other,
                            otherParameters[j] + ((otherParameters[j + 1] - otherParameters[j]) * t)) is { } point)
                    {
                        found.Add(point);
                    }
                }
            }
        }

        (double[] start, double[] end) = EdgeEnds(edge);
        var inside = new List<double[]>();
        foreach (double[] point in found)
        {
            if (Geo.Distance(point, start) > CrossingTolerance && Geo.Distance(point, end) > CrossingTolerance
                && inside.All(q => Geo.Distance(point, q) > CrossingTolerance))
            {
                inside.Add(point);
            }
        }

        return inside;
    }

    /// <summary>
    /// Whether the point (m) lies on the edge, away from its ends. The point is mapped to the edge's parameter from the nearest
    /// of points along it; the parameter must fall inside the edge, since a line maps points beyond its ends too.
    /// </summary>
    private static bool HoldsInside(Edge edge, double[] point)
    {
        (double[] start, double[] end) = EdgeEnds(edge);
        if (Geo.Distance(point, start) <= CrossingTolerance || Geo.Distance(point, end) <= CrossingTolerance)
        {
            return false;
        }

        (double[] parameters, double[][] points) = EdgeSamples(edge, 65);
        int nearest = Enumerable.Range(0, points.Length).MinBy(i => Geo.Distance(points[i], point));
        Array input = point.ToArray();
        Array guess = new[] { parameters[nearest] };
        Array deviations = new double[1];
        Array mapped = new double[1];
        Array flags = new int[1];
        try
        {
            edge.GetParamAtPoint(1, ref input, ref guess, ref deviations, ref mapped, ref flags);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Solid Edge cannot map a point that is far from the edge: it is not on it.
            return false;
        }

        double parameter = Geo.Doubles(mapped).First();
        double low = Math.Min(parameters[0], parameters[^1]);
        double high = Math.Max(parameters[0], parameters[^1]);
        double slack = (high - low) * 1e-9;
        return parameter >= low - slack && parameter <= high + slack && Geo.Distance(PointAndDerivative(edge, parameter).Point, point) <= CrossingTolerance;
    }

    private static (double[] Start, double[] End) EdgeEnds(Edge edge)
    {
        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        return ([.. Geo.Doubles(start)], [.. Geo.Doubles(end)]);
    }

    /// <summary><paramref name="count"/> points (m) evenly spaced in the edge's parameter, with their parameters.</summary>
    private static (double[] Parameters, double[][] Points) EdgeSamples(Edge edge, int count)
    {
        edge.GetParamExtents(out double low, out double high);
        double[] parameters = [.. Enumerable.Range(0, count).Select(i => low + ((high - low) * i / (count - 1)))];
        Array input = parameters.ToArray();
        Array output = new double[count * 3];
        edge.GetPointAtParam(count, ref input, ref output);
        double[] xyz = [.. Geo.Doubles(output)];
        return (parameters, [.. Enumerable.Range(0, count).Select(i => xyz[(i * 3)..((i * 3) + 3)])]);
    }

    private static (double[] Point, double[] Derivative) PointAndDerivative(Edge edge, double parameter)
    {
        Array parameters = new[] { parameter };
        Array point = new double[3];
        edge.GetPointAtParam(1, ref parameters, ref point);
        Array first = new double[3];
        Array second = new double[3];
        Array third = new double[3];
        edge.GetDerivatives(1, ref parameters, ref first, ref second, ref third);
        return ([.. Geo.Doubles(point)], [.. Geo.Doubles(first)]);
    }

    /// <summary>Where the edge crosses the plane between two parameters on either side of it, by bisection.</summary>
    private static double[] PlaneCrossing(Edge edge, double low, double high, double lowSide, Func<double[], double> side)
    {
        double[] point = PointAndDerivative(edge, low).Point;
        for (int k = 0; k < 60; k++)
        {
            double middle = (low + high) / 2;
            point = PointAndDerivative(edge, middle).Point;
            double s = side(point);
            if (Math.Abs(s) <= 1e-10)
            {
                break;
            }

            if (s * lowSide > 0)
            {
                low = middle;
                lowSide = s;
            }
            else
            {
                high = middle;
            }
        }

        return point;
    }

    /// <summary>Where two edges meet, by Newton's method from parameters near it; null if they only pass close.</summary>
    private static double[]? Meet(Edge edge, double t, Edge other, double u)
    {
        edge.GetParamExtents(out double t0, out double t1);
        other.GetParamExtents(out double u0, out double u1);
        for (int k = 0; k < 30; k++)
        {
            (double[] p, double[] dp) = PointAndDerivative(edge, t);
            (double[] q, double[] dq) = PointAndDerivative(other, u);
            double[] r = Minus(p, q);
            if (Math.Sqrt(Dot(r, r)) <= 1e-10)
            {
                break;
            }

            // Least squares step of p(t + dt) - q(u + du) = 0 with the first derivatives.
            double a = Dot(dp, dp);
            double b = -Dot(dp, dq);
            double c = Dot(dq, dq);
            double e = -Dot(dp, r);
            double f = Dot(dq, r);
            double determinant = (a * c) - (b * b);
            if (Math.Abs(determinant) <= 1e-30)
            {
                break;
            }

            t = Math.Clamp(t + (((e * c) - (b * f)) / determinant), Math.Min(t0, t1), Math.Max(t0, t1));
            u = Math.Clamp(u + (((a * f) - (b * e)) / determinant), Math.Min(u0, u1), Math.Max(u0, u1));
        }

        (double[] onEdge, double[] along) = PointAndDerivative(edge, t);
        (double[] onOther, double[] otherAlong) = PointAndDerivative(other, u);

        // Edges that only touch (tangent at the point) do not cross: there is no side to split at.
        return Geo.Distance(onEdge, onOther) <= CrossingTolerance && !Parallel(along, otherAlong) ? onEdge : null;
    }

    /// <summary>Whether two directions are parallel (or one of them is zero).</summary>
    private static bool Parallel(double[] a, double[] b) => Geo.Length(Geo.Cross(a, b)) <= 1e-6 * Geo.Length(a) * Geo.Length(b);

    /// <summary>Closest points of two segments: their distance and the fractions along each (0 to 1).</summary>
    private static (double Distance, double S, double T) SegmentDistance(double[] p0, double[] p1, double[] q0, double[] q1)
    {
        double[] d1 = Minus(p1, p0);
        double[] d2 = Minus(q1, q0);
        double[] r = Minus(p0, q0);
        double a = Dot(d1, d1);
        double e = Dot(d2, d2);
        double f = Dot(d2, r);
        double s;
        double t;
        if (a <= 1e-30 && e <= 1e-30)
        {
            (s, t) = (0, 0);
        }
        else if (a <= 1e-30)
        {
            (s, t) = (0, Math.Clamp(f / e, 0, 1));
        }
        else
        {
            double c = Dot(d1, r);
            if (e <= 1e-30)
            {
                (s, t) = (Math.Clamp(-c / a, 0, 1), 0);
            }
            else
            {
                double b = Dot(d1, d2);
                double denominator = (a * e) - (b * b);
                s = denominator > 1e-30 ? Math.Clamp(((b * f) - (c * e)) / denominator, 0, 1) : 0;
                t = ((b * s) + f) / e;
                if (t < 0)
                {
                    (s, t) = (Math.Clamp(-c / a, 0, 1), 0);
                }
                else if (t > 1)
                {
                    (s, t) = (Math.Clamp((b - c) / a, 0, 1), 1);
                }
            }
        }

        double[] onP = [p0[0] + (d1[0] * s), p0[1] + (d1[1] * s), p0[2] + (d1[2] * s)];
        double[] onQ = [q0[0] + (d2[0] * t), q0[1] + (d2[1] * t), q0[2] + (d2[2] * t)];
        return (Geo.Distance(onP, onQ), s, t);
    }
}
