// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Face = SolidEdgeGeometry.Face;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Edits of existing bends and flanges in sheet metal (ordered modeling): bend angle, bend radius and match face.</summary>
public static partial class FeatureService
{
    // Unbends.Add (unbend) is not offered: with the fixed face and the bend face of a flange Solid Edge throws RPC_E_SERVERFAULT.
    // Not offered either (verified in Solid Edge 2026): CrossBrakes.Add gives E_NOINTERFACE with any face and E_FAIL without
    // faces; BendBulgeReliefFeatures.Add leaves a failed feature at the ends of a flange's bend (both bend faces, either end, the
    // three shapes, several sizes, ordered and synchronous); DelSMFaces.Add, which takes no faces, adds with the faces selected a
    // feature that changes nothing (none, system relief) or fails (zero bend radius, both).

    /// <summary>
    /// Changes the angle of a bend: the bent part rotates about the bend until it makes <paramref name="angle"/> with the fixed
    /// face (90 = perpendicular). Verified with a 90° flange: 60 tilts its outer face normal to (0, -0.866, 0.5), 45 to
    /// (0, -0.707, 0.707).
    /// </summary>
    public static object BendAngle(SeContext context, string? documentHandle, string[]? faces, string? fixedFace, double angle)
    {
        if (faces is not [string bendHandle])
        {
            throw ToolException.InvalidArgument("bend_angle requires 'faces' with one bend face (a cylindrical face of the bend, se_geometry faces type cylinder).");
        }

