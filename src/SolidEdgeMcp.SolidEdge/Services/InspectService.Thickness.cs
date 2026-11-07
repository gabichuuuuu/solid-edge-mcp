// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeGeometry;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class InspectService
{
    /// <summary>
    /// Normal distance from a reference plane or planar face to a vertex, as the Measure Normal Distance command. Verified in
    /// Solid Edge 2026: PartDocument.NormalDistance(plane or planar face, vertex) gives the true distance and its components;
    /// two faces give E_INVALIDARG and an edge E_FAIL or E_INVALIDARG, so the vertex is the start or end of the edge given.
    /// </summary>
    public static object NormalDistance(SeContext context, string? documentHandle, string? first, string? second, string at)
    {
        string planeHandle = first ?? throw ToolException.InvalidArgument("normal_distance needs 'first': a reference plane or a planar face.");
        string edgeHandle = second ?? throw ToolException.InvalidArgument("normal_distance needs 'second': an edge, whose start (or end, with 'at') is measured.");
        bool basePlane = planeHandle.Trim().ToLowerInvariant() is "1" or "2" or "3" or "xy" or "yz" or "xz";
        ModelDocument model = ModelDocument.For(context, documentHandle, basePlane ? edgeHandle : planeHandle);
        object plane = basePlane ? model.ResolvePlane(context, planeHandle, "first") : context.Handles.Resolve(planeHandle, "first");
        object element = context.Handles.Resolve(edgeHandle, "second");
        if (plane is OccurrenceElement or Occurrence || element is OccurrenceElement or Occurrence)
        {
            throw new ToolException("not_supported", "normal_distance measures in a part or sheet metal part.",
                "In an assembly, use se_measure distance between the occurrence elements.");
        }

        if (plane is not (RefPlane or Face { Geometry: Plane }))
        {
            throw ToolException.InvalidArgument($"'{planeHandle}' in 'first' is not a reference plane or a planar face.");
        }

        if (element is not Edge edge)
        {
            throw ToolException.InvalidArgument($"'{edgeHandle}' in 'second' is not an edge (se_geometry edges).");
        }

        object vertex = (at switch
        {
            "start" => edge.StartVertex,
            "end" => edge.EndVertex,
            _ => throw ToolException.InvalidArgument($"Unknown end '{at}'.", "Use start or end."),
        }) ?? throw ToolException.InvalidArgument($"'{edgeHandle}' has no {at} vertex (a closed edge such as a full circle).",
            "Pick a straight or open edge that ends at the point to measure.");
        double distance, dx, dy, dz;
        try
        {
            model.NormalDistance(plane, vertex, out distance, out dx, out dy, out dz);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("measure_failed", $"Solid Edge could not measure the normal distance: {ex.Message.Trim()}",
                "Check that 'first' is a plane or planar face and 'second' an edge of the part.", ex);
        }

        Array point = new double[3];
        ((Vertex)vertex).GetPointData(ref point);
        return new { distance = Geo.Mm(distance), delta = new[] { Geo.Mm(dx), Geo.Mm(dy), Geo.Mm(dz) }, point = Geo.Mm(point) };
    }

    /// <summary>
    /// Wall thickness of the design body from Solid Edge's thickness analysis. Verified in Solid Edge 2026:
    /// Body.CalculateWallThickness(resolution, out up, out data, out size, identity transform) returns one row of ten values per
    /// mesh triangle: its three vertices and three normals (metres), the thickness at each vertex (metres, 0 where no wall was
    /// found), an index, the vertex indices and a flag. The thickness is measured along the inward normal, so on a 100 x 60 x 20
    /// block with three 10 mm holes 15 mm from the sides the thinnest wall is 15 mm and the thickest 100 mm (along the block).
    /// Faces square to the Z axis of the transform get 0 (an open box 100 x 60 x 30 with a 5 mm floor read 10 mm at least), so
    /// the analysis runs again with the part turned 90 degrees about X: it returns the same mesh in the same order, with the
    /// points turned, (x, y, z) to (x, z, -y), and each vertex takes the value of the run that measured it (5 mm then).
    /// A point on an edge can read almost nothing: on the open rim of a box shelled 2 mm with 5 degree draft, the inner walls'
    /// normals point slightly up and their rim vertices read 0.086 mm (triangles reading 54.9, 1.03 and 0.086 mm), while the
    /// walls read 2.008 at every corner of their triangles. So the thinnest uniform wall (a triangle whose three corners agree
    /// within 5 %) is given as well.
    /// </summary>
    public static object WallThickness(SeContext context, string? documentHandle, string resolution, double? threshold)
    {
        WallThicknessDisplayResolution level = resolution switch
        {
            "coarse" => WallThicknessDisplayResolution.DisplayResolutionCoarse,
            "standard" => WallThicknessDisplayResolution.DisplayResolutionStandard,
            "fine" => WallThicknessDisplayResolution.DisplayResolutionFine,
            _ => throw ToolException.InvalidArgument($"Unknown resolution '{resolution}'.", "Use coarse, standard or fine."),
        };
        double? limit = threshold is { } t ? Units.MmToM(Units.RequirePositive(t, "threshold")) : null;
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        Body body = model.RequireBody() as Body
            ?? throw new ToolException("no_body", $"'{model.Document.Name}' has no solid body to analyse.");
        object[,] rows = Analyse(body, level, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1])
            ?? throw new ToolException("measure_failed", "Solid Edge returned no wall thickness data for the body.");
        object[,]? turned = Analyse(body, level, [1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1]);
        if (turned is not null && turned.GetLength(0) != rows.GetLength(0))
        {
            turned = null;
        }

        int r0 = rows.GetLowerBound(0), c0 = rows.GetLowerBound(1);
        double min = double.MaxValue, max = 0, area = 0, below = 0;
        double[] thinnest = [], thickest = [];
        double uniform = double.MaxValue;
        double[] uniformAt = [];
        int triangles = rows.GetLength(0);
        for (int r = r0; r < r0 + triangles; r++)
        {
            double[][] p = [.. Enumerable.Range(0, 3).Select(j => rows[r, c0 + j] is Array a ? Geo.Doubles(a).ToArray() : [])];
            if (p.Any(v => v.Length != 3) || rows[r, c0 + 6] is not Array thickness)
            {
                continue;
            }

            double[] values = [.. Geo.Doubles(thickness)];
            if (turned is not null && values.Any(v => v <= 0))
            {
                values = Merge(values, p, turned, turned.GetLowerBound(0) + r - r0, turned.GetLowerBound(1));
            }

            double triangle = Geo.Length(Geo.Cross([p[1][0] - p[0][0], p[1][1] - p[0][1], p[1][2] - p[0][2]],
                [p[2][0] - p[0][0], p[2][1] - p[0][1], p[2][2] - p[0][2]])) / 2;
            area += triangle;
            double[] found = [.. values.Where(v => v > 0)];
            for (int j = 0; j < values.Length && j < 3; j++)
            {
                if (values[j] > 0 && values[j] < min)
                {
                    (min, thinnest) = (values[j], p[j]);
                }

                if (values[j] > max)
                {
                    (max, thickest) = (values[j], p[j]);
                }
            }

            if (found.Length == 3 && found.Max() <= found.Min() * UniformSpread && found.Average() < uniform)
            {
                (uniform, uniformAt) = (found.Average(), [.. Enumerable.Range(0, 3).Select(k => (p[0][k] + p[1][k] + p[2][k]) / 3)]);
            }

            if (limit is { } l && found.Length > 0 && found.Average() < l)
            {
                below += triangle;
            }
        }

        if (thinnest.Length == 0)
        {
            throw new ToolException("measure_failed", "Solid Edge found no wall in the body (no thickness above zero).");
        }

        bool uniformFound = uniformAt.Length == 3;
        return new
        {
            resolution,
            triangles,
            turnedRun = turned is not null,
            minThickness = Geo.Mm(min),
            thinnestAt = thinnest.Select(Geo.Mm).ToArray(),
            minUniformThickness = uniformFound ? Geo.Mm(uniform) : (double?)null,
            uniformAt = uniformFound ? uniformAt.Select(Geo.Mm).ToArray() : null,
            maxThickness = Geo.Mm(max),
            thickestAt = thickest.Select(Geo.Mm).ToArray(),
            area = Geo.R(area * 1e6),
            threshold,
            areaBelow = limit is null ? (double?)null : Geo.R(below * 1e6),
            percentBelow = limit is null || area <= 0 ? (double?)null : Math.Round(100 * below / area, 3),
            note = uniformFound && min < uniform / UniformSpread
                ? $"The thinnest reading ({Geo.Mm(min)} mm) is at an edge, where the measuring ray can leave the body at once (e.g. the open rim of a shelled part): the thinnest uniform wall is {Geo.Mm(uniform)} mm."
                : null,
        };
    }

    /// <summary>Largest ratio between the readings at the corners of a mesh triangle for it to count as a uniform wall.</summary>
    private const double UniformSpread = 1.05;

    private static object[,]? Analyse(Body body, WallThicknessDisplayResolution level, double[] transform)
    {
        try
        {
            body.CalculateWallThickness(level, out _, out object data, out int size, transform);
            return data is object[,] rows && size > 0 && rows.GetLength(1) >= 7 ? rows : null;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("measure_failed", $"Solid Edge could not analyse the wall thickness: {ex.Message.Trim()}",
                "Check the model for failed features (se_feature_tree).", ex);
        }
    }

    /// <summary>The vertices the upright run left at 0 take the turned run's value, when that row is the same triangle.</summary>
    private static double[] Merge(double[] values, double[][] points, object[,] turned, int row, int c0)
    {
        double[] merged = [.. values];
        for (int j = 0; j < 3 && j < values.Length; j++)
        {
            if (values[j] > 0 || turned[row, c0 + j] is not Array a || turned[row, c0 + 6] is not Array t)
            {
                continue;
            }

            double[] q = [.. Geo.Doubles(a)];
            double[] other = [.. Geo.Doubles(t)];
            bool same = q.Length == 3 && Math.Abs(q[0] - points[j][0]) + Math.Abs(-q[2] - points[j][1]) + Math.Abs(q[1] - points[j][2]) < 1e-9;
            if (same && j < other.Length)
            {
                merged[j] = other[j];
            }
        }

        return merged;
    }
}
