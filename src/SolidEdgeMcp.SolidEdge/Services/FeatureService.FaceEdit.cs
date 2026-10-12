using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Direct edits of the solid in ordered modelling, as done on imported parts: delete faces (the gap heals), holes up to a
/// diameter and rounds, offset, replace and redefine faces, and resize holes and rounds.
/// </summary>
public static partial class FeatureService
{
    // Verified: DeleteFaces, DeleteBlends and FaceOffsets reject an array of faces (E_NOINTERFACE); they take one face or a
    // face collection made with Body.CreateCollection(seFaceCollection) and filled with Add.
    private const int FaceCollection = 1;

    /// <summary>Deletes faces and heals the solid: a hole face fills the hole, the side and top faces of a boss remove it.</summary>
    private static object DeleteFaces(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "delete_faces", "the faces to delete");
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        object set = FaceSet(target, faces);
        object feature = Create("delete faces", () => target.DeleteFaces.Add(set));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>Deletes every hole (hole features and cylindrical or conical cuts) up to a diameter.</summary>
    private static object DeleteHoles(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double diameter = Units.MmToM(Units.RequirePositive(spec.Diameter ?? throw ToolException.InvalidArgument(
            "delete_holes requires 'diameter': the largest hole diameter to delete (mm)."), "diameter"));
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object feature = Create("delete holes", () => target.DeleteHoles.Add(HoleTypeToDeleteConstants.seHoleTypeToDeleteAll, diameter));
        return Verify(context, model, feature, before, 1, switched);
    }

