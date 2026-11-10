// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
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
/// normal-to-edge and tangent planes, and coordinate systems; show, hide, rename and delete them. Each new plane is checked
/// against the geometry it comes from. Part planes are made global (<c>Local: false</c>), as the Planes commands make them:
/// the API's default is a local plane, which has no name, is not in the feature tree and cannot be renamed (E_FAIL) or
/// deleted (E_INVALIDARG). Assembly planes: RefPlaneService.Assembly.cs.
/// </summary>
public static partial class RefPlaneService
{
    private const double Tolerance = 1e-7;

    public static object List(SeContext context, string? documentHandle)
    {
        if (!string.IsNullOrWhiteSpace(documentHandle) && context.Handles.Resolve(documentHandle, "document") is SolidEdgeAssembly.Occurrence occurrence)
        {
            return OccurrenceList(context, occurrence);
        }

        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (document is SolidEdgeAssembly.AssemblyDocument assembly)
        {
            return AssemblyList(context, assembly);
        }

        ModelDocument model = ModelDocument.From(document);
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
        double d = Units.MmToM(Units.RequireFinite(distance ?? throw ToolException.InvalidArgument("Action 'offset' requires 'distance' (mm)."), "distance"));
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (document is SolidEdgeAssembly.AssemblyDocument assembly)
        {
            return AssemblyOffset(context, assembly, parent, flip ? -d : d);
        }

        ModelDocument model = ModelDocument.From(document);
        object parentPlane = ResolveParent(context, model, parent);
        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        RefPlane plane = model.RefPlanes.AddParallelByDistance(parentPlane, Math.Abs(d), d < 0 ? Flip(side) : side, Local: false);
        return Describe(context, model, plane, model.RefPlanes.Count);
    }

    /// <summary>
    /// Plane rotated by an angle (degrees) relative to another plane, about its intersection with the pivot plane, or about a
    /// straight edge: 90 degrees is the Perpendicular plane, 0 the Coincident by Axis one (on the parent, its x axis along the
    /// edge). Verified: on the top of a 100 x 60 x 20 block with a top edge, 90 gives the plane through the edge square to the
    /// top and 0 the top itself; the x axis runs along the edge away from the pivot origin (igPivotStart: the edge start).
    /// </summary>
    public static object Angle(SeContext context, string? documentHandle, string parent, string pivot, double? angle, bool flip, EdgeEnd at = EdgeEnd.Start)
    {
        double a = Units.DegToRad(Units.RequireFinite(angle ?? throw ToolException.InvalidArgument("Action 'angle' requires 'angle' (degrees)."), "angle"));
        ReferenceElementConstants origin = at switch
        {
            EdgeEnd.Start => ReferenceElementConstants.igPivotStart,
            EdgeEnd.End => ReferenceElementConstants.igPivotEnd,
            _ => throw new ArgumentOutOfRangeException(nameof(at)),
        };
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (document is SolidEdgeAssembly.AssemblyDocument assembly)
        {
            return AssemblyAngle(context, assembly, parent, pivot, a, flip, origin);
        }

        ModelDocument model = ModelDocument.From(document);
        object parentPlane = ResolveParent(context, model, parent);
        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        if (!int.TryParse(pivot, out _) && !OrientedPlanes.TryAxis(pivot, out _) && context.Handles.Resolve(pivot, "pivot") is Edge edge)
        {
            (double[] s, double[] e) = StraightEdge(edge, pivot);
            RequireOnParent(PlaneData(parentPlane), s, e, pivot);
            RefPlane turned = Created(model, () => model.RefPlanes.AddAngularByAngle(parentPlane, a, side, edge, origin, Local: false));
            return OnHinge(turned, PlaneData(parentPlane).Normal, a, s, e)
                ? Describe(context, model, turned, model.RefPlanes.Count)
                : Discard(turned, "angle");
        }

        object pivotPlane = ResolveParent(context, model, pivot, "pivot");
        RefPlane plane = model.RefPlanes.AddAngularByAngle(parentPlane, a, side, pivotPlane, ReferenceElementConstants.igPivotEnd, Local: false);
        return Describe(context, model, plane, model.RefPlanes.Count);
    }

    /// <summary>The ends of a straight edge used as a hinge (m).</summary>
    private static (double[] Start, double[] End) StraightEdge(Edge edge, string handle)
    {
        if (edge.Geometry is not SolidEdgeGeometry.Line)
        {
            throw ToolException.InvalidArgument($"'{handle}' in 'pivot' is not a straight edge.", "Use a straight edge (se_geometry edges with type 'line') or a plane.");
        }

        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        return (Geo.Doubles(start).ToArray(), Geo.Doubles(end).ToArray());
    }

    /// <summary>The hinge edge must lie on the parent plane or face: the new plane turns about it from there.</summary>
    private static void RequireOnParent((double[] Root, double[] Normal) parent, double[] start, double[] end, string handle)
    {
        if (Math.Abs(Geo.Dot(parent.Normal, Geo.Minus(start, parent.Root))) > Tolerance || Math.Abs(Geo.Dot(parent.Normal, Geo.Minus(end, parent.Root))) > Tolerance)
        {
            throw ToolException.InvalidArgument($"The edge '{handle}' in 'pivot' does not lie on 'plane': the new plane turns about it from there.",
                "Pick an edge of that face (se_geometry edges with 'source' = the face) or a plane.");
        }
    }