        string fixedHandle = fixedFace ?? throw ToolException.InvalidArgument("bend_angle requires 'fixedFace': a planar face of the part that stays in place.");
        if (!(angle > 0 && angle < 180))
        {
            throw ToolException.InvalidArgument("'angle' must be between 0 and 180 degrees (90 = perpendicular).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, bendHandle);
        RequireSheet(model);
        Face bend = context.Handles.Resolve<Face>(bendHandle, "faces");
        Face fixedPlane = context.Handles.Resolve<Face>(fixedHandle, "fixedFace");
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        double[] range = Range(model);
        object feature = Create("bend angle change", () => target.ChangeBendAngles.Add(bend, fixedPlane, Units.DegToRad(angle)));
        return Verify(context, model, feature, before, 0, switched, changedOtherwise: () => !Range(model).SequenceEqual(range));
    }

    /// <summary>Changes the inside radius of one or more bends. Verified: the inner bend face takes the radius and the outer one the radius plus the thickness.</summary>
    public static object BendRadius(SeContext context, string? documentHandle, string[]? faces, double? radius)
    {
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("bend_radius requires 'faces': bend faces (cylindrical faces of the bends, se_geometry faces type cylinder).");
        }

        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("bend_radius requires 'radius': the new inside radius in mm."), "radius"));
        ModelDocument model = ModelDocument.For(context, documentHandle, faces[0]);
        RequireSheet(model);
        Array bends = faces.Select(h => (object)context.Handles.Resolve<Face>(h, "faces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        double[] range = Range(model);
        object feature = Create("bend radius change", () => target.ResizeBends.Add(bends.Length, ref bends, r));
        return Verify(context, model, feature, before, 0, switched, changedOtherwise: () => !Range(model).SequenceEqual(range));
    }

    /// <summary>
    /// Moves a flange so that one of its main faces lies on a parallel plane, or <paramref name="distance"/> short of it on the side
    /// the face comes from (negative: beyond it), as an ordered feature (Match Flange Face). Verified with a 100 x 60 mm tab and a
    /// 20 mm flange on its edge y = 0: its outer face to a plane at y = 10 shortens the tab by 10 mm (-1000 mm3), to y = -10
    /// lengthens it, and its inner face to y = 10 leaves the outer one at y = 9; Solid Edge measures the offset from the plane,
    /// igNormal along its normal and igReverseNormal against it. The edge face of a flange leaves the feature failed, and so does a
    /// face of the part passed as the target itself: a local plane is made on it instead (verified: the end face of the tab with
    /// 20 mm short of it). In synchronous modeling the face moves but the change is lost on the next recompute.
    /// </summary>
    public static object MatchFace(SeContext context, string? documentHandle, string[]? faces, string? plane, double distance)
    {
        if (faces is not [string faceHandle])
        {
            throw ToolException.InvalidArgument("match_face requires 'faces' with one face of a flange: its outer or inner face (se_geometry faces type plane).");
        }

        string planeArgument = plane ?? throw ToolException.InvalidArgument("match_face requires 'plane': the plane the face goes to.",
            "Pass a reference plane parallel to the face (index or handle; se_ref_plane offset makes one) or a planar face parallel to it.");
        ModelDocument model = ModelDocument.For(context, documentHandle, faceHandle);
        RequireSheet(model);
        Face face = context.Handles.Resolve<Face>(faceHandle, "faces");
        if (face.Geometry is not SolidEdgeGeometry.Plane)
        {
            throw ToolException.InvalidArgument($"match_face takes a planar face; '{faceHandle}' is not one.",
                "Pass the outer or inner face of a flange (se_geometry faces with type 'plane').");
        }

        double[] faceNormal = InspectService.FaceNormal(face);
        object source = model.ResolvePlaneSource(context, planeArgument);
        double cosine = Dot(faceNormal, PlaneFrame(source).Normal);
        if (Math.Abs(cosine) < 1 - ParallelTolerance)
        {
            double degrees = Units.RadToDeg(Math.Acos(Math.Clamp(Math.Abs(cosine), -1, 1)));
            throw ToolException.InvalidArgument($"The face is not parallel to the plane: they are {degrees:0.###} degrees apart.",
                "Make a plane parallel to the face (se_ref_plane offset from a plane or face parallel to it).");
        }

        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        RefPlane refPlane = model.PlaneFrom(source);
        (double[] root, double[] normal) = PlaneFrame(refPlane);
        double side = Dot(Minus(PlaneFrame(face).Point, root), normal);
        double offset = (Math.Abs(side) > PositionTolerance ? Math.Sign(side) : Math.Sign(Dot(faceNormal, normal))) * Units.MmToM(distance);
        const string hint = "Pass the outer or inner face of a flange (not its edge face, across the thickness) and a plane parallel to it.";
        object feature = Create("match face", () => target.MatchFlangeFaces.Add(face, refPlane,
            offset >= 0 ? FeaturePropertyConstants.igNormal : FeaturePropertyConstants.igReverseNormal, Math.Abs(offset)));
        if (StatusOf(feature) != "failed" && !Reached(model, faceNormal, root, normal, offset))
        {
            TryDelete(feature);
            context.ModelChanged(model.Document, "the model changed (match face deleted)");
            throw new ToolException("feature_failed", "The face did not reach the plane, so the match face was deleted.", hint);
        }

        return Verify(context, model, feature, before, 0, switched, hint: hint);
    }

    private const double ParallelTolerance = 1e-6;
    private const double PositionTolerance = 1e-6;

    /// <summary>A point and the normal of a reference plane or of a planar face (its outward normal), in meters.</summary>
    private static (double[] Point, double[] Normal) PlaneFrame(object source)
    {
        Array point = new double[3];
        Array normal = new double[3];
        if (source is RefPlane refPlane)
        {
            refPlane.GetRootPoint(ref point);
            refPlane.GetNormal(ref normal);
            return (Geo.Doubles(point).ToArray(), Geo.Doubles(normal).ToArray());
        }

        var face = (Face)source;
        ((SolidEdgeGeometry.Plane)face.Geometry).GetPlaneData(ref point, ref normal);
        return (Geo.Doubles(point).ToArray(), InspectService.FaceNormal(face));
    }

    /// <summary>Whether a planar face of the body with the outward normal <paramref name="faceNormal"/> lies <paramref name="offset"/> (m) from the plane along its normal.</summary>
    private static bool Reached(ModelDocument model, double[] faceNormal, double[] root, double[] normal, double offset) =>
        model.Bodies().Any(b => Late.Items(b.Body.Faces[TopologyQuery.igQueryPlane]).OfType<Face>().Any(f =>
            Dot(InspectService.FaceNormal(f), faceNormal) >= 1 - ParallelTolerance
            && Math.Abs(Dot(Minus(PlaneFrame(f).Point, root), normal) - offset) <= PositionTolerance));

    /// <summary>Bounding box of the body (min and max, mm rounded), to detect changes that keep the volume.</summary>
    private static double[] Range(ModelDocument model)
    {
        if (model.TryModel()?.Body is not Body body)
        {
            return [];
        }

        Array min = new double[3];
        Array max = new double[3];
        body.GetRange(ref min, ref max);
        return [.. Geo.Mm(min), .. Geo.Mm(max)];
    }
}
