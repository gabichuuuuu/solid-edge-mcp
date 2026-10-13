// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Reference planes of an assembly (<c>AsmRefPlanes</c>), parallel to an assembly plane or to a planar face of an occurrence,
/// at an angle about the line where the parent meets a pivot plane, or normal to an edge of an occurrence. Faces and edges
/// are passed as references (occurrence + element). Verified (Solid Edge 2026): igNormalSide offsets along the parent's
/// normal (out of the material for a face) and igReverseNormalSide the other way; for an angle the two sides turn the plane
/// one way or the other, and the new plane contains the hinge line; a pivot parallel to the parent gives an unrelated plane.
/// The new planes are global (in the assembly's PathFinder). <c>AddBy3Points</c> takes point objects (arrays of coordinates
/// give E_NOINTERFACE) and there is no mid plane in assemblies.
/// </summary>
public static partial class RefPlaneService
{
    /// <summary>Parent of an assembly plane: what Solid Edge takes, and its root point and normal in assembly coordinates (m).</summary>
    internal sealed record AssemblyParent(object Element, double[] Root, double[] Normal);

    private static object AssemblyList(SeContext context, AssemblyDocument assembly)
    {
        AsmRefPlanes planes = assembly.AsmRefPlanes;
        var items = new List<object>();
        for (int i = 1; i <= planes.Count && items.Count < context.MaxListItems; i++)
        {
            items.Add(DescribeAssembly(context, assembly, planes.Item(i), i));
        }

        return new { count = planes.Count, planes = items };
    }

    /// <summary>
    /// Plane parallel to an assembly plane or a planar face of an occurrence, <paramref name="distance"/> metres along its
    /// normal (negative: the other way). Checked by its normal and by its distance to the parent; if a face's surface runs
    /// against its outward normal and the plane comes out on the other side, it is made again on the side asked for.
    /// </summary>
    private static object AssemblyOffset(SeContext context, AssemblyDocument assembly, string parent, double distance)
    {
        AssemblyParent from = ResolveAssemblyParent(context, assembly, parent, "plane");
        AsmRefPlanes planes = assembly.AsmRefPlanes;
        ReferenceElementConstants side = distance < 0 ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        AsmRefPlane plane = CreatedInAssembly(planes, () => planes.AddParallelByDistance(from.Element, Math.Abs(distance), side));

        bool At(AsmRefPlane candidate, double expected)
        {
            (double[] root, double[] normal) = PlaneData(candidate);
            return Math.Abs(Math.Abs(Dot(normal, from.Normal)) - 1) < 1e-6 && Math.Abs(Dot(from.Normal, Minus(root, from.Root)) - expected) < Tolerance;
        }

        if (distance != 0 && !At(plane, distance) && At(plane, -distance))
        {
            DeleteQuietly(plane);
            plane = CreatedInAssembly(planes, () => planes.AddParallelByDistance(from.Element, Math.Abs(distance), Flip(side)));
        }

        return At(plane, distance) ? DescribeAssembly(context, assembly, plane, planes.Count) : DiscardAssembly(plane, "offset");
    }

    /// <summary>
    /// Plane turned <paramref name="radians"/> from the parent about the line where it meets the pivot. Checked: it contains
    /// that line and its normal makes the angle with the parent's.
    /// </summary>
    private static object AssemblyAngle(SeContext context, AssemblyDocument assembly, string parent, string pivot, double radians, bool flip)
    {
        AssemblyParent from = ResolveAssemblyParent(context, assembly, parent, "plane");
        AssemblyParent hinge = ResolveAssemblyParent(context, assembly, pivot, "pivot");
        double[] axis = Geo.Cross(from.Normal, hinge.Normal);
        if (Geo.Length(axis) < 1e-6)
        {
            throw ToolException.InvalidArgument("'pivot' is parallel to 'plane': the plane turns about the line where they meet.",
                "Give a pivot plane or face that crosses 'plane'.");
        }

        ReferenceElementConstants side = flip ^ (radians < 0) ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        AsmRefPlanes planes = assembly.AsmRefPlanes;
        AsmRefPlane plane = CreatedInAssembly(planes, () =>
            planes.AddAngularByAngle(from.Element, Math.Abs(radians), hinge.Element, ReferenceElementConstants.igPivotEnd, side));