    /// <summary>Whether a plane turned about an edge holds both its ends and makes the angle with the parent's normal.</summary>
    private static bool OnHinge(object plane, double[] parentNormal, double radians, double[] start, double[] end)
    {
        (double[] root, double[] normal) = PlaneData(plane);
        return Math.Abs(Geo.Dot(normal, Geo.Minus(start, root))) < Tolerance && Math.Abs(Geo.Dot(normal, Geo.Minus(end, root))) < Tolerance
            && Math.Abs(Math.Abs(Geo.Dot(normal, parentNormal)) - Math.Abs(Math.Cos(radians))) < 1e-6;
    }

    /// <summary>Plane through three points of the model: vertices, or midpoints of straight edges (mm).</summary>
    public static object ThreePoints(SeContext context, string? documentHandle, double[]? points)
    {
        ModelDocument model = PartOnly(context.GetDocument(documentHandle), "three-point planes");
        if (points is not { Length: 9 })
        {
            throw ToolException.InvalidArgument("'points' must be three model points [x1,y1,z1, x2,y2,z2, x3,y3,z3] in mm: vertices or midpoints of straight edges.");
        }

        double[][] targets = Geo.Points3(points, "points", 3).Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
        double[] normal = Geo.Cross(Geo.Minus(targets[1], targets[0]), Geo.Minus(targets[2], targets[0]));
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

        RefPlane plane = Created(model, () => model.RefPlanes.AddBy3Points(3, ref edgeSet, ref keyPoints, Local: false));
        return Checked(context, model, plane, Geo.Unit(normal), targets[0], "three points");
    }

    /// <summary>Plane midway between two parallel planes or planar faces.</summary>
    public static object Mid(SeContext context, string? documentHandle, string first, string second)
    {
        ModelDocument model = PartOnly(context.GetDocument(documentHandle), "mid planes");
        object a = ResolveParent(context, model, first);
        object b = ResolveParent(context, model, second, "plane2");
        (double[] rootA, double[] normalA) = PlaneData(a);
        (double[] rootB, double[] normalB) = PlaneData(b);
        if (Math.Abs(Math.Abs(Geo.Dot(normalA, normalB)) - 1) > 1e-6)
        {
            throw ToolException.InvalidArgument("'plane' and 'plane2' are not parallel.", "A mid plane needs two parallel planes or faces.");
        }

        double gap = Geo.Dot(normalA, Geo.Minus(rootB, rootA));
        RefPlane plane = Created(model, () => model.RefPlanes.AddMidPlane(a, b, null!, ReferenceElementConstants.igPivotEnd, false, false));
        double[] point = rootA.Select((c, k) => c + normalA[k] * gap / 2).ToArray();
        return Checked(context, model, plane, normalA, point, "mid");
    }

    /// <summary>
    /// Plane normal to an edge at its start or end, or at a distance (mm) or a fraction of its length along it from the start
    /// or the end. The X direction of the plane is taken from the base plane most perpendicular to the edge.
    /// </summary>
    public static object NormalToEdge(SeContext context, string? documentHandle, string edgeHandle, EdgeEnd at, double? distance, double? ratio = null)
    {
        if (distance is not null && ratio is not null)
        {
            throw ToolException.InvalidArgument("Give 'distance' or 'ratio', not both.");
        }

        if (ratio is { } r && !(r >= 0 && r <= 1))
        {
            throw ToolException.InvalidArgument("'ratio' is a fraction of the edge length, from 0 to 1.");
        }

        ReferenceElementConstants from = at switch
        {
            EdgeEnd.Start => ReferenceElementConstants.igCurveStart,
            EdgeEnd.End => ReferenceElementConstants.igCurveEnd,
            _ => throw new ArgumentOutOfRangeException(nameof(at)),
        };
        SolidEdgeDocument document = string.IsNullOrWhiteSpace(documentHandle) ? context.DocumentOf(edgeHandle) : context.GetDocument(documentHandle);
        if (document is SolidEdgeAssembly.AssemblyDocument assembly)
        {
            return AssemblyNormalToEdge(context, assembly, edgeHandle, from, distance, ratio);
        }

        ModelDocument model = ModelDocument.From(document);
        Edge edge = context.Handles.Resolve<Edge>(edgeHandle, "edge");
        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        double[] s = Geo.Doubles(start).ToArray();
        double[] e = Geo.Doubles(end).ToArray();
        double[] chord = Geo.Minus(e, s);
        if (Geo.Length(chord) < 1e-9)
        {
            throw ToolException.InvalidArgument("The edge is closed (a full circle); pick an open edge.");
        }

        RefPlane orientation = OrientedPlanes.Bases(Late.Items(model.RefPlanes)).OfType<RefPlane>()
            .MinBy(p => Math.Abs(Geo.Dot(PlaneData(p).Normal, Geo.Unit(chord))))!;
        RefPlane plane;
        double[]? expectedPoint = null;
        if (ratio is { } fraction)
        {
            plane = Created(model, () => model.RefPlanes.AddNormalToCurveAtArcLengthRatio(edge, orientation, fraction, 0,
                ReferenceElementConstants.igNormalSide, from, Local: false));
            if (edge.Geometry is SolidEdgeGeometry.Line)
            {
                double[] origin = at == EdgeEnd.Start ? s : e;
                double[] other = at == EdgeEnd.Start ? e : s;
                expectedPoint = origin.Select((c, k) => c + (other[k] - c) * fraction).ToArray();
            }
        }
        else if (distance is { } d)
        {
            double metres = Units.MmToM(Units.RequirePositive(d, "distance"));
            plane = Created(model, () => model.RefPlanes.AddNormalToCurveAtDistance(edge, from, orientation, ReferenceElementConstants.igPivotEnd, metres, Local: false));
            if (edge.Geometry is SolidEdgeGeometry.Line)
            {
                double[] origin = at == EdgeEnd.Start ? s : e;
                double[] direction = Geo.Unit(at == EdgeEnd.Start ? chord : Geo.Minus(s, e));
                expectedPoint = origin.Select((c, k) => c + direction[k] * metres).ToArray();
            }
        }
        else
        {
            plane = Created(model, () => model.RefPlanes.AddNormalToCurve(edge, from, orientation, ReferenceElementConstants.igPivotEnd, Local: false));
            expectedPoint = at == EdgeEnd.Start ? s : e;
        }

