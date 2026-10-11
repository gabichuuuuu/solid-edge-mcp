using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Primitives: box, cylinder and sphere, added or cut, placed by a centre on a plane. Solid Edge 2026 builds them only in
/// synchronous modeling (in ordered mode the same calls are rejected with E_INVALIDARG), so they are created like the
/// synchronous sheet metal features: switch, create, check the volume, switch back, and undo everything if a check fails.
/// </summary>
public static partial class FeatureService
{
    public static readonly IReadOnlyList<string> PrimitiveShapes = ["box", "cylinder", "sphere"];

    /// <summary>How far (m) the centre may be from the plane.</summary>
    private const double OnPlaneTolerance = 1e-6;

    public static object Primitive(SeContext context, string? documentHandle, PrimitiveSpec spec)
    {
        if (!PrimitiveShapes.Contains(spec.Shape))
        {
            throw ToolException.InvalidArgument($"Unknown primitive '{spec.Shape}'.", $"Use one of: {string.Join(", ", PrimitiveShapes)}.");
        }

        bool cut = spec.Operation switch
        {
            "add" => false,
            "cut" => true,
            _ => throw ToolException.InvalidArgument($"Unknown operation '{spec.Operation}'.", "Use add or cut."),
        };
        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Plane ?? "1");
        if (model.Document is not PartDocument)
        {
            throw new ToolException("wrong_document_type", $"'{model.Document.Name}' is not a part.", "Primitives are made in part documents (se_documents new, kind 'part').");
        }

        if (cut && model.TryModel() is null)
        {
            throw new ToolException("no_model", $"'{model.Document.Name}' has no solid to cut yet.", "Add a primitive or a base feature first.");
        }

        List<(double X, double Y, double Z)>? corners = null;
        if (spec.Corners is not null)
        {
            if (spec.Shape != "box" || spec.Centre is not null || spec.Size is not null || spec.Angle is not null)
            {
                throw ToolException.InvalidArgument("'corners' place a box (2 opposite corners, or 3 points: the ends of one edge and a point on the opposite edge); give them instead of center, size and angle.");
            }

            corners = Geo.Points3(spec.Corners, "corners", 2);
            if (corners.Count > 3)
            {
                throw ToolException.InvalidArgument("'corners' has 2 or 3 points [x, y, z] in mm on the plane.");
            }
        }

        List<(double X, double Y, double Z)> centres = corners ?? Geo.Points3(spec.Centre, "center", 1);
        if (corners is null && centres.Count != 1)
        {
            throw ToolException.InvalidArgument("'center' must be one point [x, y, z] in mm on the plane.");
        }

        (double X, double Y, double Z) centre = centres[0];
        double width = 0, height = 0, depth = 0, radius = 0, angle = 0;
        if (spec.Angle is not null && spec.Shape != "box")
        {
            throw ToolException.InvalidArgument("'angle' rotates a box; a cylinder or a sphere has no angle.");
        }

        if (spec.Shape == "box" && corners is null)
        {
            angle = Units.DegToRad(spec.Angle ?? 0);
            if (spec.Size is not { Length: 2 } size)
            {
                throw ToolException.InvalidArgument("'size' [width, height] in mm is required for a box: along the x and y axes of the plane (the frame of se_sketch create).");
            }

            width = Units.MmToM(Units.RequirePositive(size[0], "size[0]"));
            height = Units.MmToM(Units.RequirePositive(size[1], "size[1]"));
        }
        else if (spec.Shape != "box")
        {
            radius = Units.MmToM(Units.RequirePositive(spec.Radius ?? throw ToolException.InvalidArgument($"'radius' in mm is required for a {spec.Shape}."), "radius"));
        }

        if (spec.Shape != "sphere")
        {
            depth = Units.MmToM(Units.RequirePositive(spec.Depth ?? throw ToolException.InvalidArgument($"'depth' in mm is required for a {spec.Shape}."), "depth"));
        }

        object source = model.ResolvePlaneSource(context, spec.Plane ?? "1");
        (double[] root, double[] normal) = RefPlaneService.PlaneData(source);
        foreach ((double X, double Y, double Z) given in centres)
        {
            double[] point = [given.X, given.Y, given.Z];
            double offPlane = point.Select((c, i) => (c - root[i]) * normal[i]).Sum();
            if (Math.Abs(offPlane) > OnPlaneTolerance)
            {
                string pointName = corners is null ? "centre" : "corner";
                throw ToolException.InvalidArgument($"The {pointName} is {Geo.Mm(Math.Abs(offPlane))} mm away from the plane; it must lie on it.",
                    $"Give a {pointName} on the plane, or choose a plane through it (se_ref_plane list shows their root points and normals).");
            }
        }

