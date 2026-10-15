// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using EdgePick = SolidEdgeMcp.SolidEdge.Services.PmiAnnotationService.EdgePick;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// New PMI dimensions. Verified in Solid Edge 2026 on a 100 x 60 x 20 block with a hole of 20 mm: Dimensions.DimInitData
/// places them (ClearCreationData, SetType, SetPlane with a reference plane, SetNumberOfParents, SetParentByIndex from 0,
/// SetDimPosition, then AddDimension).
/// <list type="bullet">
/// <item>Linear: two parents taken as key points (vertices) give their distance (the diagonal of the front face, 101.98 mm);
/// two edges not taken as key points fail with 0x80040B30.</item>
/// <item>Diameter (igDimTypeCDiameter) and radius (igDimTypeRadial) take one parent, the circular edge: 20 and 10 mm.</item>
/// <item>Angular: two straight edges, the angle as Solid Edge measures it (270 degrees between a top and a side edge of
/// the block).</item>
/// <item>The dimension is read in the given plane: a top edge dimensioned with the top plane shows its text on the top face,
/// with the dimension line on the edge. PMITrackDistance reads 0 and takes a new value with either sign: the dimension line
/// then stands off the edge along the plane's normal (10 with the top plane: 10 mm above the top edge; with the right plane,
/// 10 mm along x). An edge square to the plane cannot be measured in it (0x80040B30).</item>
/// <item>SetDimPosition is not honoured, with SetDimPositionMode on or off (the same placement for any position). The new
/// dimensions are not drivable (seNonDrivablePMI).</item>
/// </list>
/// </summary>
public static partial class PmiService
{
    public static readonly IReadOnlyList<string> DimensionTypes = ["linear", "diameter", "radius", "angle"];

    /// <summary>
    /// Moves a dimension (Move, PMI tab): its line stands off what it measures by the distance, along the normal of the plane
    /// it is read in (PMITrackDistance); the value stays as it was.
    /// </summary>
    private static object Move(SeContext context, string? documentHandle, PmiSpec spec)
    {
        if (spec.Dimensions is not [string key])
        {
            throw ToolException.InvalidArgument("move takes one dimension: pass 'dimensions' with one index, handle or name from list.");
        }

