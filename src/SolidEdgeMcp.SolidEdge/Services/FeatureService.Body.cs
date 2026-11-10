// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Parameters of the other part features (mm).</summary>
public sealed record PartFeatureSpec(
    PartFeatureAction Action,
    string[]? Faces = null,
    string? Sketch = null,
    double? Thickness = null,
    PartFeatureSide Side = PartFeatureSide.Auto,
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
    double[]? Counterbore = null,
    string? ReplaceWith = null,
    string[]? Bodies = null,
    double? Radius = null,
    string[]? TargetFaces = null,
    double? Leg = null,
    double? Leg2 = null,
    double? Angle = null,
    WeldType? Weld = null,
    WeldSide WeldSide = WeldSide.Arrow,
    string? Note = null,
    bool AllAround = false,
    bool Field = false,
    bool Coaxial = false);

public static partial class FeatureService
{
    // Threads.Add is not offered: in Solid Edge 2026 it returns E_INVALIDARG for every combination tried (cylinder faces of a
    // shaft or a hole; end faces or edges; HoleData from Add or AddEx with the ISO Metric table, regular, tapped or thread
    // type; reserved arguments missing, null or 0; arrays by value or by reference). Threads are made with se_hole 'tapped'
    // (cosmetic) or se_thread (physical, a helix cutout).
    public static object PartFeature(SeContext context, string? documentHandle, PartFeatureSpec spec) => spec.Action switch
    {
        PartFeatureAction.Rib => Rib(context, documentHandle, spec),
        PartFeatureAction.Thicken => Thicken(context, documentHandle, spec),
        PartFeatureAction.MirrorBody => MirrorBody(context, documentHandle, spec),
        PartFeatureAction.DeleteFaces => DeleteFaces(context, documentHandle, spec),
            PartFeatureAction.DeleteRegion => DeleteRegion(context, documentHandle, spec),
        PartFeatureAction.DeleteHoles => DeleteHoles(context, documentHandle, spec),
        PartFeatureAction.DeleteRounds => DeleteRounds(context, documentHandle, spec),
        PartFeatureAction.OffsetFaces => OffsetFaces(context, documentHandle, spec),
        PartFeatureAction.ReplaceFaces => ReplaceFaces(context, documentHandle, spec),
        PartFeatureAction.CutByPlane => CutByPlane(context, documentHandle, spec),
        PartFeatureAction.RotateFaces => RotateFaces(context, documentHandle, spec),
        PartFeatureAction.RedefineFaces => RedefineFaces(context, documentHandle, spec),
        PartFeatureAction.ResizeHoles => ResizeHoles(context, documentHandle, spec),
        PartFeatureAction.ResizeRounds => ResizeRounds(context, documentHandle, spec),
        PartFeatureAction.Radiate => Radiate(context, documentHandle, spec),
        PartFeatureAction.NormalProtrusion => NormalToFace(context, documentHandle, spec, cut: false),
        PartFeatureAction.NormalCutout => NormalToFace(context, documentHandle, spec, cut: true),
        PartFeatureAction.Split => SplitBody(context, documentHandle, spec),
        PartFeatureAction.Unite => UniteBodies(context, documentHandle, spec),
        PartFeatureAction.Slot => Slot(context, documentHandle, spec),
        PartFeatureAction.WebNetwork => WebNetwork(context, documentHandle, spec),
        PartFeatureAction.Lip => Lip(context, documentHandle, spec),
        PartFeatureAction.ScaleBody => ScaleBody(context, documentHandle, spec),
        PartFeatureAction.CopyPart => CopyPart(context, documentHandle, spec),
        PartFeatureAction.FilletWeld => FilletWeld(context, documentHandle, spec),
        PartFeatureAction.LabelWeld => LabelWeld(context, documentHandle, spec),
        _ => throw new ArgumentOutOfRangeException(nameof(spec)),
    };

    /// <summary>
    /// Rib from an open sketch (one chain closed with se_sketch 'close' and open: true) drawn across the part; the
    /// profile is extended to the part and the rib grows in the sketch plane, with the thickness across it.
    /// </summary>
    private static object Rib(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string sketch = spec.Sketch ?? throw ToolException.InvalidArgument("'sketch' is required: an open chain closed with se_sketch 'close' and open: true.");
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.ResolveDefined(context, sketch);
        double thickness = Units.MmToM(Units.RequirePositive(spec.Thickness ?? throw ToolException.InvalidArgument("rib requires 'thickness' (mm)."), "thickness"));
        FeaturePropertyConstants thicknessSide = spec.Side switch
        {
            PartFeatureSide.Auto or PartFeatureSide.Symmetric => FeaturePropertyConstants.igSymmetric,
            PartFeatureSide.Normal => FeaturePropertyConstants.igRight,
            PartFeatureSide.Reverse => FeaturePropertyConstants.igLeft,
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
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
                Hide(included);
                return result;
            }
            catch (ToolException ex) when (ex.Code == "feature_failed")
            {
                last = ex;
            }
        }

        throw last!;
    }

    /// <summary>
    /// Thickens faces by a distance: outwards by default (adds material), or the other way. In a part that has only
    /// surfaces the thickened surface becomes the first solid (Models.AddThickenFeature; verified: a 120 degree arc of
    /// radius 40 extruded 60 as a surface and thickened 2 mm gives 10304.42 mm3).
    /// </summary>
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
            PartFeatureSide.Auto => [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
            PartFeatureSide.Normal => [FeaturePropertyConstants.igRight],
            PartFeatureSide.Reverse => [FeaturePropertyConstants.igLeft],
            _ => throw ToolException.InvalidArgument($"Unknown side '{ArgumentNames.Snake(spec.Side)}' for thicken.", "Use auto, normal or reverse."),
        };
        Array faces = spec.Faces.Select(h => (object)context.Handles.Resolve<Face>(h, "faces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model? target = model.TryModel();
        double before = target is null ? 0 : model.Volume();
        ToolException? last = null;
        foreach (FeaturePropertyConstants side in sides)
        {
            try
            {
                object feature = Create("thicken", () =>
                {
                    if (target is not null)
                    {
                        return target.Thickens.Add(side, thickness, faces.Length, ref faces);
                    }

                    Model created = model.Models.AddThickenFeature(side, thickness, faces.Length, ref faces);
                    return created.Thickens.Item(created.Thickens.Count);
                });
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