        // For a straight edge the plane is normal to it; for a curve only the point is checked.
        double[]? expectedNormal = edge.Geometry is SolidEdgeGeometry.Line ? Geo.Unit(chord) : null;
        return Checked(context, model, plane, expectedNormal, expectedPoint, "normal to edge");
    }

    /// <summary>
    /// Plane tangent to a face: parallel to a plane and tangent to a cylindrical or conical face, on one side or the other; with
    /// <paramref name="point"/>, tangent to any curved face at a model point (an edge end on it); with <paramref name="angle"/>,
    /// tangent to a cylinder turned about its axis from a plane that contains the axis.
    /// </summary>
    public static object Tangent(SeContext context, string? documentHandle, string? parent, string faceHandle, bool flip, double[]? point = null, double? angle = null)
    {
        ModelDocument model = PartOnly(string.IsNullOrWhiteSpace(documentHandle) ? context.DocumentOf(faceHandle) : context.GetDocument(documentHandle), "tangent planes");
        Face face = context.Handles.Resolve<Face>(faceHandle, "face");
        if (point is not null)
        {
            return angle is null
                ? TangentAtPoint(context, model, parent, face, faceHandle, point, flip)
                : throw ToolException.InvalidArgument("Give 'points' (tangent at a point) or 'angle' (tangent to a cylinder at an angle), not both.");
        }

        object parentPlane = ResolveParent(context, model, parent ?? throw ToolException.InvalidArgument(
            "'plane' is required for tangent: the parallel plane, or with 'angle' a plane through the cylinder axis.",
            "Or give 'points' = [x,y,z] for a plane tangent to the face at that point."));
        if (angle is not null)
        {
            return TangentAtAngle(context, model, parentPlane, face, faceHandle, angle.Value, flip);
        }

        if (face.Geometry is not (SolidEdgeGeometry.Cylinder or SolidEdgeGeometry.Cone))
        {
            throw ToolException.InvalidArgument($"'{faceHandle}' is not a cylindrical or conical face.", "Use se_geometry faces with type 'cylinder'.");
        }

        KeyPointExtentConstants side = flip ? KeyPointExtentConstants.igReverseTangentNormal : KeyPointExtentConstants.igTangentNormal;
        RefPlane plane = Created(model, () => model.RefPlanes.AddParallelByTangent(parentPlane, face, side, Local: false));
        return Checked(context, model, plane, PlaneData(parentPlane).Normal, null, "tangent");
    }

    /// <summary>
    /// Plane tangent to a curved face at a model point (an end of an edge, on the face), its normal out of the face (flip: into
    /// it). The parent plane only sets the x axis of the new plane; by default the base plane most across the tangent plane.
    /// </summary>
    private static object TangentAtPoint(SeContext context, ModelDocument model, string? parent, Face face, string faceHandle, double[] point, bool flip)
    {
        if (face.Geometry is SolidEdgeGeometry.Plane)
        {
            throw ToolException.InvalidArgument($"'{faceHandle}' is planar: a plane on it is an offset at distance 0.", "Use action 'offset' with that face.");
        }

        if (point is not { Length: 3 })
        {
            throw ToolException.InvalidArgument("For tangent, 'points' is one model point [x,y,z] in mm: an end of an edge on the face.");
        }

        double[] target = Geo.Points3(point, "points", 1).Select(p => new[] { p.X, p.Y, p.Z }).First();
        Edge[] edges = Late.Items(((Body)model.RequireModel().Body).Edges[TopologyQuery.igQueryAll]).Cast<Edge>().ToArray();
        (Edge edge, KeyPointType type) = Snap(edges, target) ?? throw ToolException.InvalidArgument(
            $"There is no edge end of the model at ({string.Join(", ", target.Select(Geo.Mm))}).", "Use edge ends from se_geometry edges (start, end).");
        double[] outward = NormalAt(face, target) ?? throw ToolException.InvalidArgument(
            $"The point ({string.Join(", ", target.Select(Geo.Mm))}) is not on the face '{faceHandle}'.", "Use an end of an edge that bounds the face.");

        object parentPlane = parent is null
            ? new[] { "xy", "yz", "xz" }.Select(o => ResolveParent(context, model, o)).MinBy(p => Math.Abs(Geo.Dot(PlaneData(p).Normal, outward)))!
            : ResolveParent(context, model, parent);
        if (Math.Abs(Math.Abs(Geo.Dot(PlaneData(parentPlane).Normal, outward)) - 1) < 1e-6)
        {
            throw ToolException.InvalidArgument("'plane' is parallel to the tangent plane: it must cross it (it sets the x axis of the new plane).",
                "Omit 'plane' to use the base plane most across it.");
        }

        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        RefPlane plane = Created(model, () => model.RefPlanes.AddTangentToCurvedSurfaceAtKeyPoint(face, parentPlane, edge, type, 0, side, Local: false));
        return Checked(context, model, plane, flip ? outward.Select(c => -c).ToArray() : outward, target, "tangent", signed: true);
    }

