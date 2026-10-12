// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_face_split does: the action and its inputs. Lengths in mm.</summary>
public sealed record FaceSplitSpec(string Action, string[]? Faces = null, string[]? Tools = null, string? Plane = null, string? Edge = null,
    double? Distance = null);

/// <summary>
/// Faces of the design body split into more faces, the volume unchanged (for a draft, a colour or a relation on part of a
/// face). Verified in Solid Edge 2026 on a 100 x 60 x 20 block and a sphere of radius 20:
/// <list type="bullet">
/// <item>Model.SplitFaces.Add(n, faces, n, tools): the top split at x = 50 by a reference plane or by the face of a crossing
/// surface (3000 + 3000 mm²), the bottom by a curve edge lying on it; the surface feature itself gives E_INVALIDARG.</item>
/// <item>Model.PartingSplits.AddPartingSplit(plane, n, faces): the sphere split at its silhouette seen along the normal of
/// XY, the equator (two halves of 2513.27 mm²).</item>
/// <item>Model.OffsetEdges.Add(edge, face, distance, side): a line at the distance from the edge splits one of the two faces
/// that meet at it, chosen by the side and the edge's direction, not by the face given (the bottom of the block with its
/// front edge, 10 mm: igRight splits the bottom, 1000 + 5000 mm²; igLeft and igNone the front face, 1000 + 1000).</item>
/// </list>
/// </summary>
public static partial class FeatureService
{
    private static readonly string[] FaceSplitActions = ["split", "parting", "offset_edge"];

