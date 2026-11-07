// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeGeometry;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// Helical construction curve about a cylindrical face or a straight edge, starting on a plane across the axis. Verified in
    /// Solid Edge 2026: HelicalCurves.AddSingleSectionCurve makes it with the axis given as a cylindrical face or a straight
    /// edge, one space point and a planar face as the base (a reference axis or a sketch line as the axis leave it failed);
    /// DirectionStartToEnd sets the way it climbs; on a cylindrical face it keeps the cylinder's diameter. Ø20, 5 turns over
    /// 40 mm: 316.696 mm = 5 x sqrt((20π)² + 8²).
    /// </summary>
    public static object HelixCurve(SeContext context, string? documentHandle, string[]? faces, string[]? edges, string? plane, double? diameter,
        double? pitch, double? turns, double? height, string hand, bool reverse)
    {
        bool byFace = faces is { Length: > 0 };
        if (byFace == edges is { Length: > 0 } || (byFace ? faces! : edges!).Length != 1)
        {
            throw ToolException.InvalidArgument("helix needs its axis: 'faces' = [a cylindrical face] or 'edges' = [a straight edge].");
        }

        string axisHandle = byFace ? faces![0] : edges![0];
        string start = plane ?? throw ToolException.InvalidArgument("helix needs 'plane': the planar face or reference plane it starts on, across the axis.");
        ModelDocument model = ModelDocument.For(context, documentHandle, axisHandle);
        object axis = context.Handles.Resolve(axisHandle, byFace ? "faces" : "edges");
        Array rootArray = new double[3];
        Array directionArray = new double[3];
        double? ownDiameter = null;
        double[] middle;
        switch (axis)
        {
            case Face face when face.Geometry is Cylinder cylinder:
                cylinder.GetCylinderData(ref rootArray, ref directionArray, out double radius);
                ownDiameter = 2 * radius;
                Array lo = new double[3];
                Array hi = new double[3];
                face.GetRange(ref lo, ref hi);
                middle = [.. Geo.Doubles(lo).Zip(Geo.Doubles(hi), (a, b) => (a + b) / 2)];
                break;
            case Edge edge when edge.Geometry is SolidEdgeGeometry.Line line:
                line.GetLineData(ref rootArray, ref directionArray);
                Array s = new double[3];
                Array e = new double[3];
                edge.GetEndPoints(ref s, ref e);
                middle = [.. Geo.Doubles(s).Zip(Geo.Doubles(e), (a, b) => (a + b) / 2)];
                break;
            default:
                throw ToolException.InvalidArgument($"'{axisHandle}' is not a cylindrical face or a straight edge.", "Get one with se_geometry faces (type cylinder) or edges (type line).");
        }

        double[] root = [.. Geo.Doubles(rootArray)];
        double[] direction = Unit3([.. Geo.Doubles(directionArray)]);
        double d = diameter is { } given ? Units.MmToM(Units.RequirePositive(given, "diameter"))
            : ownDiameter ?? throw ToolException.InvalidArgument("'diameter' (mm) is required with an edge as the axis.");
        if (ownDiameter is { } own && Math.Abs(d - own) > 1e-9)
        {
            // Verified: on a cylindrical face Solid Edge keeps the cylinder's diameter whatever is asked.
            throw ToolException.InvalidArgument($"A helix about a cylindrical face lies on it: its diameter is the cylinder's, {Geo.Mm(own)} mm.",
                "For another diameter give a straight edge along the axis in 'edges', e.g. of a construction line (se_curve points, then se_geometry edges with source = the curve).");
        }
        (double h, double p, double n) = HelixSize(pitch, turns, height);
        bool right = hand switch
        {
            "right" => true,
            "left" => false,
            _ => throw ToolException.InvalidArgument($"Unknown hand '{hand}'.", "Use right or left."),
        };

        object basePlane = model.ResolvePlaneSource(context, start, "plane");
        (double[] planeRoot, double[] planeNormal) = PlaneData(basePlane);
        double across = Dot3(direction, planeNormal);
        if (Math.Abs(across) < 1e-6)
        {
            throw ToolException.InvalidArgument("The plane does not cross the axis.", "Give the planar face or plane square to the axis where the helix starts.");
        }

        double[] basePoint = Add3(root, Scale3(direction, Dot3(Sub3(planeRoot, root), planeNormal) / across));
        double[] side = Unit3(Math.Abs(direction[0]) < 0.9 ? Cross3(direction, [1, 0, 0]) : Cross3(direction, [0, 1, 0]));
        double[] point = Add3(basePoint, Scale3(side, d / 2));
        // The way into the axis element (towards its middle), or away from it with reverse.
        double wanted = Math.Sign(Dot3(Sub3(middle, basePoint), direction)) * (reverse ? -1 : 1);
        if (wanted == 0)
        {
            wanted = reverse ? -1 : 1;
        }

        bool switched = model.EnsureOrdered();
        HelicalCurves helices = ConstructionsOf(model).HelicalCurves;
        string? problem = null;
        foreach (bool startToEnd in new[] { true, false })
        {
            Array types = new[] { (int)PointTypeConstants.igSpacePoint };
            Array vertices = new object?[] { null };
            Array positions = point;
            object curve;
            try
            {
                curve = helices.AddSingleSectionCurve(axis, 1, ref types, ref vertices, ref positions, basePlane, right,
                    HelicalCurveMethodType.igHelicalCurveMethodHeightAndTurns, HelicalCurveTaperByType.igHelicalCurveTaperNone, false, true, startToEnd,
                    d, d, 0, h, n, p, p);
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                problem = ex.Message.Trim();
                continue;
            }

            double[][] ends = CurveEdges(curve).Length > 0 ? CurveEnds(curve) : [];
            if (ends.Length == 2)
            {
                double[] first = Geo.Distance(ends[0], basePoint) <= Geo.Distance(ends[1], basePoint) ? ends[0] : ends[1];
                double[] last = ReferenceEquals(first, ends[0]) ? ends[1] : ends[0];
                if (Math.Sign(Dot3(Sub3(last, first), direction)) != wanted)
                {
                    TryDeleteCurve(curve);
                    problem = "it climbed the other way";
                    continue;
                }
            }

            double expected = n * Math.Sqrt(Math.Pow(Math.PI * d, 2) + (p * p));
            return CheckedCurve(context, model, curve, "helix", switched, c =>
            {
                double length = CurveEdges(c).Sum(EdgeLength);
                return Math.Abs(length - expected) <= expected * 1e-4 ? null
                    : $"its length is {Geo.Mm(length)} mm, not the {Geo.Mm(expected)} mm of the helix";
            });
        }

        throw new ToolException("feature_failed", $"Solid Edge did not make the helical curve ({problem}).",
            "Give a cylindrical face or a straight edge as the axis and a planar face or plane across it where the helix starts.");
    }

    /// <summary>Root point and unit normal (m) of a reference plane or a planar face.</summary>
    private static (double[] Root, double[] Normal) PlaneData(object plane)
    {
        Array root = new double[3];
        Array normal = new double[3];
        switch (plane)
        {
            case RefPlane refPlane:
                refPlane.GetRootPoint(ref root);
                refPlane.GetNormal(ref normal);
                break;
            case Face face when face.Geometry is SolidEdgeGeometry.Plane geometry:
                geometry.GetPlaneData(ref root, ref normal);
                break;
            default:
                throw ToolException.InvalidArgument("'plane' must be a reference plane or a planar face.");
        }

        return ([.. Geo.Doubles(root)], Unit3([.. Geo.Doubles(normal)]));
    }

    private static double[] Add3(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

    private static double[] Scale3(double[] a, double k) => [a[0] * k, a[1] * k, a[2] * k];

    private static double[] Cross3(double[] a, double[] b) => Geo.Cross(a, b);
}