    /// <summary>
    /// Plane tangent to a cylinder where it is turned <paramref name="angle"/> degrees about the axis from a plane that contains
    /// the axis, positive towards that plane's normal; its normal out of the face (flip: into it). Checked by the distance to the
    /// axis (the radius) and the angle of the normal with the plane.
    /// </summary>
    private static object TangentAtAngle(SeContext context, ModelDocument model, object parentPlane, Face face, string faceHandle, double angle, bool flip)
    {
        if (face.Geometry is not SolidEdgeGeometry.Cylinder cylinder)
        {
            throw ToolException.InvalidArgument($"'{faceHandle}' is not a cylindrical face: a tangent plane at an angle needs a cylinder.",
                "For other curved faces give 'points' = an edge end on the face.");
        }

        Array basePoint = new double[3];
        Array direction = new double[3];
        cylinder.GetCylinderData(ref basePoint, ref direction, out double radius);
        double[] axisPoint = Geo.Doubles(basePoint).ToArray();
        double[] axis = Geo.Unit(Geo.Doubles(direction).ToArray());
        (double[] parentRoot, double[] parentNormal) = PlaneData(parentPlane);
        if (Math.Abs(Geo.Dot(parentNormal, axis)) > 1e-6 || Math.Abs(Geo.Dot(parentNormal, Geo.Minus(axisPoint, parentRoot))) > Tolerance)
        {
            throw ToolException.InvalidArgument("'plane' must contain the cylinder axis: the angle is measured about the axis from it.",
                "Use a base plane or a reference plane through the axis.");
        }

        double radians = Units.DegToRad(Units.RequireFinite(angle, "angle"));
        FeaturePropertyConstants towards = radians >= 0 ? FeaturePropertyConstants.igLeft : FeaturePropertyConstants.igRight;
        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        RefPlane plane = Created(model, () => model.RefPlanes.AddTangentToCylinderOrConeAtAngle(face, parentPlane, Math.Abs(radians), 0, towards, side, Local: false));

        (double[] root, double[] normal) = PlaneData(plane);
        double[] outward = flip ? normal.Select(c => -c).ToArray() : normal;
        bool tangent = Math.Abs(Geo.Dot(normal, axis)) < 1e-6
            && Math.Abs(Geo.Dot(outward, Geo.Minus(root, axisPoint)) - radius) < Tolerance
            && Math.Abs(Geo.Dot(outward, parentNormal) - Math.Sin(radians)) < 1e-6;
        return tangent ? Describe(context, model, plane, model.RefPlanes.Count) : Discard(plane, "tangent");
    }

    /// <summary>Unit normal of the face at a point (m), or null when the point is not on the face.</summary>
    private static double[]? NormalAt(Face face, double[] point)
    {
        Array points = point.ToArray();
        Array guess = new double[2];
        Array deviations = new double[1];
        Array param = new double[2];
        Array flags = new int[1];
        face.GetParamAtPoint(1, ref points, ref guess, ref deviations, ref param, ref flags);
        Array back = new double[3];
        face.GetPointAtParam(1, ref param, ref back);
        if (Geo.Length(Geo.Minus(Geo.Doubles(back).ToArray(), point)) > Tolerance)
        {
            return null;
        }

        Array normal = new double[3];
        face.GetNormal(1, ref param, ref normal);
        return Geo.Unit(Geo.Doubles(normal).ToArray());
    }

    /// <summary>
    /// Coordinate system at an origin (mm) rotated by angles (degrees) about X, then Y, then Z; or, with two straight edges,
    /// its X and Y axes along them, pointing away from the origin, which is an end of either edge. In an assembly only by
    /// origin and angles: its planes are faces there, read the same way.
    /// </summary>
    public static object AddCoordinateSystem(SeContext context, string? documentHandle, double[]? origin, double[]? angles, string[]? edges = null)
    {
        if (edges is not null)
        {
            return angles is null
                ? CoordinateSystemOnEdges(context, documentHandle, origin, edges)
                : throw ToolException.InvalidArgument("Give 'angles' or 'edges', not both: with edges the axes follow them.");
        }

        SolidEdgeDocument document = SystemsDocument(context.GetDocument(documentHandle));
        if (origin is { Length: not 3 } || angles is { Length: not 3 })
        {
            throw ToolException.InvalidArgument("'origin' must be [x,y,z] in mm and 'angles' [ax,ay,az] in degrees.");
        }

        double[] o = (origin ?? [0, 0, 0]).Select(v => Units.MmToM(Units.RequireFinite(v, "origin"))).ToArray();
        double[] r = (angles ?? [0, 0, 0]).Select(v => Units.DegToRad(Units.RequireFinite(v, "angles"))).ToArray();
        var systems = (CoordinateSystems)Late.Get(document, "CoordinateSystems")!;
        int before = systems.Count;
        SolidEdgePart.CoordinateSystem system = systems.Add(o[0], o[1], o[2], r[0], r[1], r[2]);
        if (systems.Count != before + 1)
        {
            throw new ToolException("refplane_failed", "Solid Edge did not add the coordinate system.", "List them with action 'coordinate_systems'.");
        }

        return DescribeSystem(context, document, system);
    }

