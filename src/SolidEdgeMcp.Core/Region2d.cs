// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

namespace SolidEdgeMcp.Core;

/// <summary>
/// Plane geometry of closed regions, in metres. Solid Edge 2026 looks for the region round a point among the given elements
/// (Boundaries2d.AddByObjects, AreaPropertiesCollection.Add) and fails with RPC_E_SERVERFAULT when the point is outside every
/// closed region, so the point is first proved to be inside a closed loop of those elements.
/// </summary>
public static class Region2d
{
    /// <summary>
    /// A 2D element as a polyline: an open curve from its start to its end point, or a closed curve (circle, ellipse, closed
    /// spline) whose last point joins the first.
    /// </summary>
    public sealed record Curve((double X, double Y)[] Points, bool Closed);

    public enum Place
    {
        Inside,
        Outside,
        OnElement,
        NoLoop,
    }

    /// <summary>
    /// Where the point is with respect to the closed loops of the curves: closed curves and chains of open curves whose ends
    /// meet (closer than <paramref name="join"/>). Inside is a proof: the point has an odd number of crossings with one loop,
    /// so it cannot reach the outside without crossing an element. A point closer than <paramref name="margin"/> to an
    /// element is OnElement.
    /// </summary>
    public static Place Locate(IReadOnlyList<Curve> curves, (double X, double Y) point, double join, double margin)
    {
        if (curves.Any(c => Distance(c, point) < margin))
        {
            return Place.OnElement;
        }

        List<int[]> loops = Loops(curves, join);
        if (loops.Count == 0)
        {
            return Place.NoLoop;
        }

        (double X, double Y) ray = RayDirection(curves, point);
        bool[] odd = [.. curves.Select(c => Crossings(c, point, ray) % 2 == 1)];
        return loops.Any(loop => loop.Aggregate(false, (parity, i) => parity ^ odd[i])) ? Place.Inside : Place.Outside;
    }

    /// <summary>
    /// Closed loops of the curves as lists of curve indices: each closed curve, and one fundamental cycle of the graph of the
    /// open curves (nodes = their ends, merged when closer than <paramref name="join"/>) for each curve outside a spanning
    /// forest. Every closed loop of the curves is a combination of these, so a point inside any loop is inside one of them.
    /// </summary>
    public static List<int[]> Loops(IReadOnlyList<Curve> curves, double join)
    {
        var loops = new List<int[]>();
        var nodes = new List<(double X, double Y)>();
        var edges = new List<(int Curve, int A, int B)>();
        for (int i = 0; i < curves.Count; i++)
        {
            if (curves[i].Closed)
            {
                loops.Add([i]);
            }
            else if (curves[i].Points.Length >= 2)
            {
                edges.Add((i, Node(nodes, curves[i].Points[0], join), Node(nodes, curves[i].Points[^1], join)));
            }
        }

        // Spanning forest by union-find; the other edges close a cycle each.
        int[] root = [.. Enumerable.Range(0, nodes.Count)];
        int Find(int n) => root[n] == n ? n : root[n] = Find(root[n]);
        var tree = new List<(int Curve, int A, int B)>();
        var closing = new List<(int Curve, int A, int B)>();
        foreach ((int Curve, int A, int B) edge in edges)
        {
            int a = Find(edge.A), b = Find(edge.B);
            if (a == b)
            {
                closing.Add(edge);
            }
            else
            {
                root[a] = b;
                tree.Add(edge);
            }
        }

        // Parent of each node in the forest, with the curve that joins them and its depth.
        var adjacent = new List<(int Node, int Curve)>[nodes.Count];
        for (int n = 0; n < nodes.Count; n++)
        {
            adjacent[n] = [];
        }

        foreach ((int curve, int a, int b) in tree)
        {
            adjacent[a].Add((b, curve));
            adjacent[b].Add((a, curve));
        }

        int[] parent = [.. Enumerable.Repeat(-1, nodes.Count)];
        int[] via = new int[nodes.Count];
        int[] depth = new int[nodes.Count];
        bool[] seen = new bool[nodes.Count];
        for (int start = 0; start < nodes.Count; start++)
        {
            if (seen[start])
            {
                continue;
            }

            seen[start] = true;
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                foreach ((int next, int curve) in adjacent[n].Where(e => !seen[e.Node]))
                {
                    seen[next] = true;
                    parent[next] = n;
                    via[next] = curve;
                    depth[next] = depth[n] + 1;
                    queue.Enqueue(next);
                }
            }
        }

