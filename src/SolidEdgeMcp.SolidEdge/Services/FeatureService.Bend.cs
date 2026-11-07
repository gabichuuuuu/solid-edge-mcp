// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using SmartCollectionTypeConstants = SolidEdgeGeometry.SmartCollectionTypeConstants;
using TopologyCollectionTypeConstants = SolidEdgeGeometry.TopologyCollectionTypeConstants;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Gusset across a bend (mm): see <see cref="FeatureService.Gusset"/>.</summary>
public sealed record GussetSpec(string[]? Faces, string[]? Edges, double? Depth, double? Width, string Shape = "round", double? Radius = null,
    int? Count = null, double? Spacing = null, double? Distance = null);

/// <summary>Edits of existing bends and flanges in sheet metal (ordered modeling): bend angle, bend radius, match face and unbend.</summary>
public static partial class FeatureService
{
    /// <summary>
    /// Bend table of a sheet metal part (Bend Table command): the bends in their sequence, and a bend moved to another place
    /// of it. Verified in Solid Edge 2026 on a part with two flanges: SheetMetalDocument.BendTable.BendCount 2,
    /// GetBendDataEx(1..n) = feature, radius, angle, direction, included angle (index 0 fails); ChangeBendSequence(1, 2) swaps them.
    /// In a document built in this session the first call names the rows "Bend 1", "Bend 2"... and the next calls "Flange 1",
    /// "Flange 2" (neither waiting, DoIdle, pumping nor a new BendTable object changes it within the call).
    /// </summary>
    public static object BendTable(SeContext context, string? documentHandle, int[]? sequence)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        RequireSheet(model);
        SolidEdgePart.BendTable table = ((SheetMetalDocument)model.Document).BendTable;
        int count = table.BendCount;
        if (sequence is not null)
        {
            if (sequence.Length != 2 || sequence.Any(i => i < 1 || i > count))
            {
                throw ToolException.InvalidArgument($"'sequence' must be [from, to], two positions from 1 to {count}.", "List the bends with action bend_table first.");
            }

            string[] before = BendNames(table, count);
            table.ChangeBendSequence(sequence[0], sequence[1]);
            string[] after = BendNames(table, count);
            // The first read of a document built in this session can give "Bend 1", "Bend 2"... and a later one the features'
            // names: the move is checked when the names stay the same.
            if (before.Order().SequenceEqual(after.Order()) && after[sequence[1] - 1] != before[sequence[0] - 1])
            {
                throw new ToolException("setting_rejected", $"Solid Edge did not move '{before[sequence[0] - 1]}' to place {sequence[1]} of the bend sequence.");
            }

            context.ModelChanged(model.Document, "the bend sequence changed");
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            count,
            bends = Enumerable.Range(1, count).Select(i => Bend(table, i)).ToArray(),
        };
    }

    private static string[] BendNames(SolidEdgePart.BendTable table, int count) =>
        [.. Enumerable.Range(1, count).Select(i =>
        {
            table.GetBendDataEx(i, out string feature, out _, out _, out _, out _);
            return feature;
        })];

    private sealed record BendRow(int Sequence, string Feature, double Radius, double Angle, string Direction, double IncludedAngle, string Calculation, double CalculationValue);

    private static BendRow Bend(SolidEdgePart.BendTable table, int index)
    {
        table.GetBendDataEx(index, out string feature, out double radius, out double angle, out BendDirectionConstants direction, out double included);
        table.GetBendCalculationMethodAndValue(index, out BendCalculationMethodConstants method, out double value);
        static string Plain(string text, string prefix) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text);
        return new BendRow(index, feature, Geo.Mm(radius), Math.Round(Units.RadToDeg(angle), 6), Plain(direction.ToString(), "seBendDirection"),
            Math.Round(Units.RadToDeg(included), 6), Plain(method.ToString(), "BendCalculationMethod"), value);
    }

    // Not offered (verified in Solid Edge 2026): CrossBrakes.Add gives E_NOINTERFACE with any face and E_FAIL without
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

    /// <summary>
    /// Unbends bends of the sheet body about a planar face that stays in place (Unbend command, ordered): the given bend faces,
    /// or every bend; deleting the feature bends them again. Verified: <c>Unbends.Add</c> takes the fixed face and a Faces
    /// collection of bend faces, either <c>Body.CreateSmartCollection(body, seBendFacesOfBody)</c> (every bend, holes left out)
    /// or <c>Body.CreateCollection(seFaceCollection, n, faces)</c> (one face of a bend is enough; the other bends stay). A flange
    /// with a 90° bend, inside radius 1, thickness 1 and 19 mm past the bend lies flat on the plane of the fixed face, 19.09 mm
    /// beyond its edge (neutral factor 0.33), and loses 26.703 mm3. An empty collection or one holding only a hole's cylinder
    /// throws RPC_E_SERVERFAULT, so every face is checked to be a bend face first; an object array gives InvalidCastException
    /// and the flange feature's own faces E_FAIL. <c>Rebends.Add</c> adds nothing (null with any Faces collection,
    /// E_NOINTERFACE with the Unbend), so deleting the Unbend is the rebend.
    /// </summary>
    public static object Unbend(SeContext context, string? documentHandle, string? fixedFace, string[]? faces)
    {
        string fixedHandle = fixedFace ?? throw ToolException.InvalidArgument("unbend requires 'fixedFace': a planar face of the sheet that stays in place.",
            "Pass a main face of the base tab (se_geometry faces type plane).");
        ModelDocument model = ModelDocument.For(context, documentHandle, fixedHandle);
        RequireSheet(model);
        Face fixedPlane = context.Handles.Resolve<Face>(fixedHandle, "faces");
        if (fixedPlane.Geometry is not SolidEdgeGeometry.Plane)
        {
            throw ToolException.InvalidArgument($"unbend takes a planar face that stays in place; '{fixedHandle}' is not planar.",
                "Pass a main face of the base tab (se_geometry faces with type 'plane').");
        }

        Face[] chosen = (faces ?? []).Select(h => context.Handles.Resolve<Face>(h, "faces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        Body body = (Body)target.Body;
        object allBends = BendFaces(model, body, "unbend");
        for (int i = 0; i < chosen.Length; i++)
        {
            RequireBendFace(allBends, chosen[i], faces![i]);
        }

        Array chosenArray = chosen;
        object bends = chosen.Length == 0 ? allBends : body.CreateCollection(TopologyCollectionTypeConstants.seFaceCollection, chosen.Length, chosenArray);
        double before = model.Volume();
        double[] range = Range(model);
        object feature = Create("unbend", () => target.Unbends.Add(fixedPlane, bends));
        (double after, string? status) = Check(context, model, feature, before, 0, null, null, () => !Range(model).SequenceEqual(range),
            hint: "Pass a planar face of the base tab; delete the unbend (se_feature_edit delete) to bend the sheet again.");
        double[] box = Range(model);
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            range = new { min = box[..3], max = box[3..] },
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>
    /// Gussets across a bend (Gussets.AddByBend, ordered): one at <c>distance</c> from an end of the bend (default: its middle),
    /// <c>count</c> spread evenly along it, or <c>count</c> every <c>spacing</c> from that end. Verified on the 90° bend of a
    /// 100 mm flange (radius 1, thickness 1), depth 8, width 5, formed radius 2, punch and die 0.5: a rounded gusset 50 mm
    /// from the end adds 69.993 mm3 and spans x 47.5..52.5 (the same gusset at x 90 adds 72.743: Solid Edge's volume of the same faces
    /// varies with the position), a square one more; fit 3 -> centres 25, 50 and 75; fixed 3 every
    /// 25 -> 25, 50 and 75 (the distance is ignored); the gussets stay after saving and reopening. The end is an arc edge of the
    /// bend face (a straight edge measures from its midpoint, so it is not offered); the fill pattern leaves the feature failed.
    /// </summary>
    public static object Gusset(SeContext context, string? documentHandle, GussetSpec spec)
    {
        if (spec.Faces is not [string bendHandle])
        {
            throw ToolException.InvalidArgument("gusset requires 'faces' with one bend face (a cylindrical face of the bend, se_geometry faces type cylinder).");
        }

        double depth = Units.MmToM(Units.RequirePositive(spec.Depth ?? throw ToolException.InvalidArgument("gusset requires 'depth' (mm): how far it reaches from the bend."), "depth"));
        double width = Units.MmToM(Units.RequirePositive(spec.Width ?? throw ToolException.InvalidArgument("gusset requires 'width' (mm): its size across the bend."), "width"));
        GussetConstants shape = spec.Shape switch
        {
            "round" => GussetConstants.igRoundShape,
            "square" => GussetConstants.igSquareShape,
            _ => throw ToolException.InvalidArgument($"Unknown gusset shape '{spec.Shape}'.", "Use round or square."),
        };
        int count = spec.Count ?? 1;
        if (count < 1)
        {
            throw ToolException.InvalidArgument("'count' must be 1 or more.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, bendHandle);
        SheetMetalDocument sheet = RequireSheet(model);
        Face bend = context.Handles.Resolve<Face>(bendHandle, "faces");
        RequireBendFace(BendFaces(model, (Body)model.RequireModel().Body, "gusset"), bend, bendHandle);
        Edge[] bendEdges = [.. Late.Items(bend.Edges).OfType<Edge>()];
        Edge end;
        if (spec.Edges is [string endHandle])
        {
            end = context.Handles.Resolve<Edge>(endHandle, "edges");
            if (end.Geometry is not SolidEdgeGeometry.Circle || !bendEdges.Any(e => e.ID == end.ID))
            {
                throw ToolException.InvalidArgument($"'{endHandle}' is not an end edge of the bend face.",
                    "Pass one of the two arc edges of the bend face (se_geometry edges with source = the bend face), or no edges for the end nearest the origin.");
            }
        }
        else if (spec.Edges is { Length: > 0 })
        {
            throw ToolException.InvalidArgument("gusset takes at most one edge in 'edges': the end of the bend the distances are measured from.");
        }
        else
        {
            end = bendEdges.Where(e => e.Geometry is SolidEdgeGeometry.Circle).OrderBy(e => EdgeMin(e), Comparer<double[]>.Create(Lexicographic)).FirstOrDefault()
                ?? throw ToolException.InvalidArgument($"'{bendHandle}' has no arc edges at its ends.", "Pass the bend's cylindrical face (se_geometry faces type cylinder).");
        }

        double length = bendEdges.Where(e => e.Geometry is SolidEdgeGeometry.Line).Select(EdgeLength).DefaultIfEmpty(0).Max();
        if (spec.Distance is < 0)
        {
            throw ToolException.InvalidArgument("'distance' must be positive: mm from the end of the bend (0 or none = its middle).");
        }

        double distance = spec.Distance is > 0 ? Units.MmToM(spec.Distance.Value) : length / 2;
        double spacing = spec.Spacing is { } s ? Units.MmToM(Units.RequirePositive(s, "spacing")) : 0;
        GussetConstants pattern = spec.Spacing is not null ? GussetConstants.igPatternFixed : count > 1 ? GussetConstants.igPatternFit : GussetConstants.igGussetNone;
        if (pattern == GussetConstants.igGussetNone && distance >= length)
        {
            throw ToolException.InvalidArgument($"'distance' must be less than the bend length ({Units.MToMm(length):0.###} mm).");
        }

        if (pattern == GussetConstants.igPatternFixed && spacing * count >= length)
        {
            throw ToolException.InvalidArgument($"{count} gussets every {Units.MToMm(spacing):0.###} mm do not fit on the bend ({Units.MToMm(length):0.###} mm).",
                "Lower 'count' or 'spacing'; the first gusset is 'spacing' from the end.");
        }

        double radius = spec.Radius is { } r ? Units.MmToM(Units.RequirePositive(r, "radius")) : width * 0.4;
        double tool = GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalMaterialThickness) / 2;
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object feature = Create("gusset", () => target.Gussets.AddByBend(bend, end, distance, depth, width, 0, shape, radius,
            GussetConstants.igGussetNone, tool, tool, pattern, count, spacing));
        return Verify(context, model, feature, before, 1, switched,
            hint: "Make the gusset smaller (depth, width, radius) so that it fits inside the bend's flanges, or move it away from the ends.");
    }

    /// <summary>Every bend face of the sheet body (Body.CreateSmartCollection seBendFacesOfBody); throws not_found without bends.</summary>
    private static object BendFaces(ModelDocument model, Body body, string action)
    {
        object? bends = null;
        try
        {
            bends = body.CreateSmartCollection(body, SmartCollectionTypeConstants.seBendFacesOfBody);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // A sheet without bends gives 0x807B000D (verified).
        }

        return bends is not null && Late.Items(bends).Any() ? bends
            : throw new ToolException("not_found", $"'{model.Document.Name}' has no bends for {action}.", "Add a flange or a bend first (se_sheetmetal flange).");
    }

    /// <summary>
    /// Checks that a face is one of the bend faces: Solid Edge throws a server fault when a hole's cylinder or another face is
    /// passed where a bend is expected (verified with Unbends.Add).
    /// </summary>
    private static void RequireBendFace(object bends, Face face, string handle)
    {
        if (!Late.Items(bends).OfType<Face>().Any(f => f.ID == face.ID))
        {
            throw ToolException.InvalidArgument($"'{handle}' is not a bend face of the sheet.",
                "Pass cylindrical faces of the bends (se_geometry faces type cylinder; not the faces of holes).");
        }
    }

    private static double[] EdgeMin(Edge edge)
    {
        Array min = new double[3];
        Array max = new double[3];
        edge.GetRange(ref min, ref max);
        return [.. Geo.Doubles(min)];
    }

    private static int Lexicographic(double[] a, double[] b)
    {
        for (int k = 0; k < 3; k++)
        {
            if (Math.Abs(a[k] - b[k]) > PositionTolerance)
            {
                return a[k].CompareTo(b[k]);
            }
        }

        return 0;
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