    /// <summary>
    /// Verified with AddByGeometry: igLeft points an axis along the edge direction (start to end) and igRight against it; the
    /// origin is the start or end key point of the edge given for it. Z is X x Y. The axes and the origin are checked.
    /// </summary>
    private static object CoordinateSystemOnEdges(SeContext context, string? documentHandle, double[]? origin, string[] edgeHandles)
    {
        if (edgeHandles is not [string xHandle, string yHandle])
        {
            throw ToolException.InvalidArgument("'edges' must be two straight edges: the X axis, then the Y axis.");
        }

        double[] o = origin is { Length: 3 } point
            ? point.Select(v => Units.MmToM(Units.RequireFinite(v, "origin"))).ToArray()
            : throw ToolException.InvalidArgument("With 'edges', 'origin' [x,y,z] in mm is required: an end of either edge.");
        ModelDocument model = PartOnly(string.IsNullOrWhiteSpace(documentHandle) ? context.DocumentOf(xHandle) : context.GetDocument(documentHandle),
            "coordinate systems along edges");
        var axes = new[] { xHandle, yHandle }.Select(h =>
        {
            Edge edge = context.Handles.Resolve<Edge>(h, "edges");
            if (edge.Geometry is not SolidEdgeGeometry.Line)
            {
                throw ToolException.InvalidArgument($"'{h}' is not a straight edge.", "Use se_geometry edges (type line).");
            }

            Array start = new double[3];
            Array end = new double[3];
            edge.GetEndPoints(ref start, ref end);
            return (Edge: edge, Start: Geo.Doubles(start).ToArray(), End: Geo.Doubles(end).ToArray());
        }).ToArray();

        double[] xDirection = Geo.Unit(Geo.Minus(axes[0].End, axes[0].Start));
        double[] yDirection = Geo.Unit(Geo.Minus(axes[1].End, axes[1].Start));
        if (Geo.Length(Geo.Cross(xDirection, yDirection)) < 1e-6)
        {
            throw ToolException.InvalidArgument("The two edges are parallel: they cannot give the X and Y axes.");
        }

        var ends = axes.SelectMany(a => new[] { (a.Edge, Point: a.Start, Key: KeyPointType.igKeyPointStart), (a.Edge, Point: a.End, Key: KeyPointType.igKeyPointEnd) });
        var at = ends.FirstOrDefault(p => Geo.Length(Geo.Minus(p.Point, o)) < 1e-6);
        if (at.Edge is null)
        {
            throw ToolException.InvalidArgument("'origin' must be an end of one of the edges.", "Give the corner where the edges meet, e.g. a vertex from se_geometry.");
        }

        // Each axis points away from the origin: along the edge if the origin is nearer its start, against it otherwise.
        FeaturePropertyConstants Away((Edge Edge, double[] Start, double[] End) a, out double[] direction)
        {
            bool along = Geo.Length(Geo.Minus(a.Start, o)) <= Geo.Length(Geo.Minus(a.End, o));
            direction = Geo.Unit(along ? Geo.Minus(a.End, a.Start) : Geo.Minus(a.Start, a.End));
            return along ? FeaturePropertyConstants.igLeft : FeaturePropertyConstants.igRight;
        }

        FeaturePropertyConstants xSide = Away(axes[0], out double[] expectedX);
        FeaturePropertyConstants ySide = Away(axes[1], out _);
        var systems = (CoordinateSystems)Late.Get(model.Document, "CoordinateSystems")!;
        int before = systems.Count;
        SolidEdgePart.CoordinateSystem system = systems.AddByGeometry(CoordinateSystemFeatureConstants.seCoordSysXAxis, axes[0].Edge, xSide,
            CoordinateSystemFeatureConstants.seCoordSysYAxis, axes[1].Edge, ySide, at.Edge, at.Key);
        if (systems.Count != before + 1)
        {
            throw new ToolException("refplane_failed", "Solid Edge did not add the coordinate system.", "Check the edges with se_geometry.");
        }

        (double[] actualOrigin, double[] x, _, _) = Axes(system);
        if (Geo.Length(Geo.Minus(actualOrigin, o)) > 1e-6 || Geo.Dot(x, expectedX) < 1 - 1e-6)
        {
            try
            {
                system.Delete();
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                // The error below is what matters; the system stays in the tree.
            }

            throw new ToolException("refplane_failed", "The coordinate system did not come out at the origin with X along the first edge (removed).",
                "Check the edges and the origin with se_geometry.");
        }

        return DescribeSystem(context, model.Document, system);
    }

    public static object ListCoordinateSystems(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = SystemsDocument(context.GetDocument(documentHandle));
        var systems = (CoordinateSystems)Late.Get(document, "CoordinateSystems")!;
        object[] items = Late.Items(systems).OfType<SolidEdgePart.CoordinateSystem>().Select(s => DescribeSystem(context, document, s)).ToArray();
        return new { count = items.Length, coordinateSystems = items };
    }

    /// <summary>
    /// Shows or hides a reference plane or a coordinate system, or with 'all' every reference plane of the document. Planes
    /// that Solid Edge does not change (a local plane of a feature) are counted apart.
    /// </summary>
    public static object SetVisible(SeContext context, string? documentHandle, string target, bool visible)
    {
        SolidEdgeDocument document = DocumentFor(context, documentHandle, target);
        object[] items = target.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? Planes(document) : [Element(context, document, target)];
        var changed = new List<string>();
        int unchanged = 0;
        foreach (object item in items)
        {
            try
            {
                Late.Set(item, "Visible", visible);
                if (Late.Get(item, "Visible") is bool now && now == visible)
                {
                    changed.Add(Label(item));
                    continue;
                }
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                // Counted below: the other planes are still shown or hidden.
            }

            unchanged++;
        }

