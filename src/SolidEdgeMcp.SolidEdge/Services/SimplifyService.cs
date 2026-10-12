// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// The simplified model of a part or sheet metal part (Simplify tab): a lighter copy of the design body, without holes,
/// rounds or chosen faces, that assemblies can show instead of the design body. Verified (block 100 x 60 x 20 with a 10 mm
/// hole and a 5 mm round, 117892.70 mm³): SimplifiedModels.Add(Models.Item(1)) makes it and activates it; DeleteBlends.Add
/// with a face collection of its body gives back the round (118429.20 mm³) and DeleteHoles.Add the hole (120000 mm³); the
/// design body does not change; MakeActive switches between the design and the simplified model; deleting its features
/// brings its body back. SimplifiedModels has no Remove: once made, the simplified model stays in the document.
/// </summary>
public static class SimplifyService
{
    public static readonly IReadOnlyList<string> Actions = ["status", "create", "delete_holes", "delete_rounds", "delete_faces", "delete_region", "activate", "delete"];

    private const int FaceCollection = 1;
    private const int EdgeCollection = 2;

    public static object Run(SeContext context, string? documentHandle, string action, double? diameter, string[]? faces, string? model, string? feature,
        string[]? edges = null) => action switch
    {
        "status" => Status(context, Document(context, documentHandle, faces, feature)),
        "create" => Create(context, Document(context, documentHandle, faces, feature)),
        "delete_holes" => DeleteHoles(context, Document(context, documentHandle, faces, feature), diameter),
        "delete_rounds" => DeleteFaces(context, Document(context, documentHandle, faces, feature), faces, rounds: true),
        "delete_faces" => DeleteFaces(context, Document(context, documentHandle, faces, feature), faces, rounds: false),
        "delete_region" => DeleteRegion(context, Document(context, documentHandle, faces, feature), faces, edges),
        "activate" => Activate(context, Document(context, documentHandle, faces, feature), model),
        "delete" => DeleteFeature(context, Document(context, documentHandle, faces, feature), feature),
        _ => throw ToolException.InvalidArgument($"Unknown action '{action}'.", $"Use one of: {string.Join(", ", Actions)}."),
    };

    private static ModelDocument Document(SeContext context, string? documentHandle, string[]? faces, string? feature) =>
        ModelDocument.For(context, documentHandle, faces is { Length: > 0 } ? faces[0] : feature ?? "");

    private static SimplifiedModels Collection(ModelDocument model) => (SimplifiedModels)Late.Get(model.Document, "SimplifiedModels")!;

    private static SimplifiedModel? Find(ModelDocument model)
    {
        SimplifiedModels models = Collection(model);
        return models.Count == 0 ? null : models.Item(1);
    }

    private static SimplifiedModel Require(ModelDocument model) => Find(model) ?? throw new ToolException("no_simplified_model",
        $"'{model.Document.Name}' has no simplified model.", "Make it first with se_simplify action 'create'.");

    private static object Status(SeContext context, ModelDocument model)
    {
        Model design = model.RequireModel();
        SimplifiedModel? simplified = Find(model);
        double designVolume = model.Volume();
        if (simplified is null)
        {
            return new { document = model.Document.Name, simplified = false, active = "design", designVolume = Geo.Mm3(designVolume) };
        }

        Body? body = Late.TryGet(simplified, "Body") as Body;
        Features features = simplified.Features;
        var list = new List<object>();
        for (int i = 1; i <= features.Count; i++)
        {
            list.Add(FeatureService.Describe(context, model, features.Item(i), i));
        }