        // The side to try first: along the normal to add, against it to cut (into the part under a face). A sphere has no side.
        FeaturePropertyConstants[] sides = spec.Side switch
        {
            "auto" => spec.Shape == "sphere" ? [FeaturePropertyConstants.igRight]
                : cut ? [FeaturePropertyConstants.igLeft, FeaturePropertyConstants.igRight]
                : [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
            "normal" => [FeaturePropertyConstants.igRight],
            "reverse" => [FeaturePropertyConstants.igLeft],
            "symmetric" => [FeaturePropertyConstants.igSymmetric],
            _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}'.", "Use auto, normal, reverse or symmetric."),
        };
        string what = $"{spec.Shape} {(cut ? "cut" : "")}".Trim();
        string nextStep = cut
            ? "Place the primitive so that it overlaps the part, or try another side."
            : "Place the primitive so that it adds material outside the part, or try another side.";

        // A face is not accepted as the plane (E_INVALIDARG), and a local plane on a face of an ordered feature fails in
        // synchronous mode (E_FAIL): a reference plane on the face is made first, in the document's mode. Verified: it
        // works for faces of both kinds, and Solid Edge absorbs it when the mode changes; if it is still there, it is removed.
        RefPlane? own = source as RefPlane is null
            ? model.RefPlanes.AddParallelByDistance(source, 0.0, ReferenceElementConstants.igNormalSide, Local: false)
            : null;
        RefPlane plane = own ?? (RefPlane)source;
        try
        {
            if (corners is not null)
            {
                (centre, width, height, angle) = BoxFromCorners(plane, corners);
            }

            if (angle != 0)
            {
                centre = PivotedCentre(plane, centre, width, height, angle);
            }

            for (int i = 0; ; i++)
            {
                FeaturePropertyConstants side = sides[i];
                try
                {
                    return InSynchronous(context, model, what, cut ? -1 : 1,
                        () => AddPrimitive(model, spec.Shape, cut, plane, centre, width, height, angle, depth, radius, side), nextStep);
                }
                catch (ToolException ex) when (ex.Code == "feature_failed" && i + 1 < sides.Length)
                {
                    // The first side changed nothing (e.g. inside the material): try the other one.
                }
            }
        }
        finally
        {
            if (own is not null)
            {
                TryDelete(own);
            }
        }
    }

    /// <summary>
    /// Creates one feature in synchronous modeling and checks it with the volume measured in the document's own mode before
    /// and after (in synchronous mode the body of a part with only ordered features cannot be read: E_FAIL). If Solid Edge
    /// rejects it (a cut that removes nothing gives E_INVALIDARG) or the check fails, everything done in the call is undone.
    /// </summary>
    private static object InSynchronous(SeContext context, ModelDocument model, string what, int expectedSign, Func<object?> create, string nextStep)
    {
        double before = model.Volume();
        int undoStart = UndoCount(model);
        bool switched = model.EnsureSynchronous();
        object? feature;
        try
        {
            feature = create();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsDisconnected(ex))
        {
            UndoSince(model, undoStart);
            if (switched)
            {
                RestoreOrdered(model);
            }

            context.ModelChanged(model.Document, "the model changed (call undone)");
            throw new ToolException("feature_failed", $"Solid Edge rejected the {what}: {ComErrors.Unwrap(ex).Message.Trim()}", nextStep, ex);
        }

        if (switched)
        {
            RestoreOrdered(model);
        }

        if (feature is null)
        {
            UndoSince(model, undoStart);
            throw new ToolException("feature_failed", $"Solid Edge did not create the {what}.", nextStep);
        }

        double after;
        string? status;
        try
        {
            (after, status) = Check(context, model, feature, before, expectedSign, null, null, undoneByCaller: true);
        }
        catch (ToolException ex) when (ex.Code == "feature_failed")
        {
            UndoSince(model, undoStart);
            if (switched)
            {
                RestoreOrdered(model);
            }

            context.ModelChanged(model.Document, "the model changed (call undone)");
            throw new ToolException(ex.Code, ex.Message, nextStep, ex);
        }

        var warnings = new List<string>();
        if (status == "warning")
        {
            warnings.Add("Solid Edge reports a warning on this feature.");
        }

        if (switched && model.ModelingMode != "ordered")
        {
            warnings.Add("The document could not return to ordered modeling.");
        }

        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            modelingMode = model.ModelingMode,
            warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    /// <summary>
    /// Verified: Solid Edge rotates a box about its first corner (centre - width/2 x - height/2 y of the plane), not about its
    /// centre. The centre passed is moved by d - R(d), d being the corner-to-centre vector, so the rotated box stays centred on
    /// the given point.
    /// </summary>
    private static (double X, double Y, double Z) PivotedCentre(RefPlane plane, (double X, double Y, double Z) c, double width, double height, double angle)
    {
        Array x = new double[3];
        Array n = new double[3];
        plane.GetReferenceDirection(ref x);
        plane.GetNormal(ref n);
        double[] u = [.. x.Cast<double>()];
        double[] normal = [.. n.Cast<double>()];
        double[] v = Geo.Cross(normal, u);
        double[] d = [.. u.Select((_, k) => width / 2 * u[k] + height / 2 * v[k])];
        double[] turned = Geo.Cross(normal, d);
        double[] shift = [.. d.Select((_, k) => d[k] - (d[k] * Math.Cos(angle) + turned[k] * Math.Sin(angle)))];
        return (c.X + shift[0], c.Y + shift[1], c.Z + shift[2]);
    }

    /// <summary>
    /// Centre, size and angle of a box given by two opposite corners (its sides along the axes of the plane) or by three points:
    /// the ends of one edge, then a point on the opposite edge.
    /// </summary>
    private static ((double X, double Y, double Z) Centre, double Width, double Height, double Angle) BoxFromCorners(RefPlane plane,
        List<(double X, double Y, double Z)> corners)
    {
        Array x = new double[3];
        Array n = new double[3];
        plane.GetReferenceDirection(ref x);
        plane.GetNormal(ref n);
        double[] u = [.. x.Cast<double>()];
        double[] normal = [.. n.Cast<double>()];
        double[] v = Geo.Cross(normal, u);
        double[][] p = [.. corners.Select(c => new[] { c.X, c.Y, c.Z })];
        double[] edge = Minus(p[1], p[0]);
        double width, height, angle;
        double[] centre;
        if (p.Length == 2)
        {
            (width, height, angle) = (Math.Abs(Dot(edge, u)), Math.Abs(Dot(edge, v)), 0);
            centre = [.. p[0].Select((c, k) => c + edge[k] / 2)];
        }
        else
        {
            width = Geo.Length(edge);
            double[] along = [.. edge.Select(c => c / Math.Max(width, double.Epsilon))];
            double[] across = Geo.Cross(normal, along);
            double offset = Dot(Minus(p[2], p[0]), across);
            (height, angle) = (Math.Abs(offset), Math.Atan2(Dot(along, v), Dot(along, u)));
            centre = [.. p[0].Select((c, k) => c + edge[k] / 2 + across[k] * offset / 2)];
        }

        if (width < OnPlaneTolerance || height < OnPlaneTolerance)
        {
            throw ToolException.InvalidArgument("The corners do not span a rectangle: they are in line or repeated.",
                "Give 2 opposite corners, or the ends of one edge and a point on the opposite edge.");
        }

        return ((centre[0], centre[1], centre[2]), width, height, angle);
    }

    private static object? AddPrimitive(ModelDocument model, string shape, bool cut, RefPlane plane, (double X, double Y, double Z) c,
        double width, double height, double angle, double depth, double radius, FeaturePropertyConstants side)
    {
        // Verified: with vbKeyPointExtent false the key point object must be empty (VT_EMPTY); a null object (VT_DISPATCH) gives E_NOINTERFACE.
        KeyPointExtentConstants flags = KeyPointExtentConstants.igTangentNormal;
        const FeaturePropertyConstants inside = FeaturePropertyConstants.igLeft;
        if (model.TryModel() is not { } target)
        {
            Model created = shape switch
            {
                "box" => model.Models.AddBoxByCenter(c.X, c.Y, c.Z, width, height, angle, depth, plane, side, false, null, ref flags),
                "cylinder" => model.Models.AddCylinderByCenterAndRadius(c.X, c.Y, c.Z, radius, depth, plane, side, false, null, ref flags),
                _ => model.Models.AddSphereByCenterAndRadius(c.X, c.Y, c.Z, radius, plane, side, false, false, null, ref flags),
            };
            return shape switch
            {
                "box" => LastFeature(model, created.BoxFeatures),
                "cylinder" => LastFeature(model, created.CylinderFeatures),
                _ => LastFeature(model, created.SphereFeatures),
            };
        }

        return (shape, cut) switch
        {
            ("box", false) => target.BoxFeatures.AddByCenter(c.X, c.Y, c.Z, width, height, angle, depth, plane, side, false, null, ref flags),
            ("box", true) => target.BoxFeatures.AddCutoutByCenter(c.X, c.Y, c.Z, width, height, angle, depth, plane, inside, side, false, null, ref flags),
            ("cylinder", false) => target.CylinderFeatures.AddByCenterAndRadius(c.X, c.Y, c.Z, radius, depth, plane, side, false, null, ref flags),
            ("cylinder", true) => target.CylinderFeatures.AddCutoutByCenterAndRadius(c.X, c.Y, c.Z, radius, depth, plane, inside, side, false, null, ref flags),
            (_, false) => target.SphereFeatures.AddByCenterAndRadius(c.X, c.Y, c.Z, radius, plane, side, false, false, null, ref flags),
            (_, true) => target.SphereFeatures.AddCutoutByCenterAndRadius(c.X, c.Y, c.Z, radius, plane, inside, side, false, false, null, ref flags),
        };
    }
}

/// <summary>Arguments of a primitive (mm). Shape: box | cylinder | sphere; operation: add | cut; side: auto | normal | reverse | symmetric.</summary>
public sealed record PrimitiveSpec(string Shape, string Operation, string? Plane, double[]? Centre, double[]? Size, double? Radius, double? Depth, string Side,
    double? Angle = null, double[]? Corners = null);