        return changed.Count > 0
            ? new { visible, changed, unchanged = unchanged > 0 ? unchanged : (int?)null }
            : throw new ToolException("refplane_failed", $"Solid Edge did not {(visible ? "show" : "hide")} '{target}'.",
                "Local planes made by features cannot be shown or hidden; use a plane from action 'list'.");
    }

    /// <summary>Renames a reference plane or a coordinate system; the new name is read back.</summary>
    public static object Rename(SeContext context, string? documentHandle, string target, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("Action 'rename' requires 'name'.");
        }

        SolidEdgeDocument document = DocumentFor(context, documentHandle, target);
        object item = Element(context, document, target);
        try
        {
            Late.Set(item, "Name", name);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            throw new ToolException("refplane_failed", $"Solid Edge did not rename '{target}' ({ComErrors.Unwrap(ex).Message}).",
                "A local plane made by a feature has no name of its own; rename a plane listed with a name by action 'list'.");
        }

        string? now = Late.TryGetString(item, "Name");
        return now == name
            ? DescribeAny(context, document, item)
            : throw new ToolException("refplane_failed", $"Solid Edge kept the name '{now}' instead of '{name}'.", "Use a name that no other plane or coordinate system has.");
    }

    /// <summary>
    /// Deletes a reference plane or a coordinate system that nothing is built on. Verified: the base planes and the base
    /// coordinate system can be deleted through the API, so they are refused here; deleting a part plane also deletes the
    /// sketches on it and leaves the features and planes made from it with warnings, and in an assembly the planes made from
    /// it fail, so a plane with dependents is refused. A local plane cannot be deleted (E_INVALIDARG).
    /// </summary>
    public static object Delete(SeContext context, string? documentHandle, string target)
    {
        SolidEdgeDocument document = DocumentFor(context, documentHandle, target);
        object item = Element(context, document, target);
        string label = Label(item);
        object[] planes = Planes(document);
        if (OrientedPlanes.Bases(planes).Any(b => Late.SameObject(b, item)))
        {
            throw ToolException.InvalidArgument($"'{label}' is a base plane of the document: it cannot be deleted.", "Hide it with action 'hide'.");
        }

        object? collection = item is SolidEdgePart.CoordinateSystem ? Late.Get(document, "CoordinateSystems") : null;
        if (collection is not null && Late.Items(collection).FirstOrDefault() is { } first && Late.SameObject(first, item))
        {
            throw ToolException.InvalidArgument($"'{label}' is the base coordinate system of the document: it cannot be deleted.", "Hide it with action 'hide'.");
        }

        if (collection is null && Dependents(document, item, planes) is { Count: > 0 } dependents)
        {
            throw new ToolException("in_use", $"'{label}' is used by {string.Join(", ", dependents)}.",
                "Delete or redefine those first (se_feature_tree, se_relations), or hide the plane.");
        }

        int before = collection is null ? planes.Length : Late.Count(collection);
        try
        {
            Late.Call(item, "Delete");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException { HResult: ComErrors.EInvalidArg })
        {
            throw new ToolException("refplane_failed", $"Solid Edge refused to delete '{label}' (a local plane belongs to the feature that made it).",
                "Delete or edit that feature instead (se_feature_tree).");
        }

        int after = collection is null ? Planes(document).Length : Late.Count(collection);
        context.Handles.Remove(target, "it was deleted");
        return after == before - 1
            ? new { deleted = label }
            : throw new ToolException("refplane_failed", $"After deleting '{label}' the document has {before - after} fewer planes or coordinate systems instead of 1.",
                "Check them with action 'list' or 'coordinate_systems'.");
    }

    /// <summary>
    /// Origin and axes read from the planes of the system: the XY normal is Z, the YZ normal is X, and the origin is where
    /// the three planes meet.
    /// </summary>
    private static object DescribeSystem(SeContext context, SolidEdgeDocument document, SolidEdgePart.CoordinateSystem system)
    {
        (double[] origin, double[] x, double[] y, double[] z) = Axes(system);
        return new
        {
            handle = context.Register(system, "coordsys", document),
            name = system.Name,
            origin = origin.Select(Geo.Mm).ToArray(),
            xAxis = x.Select(Geo.R).ToArray(),
            yAxis = y.Select(Geo.R).ToArray(),
            zAxis = z.Select(Geo.R).ToArray(),
        };
    }

    internal static (double[] Origin, double[] X, double[] Y, double[] Z) Axes(SolidEdgePart.CoordinateSystem system)
    {
        (double[] rootXy, double[] z) = PlaneData(system.Plane[CoordinateSystemFeatureConstants.seCoordSysXYPlane]);
        (double[] rootYz, double[] x) = PlaneData(system.Plane[CoordinateSystemFeatureConstants.seCoordSysYZPlane]);
        double[] rootZx = PlaneData(system.Plane[CoordinateSystemFeatureConstants.seCoordSysZXPlane]).Root;
        double[] y = Geo.Cross(z, x);
        double[] origin = x.Select((_, k) => x[k] * Geo.Dot(x, rootYz) + y[k] * Geo.Dot(y, rootZx) + z[k] * Geo.Dot(z, rootXy)).ToArray();
        return (origin, x, y, z);
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
            if (Geo.Length(Geo.Minus(s, point)) < Tolerance)
            {
                return (edge, KeyPointType.igKeyPointStart);
            }

            if (Geo.Length(Geo.Minus(e, point)) < Tolerance)
            {
                return (edge, KeyPointType.igKeyPointEnd);
            }

            if (edge.Geometry is SolidEdgeGeometry.Line && Geo.Length(Geo.Minus(s.Select((c, k) => (c + e[k]) / 2).ToArray(), point)) < Tolerance)
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
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("refplane_failed", $"Solid Edge did not create the plane ({ex.Message}).", "Check the inputs with se_geometry.");
        }

        return model.RefPlanes.Count > before
            ? plane
            : throw new ToolException("refplane_failed", "Solid Edge did not add the plane.", "Check the inputs with se_geometry.");
    }

    /// <summary>Checks the normal (with its sign when <paramref name="signed"/>) and a point of a new plane, or deletes it.</summary>
    private static object Checked(SeContext context, ModelDocument model, RefPlane plane, double[]? normal, double[]? point, string what, bool signed = false)
    {
        (double[] root, double[] actual) = PlaneData(plane);
        bool parallel = normal is null || Math.Abs((signed ? Geo.Dot(actual, normal) : Math.Abs(Geo.Dot(actual, normal))) - 1) < 1e-6;
        bool onPlane = point is null || Math.Abs(Geo.Dot(actual, Geo.Minus(point, root))) < Tolerance;
        return parallel && onPlane ? Describe(context, model, plane, model.RefPlanes.Count) : Discard(plane, what);
    }

    private static object Discard(RefPlane plane, string what)
    {
        try
        {
            plane.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the wrong plane.
        }

        throw new ToolException("refplane_failed", $"The {what} plane is not where expected and was deleted.", "Check the inputs with se_geometry.");
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
            case SolidEdgeAssembly.AsmRefPlane assemblyPlane:
                assemblyPlane.GetRootPoint(ref root);
                assemblyPlane.GetNormal(ref normal);
                break;
            case Face { Geometry: SolidEdgeGeometry.Plane plane }:
                plane.GetPlaneData(ref root, ref normal);
                break;
            default:
                throw new ToolException("handle_wrong_type", "Not a reference plane or a planar face.");
        }

        return (Geo.Doubles(root).ToArray(), Geo.Unit(Geo.Doubles(normal).ToArray()));
    }

    private static ReferenceElementConstants Flip(ReferenceElementConstants side) =>
        side == ReferenceElementConstants.igNormalSide ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;

    /// <summary>Part or sheet metal document; an assembly is refused for what se_ref_plane only makes in parts.</summary>
    private static ModelDocument PartOnly(SolidEdgeDocument document, string what) => document is SolidEdgeAssembly.AssemblyDocument
        ? throw new ToolException("not_supported", $"In an assembly, se_ref_plane does not create {what}.",
            "In an assembly use offset, angle or normal_to_edge (faces and edges from se_geometry with 'source' = an occurrence), or a coordinate system by origin and angles; or create it in the part.")
        : ModelDocument.From(document);

    /// <summary>Assembly, part or sheet metal document: the ones with coordinate systems.</summary>
    private static SolidEdgeDocument SystemsDocument(SolidEdgeDocument document) =>
        document is SolidEdgeAssembly.AssemblyDocument ? document : ModelDocument.From(document).Document;

    /// <summary>Document of the given handle or, without one, of the plane's handle (the active one for an index or 'all').</summary>
    private static SolidEdgeDocument DocumentFor(SeContext context, string? documentHandle, string target) =>
        !string.IsNullOrWhiteSpace(documentHandle) || int.TryParse(target, out _) || OrientedPlanes.TryAxis(target, out _)
            || target.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? SystemsDocument(context.GetDocument(documentHandle))
            : SystemsDocument(context.DocumentOf(target));

    /// <summary>Reference planes of the document in their order (assembly or part planes).</summary>
    private static object[] Planes(SolidEdgeDocument document) => document is SolidEdgeAssembly.AssemblyDocument assembly
        ? [.. Late.Items(assembly.AsmRefPlanes)]
        : [.. Late.Items(ModelDocument.From(document).RefPlanes)];

    /// <summary>Reference plane (index, 'xy'/'yz'/'xz' or handle) or coordinate system (handle) of the document.</summary>
    private static object Element(SeContext context, SolidEdgeDocument document, string value)
    {
        object[] planes = Planes(document);
        if (OrientedPlanes.TryAxis(value, out int axis))
        {
            return OrientedPlanes.Find(planes, axis, value);
        }

        if (int.TryParse(value, out int index))
        {
            return index >= 1 && index <= planes.Length
                ? planes[index - 1]
                : throw ToolException.InvalidArgument($"Reference plane index {index} is out of range (1 to {planes.Length}).", "List the planes with action 'list'.");
        }

        object resolved = context.Handles.Resolve(value, "plane");
        return resolved is RefPlane or SolidEdgeAssembly.AsmRefPlane or SolidEdgePart.CoordinateSystem
            ? resolved
            : throw new ToolException("handle_wrong_type", $"'{value}' is not a reference plane or a coordinate system.",
                "Use an index or a handle from action 'list', or a handle from 'coordinate_systems'.");
    }

    private static string Label(object item) => Late.TryGetString(item, "Name") is { Length: > 0 } name ? name
        : item is SolidEdgePart.CoordinateSystem ? "a coordinate system" : "an unnamed (local) plane";

    /// <summary>
    /// What is built on a plane: planes made from it (ParentPlane in a part, ParentRelations in an assembly), sketch and
    /// feature profiles drawn on it (directly or on a local plane made from it, which is how a part sketch sits on a
    /// reference plane) and, in an assembly, relations to it.
    /// </summary>
    private static List<string> Dependents(SolidEdgeDocument document, object plane, object[] planes)
    {
        bool Is(object? candidate) => candidate is not null && Late.SameObject(candidate, plane);

        var named = new List<string>();
        int local = 0;
        foreach (object other in planes.Where(p => !Late.SameObject(p, plane)))
        {
            if (Is(Late.TryGet(other, "ParentPlane")) || (Late.TryGet(other, "ParentRelations") is Array parents && parents.Cast<object?>().Any(Is)))
            {
                if (Late.TryGetString(other, "Name") is { Length: > 0 } name)
                {
                    named.Add($"'{name}'");
                }
                else
                {
                    local++;
                }
            }
        }

        var found = new List<string>();
        if (named.Count > 0)
        {
            found.Add($"the plane{(named.Count == 1 ? "" : "s")} {string.Join(", ", named)}");
        }

        int profiles = local + (Late.TryGet(document, "ProfileSets") is { } sets
            ? Late.Items(sets).Sum(set => Late.TryGet(set, "Profiles") is { } list ? Late.Items(list).Count(p => Is(Late.TryGet(p, "Plane"))) : 0)
            : 0);
        if (profiles > 0)
        {
            found.Add(profiles == 1 ? "a sketch or feature profile" : $"{profiles} sketch or feature profiles");
        }

        if (document is SolidEdgeAssembly.AssemblyDocument assembly && Late.Items(assembly.Relations3d).Count(r => Touches(r, Is)) is > 0 and int relations)
        {
            found.Add(relations == 1 ? "an assembly relation" : $"{relations} assembly relations");
        }

        return found;
    }

    /// <summary>Whether either element of an assembly relation passes <paramref name="isPlane"/> (GetElement1/2 have an out flag).</summary>
    private static bool Touches(object relation, Func<object?, bool> isPlane)
    {
        foreach (string method in new[] { "GetElement1", "GetElement2" })
        {
            try
            {
                object?[] args = [false];
                var modifier = new System.Reflection.ParameterModifier(1);
                modifier[0] = true;
                if (isPlane(relation.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, relation, args, [modifier],
                    System.Globalization.CultureInfo.InvariantCulture, null)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                // Rigid sets and other relations without two elements.
            }
        }

        return false;
    }

    private static object DescribeAny(SeContext context, SolidEdgeDocument document, object item)
    {
        int index = Array.FindIndex(Planes(document), p => Late.SameObject(p, item)) + 1;
        return item switch
        {
            SolidEdgePart.CoordinateSystem system => DescribeSystem(context, document, system),
            SolidEdgeAssembly.AsmRefPlane plane => DescribeAssembly(context, (SolidEdgeAssembly.AssemblyDocument)document, plane, index),
            _ => Describe(context, ModelDocument.From(document), (RefPlane)item, index),
        };
    }

    private static object ResolveParent(SeContext context, ModelDocument model, string value, string argumentName = "plane")
    {
        if (int.TryParse(value, out _) || OrientedPlanes.TryAxis(value, out _))
        {
            return model.ResolvePlane(context, value, argumentName);
        }

        object resolved = context.Handles.Resolve(value, argumentName);
        return resolved is RefPlane or SolidEdgeGeometry.Face
            ? resolved
            : throw new ToolException("handle_wrong_type", $"'{value}' in '{argumentName}' is not a reference plane or a planar face.");
    }

    /// <summary>The plane's x axis (its reference direction, the x of a sketch drawn on it), or null if Solid Edge gives none.</summary>
    internal static double[]? XAxis(object plane)
    {
        try
        {
            Array direction = new double[3];
            switch (plane)
            {
                case RefPlane part:
                    part.GetReferenceDirection(ref direction);
                    break;
                case SolidEdgeAssembly.AsmRefPlane assembly:
                    assembly.GetReferenceDirection(ref direction);
                    break;
                default:
                    return null;
            }

            return Geo.Plain(direction);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The reference planes of an occurrence's part, in assembly coordinates, with handles that carry the occurrence (for
    /// se_relations and measurements, like the faces of se_geometry with the occurrence as source).
    /// </summary>
    private static object OccurrenceList(SeContext context, SolidEdgeAssembly.Occurrence occurrence)
    {
        if (occurrence.OccurrenceDocument is not SolidEdgeDocument part || occurrence.Subassembly)
        {
            throw ToolException.InvalidArgument($"'{occurrence.Name}' is a subassembly: list the planes of a part occurrence, or open the subassembly.");
        }

        var assembly = (SolidEdgeDocument)occurrence.TopLevelDocument;
        Placement placement = Placement.Of(occurrence);
        RefPlanes planes = ModelDocument.From(part).RefPlanes;
        var items = new List<object>();
        for (int i = 1; i <= planes.Count && items.Count < context.MaxListItems; i++)
        {
            RefPlane plane = planes.Item(i);
            Array normal = new double[3];
            Array root = new double[3];
            plane.GetNormal(ref normal);
            plane.GetRootPoint(ref root);
            items.Add(new
            {
                handle = context.Register(new OccurrenceElement(occurrence, plane), "refplane", assembly),
                index = i,
                name = plane.Name,
                normal = Geo.Plain(placement.Vector([.. Geo.Doubles(normal)])),
                rootPoint = Geo.Mm(placement.Point([.. Geo.Doubles(root)])),
                visible = plane.Visible,
            });
        }

        return new { occurrence = occurrence.Name, count = planes.Count, planes = items, note = "Assembly coordinates; the handles are for se_relations and se_measure in the assembly." };
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
            xAxis = XAxis(plane),
            visible = plane.Visible,
        };
    }
}
