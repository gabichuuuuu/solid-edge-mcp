// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Circle = SolidEdgeGeometry.Circle;
using Cylinder = SolidEdgeGeometry.Cylinder;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>A physical thread cut into a cylindrical face: metric designation or pitch, length from one end (mm).</summary>
public sealed record ThreadSpec(string? Face, string? Thread, double? Pitch, double? Length, bool Reverse = false, string Hand = "right");

public static partial class FeatureService
{
    /// <summary>ISO 68-1 thread depth of an external thread (h3 = 0.6134·P) and of an internal one ((D - D1)/2 = 0.5413·P).</summary>
    private const double ExternalDepth = 0.6134;
    private const double InternalDepth = 0.5413;

    /// <summary>The section reaches this far past the cylinder surface (as a share of the pitch) so the cut leaves no skin.</summary>
    private const double ThreadClearance = 0.2;

    /// <summary>
    /// A physical thread: the 60° ISO profile swept as a helical cutout along a cylindrical face, outside on a shaft and inside
    /// on a hole (Threads.Add and AddEx give E_INVALIDARG in Solid Edge 2026, so the thread is modelled as Solid Edge's own
    /// helix cutout). The section is drawn on a plane through the axis (AddBy3Points: the centres of the two end circles and
    /// the start of one) at the start end and climbs along the axis for the length. Verified with M10x1.5 on a Ø10 x 30 shaft,
    /// 27 mm: 18 turns remove 259.35 mm3, against 0.4887 mm2 x 2π x 4.693 mm x 18 = 259.4 by hand.
    /// </summary>
    public static object Thread(SeContext context, string? documentHandle, ThreadSpec spec)
    {
        string faceHandle = spec.Face ?? throw ToolException.InvalidArgument("se_thread needs 'face': a cylindrical face (se_geometry faces type cylinder).");
        ModelDocument model = ModelDocument.For(context, documentHandle, faceHandle);
        Face face = context.Handles.Resolve<Face>(faceHandle, "face");
        if (face.Geometry is not Cylinder cylinder)
        {
            throw ToolException.InvalidArgument($"'{faceHandle}' is not a cylindrical face.", "Pass a face of type cylinder from se_geometry faces.");
        }

        Array basePoint = new double[3];
        Array axisVector = new double[3];
        cylinder.GetCylinderData(ref basePoint, ref axisVector, out double radius);
        double[] origin = Geo.Doubles(basePoint).ToArray();
        double[] axis = Unit3(Geo.Doubles(axisVector).ToArray());
        Edge[] circles = Late.Items(face.Edges).OfType<Edge>().Where(e => e.Geometry is Circle).ToArray();
        if (circles.Length != 2)
        {
            throw ToolException.InvalidArgument($"The thread needs a full cylindrical face closed by two circular edges; this one has {circles.Length}.",
                "Thread a plain shaft or hole (a face cut by other features has more edges).");
        }

        double[] Center(Edge edge)
        {
            Array c = new double[3];
            ((Circle)edge.Geometry).GetCenterPoint(ref c);
            return Geo.Doubles(c).ToArray();
        }

        // The two ends along the axis, in order of the axis direction; 'reverse' starts at the far one.
        Edge[] ends = circles.OrderBy(e => Dot3(Sub3(Center(e), origin), axis)).ToArray();
        if (spec.Reverse)
        {
            Array.Reverse(ends);
        }

        double[] startCenter = Center(ends[0]);
        double[] endCenter = Center(ends[1]);
        double faceLength = Geo.Length(Sub3(endCenter, startCenter));
        double[] direction = Unit3(Sub3(endCenter, startCenter));

        (double pitch, double? nominal, string description) = ThreadSize(spec);
        double length = spec.Length is { } l ? Units.MmToM(Units.RequirePositive(l, "length")) : faceLength;
        if (length > faceLength + 1e-9)
        {
            throw ToolException.InvalidArgument($"'length' ({Units.MToMm(length):0.###} mm) is longer than the cylinder ({Units.MToMm(faceLength):0.###} mm).");
        }

