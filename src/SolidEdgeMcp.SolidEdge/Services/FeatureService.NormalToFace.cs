// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// Raises or recesses, normal to the faces, the region inside (or outside) closed curves that lie on them: the Normal
    /// Protrusion and Normal Cutout commands, e.g. for a logo projected or wrapped onto the part. Verified with the 4 edges
    /// of a 40 x 20 rectangle projected onto the top of a 100 x 60 x 20 block (5 mm: +4000 mm³ inside, +26000 outside, -4000
    /// as a cutout) and of a 20 x 10 rectangle wrapped on a cylinder of radius 20 (2 mm: +420). The Add methods take the
    /// edges (the curve feature itself gives E_NOINTERFACE); LongestCurveSide (igLeft or igRight) picks the side by the
    /// direction of the chain, so the side is checked from the box of the new faces and swapped when it is the other one.
    /// </summary>
    private static object NormalToFace(SeContext context, string? documentHandle, PartFeatureSpec spec, bool cut)
    {
        string action = cut ? "normal_cutout" : "normal_protrusion";
        if (spec.Edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} requires 'edges': the edges of closed curves that lie on faces.",
                "Project or wrap a closed sketch onto the faces (se_curve project or wrap), then take its edges (se_geometry edges with source = the curve).");
        }

        double distance = Units.RequirePositive(spec.Distance ?? throw ToolException.InvalidArgument($"{action} requires 'distance': how far from the faces, in mm."), "distance");
        bool inside = spec.Side switch
        {
            PartFeatureSide.Auto or PartFeatureSide.Normal => true,
            PartFeatureSide.Reverse => false,
            _ => throw ToolException.InvalidArgument($"{action}: 'side' must be auto (inside the curves) or reverse (outside them, on the faces they lie on)."),
        };
        (ModelDocument model, object[] edges) = ResolveEdges(context, documentHandle, spec.Edges);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        double metres = Units.MmToM(distance);
        // Before the feature: afterwards the edges of a wrapped curve no longer give their range (E_FAIL, verified).
        (double[] Min, double[] Max) chain = BoxOf(edges.Select(e => EdgeRange((Edge)e)));
        object feature = Create(cut ? "normal cutout" : "normal protrusion", () => cut
            ? target.NormalToFaceCutouts.Add(edges.Length, edges, FeaturePropertyConstants.igLeft, metres, FeaturePropertyConstants.igFacesTouchingCurvesOnly)
            : target.NormalToFaceProtrusions.Add(edges.Length, edges, FeaturePropertyConstants.igLeft, metres, FeaturePropertyConstants.igFacesTouchingCurvesOnly));
        if (StatusOf(feature) is "ok" or "warning" && InsideChain(feature, chain, metres) != inside)
        {
            Late.Set(feature, "LongestCurveSide", (int)FeaturePropertyConstants.igRight);
        }

        (double after, string? status) = Check(context, model, feature, before, cut ? -1 : 1, null, null,
            hint: "Give the edges of closed curves lying on the faces (se_curve project or wrap, then se_geometry edges with source = the curve).");
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            side = inside ? "inside" : "outside",
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>Whether the faces of the feature lie within the box of the curves grown by the distance: the region inside them.</summary>
    private static bool InsideChain(object feature, (double[] Min, double[] Max) chain, double distance)
    {
        Face[] faces = FacesOf(feature);
        if (faces.Length == 0)
        {
            return true;
        }

        (double[] lo, double[] hi) = chain;
        (double[] flo, double[] fhi) = BoxOf(faces.Select(f => RangeOf(f.GetRange)));
        double tolerance = distance + 1e-6;
        return Enumerable.Range(0, 3).All(i => flo[i] >= lo[i] - tolerance && fhi[i] <= hi[i] + tolerance);
    }

    /// <summary>Box of an edge: its range, or else the box of points along it.</summary>
    private static (double[] Min, double[] Max) EdgeRange(Edge edge)
    {
        try
        {
            return RangeOf(edge.GetRange);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            edge.GetParamExtents(out double start, out double end);
            const int Samples = 17;
            Array parameters = Enumerable.Range(0, Samples).Select(i => start + ((end - start) * i / (Samples - 1))).ToArray();
            Array points = new double[Samples * 3];
            edge.GetPointAtParam(Samples, ref parameters, ref points);
            double[] xyz = [.. Geo.Doubles(points)];
            return BoxOf(Enumerable.Range(0, Samples).Select(i => (xyz[(i * 3)..((i * 3) + 3)], xyz[(i * 3)..((i * 3) + 3)])));
        }
    }

    private delegate void RangeGetter(ref Array min, ref Array max);

    private static (double[] Min, double[] Max) RangeOf(RangeGetter get)
    {
        Array min = new double[3];
        Array max = new double[3];
        get(ref min, ref max);
        return ([.. Geo.Doubles(min)], [.. Geo.Doubles(max)]);
    }

    private static (double[] Min, double[] Max) BoxOf(IEnumerable<(double[] Min, double[] Max)> ranges)
    {
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach ((double[] min, double[] max) in ranges)
        {
            for (int i = 0; i < 3; i++)
            {
                lo[i] = Math.Min(lo[i], min[i]);
                hi[i] = Math.Max(hi[i], max[i]);
            }
        }

        return (lo, hi);
    }
}