    public static object FaceSplit(SeContext context, string? documentHandle, FaceSplitSpec spec) => spec.Action switch
    {
        "split" => SplitFaces(context, documentHandle, spec),
        "parting" => PartingSplit(context, documentHandle, spec),
        "offset_edge" => OffsetEdge(context, documentHandle, spec),
        _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", FaceSplitActions)}."),
    };

    private static object SplitFaces(SeContext context, string? documentHandle, FaceSplitSpec spec)
    {
        (ModelDocument model, Model owner, Body body, Face[] faces) = SplitTargets(context, documentHandle, spec.Faces, "split");
        if (spec.Tools is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("split needs 'tools': reference planes ('1'-'3', 'xy', 'yz', 'xz' or handles), surfaces or faces of surfaces, curves or edges that cross the faces.");
        }

        object[] tools = spec.Tools.SelectMany(h => CuttingElements(context, model, h, "tools", planes: true)).Select(t => t is Face f && IsOnBody(f, body)
            ? throw ToolException.InvalidArgument("A face of the body cannot split another one.", "Use a reference plane, a surface or a curve.")
            : t).ToArray();
        return SplitChecked(context, model, owner, body, "split", () =>
        {
            Array targets = faces.Cast<object>().ToArray();
            Array toolArray = tools;
            return owner.SplitFaces.Add(targets.Length, ref targets, toolArray.Length, ref toolArray);
        }, "The tools must cross the faces: reference planes, surfaces or curves lying on them.");
    }

    private static object PartingSplit(SeContext context, string? documentHandle, FaceSplitSpec spec)
    {
        (ModelDocument model, Model owner, Body body, Face[] faces) = SplitTargets(context, documentHandle, spec.Faces, "parting");
        RefPlane plane = model.ResolvePlane(context, spec.Plane ?? throw ToolException.InvalidArgument(
            "parting requires 'plane': the faces are split along their silhouette seen along its normal ('1'-'3', 'xy', 'yz', 'xz' or a handle)."));
        return SplitChecked(context, model, owner, body, "parting split", () =>
        {
            Array targets = faces.Cast<object>().ToArray();
            return owner.PartingSplits.AddPartingSplit(plane, targets.Length, ref targets);
        }, "The faces must curve through the direction of the plane normal (a sphere or a cylinder lying across it); flat faces have no parting line.");
    }

    private static object OffsetEdge(SeContext context, string? documentHandle, FaceSplitSpec spec)
    {
        string edgeHandle = spec.Edge ?? throw ToolException.InvalidArgument("offset_edge requires 'edge': the edge the line runs along (se_geometry edges).");
        if (spec.Faces is not [string faceHandle])
        {
            throw ToolException.InvalidArgument("offset_edge takes one face in 'faces': the face next to the edge where the line is drawn.");
        }

        double d = Units.MmToM(Units.RequirePositive(spec.Distance ?? throw ToolException.InvalidArgument("offset_edge requires 'distance' (mm)."), "distance"));
        (ModelDocument model, Model owner, Body body, Face[] faces) = SplitTargets(context, documentHandle, [faceHandle], "offset_edge");
        Edge edge = context.Handles.Resolve<Edge>(edgeHandle, "edge");
        (int faceId, double area, int edgeId) = (faces[0].ID, faces[0].Area, edge.ID);
        if (!Late.Items(Late.Get(faces[0], "Edges")!).OfType<Edge>().Any(e => e.ID == edgeId))
        {
            throw ToolException.InvalidArgument($"'{edgeHandle}' does not bound '{faceHandle}'.", "Pass an edge of the face (se_geometry edges with source = the face).");
        }

        // The side of the edge the line goes to follows the edge's direction, not the face given (igNone and igLeft put it on the
        // neighbouring face of the bottom of a block, igRight on the bottom): the side that splits the face given is kept.
        ToolException? failed = null;
        foreach (FeaturePropertyConstants side in (FeaturePropertyConstants[])[FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft])
        {
            Body current = Late.TryGet(owner, "Body") as Body ?? body;
            Face face = BodyFaces(current).FirstOrDefault(f => f.ID == faceId) ?? faces[0];
            Edge along = Late.Items(current.Edges[TopologyQuery.igQueryAll]).OfType<Edge>().FirstOrDefault(e => e.ID == edgeId) ?? edge;
            try
            {
                return SplitChecked(context, model, owner, current, "offset edge", () => owner.OffsetEdges.Add(along, face, d, side),
                    "The distance must leave the line inside the face.", (faceId, area));
            }
            catch (ToolException ex) when (ex.Code == "feature_failed")
            {
                failed = ex;
            }
        }

        throw failed!;
    }

    /// <summary>Faces of one design body, its model and the body.</summary>
    private static (ModelDocument Model, Model Owner, Body Body, Face[] Faces) SplitTargets(SeContext context, string? documentHandle, string[]? faces, string action)
    {
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} needs 'faces': faces of the body (se_geometry faces).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, faces[0]);
        Face[] resolved = faces.Select(h => context.Handles.Resolve<Face>(h, "faces")).ToArray();
        IReadOnlyList<(Model Model, Body Body)> bodies = model.Bodies();
        int tag = Late.Get(resolved[0], "Body") is Body first ? first.Tag : 0;
        (Model Model, Body Body) owner = bodies.FirstOrDefault(b => b.Body.Tag == tag);
        if (owner.Model is null)
        {
            throw ToolException.InvalidArgument($"'{faces[0]}' is not a face of a design body.", "Pass faces of a design body (se_geometry faces); surfaces are edited with se_surface.");
        }

        for (int i = 1; i < resolved.Length; i++)
        {
            if (!IsOnBody(resolved[i], owner.Body))
            {
                throw ToolException.InvalidArgument($"'{faces[i]}' is on another body than '{faces[0]}'.", "Pass the faces of one body at a time.");
            }
        }

        return (model, owner.Model, owner.Body, resolved);
    }

    private static bool IsOnBody(Face face, Body body) => Late.Get(face, "Body") is Body b && b.Tag == body.Tag;

    /// <summary>
    /// Makes the split and checks it: more faces on the body, the same volume and, with <paramref name="mustSplit"/> (a face ID
    /// and its area), that face split; otherwise it is deleted. The pieces are the faces of the body that are new (by ID) or
    /// whose area changed.
    /// </summary>
    private static object SplitChecked(SeContext context, ModelDocument model, Model owner, Body body, string what, Func<object> create,
        string hint, (int Id, double Area)? mustSplit = null)
    {
        Face[] original = BodyFaces(body);
        int before = original.Length;
        var areas = new Dictionary<int, double>();
        foreach (Face face in original)
        {
            areas.TryAdd(face.ID, face.Area);
        }

        double volume = model.Volume();
        bool switched = model.EnsureOrdered();
        object feature = Create(what, create);
        context.ModelChanged(model.Document, $"the model changed ({what})");
        string? status = StatusOf(feature);
        Face[] now = Late.TryGet(owner, "Body") is Body after ? BodyFaces(after) : [];
        double volumeAfter = model.Volume();
        string? problem = status is not ("ok" or "warning") ? $"status {status}"
            : now.Length <= before ? "the faces were not split"
            : Math.Abs(volumeAfter - volume) > Math.Max(1e-12, volume * 1e-9) ? "the volume changed"
            : mustSplit is { } kept && now.Any(f => f.ID == kept.Id && Math.Abs(f.Area - kept.Area) <= Math.Max(1e-12, kept.Area * 1e-9))
                ? "the line went onto the neighbouring face"
            : null;
        if (problem is not null)
        {
            TryDeleteSurface(feature);
            throw new ToolException("feature_failed", $"Solid Edge did not make the {what} ({problem}); it was deleted.", hint);
        }

        Face[] made = [.. now.Where(f => !areas.TryGetValue(f.ID, out double area) || Math.Abs(f.Area - area) > Math.Max(1e-12, area * 1e-9))];
        return new
        {
            feature = Describe(context, model, feature),
            facesBefore = before,
            faces = now.Length,
            pieces = made.Select(f => new { handle = context.Register(f, "face", model.Document), area = Geo.Mm2(f.Area) }).ToArray(),
            edges = what == "offset edge" ? CurveEdges(feature).Select(e => new { length = Geo.Mm(EdgeLength(e)) }).ToArray() : null,
            volume = Geo.Mm3(volumeAfter),
            warnings = Warnings(switched, status == "warning"),
        };
    }
}
