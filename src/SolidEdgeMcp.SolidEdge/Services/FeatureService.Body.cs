using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Parameters of the other part features (mm).</summary>
public sealed record PartFeatureSpec(
    string Action,
    string[]? Faces = null,
    string? Sketch = null,
    double? Thickness = null,
    string Side = "auto",
    string? Plane = null,
    double? Depth = null,
    double? Distance = null,
    double? Diameter = null,
    double? Width = null,
    bool SquareEnds = false,
    double[]? Scale = null,
    string[]? Edges = null,
    double? Height = null,
    string? Path = null,
    double[]? Counterbore = null);

public static partial class FeatureService
{
    // Threads.Add is not offered: in Solid Edge 2026 it returns E_INVALIDARG for every combination tried (cylinder faces of a
    // shaft or a hole; end faces or edges; HoleData from Add or AddEx with the ISO Metric table, regular, tapped or thread
    // type; reserved arguments missing, null or 0; arrays by value or by reference). Threads are made with se_hole 'tapped'
    // (cosmetic) or a helix cut with se_loft_sweep (physical).
    public static readonly IReadOnlyList<string> PartFeatureActions = ["rib", "thicken", "mirror_body", "delete_faces", "delete_holes", "delete_rounds", "offset_faces", "slot", "web_network", "lip", "scale_body", "copy_part"];

    public static object PartFeature(SeContext context, string? documentHandle, PartFeatureSpec spec) => spec.Action switch
    {
        "rib" => Rib(context, documentHandle, spec),
        "thicken" => Thicken(context, documentHandle, spec),
        "mirror_body" => MirrorBody(context, documentHandle, spec),
        "delete_faces" => DeleteFaces(context, documentHandle, spec),
        "delete_holes" => DeleteHoles(context, documentHandle, spec),
        "delete_rounds" => DeleteRounds(context, documentHandle, spec),
        "offset_faces" => OffsetFaces(context, documentHandle, spec),
        "slot" => Slot(context, documentHandle, spec),
        "web_network" => WebNetwork(context, documentHandle, spec),
        "lip" => Lip(context, documentHandle, spec),
        "scale_body" => ScaleBody(context, documentHandle, spec),
        "copy_part" => CopyPart(context, documentHandle, spec),
        _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", PartFeatureActions)}."),
    };

    /// <summary>
    /// Rib from an open sketch (one chain closed with se_sketch 'close' and open: true) drawn across the part; the
    /// profile is extended to the part and the rib grows in the sketch plane, with the thickness across it.
    /// </summary>
    private static object Rib(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string sketch = spec.Sketch ?? throw ToolException.InvalidArgument("'sketch' is required: an open chain closed with se_sketch 'close' and open: true.");
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.Resolve(context, sketch);
        double thickness = Units.MmToM(Units.RequirePositive(spec.Thickness ?? throw ToolException.InvalidArgument("rib requires 'thickness' (mm)."), "thickness"));
        FeaturePropertyConstants thicknessSide = spec.Side switch
        {
            "auto" or "symmetric" => FeaturePropertyConstants.igSymmetric,
            "normal" => FeaturePropertyConstants.igRight,
            "reverse" => FeaturePropertyConstants.igLeft,
            _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}' for a rib.", "Use symmetric, normal or reverse."),
        };
        double? depth = spec.Depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : null;
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();

        // The material side (which side of the chain the rib grows towards) is tried both ways, as a person flips
        // the arrow until the preview meets the part.
        ToolException? last = null;
        foreach (FeaturePropertyConstants materialSide in new[] { FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft })
        {
            IncludedProfile included = Include(model, sketchProfile);
            try
            {
                object feature = Create("rib", included, () => depth is { } finite
                    ? target.Ribs.Add(included.Profile, FeaturePropertyConstants.igExtend, FeaturePropertyConstants.igThkNormalToProfilePlane,
                        materialSide, thicknessSide, thickness, finite)
                    : target.Ribs.Add(included.Profile, FeaturePropertyConstants.igExtend, FeaturePropertyConstants.igThkNormalToProfilePlane,
                        materialSide, thicknessSide, thickness));
                object result = Verify(context, model, feature, before, 1, switched, sketch, included);
                Hide(sketchProfile);
                Hide(included.Profile);
                return result;
            }
            catch (ToolException ex) when (ex.Code == "feature_failed")
            {
                last = ex;
            }
        }

        throw last!;
    }

    /// <summary>Thickens faces by a distance: outwards by default (adds material), or the other way.</summary>
    private static object Thicken(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (spec.Faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'faces' must list the faces to thicken.", "Get them with se_geometry action 'faces'.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Faces[0]);
        double thickness = Units.MmToM(Units.RequirePositive(spec.Thickness ?? throw ToolException.InvalidArgument("thicken requires 'thickness' (mm)."), "thickness"));
        FeaturePropertyConstants[] sides = spec.Side switch
        {
            "auto" => [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
            "normal" => [FeaturePropertyConstants.igRight],
            "reverse" => [FeaturePropertyConstants.igLeft],
            _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}' for thicken.", "Use auto, normal or reverse."),
        };
        Array faces = spec.Faces.Select(h => (object)context.Handles.Resolve<Face>(h, "faces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        ToolException? last = null;
        foreach (FeaturePropertyConstants side in sides)
        {
            try
            {
                object feature = Create("thicken", () => target.Thickens.Add(side, thickness, faces.Length, ref faces));
                return Verify(context, model, feature, before, 1, switched,
                    direction: side == FeaturePropertyConstants.igRight ? "normal" : "reverse");
            }
            catch (ToolException ex) when (ex.Code == "feature_failed")
            {
                // The faces were resolved before the change; a failed attempt is deleted, so they are still valid.
                last = ex;
            }
        }

        throw last!;
    }

    /// <summary>Mirrors the whole body about a plane (the "Mirror copy part" command), keeping the original.</summary>
    private static object MirrorBody(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string plane = spec.Plane ?? throw ToolException.InvalidArgument("'plane' is required: '1'-'3', a refplane or a planar face handle.");
        ModelDocument model = ModelDocument.For(context, documentHandle, plane);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        RefPlane refPlane = model.ResolvePlane(context, plane);
        double before = model.Volume();
        object feature = Create("mirror body", () => target.MirrorParts.Add(refPlane));
        return Verify(context, model, feature, before, 1, switched);
    }
}