        (double[] root, double[] normal) = PlaneData(plane);
        double[] line = HingePoint(from, hinge);
        bool turned = Math.Abs(Dot(normal, Unit(axis))) < 1e-6
            && Math.Abs(Math.Abs(Dot(normal, from.Normal)) - Math.Abs(Math.Cos(radians))) < 1e-6
            && Math.Abs(Dot(normal, Minus(line, root))) < Tolerance;
        return turned ? DescribeAssembly(context, assembly, plane, planes.Count) : DiscardAssembly(plane, "angle");
    }

    /// <summary>
    /// Plane normal to an edge of an occurrence at an end, at a distance (m) along it from that end or at a fraction of its
    /// length; the x direction comes from the assembly base plane most along the edge. Checked like in a part: for a straight
    /// edge the normal and the point, for a curve the point when it is an end.
    /// </summary>
    private static object AssemblyNormalToEdge(SeContext context, AssemblyDocument assembly, string edgeHandle, ReferenceElementConstants from,
        double? distance, double? ratio)
    {
        if (context.Handles.Resolve(edgeHandle, "edge") is not OccurrenceElement { Element: Edge edge } element)
        {
            throw ToolException.InvalidArgument($"'{edgeHandle}' is not an edge of an occurrence.", "Use se_geometry edges with 'source' = the occurrence handle.");
        }

        Placement? placement = InspectService.GeometryPlacement(element.Occurrence);
        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        double[] s = placement?.Point(Geo.Doubles(start).ToArray()) ?? Geo.Doubles(start).ToArray();
        double[] e = placement?.Point(Geo.Doubles(end).ToArray()) ?? Geo.Doubles(end).ToArray();
        double[] chord = Minus(e, s);
        if (Geo.Length(chord) < 1e-9)
        {
            throw ToolException.InvalidArgument("The edge is closed (a full circle); pick an open edge.");
        }

        AsmRefPlane orientation = OrientedPlanes.Bases(Late.Items(assembly.AsmRefPlanes)).OfType<AsmRefPlane>()
            .MinBy(p => Math.Abs(Dot(PlaneData(p).Normal, Unit(chord))))!;
        object reference = element.Reference(assembly);
        bool line = edge.Geometry is SolidEdgeGeometry.Line;
        double[] origin = from == ReferenceElementConstants.igCurveStart ? s : e;
        double[] other = from == ReferenceElementConstants.igCurveStart ? e : s;
        AsmRefPlanes planes = assembly.AsmRefPlanes;
        AsmRefPlane plane;
        double[]? point;
        if (distance is { } d)
        {
            double metres = Units.MmToM(Units.RequirePositive(d, "distance"));
            plane = CreatedInAssembly(planes, () => planes.AddNormalToCurveAtDistanceAlongCurve(reference, orientation, metres, 0,
                ReferenceElementConstants.igNormalSide, from));
            double[] direction = Unit(Minus(other, origin));
            point = line ? origin.Select((c, k) => c + direction[k] * metres).ToArray() : null;
        }
        else
        {
            double fraction = ratio ?? 0;
            plane = CreatedInAssembly(planes, () => planes.AddNormalToCurveAtArcLengthRatio(reference, orientation, fraction, 0,
                ReferenceElementConstants.igNormalSide, from));
            point = line || fraction is 0 or 1 ? origin.Select((c, k) => c + (other[k] - c) * fraction).ToArray() : null;
        }

        (double[] root, double[] normal) = PlaneData(plane);
        bool normalOk = !line || Math.Abs(Math.Abs(Dot(normal, Unit(chord))) - 1) < 1e-6;
        bool pointOk = point is null || Math.Abs(Dot(normal, Minus(point, root))) < Tolerance;
        return normalOk && pointOk ? DescribeAssembly(context, assembly, plane, planes.Count) : DiscardAssembly(plane, "normal to edge");
    }

