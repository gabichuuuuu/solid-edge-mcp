using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Planos de referencia: listar (con normal y punto raíz) y crear desplazados o en ángulo.</summary>
public static class RefPlaneService
{
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

    /// <summary>Plano paralelo a otro plano o cara plana, a una distancia en mm.</summary>
    public static object Offset(SeContext context, string? documentHandle, string parent, double? distance, bool flip)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        double d = Units.MmToM(Units.RequireFinite(distance ?? throw ToolException.InvalidArgument("Action 'offset' requires 'distance' (mm)."), "distance"));
        object parentPlane = ResolveParent(context, model, parent);
        ReferenceElementConstants side = flip ? ReferenceElementConstants.igReverseNormalSide : ReferenceElementConstants.igNormalSide;
        RefPlane plane = model.RefPlanes.AddParallelByDistance(parentPlane, Math.Abs(d), d < 0 ? Flip(side) : side);
        return Describe(context, model, plane, model.RefPlanes.Count);
    }

    /// <summary>Plano girado un ángulo (grados) respecto de otro plano, alrededor de su intersección con el plano pivote.</summary>
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
