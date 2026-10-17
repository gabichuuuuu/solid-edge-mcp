// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Slots along an open path, web networks and lips of shelled parts, and scaling of the whole body.</summary>
public static partial class FeatureService
{
    /// <summary>
    /// Slot cut along an open path (lines and arcs), through the part or to a finite depth. Verified: the end condition
    /// igFormedEnd gives rounded ends (the path is extended by half the width at each end); igNone gives square ends that stop
    /// at the ends of the path. Optionally with a counterbore at the top. The cut side is tried both ways, as a person flips the
    /// arrow towards the part.
    /// </summary>
    private static object Slot(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string sketch = spec.Sketch ?? throw ToolException.InvalidArgument("'sketch' is required: an open path (lines, arcs) closed with se_sketch 'close' and open: true.");
        double width = Units.MmToM(Units.RequirePositive(spec.Width ?? throw ToolException.InvalidArgument("slot requires 'width' (mm)."), "width"));
        double? depth = spec.Depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : null;
        FeaturePropertyConstants ends = spec.SquareEnds ? FeaturePropertyConstants.igNone : FeaturePropertyConstants.igFormedEnd;
        // Verified: Solid Edge takes the counterbore as an offset on each side of the slot (total width = width + 2 offsets),
        // around the rounded ends too; with square ends it stops at the ends of the path like the slot.
        (double cbOffset, double cbDepth) = spec.Counterbore switch
        {
            null => (0, 0),
            [double w, double h] => (Units.MmToM(Units.RequirePositive(w, "counterbore[0]")) - width) / 2 is var offset && offset > 0
                ? (offset, Units.MmToM(Units.RequirePositive(h, "counterbore[1]")))
                : throw ToolException.InvalidArgument("The counterbore must be wider than the slot: 'counterbore' is [total width, depth] in mm."),
            _ => throw ToolException.InvalidArgument("'counterbore' is [total width, depth] in mm."),
        };
        if (depth is { } finite && cbDepth >= finite)
        {
            throw ToolException.InvalidArgument("The counterbore must be shallower than the slot 'depth'.");
        }
        FeaturePropertyConstants type = spec.Counterbore is null ? FeaturePropertyConstants.igRegularSlot : FeaturePropertyConstants.igRecessedCounterboreSlot;
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.ResolveDefined(context, sketch);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();

        ToolException? last = null;
        foreach (FeaturePropertyConstants side in new[] { FeaturePropertyConstants.igLeft, FeaturePropertyConstants.igRight })
        {
            IncludedProfile included = Include(model, sketchProfile);
            try
            {
                object feature = Create("slot", included, () => target.Slots.Add(included.Profile, type, ends,
                    width, cbOffset, cbDepth, depth is null ? FeaturePropertyConstants.igThroughAll : FeaturePropertyConstants.igFinite, side, depth ?? 0,
                    0, null, FeaturePropertyConstants.igNone, FeaturePropertyConstants.igNone, 0, 0, null,
                    null, OffsetSideConstants.seOffsetNone, 0, null, OffsetSideConstants.seOffsetNone, 0));
                object result = Verify(context, model, feature, before, -1, switched, sketch, included);
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
    /// Web network (ribs inside a shelled part) from open lines drawn on a plane across the part: each line becomes a wall of
    /// the given thickness that grows from the plane to the next face, or to a finite depth. Verified with a line across a
    /// shelled box: seWebNormal grows towards the floor; the other direction is tried when it fails.
    /// </summary>
    private static object WebNetwork(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string sketch = spec.Sketch ?? throw ToolException.InvalidArgument("'sketch' is required: open lines closed with se_sketch 'close' and open: true.");
        double thickness = Units.MmToM(Units.RequirePositive(spec.Thickness ?? throw ToolException.InvalidArgument("web_network requires 'thickness' (mm)."), "thickness"));
        double? depth = spec.Depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : null;
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.ResolveDefined(context, sketch);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();

        ToolException? last = null;
        foreach (WebNetworkFeatureConstants direction in new[] { WebNetworkFeatureConstants.seWebNormal, WebNetworkFeatureConstants.seWebReverseNormal })
        {
            IncludedProfile included = Include(model, sketchProfile);
            try
            {
                Array profiles = new object[] { included.Profile };
                object feature = Create("web network", included, () => depth is { } finite
                    ? target.WebNetworks.Add(1, ref profiles, thickness, direction, WebNetworkFeatureConstants.seWebExtendFinite,
                        WebNetworkFeatureConstants.seWebProfileExtend, finite)
                    : target.WebNetworks.Add(1, ref profiles, thickness, direction));
                object result = Verify(context, model, feature, before, 1, switched, sketch, included,
                    direction: direction == WebNetworkFeatureConstants.seWebNormal ? "normal" : "reverse");
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
    /// Lip along a chain of edges of a rim (e.g. the outer edges of the open face of a shelled part): a strip of the given width
    /// and height on the rim, flush with the side of the edges. Verified: one outer edge of 100 with width 1 and height 2 adds 200;
    /// the four outer edges of a 100x60 rim add 1*2*(320 - 4) = 632 (with the faces swapped the lip lies outside the side face:
    /// 1*2*(320 + 8) = 656).
    /// </summary>
    private static object Lip(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (spec.Edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("lip requires 'edges': one chain of edges of the rim (se_geometry 'edges').");
        }

        double width = Units.MmToM(Units.RequirePositive(spec.Width ?? throw ToolException.InvalidArgument("lip requires 'width' (mm)."), "width"));
        double height = Units.MmToM(Units.RequirePositive(spec.Height ?? throw ToolException.InvalidArgument("lip requires 'height' (mm)."), "height"));
        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Edges[0]);
        Array edges = spec.Edges.Select(h => (object)context.Handles.Resolve<Edge>(h, "edges")).ToArray();
        var first = (Edge)edges.GetValue(0)!;
        Array adjacent = Array.Empty<object>();
        first.GetFaces(out int count, ref adjacent);
        if (count != 2)
        {
            throw ToolException.InvalidArgument($"The first edge touches {count} faces; a lip needs an edge between the rim face and a side face.");
        }

        // The cap is the face the lip stands on (the rim) and the side the face it is flush with; swapped, the lip grows outside
        // the side face. Without 'faces', the cap is the smaller face (a rim is narrow).
        Face[] pair = [(Face)adjacent.GetValue(0)!, (Face)adjacent.GetValue(1)!];
        Face cap = spec.Faces is [string capHandle]
            ? context.Handles.Resolve<Face>(capHandle, "faces")
            : pair.MinBy(f => f.Area)!;
        Face? side = pair.FirstOrDefault(f => f.Tag != cap.Tag);
        if (side is null || pair.All(f => f.Tag != cap.Tag))
        {
            throw ToolException.InvalidArgument("The face in 'faces' must be one of the two faces of the first edge (the rim face the lip stands on).");
        }
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object feature = Create("lip", () => target.Lips.Add(edges.Length, ref edges, side, cap, width, height));
        return Verify(context, model, feature, before, 1, switched);
    }
    /// <summary>
    /// Scales the body about the part origin (with several design bodies, the first one): one factor (uniform) or one per
    /// axis. Verified: the volume is multiplied by the product of the factors and the centre of volume by each factor.
    /// </summary>
    private static object ScaleBody(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        double[] factors = spec.Scale switch
        {
            [double uniform] => [uniform, uniform, uniform],
            [double x, double y, double z] => [x, y, z],
            _ => throw ToolException.InvalidArgument("scale_body requires 'scale': one factor (uniform) or three (x, y, z).", "Example: [2] or [1, 1, 0.5]."),
        };
        foreach (double factor in factors)
        {
            Units.RequirePositive(factor, "scale");
        }

        double product = factors[0] * factors[1] * factors[2];
        if (Math.Abs(product - 1) < 1e-9)
        {
            throw ToolException.InvalidArgument("The factors in 'scale' leave the volume unchanged.", "Pass factors whose product is not 1.");
        }

        ModelDocument model = ModelDocument.Get(context, documentHandle);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object body = target.Body;
        // Only this body is scaled: with several design bodies (a split part) the others keep their volume.
        double bodyBefore = ((SolidEdgeGeometry.Body)body).Volume;
        Array bodies = new object[] { body };
        object feature = Create("scale", () => target.ScaleBodyFeatures.Add(1, ref bodies, factors[0], factors[1], factors[2]));
        object result = Verify(context, model, feature, before, Math.Sign(product - 1), switched);
        double expected = before + ((product - 1) * bodyBefore);
        double after = model.Volume();
        if (Math.Abs(after - expected) > Math.Max(VolumeTolerance, expected * 1e-6))
        {
            TryDelete(feature);
            throw new ToolException("feature_failed",
                $"The scaled volume is {Geo.Mm3(after)} mm³ instead of {Geo.Mm3(expected)} mm³ (the factors times the volume before); the scale was deleted.");
        }

        return result;
    }

    /// <summary>
    /// Copy of a saved part file (scaled, optionally mirrored about a reference plane): in an empty part it is the base body;
    /// otherwise it is united with the body. Verified: a copy that touches the body adds its volume; one that adds nothing
    /// (inside the body) is left failed, and is deleted here.
    /// </summary>
    private static object CopyPart(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string file = context.Paths.Check(spec.Path, "path");
        if (System.IO.Path.GetExtension(file).ToLowerInvariant() is not (".par" or ".psm"))
        {
            throw ToolException.InvalidArgument("copy_part copies a part or sheet metal file (.par or .psm).");
        }

        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{file}' does not exist.", "Save the part to copy first (se_documents save_as) or check the path.");
        }

        double[] factors = spec.Scale switch
        {
            null => [1, 1, 1],
            [double uniform] => [uniform, uniform, uniform],
            [double x, double y, double z] => [x, y, z],
            _ => throw ToolException.InvalidArgument("'scale' is one factor (uniform) or three (x, y, z).", "Example: [2] or [1, 1, 0.5]."),
        };
        foreach (double factor in factors)
        {
            Units.RequirePositive(factor, "scale");
        }

        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object mirror = System.Reflection.Missing.Value;
        if (spec.Plane is not null)
        {
            mirror = model.ResolvePlaneSource(context, spec.Plane) as RefPlane
                ?? throw ToolException.InvalidArgument("The mirror 'plane' of copy_part must be a reference plane ('1'-'3' or a refplane handle).");
        }

        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        object feature = Create("copied part", () =>
        {
            Model copy = model.Models.AddCopiedPart(file, factors[0], factors[1], factors[2], mirror);
            CopiedParts copies = copy.CopiedParts;
            return copies.Item(copies.Count);
        });
        return Verify(context, model, feature, before, 1, switched);
    }
}