        double distance = Units.MmToM(Units.RequireFinite(spec.Distance ?? throw ToolException.InvalidArgument(
            "move needs 'distance': mm the dimension line stands off what it measures, along the normal of its plane."), "distance"));
        ModelDocument model = DocumentOf(context, documentHandle, spec.Dimensions);
        (Dimension dimension, int index) = Find(context, DimensionsOf(model), key);
        double value = dimension.Value;
        double previous = dimension.PMITrackDistance;
        try
        {
            dimension.PMITrackDistance = distance;
            if (Math.Abs(dimension.PMITrackDistance - distance) > LengthTolerance
                || Math.Abs(dimension.Value - value) > (IsAngle(dimension) ? AngleTolerance : LengthTolerance))
            {
                throw new ToolException("dimension_failed", $"Solid Edge did not move the dimension to {Geo.Mm(distance)} mm; it was put back.",
                    "Give another 'distance'.");
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
        {
            try
            {
                dimension.PMITrackDistance = previous;
            }
            catch (COMException)
            {
                // Left where Solid Edge put it.
            }

            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("dimension_failed", $"Solid Edge could not move the dimension: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the dimension with se_pmi list.", ex);
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            dimension = Describe(context, model, dimension, index),
            distance = Geo.Mm(dimension.PMITrackDistance),
            previous = Geo.Mm(previous),
        };
    }

    private static object AddDimension(SeContext context, string? documentHandle, PmiSpec spec)
    {
        string type = spec.Type is { } t && DimensionTypes.Contains(t)
            ? t
            : throw ToolException.InvalidArgument(spec.Type is null ? "dimension needs 'type'." : $"Unknown dimension type '{spec.Type}'.",
                $"Use one of: {string.Join(", ", DimensionTypes)}.");
        double? track = spec.Distance is { } d ? Units.MmToM(Units.RequireFinite(d, "distance")) : null;
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiOf(model);
        PmiAnnotationService.Frame frame = PmiAnnotationService.FrameOf(context, model, pmi, spec.Plane);
        (DimTypeConstants apiType, EdgePick[][] attempts, double[] expected) = type switch
        {
            "linear" => Linear(model, spec.Points, frame),
            "angle" => One(Angular(model, spec.Points)),
            _ => One(Circular(model, spec.Points, type)),
        };
        bool angle = type == "angle";
        Dimensions dimensions = DimensionsOf(model);
        int before = dimensions.Count;
        Dimension? dimension = null;
        double value = double.NaN;
        ToolException? rejected = null;
        foreach (EdgePick[] parents in attempts)
        {
            Dimension placed;
            try
            {
                placed = Place(dimensions, before, frame, apiType, parents, type);
            }
            catch (ToolException ex) when (attempts.Length > 1)
            {
                rejected = ex;
                continue;
            }

            try
            {
                value = placed.Value;
                if (dimensions.Count != before + 1)
                {
                    throw new ToolException("dimension_failed", "Solid Edge did not add the dimension to the PMI.", "Check it with se_pmi list.");
                }
            }
            catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
            {
                Delete(placed);
                throw ex is ToolException ? ex
                    : new ToolException("dimension_failed", $"Solid Edge rejected the {type} dimension: {ComErrors.Unwrap(ex).Message.Trim()}", "Pick the elements again.", ex);
            }

            if (expected.Any(e => Math.Abs(value - e) <= (angle ? AngleTolerance : LengthTolerance)))
            {
                dimension = placed;
                break;
            }

            Delete(placed);
        }

        if (dimension is null)
        {
            throw double.IsNaN(value) && rejected is not null ? rejected
                : new ToolException("dimension_failed",
                    $"The new dimension measures {Text(value, angle)}, not {string.Join(" or ", expected.Select(e => Text(e, angle)))} as the geometry does.",
                    "Pick the elements again, or use another plane.");
        }

        if (track is { } distance)
        {
            try
            {
                dimension.PMITrackDistance = distance;
                if (Math.Abs(dimension.PMITrackDistance - distance) > LengthTolerance)
                {
                    throw new ToolException("dimension_failed", $"Solid Edge did not keep the distance of {Geo.Mm(distance)} mm.", "Give another 'distance'.");
                }
            }
            catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
            {
                Delete(dimension);
                throw ex is ToolException ? ex
                    : new ToolException("dimension_failed", $"Solid Edge rejected the {type} dimension: {ComErrors.Unwrap(ex).Message.Trim()}", "Pick the elements again.", ex);
            }
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            plane = frame.Name,
            dimension = Describe(context, model, dimension, before + 1),
            distance = Geo.Mm(dimension.PMITrackDistance),
        };
    }

    private static (DimTypeConstants, EdgePick[][], double[]) One((DimTypeConstants Type, EdgePick[] Parents, double[] Expected) made) =>
        (made.Type, [made.Parents], made.Expected);

    /// <summary>Places a dimension on the parents; on an error, whatever Solid Edge left in the collection is removed.</summary>
    private static Dimension Place(Dimensions dimensions, int before, PmiAnnotationService.Frame frame, DimTypeConstants apiType, EdgePick[] parents, string type)
    {
        try
        {
            DimInitData data = dimensions.DimInitData;
            data.ClearCreationData();
            data.SetType(apiType);
            data.SetPlane(frame.Plane);
            data.SetNumberOfParents(parents.Length);
            for (int i = 0; i < parents.Length; i++)
            {
                double[] p = parents[i].Point;
                data.SetParentByIndex(i, parents[i].Edge, parents[i].KeyPoint, false, false, false, p[0], p[1], p[2]);
            }

            double[] at = parents[0].Point;
            data.SetDimPosition(at[0], at[1], at[2]);
            return dimensions.AddDimension(data)
                ?? throw new ToolException("dimension_failed", "Solid Edge did not place the dimension.", "Check the plane and the picked edges.");
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
        {
            // Verified: a dimension Solid Edge rejects (0x80040B30) may be counted for a moment; whatever is left is removed.
            for (int i = dimensions.Count; i > before; i--)
            {
                dimensions.Item(i).Delete();
            }

            throw ex is ToolException ? ex
                : new ToolException("dimension_failed", $"Solid Edge could not place the {type} dimension: {ComErrors.Unwrap(ex).Message.Trim()}",
                    "Pick the vertices, circle or straight edges again (se_geometry edges), in a plane they are not square to.", ex);
        }
    }

    private static void Delete(Dimension dimension)
    {
        try
        {
            dimension.Delete();
        }
        catch (COMException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Between two vertices, or the length of one straight edge. The value may be the distance itself, its projection onto the
    /// plane or onto one of the plane's axes (aligned, horizontal or vertical as Solid Edge chooses). A vertex is given by an
    /// edge that ends there, and Solid Edge may take another key point of it (on a loft, the far end of a B-spline edge:
    /// 77.39 mm instead of 53.85), so every edge at each vertex is an attempt, straight edges first, until the value matches.
    /// </summary>
    private static (DimTypeConstants, EdgePick[][], double[]) Linear(ModelDocument model, double[]? values, PmiAnnotationService.Frame frame)
    {
        int count = values is { Length: 3 } ? 1 : 2;
        List<double[]> points = PmiAnnotationService.ModelPoints(values, "points", count, "two vertices, or one point on a straight edge (its length)");
        EdgePick[] parents;
        if (count == 1)
        {
            EdgePick edge = PmiAnnotationService.PickEdge(model, points[0], "straight edge", PmiAnnotationService.IsLine);
            (double[] start, double[] end) = PmiAnnotationService.Ends(edge.Edge);
            parents = [new EdgePick(edge.Edge, start, true), new EdgePick(edge.Edge, end, true)];
        }
        else
        {
            parents = [.. points.Select(p => PmiAnnotationService.PickEdge(model, p, "edge", _ => true))];
            if (parents.FirstOrDefault(p => !p.KeyPoint) is { } loose)
            {
                throw ToolException.InvalidArgument($"({string.Join(", ", loose.Point.Select(Geo.Mm))}) mm is not a vertex.",
                    "A linear dimension between two points needs vertices (ends of edges, se_geometry edges), or one point on a straight edge for its length.");
            }
        }

        double[] span = [.. parents[1].Point.Select((c, k) => c - parents[0].Point[k])];
        double length = Geo.Length(span);
        if (length < LengthTolerance)
        {
            throw ToolException.InvalidArgument("The two points are the same vertex.");
        }

        double alongX = Math.Abs(span.Select((c, k) => c * frame.X[k]).Sum());
        double alongY = Math.Abs(span.Select((c, k) => c * frame.Y[k]).Sum());
        double[] expected = [.. new[] { length, Math.Sqrt((alongX * alongX) + (alongY * alongY)), alongX, alongY }.Where(v => v >= LengthTolerance)];
        if (count == 1)
        {
            return (DimTypeConstants.igDimTypeLinear, [parents], expected);
        }

        List<EdgePick>[] choices = [.. parents.Select(p => PmiAnnotationService.VertexEdges(model, p.Point) is { Count: > 0 } found ? found : [p])];
        EdgePick[][] attempts = [.. choices[0].Take(4).SelectMany(a => choices[1].Take(4).Select(b => new[] { a, b }))];
        return (DimTypeConstants.igDimTypeLinear, attempts, expected);
    }

    private static (DimTypeConstants, EdgePick[], double[]) Circular(ModelDocument model, double[]? values, string type)
    {
        double[] point = PmiAnnotationService.ModelPoints(values, "points", 1, "on the circular edge")[0];
        EdgePick circle = PmiAnnotationService.PickEdge(model, point, "circular edge", PmiAnnotationService.IsCircle);
        double radius = PmiAnnotationService.CircleOf(circle.Edge).Radius;
        return type == "diameter"
            ? (DimTypeConstants.igDimTypeCDiameter, [circle], [2 * radius])
            : (DimTypeConstants.igDimTypeRadial, [circle], [radius]);
    }

    /// <summary>Between two straight edges: θ, 180 − θ, 180 + θ or 360 − θ, as Solid Edge measures it.</summary>
    private static (DimTypeConstants, EdgePick[], double[]) Angular(ModelDocument model, double[]? values)
    {
        List<double[]> points = PmiAnnotationService.ModelPoints(values, "points", 2, "one point on each of two straight edges");
        EdgePick[] edges = [.. points.Select(p => PmiAnnotationService.PickEdge(model, p, "straight edge", PmiAnnotationService.IsLine))];
        if (Late.SameObject(edges[0].Edge, edges[1].Edge))
        {
            throw ToolException.InvalidArgument("Both points pick the same edge.", "Give one point on each of two straight edges.");
        }

        double[][] directions = [.. edges.Select(e =>
        {
            (double[] start, double[] end) = PmiAnnotationService.Ends(e.Edge);
            double[] d = [.. end.Select((c, k) => c - start[k])];
            double length = Geo.Length(d);
            return d.Select(c => c / length).ToArray();
        })];
        double cos = Math.Clamp(directions[0].Select((c, k) => c * directions[1][k]).Sum(), -1, 1);
        double theta = Math.Acos(cos);
        if (theta < AngleTolerance || Math.PI - theta < AngleTolerance)
        {
            throw ToolException.InvalidArgument("The two edges are parallel: they make no angle.", "Pick two edges that meet at an angle.");
        }

        EdgePick[] parents = [.. edges.Select(e => e with { KeyPoint = false })];
        return (DimTypeConstants.igDimTypeAngular, parents, [theta, Math.PI - theta, Math.PI + theta, (2 * Math.PI) - theta]);
    }
}
