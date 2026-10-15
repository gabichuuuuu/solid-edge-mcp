// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
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

    /// <summary>SolidEdgeGeometry.TopologyCollectionTypeConstants.seEdgeCollection.</summary>
    private const int EdgeCollection = 2;

    /// <summary>Deletes faces and heals the solid: a hole face fills the hole, the side and top faces of a boss remove it.</summary>
    private static object DeleteFaces(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "delete_faces", "the faces to delete");
        return InDocumentMode(context, model, target, "delete faces", 0, owner => owner.DeleteFaces.Add(FaceSet(owner, faces)), featureless: true);
    }

    /// <summary>
    /// Deletes the region of the body bounded by a closed loop of edges, on the side of one face inside it, and heals the
    /// solid. Verified: DeleteRegions.Add(edge collection of Body.CreateCollection(seEdgeCollection), side face) took a
    /// 20 x 20 x 5 boss off a 100 x 60 x 20 block (122000 to 120000 mm³, 11 faces to 6).
    /// </summary>
    private static object DeleteRegion(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (spec.Faces is not { Length: 1 })
        {
            throw ToolException.InvalidArgument("delete_region takes one face in 'faces': a face inside the region to delete (it picks the side of the edges).");
        }

        if (spec.Edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("delete_region requires 'edges': the closed loop of edges that bounds the region (se_geometry edges, e.g. with source = a face).");
        }

        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "delete_region", "one face inside the region to delete");
        Edge[] edges = spec.Edges.Select(h => context.Handles.Resolve<Edge>(h, "edges")).ToArray();
        int tag = ((Body)target.Body).Tag;
        for (int i = 0; i < edges.Length; i++)
        {
            if (Late.Get(edges[i], "Body") is not Body owner || owner.Tag != tag)
            {
                throw ToolException.InvalidArgument($"'{spec.Edges[i]}' is not an edge of the body of the face.", "Pass edges of the same design body.");
            }
        }

        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        object set = Late.Call(target.Body, "CreateCollection", EdgeCollection)
            ?? throw new ToolException("feature_failed", "Solid Edge did not create the edge collection.");
        foreach (Edge edge in edges)
        {
            Late.Call(set, "Add", edge);
        }

        object feature = Create("delete region", () => target.DeleteRegions.Add(set, faces[0]));
        return Verify(context, model, feature, before, 0, switched,
            hint: "The edges must close a loop around the region (all of its boundary), and the face must lie inside the region.");
    }

    /// <summary>
    /// Deletes every hole (hole features and cylindrical or conical cuts) up to a diameter, on every design body: a split part
    /// has one model per body and DeleteHoles of a model only fills the holes of its own body. A model where nothing changed
    /// keeps no feature.
    /// </summary>
    private static object DeleteHoles(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double diameter = Units.MmToM(Units.RequirePositive(spec.Diameter ?? throw ToolException.InvalidArgument(
            "delete_holes requires 'diameter': the largest hole diameter to delete (mm)."), "diameter"));
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        bool switched = model.EnsureOrdered();
        model.RequireModel();
        IReadOnlyList<(Model Model, Body Body)> bodies = model.Bodies();
        if (bodies.Count <= 1)
        {
            double single = model.Volume();
            Model target = model.RequireModel();
            object feature = Create("delete holes", () => target.DeleteHoles.Add(HoleTypeToDeleteConstants.seHoleTypeToDeleteAll, diameter));
            return Verify(context, model, feature, single, 1, switched);
        }

        // Volumes first: once a model changes, the Body objects read before from the other models fail (E_FAIL).
        double before = model.Volume();
        (Model Model, double Volume)[] targets = [.. bodies.Select(b => (b.Model, b.Body.Volume))];
        var made = new List<object>();
        bool warned = false;
        foreach ((Model target, double bodyBefore) in targets)
        {
            object feature;
            try
            {
                feature = Create("delete holes", () => target.DeleteHoles.Add(HoleTypeToDeleteConstants.seHoleTypeToDeleteAll, diameter));
            }
            catch (ToolException)
            {
                continue;
            }

            context.ModelChanged(model.Document, "the model changed (delete holes)");
            string? status = StatusOf(feature);
            double bodyAfter = Late.TryGet(target, "Body") is Body after ? after.Volume : bodyBefore;
            if (status == "failed" || bodyAfter - bodyBefore <= VolumeTolerance)
            {
                TryDelete(feature);
                continue;
            }

            warned |= status == "warning";
            made.Add(feature);
        }

        context.ModelChanged(model.Document, "the model changed (delete holes)");
        if (made.Count == 0)
        {
            throw new ToolException("feature_failed", $"No design body has holes up to {Geo.Mm(diameter)} mm: nothing was deleted.",
                "Check the hole diameters (se_geometry faces, type cylinder) and pass a larger 'diameter'.");
        }

        double volume = model.Volume();
        return new
        {
            feature = Describe(context, model, made[0]),
            features = made.Count > 1 ? made.Select(f => Describe(context, model, f)).ToList() : null,
            volume = Geo.Mm3(volume),
            volumeChange = Geo.Mm3(volume - before),
            warnings = Warnings(switched, warned),
        };
    }

    /// <summary>Deletes rounds and fillets given by their faces; a round on an outer edge restores the sharp edge.</summary>
    private static object DeleteRounds(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "delete_rounds", "the faces of the rounds");
        return InDocumentMode(context, model, target, "delete rounds", 0, owner => owner.DeleteBlends.Add(FaceSet(owner, faces)), featureless: true);
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
        return InDocumentMode(context, model, target, "offset faces", Math.Sign(distance), owner => owner.FaceOffsets.Add(FaceSet(owner, faces),
            FeaturePropertyConstants.igNone, FeaturePropertyConstants.igNormal, Units.MmToM(distance), null, null, 0, FeaturePropertyConstants.igNone),
            direction: distance > 0 ? "outwards" : "inwards", featureless: true);
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
    /// Turns a planar face about one of its straight edges by an angle in degrees, positive outwards (adds material): a
    /// reference plane at that angle to the face about the edge (the pivot is the other face along the edge, which must be
    /// planar), then the face replaced by it. Verified: FaceRotates.Add leaves the feature failed; this route on the top of a
    /// 100 x 60 x 20 block about its front edge, 10°, adds the wedge 0.5 x 60 x 60 tan 10° x 100 = 31738.86 mm³.
    /// </summary>
    private static object RotateFaces(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double degrees = spec.Angle ?? throw ToolException.InvalidArgument("rotate_faces requires 'angle' in degrees: positive turns the face outwards (adds material), negative inwards.");
        if (!double.IsFinite(degrees) || Math.Abs(degrees) < 1e-6 || Math.Abs(degrees) >= 90)
        {
            throw ToolException.InvalidArgument("'angle' must be between -90 and 90 degrees and not 0.");
        }

        if (spec.Faces is not { Length: 1 } || spec.Edges is not { Length: 1 })
        {
            throw ToolException.InvalidArgument("rotate_faces takes one planar face in 'faces' and the straight edge of it to turn about in 'edges'.",
                "Get them with se_geometry 'faces' and 'edges' (source = the face).");
        }

        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "rotate_faces", "the face to turn");
        var face = (Face)faces[0];
        Edge edge = context.Handles.Resolve<Edge>(spec.Edges[0], "edges");
        if (face.Geometry is not SolidEdgeGeometry.Plane || edge.Geometry is not SolidEdgeGeometry.Line)
        {
            throw ToolException.InvalidArgument("rotate_faces turns a planar face about a straight edge of it.");
        }

        Array adjacent = Array.Empty<object>();
        edge.GetFaces(out int count, ref adjacent);
        Face[] sides = [.. adjacent.Cast<object>().OfType<Face>()];
        if (count != 2 || !sides.Any(f => f.ID == face.ID))
        {
            throw ToolException.InvalidArgument("The edge is not an edge of the face.", "Pass an edge from se_geometry 'edges' with source = the face.");
        }

        Face pivot = sides.First(f => f.ID != face.ID);
        if (pivot.Geometry is not SolidEdgeGeometry.Plane)
        {
            throw ToolException.InvalidArgument("The other face along the edge must be planar: it sets the axis the face turns about.");
        }

        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        RefPlane plane = model.RefPlanes.AddAngularByAngle(face, Units.DegToRad(degrees), ReferenceElementConstants.igNormalSide, pivot,
            ReferenceElementConstants.igPivotEnd, Local: true);
        try
        {
            object feature = Create("rotate face", () => target.ReplaceFaces.Add(face, plane));
            return Verify(context, model, feature, before, Math.Sign(degrees), switched, direction: degrees > 0 ? "outwards" : "inwards",
                hint: "Turn a planar face about an edge between it and another planar face, by less than the angle that would make it meet another face.");
        }
        catch (ToolException)
        {
            TryDeleteObject(plane);
            throw;
        }
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

    /// <summary>
    /// Radiate on a planar face or on a face of a hole feature (e.g. one made by se_recognize holes) makes Solid Edge reject the
    /// change with its own modal dialog ("The requested modification cannot be made"), which DisplayAlerts does not suppress and
    /// which blocks Solid Edge until someone answers it; such faces are refused before the call.
    /// </summary>
    private static void CheckRadiateFaces(Model target, object[] faces)
    {
        if (faces.OfType<Face>().FirstOrDefault(f => Late.TryGet(f, "Geometry") is not (SolidEdgeGeometry.Cylinder or SolidEdgeGeometry.Cone or SolidEdgeGeometry.Sphere or SolidEdgeGeometry.Torus)) is not null)
        {
            throw ToolException.InvalidArgument("radiate changes round faces only (cylinders, cones, spheres, tori); a face given is not round.",
                "Pick the faces with se_geometry faces, type cylinder (or cone, sphere, torus).");
        }

        HashSet<int> ids = [.. faces.OfType<Face>().Select(f => f.ID)];
        foreach (Hole hole in Late.Items(target.Holes).OfType<Hole>())
        {
            if (Late.Items(hole.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]).OfType<Face>().Any(f => ids.Contains(f.ID)))
            {
                throw ToolException.InvalidArgument($"A face given belongs to the hole '{hole.Name}'; Solid Edge does not radiate the faces of a hole feature.",
                    "Change the hole's size with resize_holes ('diameter'), or radiate faces that are not part of a hole feature.");
            }
        }
    }

    /// <summary>
    /// Radiate, the synchronous command that grows or shrinks round faces about their axes. Verified in Solid Edge 2026:
    /// Model.SyncRadiate(1, [face], delta, coaxial, ...) adds delta to the radius of a cylindrical face (a 10 mm hole through a
    /// 20 mm plate with 6 mm: radius 11, volume -6031.858 = pi (11^2 - 5^2) 20; with -2 mm: radius 3). It is not a feature,
    /// so the document is switched to synchronous and the change is checked by the volume.
    /// </summary>
    private static object Radiate(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double delta = Units.MmToM(Units.RequireFinite(spec.Distance ?? throw ToolException.InvalidArgument(
            "radiate requires 'distance': mm added to the radius of the faces (negative makes them smaller)."), "distance"));
        if (delta == 0)
        {
            throw ToolException.InvalidArgument("radiate needs a 'distance' other than 0.");
        }

        (ModelDocument model, Model target, object[] faces) = FaceEdit(context, documentHandle, spec, "radiate", "cylindrical, conical or round faces");
        CheckRadiateFaces(target, faces);
        bool switched = model.EnsureSynchronous();
        double before = model.Volume();
        Array faceArray = faces;
        int groups = 0;
        Array positions = Array.Empty<double>(), counts = Array.Empty<int>(), entities = Array.Empty<object>();
        try
        {
            target.SyncRadiate(faces.Length, ref faceArray, delta, spec.Coaxial, ref groups, ref positions, ref counts, ref entities);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("feature_failed", $"Solid Edge could not radiate the faces: {ex.Message.Trim()}",
                "Give round faces (cylinders, cones) of the synchronous body and a distance that keeps them inside the part.", ex);
        }

        context.ModelChanged(model.Document, "the model changed (radiate)");
        double after = model.Volume();
        if (Math.Abs(after - before) <= VolumeTolerance)
        {
            throw new ToolException("feature_failed", "Solid Edge did not change the faces: the volume is the same.",
                "Give round faces of the synchronous body and a distance that keeps them inside the part.");
        }

        return new
        {
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            modelingMode = model.ModelingMode,
            warnings = switched ? new[] { "The document was switched to synchronous modelling: radiate is a synchronous command." } : null,
        };
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