        bool external = IsOutward(face, origin, axis);
        double depth;
        if (external)
        {
            if (nominal is { } d && Math.Abs(d - (2 * radius)) > 0.0002)
            {
                throw ToolException.InvalidArgument($"The shaft is Ø{Units.MToMm(2 * radius):0.###} mm; {description} needs Ø{Units.MToMm(d):0.###}.",
                    "Thread a shaft of the nominal diameter, or give 'pitch' alone to cut a thread of that pitch on this diameter.");
            }

            depth = ExternalDepth * pitch;
        }
        else
        {
            depth = nominal is { } d ? (d / 2) - radius : InternalDepth * pitch;
            if (depth <= 0 || depth > 0.75 * pitch)
            {
                throw ToolException.InvalidArgument($"The hole is Ø{Units.MToMm(2 * radius):0.###} mm: {description} needs about Ø{Units.MToMm(nominal!.Value - (2 * InternalDepth * pitch)):0.###} (the minor diameter).",
                    "Drill the hole at the minor diameter of the thread, or give 'pitch' alone.");
            }
        }

        // A radial direction at the start end: towards the start point of the first circle.
        Array circleStart = new double[3];
        Array circleEnd = new double[3];
        ends[0].GetEndPoints(ref circleStart, ref circleEnd);
        double[] rim = Geo.Doubles(circleStart).ToArray();
        double[] radial = Unit3(Sub3(rim, startCenter));
        double clearance = ThreadClearance * pitch;
        double half = (depth + clearance) * Math.Tan(Math.PI / 6);
        double outer = external ? radius + clearance : radius - clearance;
        double tip = external ? radius - depth : radius + depth;
        double[] At(double along, double r) => [.. Enumerable.Range(0, 3).Select(k => startCenter[k] + (direction[k] * along) + (radial[k] * r))];
        double[][] section = [At(-half, outer), At(0, tip), At(half, outer)];

        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        Array edgeSet = new object[] { ends[0], ends[1], ends[0] };
        Array keyPoints = new[] { (int)KeyPointType.igKeyPointCenter, (int)KeyPointType.igKeyPointCenter, (int)KeyPointType.igKeyPointStart };
        RefPlane plane = model.RefPlanes.AddBy3Points(3, ref edgeSet, ref keyPoints, Local: true);
        Sketch sketch = model.Sketches.Add();
        Profile profile = sketch.Profiles.Add(plane);
        (double U, double V)[] uv = section.Select(p =>
        {
            profile.Convert3DCoordinate(p[0], p[1], p[2], out double u, out double v);
            return (u, v);
        }).ToArray();
        var lines = new List<SolidEdgeFrameworkSupport.Line2d>();
        for (int i = 0; i < 3; i++)
        {
            (double U, double V) a = uv[i];
            (double U, double V) b = uv[(i + 1) % 3];
            lines.Add(profile.Lines2d.AddBy2Points(a.U, a.V, b.U, b.V));
        }

        var relations = (SolidEdgeFrameworkSupport.Relations2d)profile.Relations2d;
        for (int i = 0; i < 3; i++)
        {
            relations.AddKeypoint(lines[i], 1, lines[(i + 1) % 3], 0);
        }

        double[] axisEnd = At(length, 0);
        profile.Convert3DCoordinate(startCenter[0], startCenter[1], startCenter[2], out double u0, out double v0);
        profile.Convert3DCoordinate(axisEnd[0], axisEnd[1], axisEnd[2], out double u1, out double v1);
        SolidEdgeFrameworkSupport.Line2d axisLine = profile.Lines2d.AddBy2Points(u0, v0, u1, v1);
        var refAxis = (RefAxis)profile.SetAxisOfRevolution(axisLine);
        profile.End((ProfileValidationType)((int)ProfileValidationType.igProfileClosed | (int)ProfileValidationType.igProfileRefAxisRequired));