        return new
        {
            document = model.Document.Name,
            simplified = true,
            active = simplified.IsModelActive ? "simplified" : Late.TryGet(design, "IsModelActive") is true ? "design" : "unknown",
            upToDate = Late.TryGet(simplified, "IsUpToDate") as bool?,
            designVolume = Geo.Mm3(designVolume),
            body = body is null ? null : context.Register(body, "body", model.Document),
            volume = body is null ? (double?)null : Geo.Mm3(body.Volume),
            faces = body is null ? (int?)null : Late.Count(body.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]),
            features = list,
        };
    }

    private static object Create(SeContext context, ModelDocument model)
    {
        if (Find(model) is not null)
        {
            throw new ToolException("already_exists", $"'{model.Document.Name}' already has a simplified model.",
                "Change it with delete_holes, delete_rounds or delete_faces, or switch to it with activate.");
        }

        Model design = model.RequireModel();
        bool switched = model.EnsureOrdered();
        SimplifiedModel simplified;
        try
        {
            simplified = Collection(model).Add(design);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("feature_failed", $"Solid Edge did not make the simplified model: {ex.Message}", "Check that the part has a design body (se_geometry summary).", ex);
        }

        context.ModelChanged(model.Document, "the model changed (simplified model)");
        object status = Status(context, model);
        return switched ? new { result = status, warnings = new[] { "The document was switched to ordered modeling." } } : status;
    }

    private static object DeleteHoles(SeContext context, ModelDocument model, double? diameter)
    {
        double d = Units.MmToM(Units.RequirePositive(diameter ?? throw ToolException.InvalidArgument(
            "delete_holes requires 'diameter': the largest hole diameter to delete from the simplified model (mm)."), "diameter"));
        SimplifiedModel simplified = Require(model);
        return Simplify(context, model, simplified, "delete holes",
            () => simplified.DeleteHoles.Add(HoleTypeToDeleteConstants.seHoleTypeToDeleteAll, d),
            "Check the hole diameters (se_geometry faces of the simplified body, type cylinder) and pass a larger 'diameter'.");
    }

    private static object DeleteFaces(SeContext context, ModelDocument model, string[]? faceHandles, bool rounds)
    {
        string action = rounds ? "delete_rounds" : "delete_faces";
        if (faceHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} requires 'faces': faces of the simplified body ({(rounds ? "the faces of the rounds" : "the faces to delete")}).",
                "Get the body with se_simplify status and its faces with se_geometry faces, source = that body.");
        }

        SimplifiedModel simplified = Require(model);
        Body body = Late.Get(simplified, "Body") as Body ?? throw new ToolException("no_simplified_model", "The simplified model has no body to edit.");
        Face[] faces = faceHandles.Select(h => context.Handles.Resolve<Face>(h, "faces")).ToArray();
        for (int i = 0; i < faces.Length; i++)
        {
            if (Late.Get(faces[i], "Body") is not Body owner || owner.Tag != body.Tag)
            {
                throw ToolException.InvalidArgument($"'{faceHandles[i]}' is not a face of the simplified body.",
                    "Get the body with se_simplify status and its faces with se_geometry faces, source = that body (faces of the design body are edited with se_part_feature).");
            }
        }

        object set = Late.Call(body, "CreateCollection", FaceCollection) ?? throw new ToolException("feature_failed", "Solid Edge did not create the face collection.");
        foreach (Face face in faces)
        {
            Late.Call(set, "Add", face);
        }

        return Simplify(context, model, simplified, rounds ? "delete rounds" : "delete faces",
            () => rounds ? simplified.DeleteBlends.Add(set) : simplified.DeleteFaces.Add(set),
            rounds ? "Pass the faces of rounds of the simplified body." : "Pass faces whose removal leaves a closed body (e.g. the faces of a boss or a pocket).");
    }

    /// <summary>Removes the region of the simplified body bounded by a closed loop of edges, on the side of one face inside it.</summary>
    private static object DeleteRegion(SeContext context, ModelDocument model, string[]? faceHandles, string[]? edgeHandles)
    {
        if (faceHandles is not [string faceHandle] || edgeHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("delete_region requires 'edges' (the closed loop around the region) and one face inside it in 'faces', both of the simplified body.",
                "Get the body with se_simplify status and its faces and edges with se_geometry, source = that body or a face of it.");
        }

        SimplifiedModel simplified = Require(model);
        Body body = Late.Get(simplified, "Body") as Body ?? throw new ToolException("no_simplified_model", "The simplified model has no body to edit.");
        Face face = context.Handles.Resolve<Face>(faceHandle, "faces");
        SolidEdgeGeometry.Edge[] edges = edgeHandles.Select(h => context.Handles.Resolve<SolidEdgeGeometry.Edge>(h, "edges")).ToArray();
        if (Late.Get(face, "Body") is not Body faceBody || faceBody.Tag != body.Tag
            || edges.Any(e => Late.Get(e, "Body") is not Body edgeBody || edgeBody.Tag != body.Tag))
        {
            throw ToolException.InvalidArgument("The face and the edges must belong to the simplified body.",
                "Get the body with se_simplify status and its faces and edges with se_geometry, source = that body (the design body is edited with se_part_feature delete_region).");
        }

        object set = Late.Call(body, "CreateCollection", EdgeCollection) ?? throw new ToolException("feature_failed", "Solid Edge did not create the edge collection.");
        foreach (SolidEdgeGeometry.Edge edge in edges)
        {
            Late.Call(set, "Add", edge);
        }

        return Simplify(context, model, simplified, "delete region", () => simplified.DeleteRegions.Add(set, face),
            "The edges must close a loop around the region, and the face must lie inside it.");
    }

    /// <summary>Adds a simplification and checks that the simplified body changed while the design body did not; otherwise deletes it.</summary>
    private static object Simplify(SeContext context, ModelDocument model, SimplifiedModel simplified, string what, Func<object> create, string hint)
    {
        double designBefore = model.Volume();
        double before = (Late.Get(simplified, "Body") as Body)?.Volume ?? 0;
        int facesBefore = Late.Get(simplified, "Body") is Body b0 ? Late.Count(b0.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]) : 0;
        object feature;
        try
        {
            feature = create();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("feature_failed", $"Solid Edge rejected the {what}: {com.Message}", hint, ex);
        }

        context.ModelChanged(model.Document, $"the model changed ({what})");
        string? status = FeatureService.StatusOf(feature);
        Body? body = Late.TryGet(simplified, "Body") as Body;
        double after = body?.Volume ?? before;
        int facesAfter = body is null ? facesBefore : Late.Count(body.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]);
        if (status == "failed" || (Math.Abs(after - before) <= 1e-12 && facesAfter == facesBefore))
        {
            TryDelete(feature);
            context.ModelChanged(model.Document, $"the model changed ({what} deleted)");
            throw new ToolException("feature_failed", $"The {what} was deleted because {(status == "failed" ? "Solid Edge reports it as failed" : "it did not change the simplified body")}.", hint);
        }

        return new
        {
            feature = FeatureService.Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            faces = facesAfter,
            designVolume = Geo.Mm3(model.Volume()),
            designChanged = Math.Abs(model.Volume() - designBefore) > 1e-12,
            warnings = status == "warning" ? new[] { "Solid Edge reports a warning on this feature." } : null,
        };
    }

    private static object Activate(SeContext context, ModelDocument model, string? which)
    {
        string target = (which ?? "").Trim().ToLowerInvariant();
        if (target is not ("design" or "simplified"))
        {
            throw ToolException.InvalidArgument("activate requires 'model': design or simplified.");
        }

        if (target == "design")
        {
            Late.Call(model.RequireModel(), "MakeActive");
        }
        else
        {
            Require(model).MakeActive();
        }

        context.ModelChanged(model.Document, "the active model changed");
        return Status(context, model);
    }

    private static object DeleteFeature(SeContext context, ModelDocument model, string? featureHandle)
    {
        if (string.IsNullOrWhiteSpace(featureHandle))
        {
            throw ToolException.InvalidArgument("delete requires 'feature': a simplification feature handle (se_simplify status).");
        }

        SimplifiedModel simplified = Require(model);
        object feature = context.Handles.Resolve(featureHandle, "feature");
        Features features = simplified.Features;
        bool member = false;
        for (int i = 1; i <= features.Count && !member; i++)
        {
            member = ReferenceEquals(features.Item(i), feature) || Late.TryGetString(features.Item(i), "Name") == Late.TryGetString(feature, "Name");
        }

        if (!member)
        {
            throw ToolException.InvalidArgument($"'{featureHandle}' is not a feature of the simplified model.", "Use the handles listed by se_simplify status.");
        }

        Late.Call(feature, "Delete");
        context.ModelChanged(model.Document, "the model changed (simplification deleted)");
        return Status(context, model);
    }

    private static void TryDelete(object feature)
    {
        try
        {
            Late.Call(feature, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The error already reports the failure.
        }
    }
}