    /// <summary>Deletes rounds and fillets given by their faces; a round on an outer edge restores the sharp edge.</summary>
    private static object DeleteRounds(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "delete_rounds", "the faces of the rounds");
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        object set = FaceSet(target, faces);
        object feature = Create("delete rounds", () => target.DeleteBlends.Add(set));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>
    /// Offsets faces along their normal by a distance: positive grows the solid (outwards), negative shrinks it. Verified: only
    /// the side igNormal works (igLeft, igRight and the normal-side constants leave the feature failed).
    /// </summary>
    private static object OffsetFaces(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double distance = spec.Distance ?? throw ToolException.InvalidArgument(
            "offset_faces requires 'distance' (mm): positive moves the faces outwards (adds material), negative inwards.");
        if (Math.Abs(distance) < 1e-9 || !double.IsFinite(distance))
        {
            throw ToolException.InvalidArgument("'distance' must be a non-zero number of mm.");
        }

        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "offset_faces", "the faces to offset");
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        object set = FaceSet(target, faces);
        object feature = Create("offset faces", () => target.FaceOffsets.Add(set, FeaturePropertyConstants.igNone,
            FeaturePropertyConstants.igNormal, Units.MmToM(distance), null, null, 0, FeaturePropertyConstants.igNone));
        return Verify(context, model, feature, before, Math.Sign(distance), switched, direction: distance > 0 ? "outwards" : "inwards");
    }

    /// <summary>
    /// Replaces faces with a reference plane, a construction surface or a face of one: the solid grows or shrinks to it.
    /// Verified (ordered): Model.ReplaceFaces.Add(face or face collection, plane / surface / surface face), checked by
    /// volume and bounding box.
    /// </summary>
    private static object ReplaceFaces(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.ReplaceWith))
        {
            throw ToolException.InvalidArgument("replace_faces requires 'replaceWith': a reference plane ('1'-'3' or refplane handle), a construction surface (se_surface) or a face of one.");
        }

        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "replace_faces", "the faces to replace");
        object surface = int.TryParse(spec.ReplaceWith, out _)
            ? model.ResolvePlaneSource(context, spec.ReplaceWith, "replaceWith")
            : context.Handles.Resolve(spec.ReplaceWith, "replaceWith");
        if (surface is Profile or Sketch or SolidEdgeGeometry.Edge or SolidEdgeGeometry.Vertex)
        {
            throw ToolException.InvalidArgument($"'{spec.ReplaceWith}' is not a plane or a surface.",
                "Pass a reference plane, a surface from se_surface or one of its faces (se_geometry faces with source = the surface).");
        }

        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        object set = faces.Length == 1 ? faces[0] : FaceSet(target, faces);
        object feature = Create("replace faces", () => target.ReplaceFaces.Add(set, surface));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>
    /// Merges adjacent faces into one B-spline face, as done on imported parts to edit them as one. Verified (ordered):
    /// Model.RedefineFaces.Add(n, faces, 0, [], [], igSurfaceByBoundarySingle, true) on the top of a 100 x 60 x 20 block and
    /// the round of radius 5 along its edge (7 faces to 6, the volume 0.46 mm³ less) and with the front face too (5 faces);
    /// the merge options minimal and multiple gave the same. Faces that do not touch give E_INVALIDARG, and one face alone is
    /// left as it was.
    /// </summary>
    private static object RedefineFaces(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (spec.Faces is not { Length: > 1 })
        {
            throw ToolException.InvalidArgument("redefine_faces requires two or more adjacent faces in 'faces': they become one B-spline face.",
                "Get them with se_geometry action 'faces'.");
        }

        (ModelDocument model, Model target, Body body, Face[] faces) = SplitTargets(context, documentHandle, spec.Faces, "redefine_faces");
        HashSet<int>[] edgeIds = [.. faces.Select(f => Late.Items(Late.Get(f, "Edges")!).OfType<Edge>().Select(e => e.ID).ToHashSet())];
        if (!Connected(edgeIds))
        {
            throw ToolException.InvalidArgument("The faces must touch one another along edges, as one patch.",
                "Pass adjacent faces (se_geometry edges with source = a face lists its edges).");
        }

        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        HashSet<int> idsBefore = [.. BodyFaces(body).Select(f => f.ID)];
        Array faceArray = faces.Cast<object>().ToArray();
        Array edges = Array.Empty<object>();
        Array tangency = Array.Empty<int>();
        object feature = Create("redefine faces", () => target.RedefineFaces.Add(faces.Length, ref faceArray, 0, ref edges, ref tangency,
            SurfaceByBoundaryPatchTopology.igSurfaceByBoundarySingle, true));
        Face[] now = Late.TryGet(target, "Body") is Body after ? BodyFaces(after) : [];
        if (now.Length >= idsBefore.Count)
        {
            TryDelete(feature);
            context.ModelChanged(model.Document, "the model changed (redefine faces deleted)");
            throw new ToolException("feature_failed", "Solid Edge did not merge the faces; the feature was deleted.",
                "Pass faces that touch one another along edges, on the same body.");
        }

        (double volume, string? status) = Check(context, model, feature, before, 0, null, null, changedOtherwise: () => true,
            hint: "Pass faces that touch one another along edges, on the same body.");
        List<string>? warnings = Warnings(switched, status == "warning");
        if (Math.Abs(volume - before) > before * 1e-4)
        {
            (warnings ??= []).Add($"The new face is a B-spline fitted to the edges around the faces, not a copy of them: the volume changed by {Geo.Mm3(volume - before)} mm³. Undo it (se_feature_edit delete) if the shape must stay.");
        }

        return new
        {
            feature = Describe(context, model, feature),
            facesBefore = idsBefore.Count,
            faces = now.Length,
            merged = now.Where(f => !idsBefore.Contains(f.ID)).Select(f => new { handle = context.Register(f, "face", model.Document), area = Geo.Mm2(f.Area) }).ToArray(),
            volume = Geo.Mm3(volume),
            volumeChange = Geo.Mm3(volume - before),
            warnings,
        };
    }

    /// <summary>Whether faces, given by the IDs of their edges, form one patch: each reached from the first through shared edges.</summary>
    private static bool Connected(HashSet<int>[] edgeIds)
    {
        var reached = new HashSet<int> { 0 };
        var queue = new Queue<int>([0]);
        while (queue.TryDequeue(out int i))
        {
            for (int j = 0; j < edgeIds.Length; j++)
            {
                if (!reached.Contains(j) && edgeIds[i].Overlaps(edgeIds[j]))
                {
                    reached.Add(j);
                    queue.Enqueue(j);
                }
            }
        }

        return reached.Count == edgeIds.Length;
    }

    /// <summary>
    /// Gives holes a new diameter, keeping their type and extent. Verified (ordered): Model.ResizeHoles.Add with the hole
    /// faces, the diameter, igFinite and a regular HoleData of that diameter; a through hole stays through and a blind one
    /// keeps its depth. A HoleData of another type (counterbore) leaves the feature failed, and none at all brings Solid Edge
    /// down (RPC_E_SERVERFAULT).
    /// </summary>
    private static object ResizeHoles(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double diameter = Units.MmToM(Units.RequirePositive(spec.Diameter ?? throw ToolException.InvalidArgument(
            "resize_holes requires 'diameter': the new diameter of the holes (mm)."), "diameter"));
        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "resize_holes", "the cylindrical faces of the holes");
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        HoleData data = model.HoleData.Add(FeaturePropertyConstants.igRegularHole, diameter);
        Array faceArray = faces;
        object feature = Create("resize holes", () => target.ResizeHoles.Add(faces.Length, ref faceArray, diameter, FeaturePropertyConstants.igFinite, data));
        return Verify(context, model, feature, before, 0, switched,
            hint: "Give the cylindrical faces of holes (se_geometry faces, type cylinder) and a diameter that fits in the part.");
    }

    /// <summary>
    /// Gives rounds a new radius. Verified (ordered): Model.ResizeRounds.Add with the faces of constant-radius rounds and the
    /// radius; the face of a variable-radius round leaves the feature failed.
    /// </summary>
    private static object ResizeRounds(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double radius = Units.MmToM(Units.RequirePositive(spec.Radius ?? throw ToolException.InvalidArgument(
            "resize_rounds requires 'radius': the new radius of the rounds (mm)."), "radius"));
        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "resize_rounds", "the faces of the rounds");
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        Array faceArray = faces;
        object feature = Create("resize rounds", () => target.ResizeRounds.Add(faces.Length, ref faceArray, radius));
        return Verify(context, model, feature, before, 0, switched,
            hint: "Give the faces of constant-radius rounds (se_geometry faces with source = the round) and a radius that fits; a variable-radius round cannot be resized this way.");
    }

    /// <summary>The faces to edit, of one design body, and the model that owns it: a split part has one model per body.</summary>
    private static (ModelDocument Model, Model Target, object[] Faces) FaceEdit(SeContext context, string? documentHandle, PartFeatureSpec spec,
        string action, string what)
    {
        if (spec.Faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} requires 'faces': {what}.", "Get them with se_geometry action 'faces'.");
        }

        (ModelDocument model, Model target, Body _, Face[] faces) = SplitTargets(context, documentHandle, spec.Faces, action);
        return (model, target, faces.Cast<object>().ToArray());
    }

    /// <summary>Face collection of the body with the given faces.</summary>
    private static object FaceSet(Model target, object[] faces)
    {
        object body = target.Body;
        object set = Late.Call(body, "CreateCollection", FaceCollection)
            ?? throw new ToolException("feature_failed", "Solid Edge did not create the face collection.");
        foreach (object face in faces)
        {
            Late.Call(set, "Add", face);
        }

        return set;
    }
}
