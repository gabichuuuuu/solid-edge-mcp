using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Edits of existing bends in sheet metal (ordered modeling): bend angle and bend radius.</summary>
public static partial class FeatureService
{
    // Unbends.Add (unbend) is not offered: with the fixed face and the bend face of a flange Solid Edge throws RPC_E_SERVERFAULT.

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