        foreach ((int curve, int a, int b) in closing)
        {
            var loop = new List<int> { curve };
            int x = a, y = b;
            while (x != y)
            {
                if (depth[x] >= depth[y])
                {
                    loop.Add(via[x]);
                    x = parent[x];
                }
                else
                {
                    loop.Add(via[y]);
                    y = parent[y];
                }
            }

            loops.Add([.. loop]);
        }

        return loops;
    }

    /// <summary>
    /// A point inside the loop formed by the curves (a closed curve or a chain whose ends meet), at least
    /// <paramref name="margin"/> from them; null if none is found.
    /// </summary>
    public static (double X, double Y)? InteriorPoint(IReadOnlyList<Curve> curves, double join, double margin)
    {
        (double X, double Y)[] all = [.. curves.SelectMany(c => c.Points)];
        if (all.Length < 2)
        {
            return null;
        }

        double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        foreach (double f in (double[])[0.5, 0.382, 0.618, 0.25, 0.75, 0.15, 0.85, 0.07, 0.93])
        {
            double y = minY + ((maxY - minY) * f);
            var xs = new List<double>();
            foreach (((double X, double Y) a, (double X, double Y) b) in curves.SelectMany(Segments))
            {
                if ((a.Y > y) != (b.Y > y))
                {
                    xs.Add(a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y)));
                }
            }

            xs.Sort();
            var spans = new List<(double Width, double X)>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                spans.Add((xs[i + 1] - xs[i], (xs[i] + xs[i + 1]) / 2));
            }

            foreach ((double _, double x) in spans.OrderByDescending(s => s.Width))
            {
                if (Locate(curves, (x, y), join, margin) == Place.Inside)
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Area properties of a plane region: area, perimeter, centroid and second moments about the centroid axes parallel to x
    /// and y, with Ixy = ∫(x − cx)(y − cy) dA (Solid Edge's dMIX, dMIY and dMIXY; the sign was verified on a right triangle).
    /// </summary>
    public sealed record Moments(double Area, double Perimeter, double Cx, double Cy, double Ix, double Iy, double Ixy)
    {
        /// <summary>The region minus holes inside it (parallel axis theorem); the perimeter adds the edges of the holes.</summary>
        public Moments Minus(IReadOnlyList<Moments> holes)
        {
            double area = Area - holes.Sum(h => h.Area);
            if (!(area > 0))
            {
                throw ToolException.InvalidArgument("The holes are as large as the region or larger.",
                    "Pass only closed elements or chains that lie inside the region as 'holes'.");
            }

            double cx = ((Area * Cx) - holes.Sum(h => h.Area * h.Cx)) / area;
            double cy = ((Area * Cy) - holes.Sum(h => h.Area * h.Cy)) / area;
            double ix = Ix + (Area * (Cy - cy) * (Cy - cy)) - holes.Sum(h => h.Ix + (h.Area * (h.Cy - cy) * (h.Cy - cy)));
            double iy = Iy + (Area * (Cx - cx) * (Cx - cx)) - holes.Sum(h => h.Iy + (h.Area * (h.Cx - cx) * (h.Cx - cx)));
            double ixy = Ixy + (Area * (Cx - cx) * (Cy - cy)) - holes.Sum(h => h.Ixy + (h.Area * (h.Cx - cx) * (h.Cy - cy)));
            return new(area, Perimeter + holes.Sum(h => h.Perimeter), cx, cy, ix, iy, ixy);
        }

        /// <summary>
        /// Principal second moments (maximum, minimum) and the direction of the axis of the maximum from x, in radians within
        /// (−π/2, π/2]: Solid Edge's dPrincipalMIX, dPrincipalMIY and dAngleOfOrientation (0 when every axis is principal).
        /// </summary>
        public (double Max, double Min, double Angle) Principal()
        {
            double mean = (Ix + Iy) / 2, half = (Ix - Iy) / 2;
            double radius = Math.Sqrt((half * half) + (Ixy * Ixy));
            if (radius <= 1e-9 * Math.Abs(mean))
            {
                return (mean, mean, 0);
            }

            // -2 * 0.0 is -0.0, and atan2(-0.0, negative) = -π: a plain zero keeps the angle in (−π/2, π/2].
            double sine = Ixy == 0 ? 0 : -2 * Ixy;
            return (mean + radius, mean - radius, Math.Atan2(sine, Ix - Iy) / 2);
        }
    }

    private static int Node(List<(double X, double Y)> nodes, (double X, double Y) point, double join)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (Dist(nodes[i], point) <= join)
            {
                return i;
            }
        }

        nodes.Add(point);
        return nodes.Count - 1;
    }

    private static IEnumerable<((double X, double Y) A, (double X, double Y) B)> Segments(Curve curve)
    {
        for (int i = 0; i + 1 < curve.Points.Length; i++)
        {
            yield return (curve.Points[i], curve.Points[i + 1]);
        }

        if (curve.Closed && curve.Points.Length > 2 && curve.Points[0] != curve.Points[^1])
        {
            yield return (curve.Points[^1], curve.Points[0]);
        }
    }

    /// <summary>
    /// A ray direction that passes as far as possible from the ends of the open curves, where two curves of a loop meet: the
    /// crossings of a loop are then counted once.
    /// </summary>
    private static (double X, double Y) RayDirection(IReadOnlyList<Curve> curves, (double X, double Y) point)
    {
        (double X, double Y)[] ends = [.. curves.Where(c => !c.Closed && c.Points.Length >= 2).SelectMany(c => (IEnumerable<(double X, double Y)>)[c.Points[0], c.Points[^1]])];
        (double X, double Y) best = (1, 0);
        double bestGap = -1;
        for (int k = 0; k < 48; k++)
        {
            double angle = 0.3 + (k * 2 * Math.PI * 0.6180339887);
            (double X, double Y) d = (Math.Cos(angle), Math.Sin(angle));
            double gap = double.MaxValue;
            foreach ((double X, double Y) e in ends)
            {
                double along = ((e.X - point.X) * d.X) + ((e.Y - point.Y) * d.Y);
                if (along > 0)
                {
                    gap = Math.Min(gap, Math.Abs((-(e.X - point.X) * d.Y) + ((e.Y - point.Y) * d.X)));
                }
            }

            if (gap > bestGap)
            {
                (best, bestGap) = (d, gap);
            }
        }

        return best;
    }

    /// <summary>Crossings of the curve with the ray from the point; a vertex on the ray counts on its side (half-open rule).</summary>
    private static int Crossings(Curve curve, (double X, double Y) point, (double X, double Y) d)
    {
        int count = 0;
        foreach (((double X, double Y) a, (double X, double Y) b) in Segments(curve))
        {
            double ua = ((a.X - point.X) * d.X) + ((a.Y - point.Y) * d.Y), va = (-(a.X - point.X) * d.Y) + ((a.Y - point.Y) * d.X);
            double ub = ((b.X - point.X) * d.X) + ((b.Y - point.Y) * d.Y), vb = (-(b.X - point.X) * d.Y) + ((b.Y - point.Y) * d.X);
            if ((va > 0) != (vb > 0) && ua + ((ub - ua) * va / (va - vb)) > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static double Distance(Curve curve, (double X, double Y) point) =>
        curve.Points.Length == 1 ? Dist(curve.Points[0], point) : Segments(curve).Select(s => SegmentDistance(s.A, s.B, point)).DefaultIfEmpty(double.MaxValue).Min();

    private static double SegmentDistance((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length2 = (dx * dx) + (dy * dy);
        double t = length2 == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length2, 0, 1);
        return Dist((a.X + (t * dx), a.Y + (t * dy)), p);
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
