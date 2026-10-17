// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeGeometry;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// A tube in a part: a circle on a plane normal to the start of the path, swept along it (with a wall, as a thin wall inside the circle). Verified in
    /// Solid Edge 2026: TubeFeatures has no Add (tubes are made with the Tube command), and a plane normal to a sketch element
    /// gives E_NOINTERFACE (AddNormalToCurve takes edges), so for a sketch path the plane is set normal to a short construction
    /// curve drawn along the tangent at the start of the path. The volume of a swept circle is its area times the path length.
    /// </summary>
    public static object Tube(SeContext context, string? documentHandle, string? pathHandle, double? outerDiameter, double? wall, string operation)
    {
        string handle = pathHandle ?? throw ToolException.InvalidArgument("tube needs 'path': an open chain of lines and arcs (se_sketch, close with open: true) or a curve (se_curve, se_sketch3d).");
        double outer = Units.RequirePositive(outerDiameter ?? throw ToolException.InvalidArgument("tube needs 'outerDiameter' (mm)."), "outerDiameter");
        double thickness = wall ?? 0;
        if (thickness < 0 || thickness >= outer / 2)
        {
            throw ToolException.InvalidArgument("'wall' must be 0 (a solid rod) or more, and less than half of 'outerDiameter'.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, handle);
        object resolved = context.Handles.Resolve(handle, "path");
        if (resolved is Sketch3D sketch3d)
        {
            resolved = sketch3d.Sketch3DFeature;
        }

        bool switched = model.EnsureOrdered();
        object? helper = null;
        Edge startEdge;
        bool atEnd;
        double length;
        if (resolved is Profile or Sketch)
        {
            (double[] start, double[] tangent, length) = SketchPathStart(SketchService.ResolveDefined(context, handle));
            helper = TangentCurve(model, start, tangent);
            startEdge = CurveEdges(helper)[0];
            atEnd = false;
        }
        else
        {
            Edge[] edges = CurveEdges(resolved);
            if (edges.Length == 0)
            {
                throw new ToolException("handle_wrong_type", $"'{handle}' in 'path' is neither a sketch nor a curve.",
                    "Pass a sketch closed with se_sketch 'close' (open: true), a curve from se_curve or a 3D sketch from se_sketch3d.");
            }

            (startEdge, atEnd) = ChainStart(edges);
            length = edges.Sum(EdgeLength);
        }

        Sketch? section = null;
        try
        {
            Array ends = new double[3];
            Array other = new double[3];
            startEdge.GetEndPoints(ref ends, ref other);
            double[] chord = Unit3(Sub3([.. Geo.Doubles(other)], [.. Geo.Doubles(ends)]));
            RefPlane orientation = OrientedPlanes.Bases(Late.Items(model.RefPlanes)).OfType<RefPlane>()
                .MinBy(p => Math.Abs(Dot3(PlaneNormal(p), chord)))!;
            RefPlane plane = model.RefPlanes.AddNormalToCurve(startEdge, atEnd ? ReferenceElementConstants.igCurveEnd : ReferenceElementConstants.igCurveStart,
                orientation, ReferenceElementConstants.igPivotEnd, Local: true);
            double[] centre = Geo.Doubles(atEnd ? other : ends).ToArray();
            section = model.Sketches.Add();
            Profile profile = section.Profiles.Add(plane);
            profile.Convert3DCoordinate(centre[0], centre[1], centre[2], out double u, out double v);
            profile.Circles2d.AddByCenterRadius(u, v, Units.MmToM(outer) / 2);
            if (profile.End(ProfileValidationType.igProfileClosed) != 0)
            {
                throw new ToolException("feature_failed", "Solid Edge did not accept the tube section.", "Check the path and the diameters.");
            }

            SketchService.Define(profile);

            string sectionHandle = context.Register(profile, "sketch", model.Document);
            // Verified: an annulus (two circles) sweeps only the inner disk, so the wall is the sweep's thin wall inside the circle.
            object swept = LoftSweep(context, documentHandle, new SweepSpec("sweep", operation, [sectionHandle], handle,
                ThinWall: thickness > 0 ? new ThinWallSpec(thickness, "inside") : null));
            double inner = outer - 2 * thickness;
            double expected = Math.PI / 4 * (outer * outer - (thickness > 0 ? inner * inner : 0)) * Units.MToMm(length);
            return new
            {
                tube = swept,
                outerDiameter = outer,
                wall = thickness > 0 ? thickness : (double?)null,
                pathLength = Geo.Mm(length),
                expectedVolume = Geo.R(expected),
                note = switched ? "The document was switched to ordered modeling." : null,
            };
        }
        catch (ToolException)
        {
            TryDeleteObject(section);
            TryDeleteObject(helper);
            throw;
        }
    }

    /// <summary>Start point, unit tangent (model coordinates, m) and length (m) of an open chain of lines and arcs in a sketch.</summary>
    private static (double[] Start, double[] Tangent, double Length) SketchPathStart(Profile profile)
    {
        if (Late.Count(profile.Circles2d) + Late.Count(profile.Ellipses2d) + Late.Count(profile.EllipticalArcs2d) + Late.Count(profile.BSplineCurves2d) > 0)
        {
            throw ToolException.InvalidArgument("A tube path drawn in a sketch is a chain of lines and arcs.",
                "For a smooth path use a curve (se_curve points) or a 3D sketch (se_sketch3d).");
        }

        // Each segment: its two ends and the direction leaving each end into the segment.
        var segments = new List<(double[] A, double[] B, double[] OutOfA, double[] OutOfB, double Length)>();
        foreach (Line2d line in Late.Items(profile.Lines2d).OfType<Line2d>())
        {
            line.GetStartPoint(out double x0, out double y0);
            line.GetEndPoint(out double x1, out double y1);
            double span = Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
            double[] d = [(x1 - x0) / span, (y1 - y0) / span];
            segments.Add(([x0, y0], [x1, y1], d, [-d[0], -d[1]], line.Length));
        }

        foreach (Arc2d arc in Late.Items(profile.Arcs2d).OfType<Arc2d>())
        {
            arc.GetCenterPoint(out double cx, out double cy);
            arc.GetStartPoint(out double x0, out double y0);
            arc.GetEndPoint(out double x1, out double y1);
            double r = arc.Radius;
            double a0 = Math.Atan2(y0 - cy, x0 - cx);
            double a1 = Math.Atan2(y1 - cy, x1 - cx);
            double sweep = Math.Abs(arc.SweepAngle);
            // Counterclockwise when turning the start by the sweep lands on the end.
            bool ccw = Math.Abs(cx + r * Math.Cos(a0 + sweep) - x1) + Math.Abs(cy + r * Math.Sin(a0 + sweep) - y1) < 1e-6 * Math.Max(1, r * 1000);
            double s = ccw ? 1 : -1;
            segments.Add(([x0, y0], [x1, y1], [-s * Math.Sin(a0), s * Math.Cos(a0)], [s * Math.Sin(a1), -s * Math.Cos(a1)], r * sweep));
        }

        if (segments.Count == 0)
        {
            throw ToolException.InvalidArgument("The path sketch has no lines or arcs.");
        }

        List<double[]> points = [.. segments.SelectMany(g => new[] { g.A, g.B })];
        int Uses(double[] p) => points.Count(q => Math.Abs(q[0] - p[0]) + Math.Abs(q[1] - p[1]) < 1e-9);
        // The free end of an open chain (a closed chain starts at its first segment).
        (double[] at, double[] direction) = (segments[0].A, segments[0].OutOfA);
        foreach ((double[] a, double[] b, double[] outOfA, double[] outOfB, _) in segments)
        {
            if (Uses(a) == 1 || Uses(b) == 1)
            {
                (at, direction) = Uses(a) == 1 ? (a, outOfA) : (b, outOfB);
                break;
            }
        }
        profile.Convert2DCoordinate(at[0], at[1], out double sx, out double sy, out double sz);
        const double step = 1e-3;
        profile.Convert2DCoordinate(at[0] + direction[0] * step, at[1] + direction[1] * step, out double tx, out double ty, out double tz);
        return ([sx, sy, sz], Unit3([tx - sx, ty - sy, tz - sz]), segments.Sum(g => g.Length));
    }

    /// <summary>A short straight construction curve from <paramref name="start"/> along <paramref name="tangent"/> (m).</summary>
    private static object TangentCurve(ModelDocument model, double[] start, double[] tangent)
    {
        const double reach = 0.01;
        Array pointTypes = new[] { (int)PointTypeConstants.igSpacePoint, (int)PointTypeConstants.igSpacePoint };
        Array edges = new object?[2];
        Array keyTypes = new int[2];
        Array positions = new[] { start[0], start[1], start[2], start[0] + tangent[0] * reach, start[1] + tangent[1] * reach, start[2] + tangent[2] * reach };
        const KeypointEndConditionConstants natural = KeypointEndConditionConstants.seKeypointEndConditionNatural;
        object curve = MakeCurve("tube start curve", () => ConstructionsOf(model).KeyPointCurves.AddEx(2, ref pointTypes, ref edges, ref keyTypes, ref positions, natural, natural),
            "Check the path.");
        try
        {
            Late.Set(curve, "Visible", false);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Only its display.
        }

        return curve;
    }

    /// <summary>The edge at a free end of a chain of edges and whether that free end is the edge's end point (any edge of a closed chain).</summary>
    private static (Edge Edge, bool AtEnd) ChainStart(Edge[] edges)
    {
        var ends = edges.Select(e =>
        {
            Array a = new double[3];
            Array b = new double[3];
            e.GetEndPoints(ref a, ref b);
            return (Edge: e, A: Geo.Doubles(a).ToArray(), B: Geo.Doubles(b).ToArray());
        }).ToList();
        List<double[]> all = [.. ends.SelectMany(e => new[] { e.A, e.B })];
        int Uses(double[] p) => all.Count(q => Geo.Distance(p, q) < 1e-7);
        foreach ((Edge edge, double[] a, double[] b) in ends)
        {
            if (Uses(a) == 1)
            {
                return (edge, false);
            }

            if (Uses(b) == 1)
            {
                return (edge, true);
            }
        }

        return (ends[0].Edge, false);
    }

    private static double[] PlaneNormal(RefPlane plane)
    {
        Array normal = new double[3];
        plane.GetNormal(ref normal);
        return [.. Geo.Doubles(normal)];
    }

    private static void TryDeleteObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            Late.Call(value, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) || ComErrors.Unwrap(ex) is COMException)
        {
            // The error being reported is the original one.
        }
    }
}
