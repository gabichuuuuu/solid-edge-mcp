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
    /// at the ends of the path. The cut side is tried both ways, as a person flips the arrow towards the part.
    /// </summary>
    private static object Slot(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string sketch = spec.Sketch ?? throw ToolException.InvalidArgument("'sketch' is required: an open path (lines, arcs) closed with se_sketch 'close' and open: true.");
        double width = Units.MmToM(Units.RequirePositive(spec.Width ?? throw ToolException.InvalidArgument("slot requires 'width' (mm)."), "width"));
        double? depth = spec.Depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : null;
        FeaturePropertyConstants ends = spec.SquareEnds ? FeaturePropertyConstants.igNone : FeaturePropertyConstants.igFormedEnd;
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.Resolve(context, sketch);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();

        ToolException? last = null;
        foreach (FeaturePropertyConstants side in new[] { FeaturePropertyConstants.igLeft, FeaturePropertyConstants.igRight })
        {
            IncludedProfile included = Include(model, sketchProfile);
            try
            {
                object feature = Create("slot", included, () => target.Slots.Add(included.Profile, FeaturePropertyConstants.igRegularSlot, ends,
                    width, 0, 0, depth is null ? FeaturePropertyConstants.igThroughAll : FeaturePropertyConstants.igFinite, side, depth ?? 0,
                    0, null, FeaturePropertyConstants.igNone, FeaturePropertyConstants.igNone, 0, 0, null,
                    null, OffsetSideConstants.seOffsetNone, 0, null, OffsetSideConstants.seOffsetNone, 0));
                object result = Verify(context, model, feature, before, -1, switched, sketch, included);
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
        Profile sketchProfile = SketchService.Resolve(context, sketch);
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
    /// Scales the whole body about the part origin: one factor (uniform) or one per axis. Verified: the volume is multiplied by
    /// the product of the factors and the centre of volume by each factor.
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
        Array bodies = new object[] { target.Body };
        object feature = Create("scale", () => target.ScaleBodyFeatures.Add(1, ref bodies, factors[0], factors[1], factors[2]));
        object result = Verify(context, model, feature, before, Math.Sign(product - 1), switched);
        double expected = before * product;
        double after = model.Volume();
        if (Math.Abs(after - expected) > Math.Max(VolumeTolerance, expected * 1e-6))
        {
            TryDelete(feature);
            throw new ToolException("feature_failed",
                $"The scaled volume is {Geo.Mm3(after)} mm³ instead of {Geo.Mm3(expected)} mm³ (the factors times the volume before); the scale was deleted.");
        }

        return result;
    }
}