        FeaturePropertyConstants hand = spec.Hand switch
        {
            "right" => FeaturePropertyConstants.igRight,
            "left" => FeaturePropertyConstants.igLeft,
            _ => throw ToolException.InvalidArgument($"Unknown hand '{spec.Hand}'.", "Use right or left."),
        };
        Array profiles = new object[] { profile };
        object feature;
        try
        {
            SketchService.Define(profile);
            feature = Create("thread", () => target.HelixCutouts.AddFinite(refAxis, FeaturePropertyConstants.igStart, 1, ref profiles,
                FeaturePropertyConstants.igRight, length, pitch, length / pitch, hand));
        }
        catch (ToolException)
        {
            TryDelete(sketch);
            throw;
        }

        // The section's area inside the material and its centroid radius give the expected volume per turn.
        double inside = depth * depth * Math.Tan(Math.PI / 6);
        double centroid = external ? radius - (depth / 3) : radius + (depth / 3);
        double expected = inside * 2 * Math.PI * centroid * (length / pitch);
        _ = Verify(context, model, feature, before, -1, switched);
        Hide(profile);
        double after = model.Volume();
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            thread = new
            {
                designation = description,
                kind = external ? "external" : "internal",
                pitch = Geo.Mm(pitch),
                depth = Geo.Mm(depth),
                diameter = Geo.Mm(2 * radius),
                length = Geo.Mm(length),
                turns = Geo.R(length / pitch),
                hand = spec.Hand,
                from = startCenter.Select(Geo.Mm).ToArray(),
                to = axisEnd.Select(Geo.Mm).ToArray(),
                expectedVolumeChange = -Geo.Mm3(expected),
            },
        };
    }

    /// <summary>Pitch (m), nominal diameter (m) if a designation was given, and the designation.</summary>
    private static (double Pitch, double? Nominal, string Description) ThreadSize(ThreadSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Thread))
        {
            (double nominal, double pitch, string description) = ParseMetricThread(spec.Thread);
            if (spec.Pitch is { } p && Math.Abs(Units.MmToM(p) - pitch) > 1e-9)
            {
                throw ToolException.InvalidArgument($"'pitch' ({p} mm) does not match {description}.", "Give the thread ('M10x1.25') or the pitch, not both.");
            }

            return (pitch, nominal, description);
        }

        double given = Units.MmToM(Units.RequirePositive(spec.Pitch ?? throw ToolException.InvalidArgument(
            "se_thread needs 'thread' (a metric designation such as 'M10' or 'M10x1.25') or 'pitch' (mm)."), "pitch"));
        return (given, null, $"pitch {Units.MToMm(given):0.###} mm");
    }

    /// <summary>Whether the face's outward normal points away from the axis (a shaft) rather than towards it (a hole).</summary>
    private static bool IsOutward(Face face, double[] origin, double[] axis)
    {
        Array min = new double[2];
        Array max = new double[2];
        face.GetParamRange(ref min, ref max);
        double[] lo = Geo.Doubles(min).ToArray();
        double[] hi = Geo.Doubles(max).ToArray();
        Array param = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2 };
        Array point = new double[3];
        face.GetPointAtParam(1, ref param, ref point);
        double[] p = Geo.Doubles(point).ToArray();
        double[] fromAxis = Sub3(p, origin);
        double along = Dot3(fromAxis, axis);
        double[] radial = [.. Enumerable.Range(0, 3).Select(k => fromAxis[k] - (axis[k] * along))];
        return Dot3(InspectService.FaceNormal(face), radial) > 0;
    }

    private static double[] Sub3(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double Dot3(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Unit3(double[] v)
    {
        double n = Geo.Length(v);
        return [v[0] / n, v[1] / n, v[2] / n];
    }
}
