using System.Runtime.InteropServices;
using SolidEdgeFramework;
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
/// Reference planes and coordinate systems: list (with normal and root point) and create offset, angled, three-point, mid,
/// normal-to-edge and tangent planes, and coordinate systems. Each new plane is checked against the geometry it comes from.
/// </summary>
public static class RefPlaneService
{
    private const double Tolerance = 1e-7;

    public static object List(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        RefPlanes planes = model.RefPlanes;
        var items = new List<object>();
        for (int i = 1; i <= planes.Count && items.Count < context.MaxListItems; i++)
        {
            items.Add(Describe(context, model, planes.Item(i), i));
        }

        return new { count = planes.Count, planes = items };
    }

    /// <summary>Plane parallel to another plane or planar face, at a distance in mm.</summary>
    public static object Offset(SeContext context, string? documentHandle, string parent, double? distance, bool flip)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        double d = Units.MmToM(Units.RequireFinite(distance ?? throw ToolException.InvalidArgument("Action 'offset' requires 'distance' (mm)."), "distance"));
        object parentPlane = ResolveParent(context, model, parent);
        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        RefPlane plane = model.RefPlanes.AddParallelByDistance(parentPlane, Math.Abs(d), d < 0 ? Flip(side) : side);
        return Describe(context, model, plane, model.RefPlanes.Count);
    }

    /// <summary>Plane rotated by an angle (degrees) relative to another plane, about its intersection with the pivot plane.</summary>
    public static object Angle(SeContext context, string? documentHandle, string parent, string pivot, double? angle, bool flip)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        double a = Units.DegToRad(Units.RequireFinite(angle ?? throw ToolException.InvalidArgument("Action 'angle' requires 'angle' (degrees)."), "angle"));
        object parentPlane = ResolveParent(context, model, parent);
        object pivotPlane = ResolveParent(context, model, pivot, "pivot");
        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        RefPlane plane = model.RefPlanes.AddAngularByAngle(parentPlane, a, side, pivotPlane, ReferenceElementConstants.igPivotEnd);
        return Describe(context, model, plane, model.RefPlanes.Count);
    }

    /// <summary>Plane through three points of the model: vertices, or midpoints of straight edges (mm).</summary>
    public static object ThreePoints(SeContext context, string? documentHandle, double[]? points)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        if (points is not { Length: 9 })
        {
            throw ToolException.InvalidArgument("'points' must be three model points [x1,y1,z1, x2,y2,z2, x3,y3,z3] in mm: vertices or midpoints of straight edges.");
        }

        double[][] targets = Geo.Points3(points, "points", 3).Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
        double[] normal = Geo.Cross(Minus(targets[1], targets[0]), Minus(targets[2], targets[0]));
        if (Geo.Length(normal) < 1e-9)
        {
            throw ToolException.InvalidArgument("The three points are in line: they do not define a plane.");
        }

        Edge[] edges = Late.Items(((Body)model.RequireModel().Body).Edges[TopologyQuery.igQueryAll]).Cast<Edge>().ToArray();
        Array edgeSet = new object[3];
        Array keyPoints = new int[3];
        for (int i = 0; i < 3; i++)
        {
            (Edge edge, KeyPointType type) = Snap(edges, targets[i]) ?? throw ToolException.InvalidArgument(
                $"There is no vertex or straight-edge midpoint of the model at ({string.Join(", ", targets[i].Select(Geo.Mm))}).",
                "Use edge ends or midpoints from se_geometry edges (start, end).");
            edgeSet.SetValue(edge, i);
            keyPoints.SetValue((int)type, i);
        }

        RefPlane plane = Created(model, () => model.RefPlanes.AddBy3Points(3, ref edgeSet, ref keyPoints));
        return Checked(context, model, plane, Unit(normal), targets[0], "three points");
    }

    /// <summary>Plane midway between two parallel planes or planar faces.</summary>
    public static object Mid(SeContext context, string? documentHandle, string first, string second)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object a = ResolveParent(context, model, first);
        object b = ResolveParent(context, model, second, "plane2");
        (double[] rootA, double[] normalA) = PlaneData(a);
        (double[] rootB, double[] normalB) = PlaneData(b);
        if (Math.Abs(Math.Abs(Dot(normalA, normalB)) - 1) > 1e-6)
        {
            throw ToolException.InvalidArgument("'plane' and 'plane2' are not parallel.", "A mid plane needs two parallel planes or faces.");
        }

        double gap = Dot(normalA, Minus(rootB, rootA));
        RefPlane plane = Created(model, () => model.RefPlanes.AddMidPlane(a, b, null!, ReferenceElementConstants.igPivotEnd, false, false));
        double[] point = rootA.Select((c, k) => c + normalA[k] * gap / 2).ToArray();
        return Checked(context, model, plane, normalA, point, "mid");
    }

    /// <summary>
    /// Plane normal to an edge at its start or end, or at a distance (mm) along it from the start or the end. The X
    /// direction of the plane is taken from the base plane most perpendicular to the edge.
    /// </summary>
    public static object NormalToEdge(SeContext context, string? documentHandle, string edgeHandle, string at, double? distance)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, edgeHandle);
        Edge edge = context.Handles.Resolve<Edge>(edgeHandle, "edge");
        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        double[] s = Geo.Doubles(start).ToArray();
        double[] e = Geo.Doubles(end).ToArray();
        ReferenceElementConstants from = at switch
        {
            "start" => ReferenceElementConstants.igCurveStart,
            "end" => ReferenceElementConstants.igCurveEnd,
            _ => throw ToolException.InvalidArgument($"Unknown 'at' value '{at}'.", "Use start or end."),
        };
        double[] chord = Minus(e, s);
        if (Geo.Length(chord) < 1e-9)
        {
            throw ToolException.InvalidArgument("The edge is closed (a full circle); pick an open edge.");
        }

        RefPlane orientation = Enumerable.Range(1, 3).Select(i => model.RefPlanes.Item(i))
            .MinBy(p => Math.Abs(Dot(PlaneData(p).Normal, Unit(chord))))!;
        RefPlane plane;
        double[]? expectedPoint = null;
        if (distance is { } d)
        {
            double metres = Units.MmToM(Units.RequirePositive(d, "distance"));
            plane = Created(model, () => model.RefPlanes.AddNormalToCurveAtDistance(edge, from, orientation, ReferenceElementConstants.igPivotEnd, metres));
            if (edge.Geometry is SolidEdgeGeometry.Line)
            {
                double[] origin = at == "start" ? s : e;
                double[] direction = Unit(at == "start" ? chord : Minus(s, e));
                expectedPoint = origin.Select((c, k) => c + direction[k] * metres).ToArray();
            }
        }
        else
        {
            plane = Created(model, () => model.RefPlanes.AddNormalToCurve(edge, from, orientation, ReferenceElementConstants.igPivotEnd));
            expectedPoint = at == "start" ? s : e;
        }

        // For a straight edge the plane is normal to it; for a curve only the point is checked.
        double[]? expectedNormal = edge.Geometry is SolidEdgeGeometry.Line ? Unit(chord) : null;
        return Checked(context, model, plane, expectedNormal, expectedPoint, "normal to edge");
    }

    /// <summary>Plane parallel to a plane and tangent to a cylindrical or conical face, on one side or the other.</summary>
    public static object Tangent(SeContext context, string? documentHandle, string parent, string faceHandle, bool flip)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, faceHandle);
        object parentPlane = ResolveParent(context, model, parent);
        Face face = context.Handles.Resolve<Face>(faceHandle, "face");
        if (face.Geometry is not (SolidEdgeGeometry.Cylinder or SolidEdgeGeometry.Cone))
        {
            throw ToolException.InvalidArgument($"'{faceHandle}' is not a cylindrical or conical face.", "Use se_geometry faces with type 'cylinder'.");
        }

        KeyPointExtentConstants side = flip ? KeyPointExtentConstants.igReverseTangentNormal : KeyPointExtentConstants.igTangentNormal;
        RefPlane plane = Created(model, () => model.RefPlanes.AddParallelByTangent(parentPlane, face, side));
        return Checked(context, model, plane, PlaneData(parentPlane).Normal, null, "tangent");
    }

    /// <summary>Coordinate system at an origin (mm) rotated by angles (degrees) about X, then Y, then Z.</summary>
    public static object AddCoordinateSystem(SeContext context, string? documentHandle, double[]? origin, double[]? angles)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        if (origin is { Length: not 3 } || angles is { Length: not 3 })
        {
            throw ToolException.InvalidArgument("'origin' must be [x,y,z] in mm and 'angles' [ax,ay,az] in degrees.");
        }

        double[] o = (origin ?? [0, 0, 0]).Select(v => Units.MmToM(Units.RequireFinite(v, "origin"))).ToArray();
        double[] r = (angles ?? [0, 0, 0]).Select(v => Units.DegToRad(Units.RequireFinite(v, "angles"))).ToArray();
        var systems = (CoordinateSystems)Late.Get(model.Document, "CoordinateSystems")!;
        int before = systems.Count;
        SolidEdgePart.CoordinateSystem system = systems.Add(o[0], o[1], o[2], r[0], r[1], r[2]);
        if (systems.Count != before + 1)
        {
            throw new ToolException("refplane_failed", "Solid Edge did not add the coordinate system.", "Check the document with se_feature_tree.");
        }

        return DescribeSystem(context, model, system);
    }

    public static object ListCoordinateSystems(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        var systems = (CoordinateSystems)Late.Get(model.Document, "CoordinateSystems")!;
        object[] items = Late.Items(systems).OfType<SolidEdgePart.CoordinateSystem>().Select(s => DescribeSystem(context, model, s)).ToArray();
        return new { count = items.Length, coordinateSystems = items };
    }

    /// <summary>
    /// Origin and axes read from the planes of the system: the XY normal is Z, the YZ normal is X, and the origin is where
    /// the three planes meet.
    /// </summary>
    private static object DescribeSystem(SeContext context, ModelDocument model, SolidEdgePart.CoordinateSystem system)
    {
        (double[] rootXy, double[] z) = PlaneData(system.Plane[CoordinateSystemFeatureConstants.seCoordSysXYPlane]);
        (double[] rootYz, double[] x) = PlaneData(system.Plane[CoordinateSystemFeatureConstants.seCoordSysYZPlane]);
        double[] rootZx = PlaneData(system.Plane[CoordinateSystemFeatureConstants.seCoordSysZXPlane]).Root;
        double[] y = Geo.Cross(z, x);
        double[] origin = x.Select((_, k) => x[k] * Dot(x, rootYz) + y[k] * Dot(y, rootZx) + z[k] * Dot(z, rootXy)).ToArray();
        return new
        {
            handle = context.Register(system, "coordsys", model.Document),
            name = system.Name,
            origin = origin.Select(Geo.Mm).ToArray(),
            xAxis = x.Select(Geo.R).ToArray(),
            yAxis = y.Select(Geo.R).ToArray(),
            zAxis = z.Select(Geo.R).ToArray(),
        };
    }

    /// <summary>Edge and key point (start, end or the middle of a straight edge) at a model point.</summary>
    private static (Edge Edge, KeyPointType Type)? Snap(Edge[] edges, double[] point)
    {
        foreach (Edge edge in edges)
        {
            Array start = new double[3];
            Array end = new double[3];
            edge.GetEndPoints(ref start, ref end);
            double[] s = Geo.Doubles(start).ToArray();
            double[] e = Geo.Doubles(end).ToArray();
            if (Geo.Length(Minus(s, point)) < Tolerance)
            {
                return (edge, KeyPointType.igKeyPointStart);
            }

            if (Geo.Length(Minus(e, point)) < Tolerance)
            {
                return (edge, KeyPointType.igKeyPointEnd);
            }

            if (edge.Geometry is SolidEdgeGeometry.Line && Geo.Length(Minus(s.Select((c, k) => (c + e[k]) / 2).ToArray(), point)) < Tolerance)
            {
                return (edge, KeyPointType.igKeyPointMiddle);
            }
        }

        return null;
    }

    private static RefPlane Created(ModelDocument model, Func<RefPlane> create)
    {
        int before = model.RefPlanes.Count;
        RefPlane plane;
        try
        {
            plane = create();
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("refplane_failed", $"Solid Edge did not create the plane ({ex.Message}).", "Check the inputs with se_geometry.");
        }

        return model.RefPlanes.Count > before
            ? plane
            : throw new ToolException("refplane_failed", "Solid Edge did not add the plane.", "Check the inputs with se_geometry.");
    }

    /// <summary>Checks the new plane (normal parallel to the expected one, expected point on it); deletes it if it is wrong.</summary>
    private static object Checked(SeContext context, ModelDocument model, RefPlane plane, double[]? normal, double[]? point, string what)
    {
        (double[] root, double[] actual) = PlaneData(plane);
        bool parallel = normal is null || Math.Abs(Math.Abs(Dot(actual, normal)) - 1) < 1e-6;
        bool onPlane = point is null || Math.Abs(Dot(actual, Minus(point, root))) < Tolerance;
        if (!parallel || !onPlane)
        {
            try
            {
                plane.Delete();
            }
            catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
            {
                // The error being reported is the wrong plane.
            }

            throw new ToolException("refplane_failed", $"The {what} plane is not where expected and was deleted.", "Check the inputs with se_geometry.");
        }

        return Describe(context, model, plane, model.RefPlanes.Count);
    }

    internal static (double[] Root, double[] Normal) PlaneData(object source)
    {
        Array root = new double[3];
        Array normal = new double[3];
        switch (source)
        {
            case RefPlane refPlane:
                refPlane.GetRootPoint(ref root);
                refPlane.GetNormal(ref normal);
                break;
            case Face { Geometry: SolidEdgeGeometry.Plane plane }:
                plane.GetPlaneData(ref root, ref normal);
                break;
            default:
                throw new ToolException("handle_wrong_type", "Not a reference plane or a planar face.");
        }

        return (Geo.Doubles(root).ToArray(), Unit(Geo.Doubles(normal).ToArray()));
    }

    private static double[] Minus(double[] a, double[] b) => a.Select((c, k) => c - b[k]).ToArray();

    private static double Dot(double[] a, double[] b) => a.Select((c, k) => c * b[k]).Sum();

    private static double[] Unit(double[] v)
    {
        double length = Geo.Length(v);
        return v.Select(c => c / length).ToArray();
    }

    private static ReferenceElementConstants Flip(ReferenceElementConstants side) =>
        side == ReferenceElementConstants.igNormalSide ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;

    private static object ResolveParent(SeContext context, ModelDocument model, string value, string argumentName = "plane")
    {
        if (int.TryParse(value, out _))
        {
            return model.ResolvePlane(context, value, argumentName);
        }

        object resolved = context.Handles.Resolve(value, argumentName);
        return resolved is RefPlane or SolidEdgeGeometry.Face
            ? resolved
            : throw new ToolException("handle_wrong_type", $"'{value}' in '{argumentName}' is not a reference plane or a planar face.");
    }

    private static object Describe(SeContext context, ModelDocument model, RefPlane plane, int index)
    {
        Array normal = new double[3];
        Array root = new double[3];
        plane.GetNormal(ref normal);
        plane.GetRootPoint(ref root);
        return new
        {
            handle = context.Register(plane, "refplane", model.Document),
            index,
            name = plane.Name,
            normal = Geo.Plain(normal),
            rootPoint = Geo.Mm(root),
            visible = plane.Visible,
        };
    }
}