    /// <summary>
    /// Assembly plane ('1'.., 'xy'/'yz'/'xz' or a handle) or a planar face of an occurrence (handle from se_geometry with
    /// 'source' = the occurrence), with its root point and normal (outward for a face) in assembly coordinates.
    /// </summary>
    internal static AssemblyParent ResolveAssemblyParent(SeContext context, AssemblyDocument assembly, string value, string argumentName)
    {
        AsmRefPlanes planes = assembly.AsmRefPlanes;
        if (OrientedPlanes.TryAxis(value, out int axis))
        {
            return FromPlane((AsmRefPlane)OrientedPlanes.Find(Late.Items(planes), axis, value));
        }

        if (int.TryParse(value, out int index))
        {
            return index >= 1 && index <= planes.Count
                ? FromPlane(planes.Item(index))
                : throw ToolException.InvalidArgument($"Assembly plane index {index} is out of range (1–{planes.Count}).", "List the planes with action 'list'.");
        }

        switch (context.Handles.Resolve(value, argumentName))
        {
            case AsmRefPlane plane:
                return FromPlane(plane);
            case OccurrenceElement { Element: Face { Geometry: SolidEdgeGeometry.Plane } face } element:
                Placement? placement = InspectService.GeometryPlacement(element.Occurrence);
                double[] root = PlaneData(face).Root;
                double[] normal = InspectService.FaceNormal(face);
                return new AssemblyParent(element.Reference(assembly), placement?.Point(root) ?? root, Unit(placement?.Vector(normal) ?? normal));
            case OccurrenceElement { Element: Face }:
                throw ToolException.InvalidArgument($"'{value}' in '{argumentName}' is not a planar face.", "Use se_geometry faces with type 'plane'.");
            default:
                throw new ToolException("handle_wrong_type", $"'{value}' in '{argumentName}' is not an assembly plane or a planar face of an occurrence.",
                    "Use an index or a handle from action 'list', 'xy'/'yz'/'xz', or a face from se_geometry faces with 'source' = an occurrence.");
        }

        static AssemblyParent FromPlane(AsmRefPlane plane)
        {
            (double[] root, double[] normal) = PlaneData(plane);
            return new AssemblyParent(plane, root, normal);
        }
    }

    /// <summary>A point of the line where two non-parallel planes meet.</summary>
    private static double[] HingePoint(AssemblyParent a, AssemblyParent b)
    {
        double c = Dot(a.Normal, b.Normal);
        double d1 = Dot(a.Normal, a.Root);
        double d2 = Dot(b.Normal, b.Root);
        double k = 1 - (c * c);
        double s = (d1 - (d2 * c)) / k;
        double t = (d2 - (d1 * c)) / k;
        return a.Normal.Select((x, i) => (s * x) + (t * b.Normal[i])).ToArray();
    }

    private static AsmRefPlane CreatedInAssembly(AsmRefPlanes planes, Func<AsmRefPlane> create)
    {
        int before = planes.Count;
        AsmRefPlane plane;
        try
        {
            plane = create();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("refplane_failed", $"Solid Edge did not create the plane ({ex.Message}).", "Check the inputs with se_geometry.");
        }

        return planes.Count > before
            ? plane
            : throw new ToolException("refplane_failed", "Solid Edge did not add the plane.", "Check the inputs with se_geometry.");
    }

    private static object DiscardAssembly(AsmRefPlane plane, string what)
    {
        DeleteQuietly(plane);
        throw new ToolException("refplane_failed", $"The {what} plane is not where expected and was deleted.", "Check the inputs with se_geometry.");
    }

    private static void DeleteQuietly(AsmRefPlane plane)
    {
        try
        {
            plane.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the wrong plane.
        }
    }

    private static object DescribeAssembly(SeContext context, AssemblyDocument assembly, AsmRefPlane plane, int index)
    {
        (double[] root, double[] normal) = PlaneData(plane);
        return new
        {
            handle = context.Register(plane, "asmrefplane", (SolidEdgeDocument)assembly),
            index,
            name = plane.Name,
            normal = normal.Select(Geo.R).ToArray(),
            rootPoint = root.Select(Geo.Mm).ToArray(),
            visible = plane.Visible,
        };
    }
}
