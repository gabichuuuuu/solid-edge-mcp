// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using OffsetType = SolidEdgeFrameworkSupport.PatternOffsetTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Copies of construction geometry (mirror and along a curve) and patterns of the whole part.</summary>
public static partial class FeatureService
{
    /// <summary>Whether a resolved handle is construction geometry: a body, face or edge, or a construction surface or curve.</summary>
    private static bool IsConstructionGeometry(object item) =>
        item is Body or Face or Edge || Late.TryGet(item, "Parent") is ConstructionModel;

    /// <summary>
    /// Mirror copy or copies along a curve of construction bodies (surfaces, curves, closed bodies). Verified (ordered):
    /// Constructions.MirrorCopyGeometries.Add(n, bodies, plane) with a construction body or a face of it (a surface feature
    /// fails with E_NOINTERFACE, so its body is passed) adds one body per copied body; PatternCopyGeometries.AddByCurve with
    /// the same arguments as a feature pattern along a curve adds the copies along the edge.
    /// </summary>
    private static object ConstructionPattern(SeContext context, string? documentHandle, PatternSpec spec, object[] items)
    {
        if (spec.Type is not ("mirror" or "curve"))
        {
            throw ToolException.InvalidArgument("Construction geometry is copied with type 'mirror' or 'curve' only.",
                "Use type 'mirror' with 'plane', or 'curve' with 'curve', 'count' and 'spacing'.");
        }

        string[] handles = spec.Features!;
        ModelDocument model = ModelDocument.For(context, documentHandle, handles[0]);
        Body[] bodies = items.Select((item, i) => ConstructionOf(model, item, handles[i], "features").Body).DistinctBy(b => b.Tag).ToArray();
        RefPlane? plane = null;
        if (spec.Type == "mirror")
        {
            if (string.IsNullOrWhiteSpace(spec.Plane))
            {
                throw ToolException.InvalidArgument("'plane' is required: the mirror plane ('1'-'3', refplane or planar face handle).");
            }

            plane = model.ResolvePlane(context, spec.Plane);
        }

        bool switched = model.EnsureOrdered();
        Constructions constructions = ConstructionsOf(model);
        HashSet<int> before = [.. ConstructionBodies(model).Select(b => b.Body.Tag)];
        Array entities = bodies.Cast<object>().ToArray();
        object feature;
        int expected;
        if (plane is not null)
        {
            RefPlane mirror = plane;
            feature = Create("mirror copy of construction geometry", () => constructions.MirrorCopyGeometries.Add(bodies.Length, ref entities, mirror));
            expected = bodies.Length;
        }
        else
        {
            (Array curves, object anchor, PatternCurveAnchorSideConstants side, double spacing) = CurveArguments(context, spec);
            Array none = Array.Empty<object>();
            feature = Create("pattern of construction geometry", () => constructions.PatternCopyGeometries.AddByCurve(bodies.Length, ref entities,
                1, ref curves, anchor, side, 0.0, OffsetType.sePatternFixedOffset, spec.Count, spacing,
                0, ref none, null, PatternCurveAnchorSideConstants.sePatternCurveLeftSide, 0.0, OffsetType.sePatternFitOffset, 1, 0.0,
                PatternTransformTypeConstants.sePatternTransformLinear, PatternTransformRotateTypeConstants.sePatternTransformRotateOnCurvePosition, null));
            expected = bodies.Length * (spec.Count - 1);
        }

        context.ModelChanged(model.Document, "the model changed (copy of construction geometry)");
        string? status = StatusOf(feature);
        List<(string? Name, Body Body)> added = [.. ConstructionBodies(model).Where(b => !before.Contains(b.Body.Tag))];
        if (status == "failed" || added.Count == 0)
        {
            TryDelete(feature);
            context.ModelChanged(model.Document, "the model changed (copy deleted)");
            throw new ToolException("feature_failed",
                $"Solid Edge did not copy the construction geometry ({(status == "failed" ? "it reports the copy as failed" : "no new body appeared")}); the copy was deleted.",
                spec.Type == "mirror" ? "Check the mirror plane." : "Check that the curve is an open edge and that the copies fit along it (count and spacing).");
        }

        List<string> warnings = Warnings(switched, status == "warning") ?? [];
        if (added.Count != expected)
        {
            warnings.Add($"{expected} new bodies were expected and {added.Count} appeared: check the copies (se_body list).");
        }

        return new
        {
            feature = Describe(context, model, feature),
            copies = added.Select(b => DescribeConstruction(context, model, b.Name, b.Body)).ToList(),
            warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    /// <summary>
    /// Pattern of the whole part (every design body) along a curve. Verified (ordered): Model.PatternParts.AddByCurve with
    /// the curve arguments of a feature pattern: a block 100 x 60 x 20 three times along a 400 mm edge, 360000 mm³.
    /// </summary>
    private static object PartPattern(SeContext context, string? documentHandle, PatternSpec spec)
    {
        if (spec.Type != "curve")
        {
            throw ToolException.InvalidArgument("part: true copies the whole part along a curve only (type 'curve').",
                "For other layouts pattern the features (rectangular, circular, mirror) or use se_part_feature mirror_body.");
        }

        if (spec.Features is { Length: > 0 })
        {
            throw ToolException.InvalidArgument("part: true copies the whole part: leave 'features' out.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Curve ?? "");
        (Array curves, object anchor, PatternCurveAnchorSideConstants side, double spacing) = CurveArguments(context, spec);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        Array none = Array.Empty<object>();
        object feature = Create("pattern of the part", () => target.PatternParts.AddByCurve(1, ref curves, anchor, side, 0.0, OffsetType.sePatternFixedOffset,
            spec.Count, spacing, 0, ref none, null, PatternCurveAnchorSideConstants.sePatternCurveLeftSide, 0.0, OffsetType.sePatternFitOffset, 1, 0.0,
            PatternTransformTypeConstants.sePatternTransformLinear, PatternTransformRotateTypeConstants.sePatternTransformRotateOnCurvePosition, null));
        return Verify(context, model, feature, before, 1, switched,
            hint: "Check that the curve is an open edge and that the copies fit along it (count and spacing).");
    }

    /// <summary>
    /// The edge, anchor (its start vertex), side and spacing (m) of a pattern along a curve: copies towards the end of the
    /// edge, or towards its start with 'reverse'.
    /// </summary>
    private static (Array Curves, object Anchor, PatternCurveAnchorSideConstants Side, double Spacing) CurveArguments(SeContext context, PatternSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Curve))
        {
            throw ToolException.InvalidArgument("Type 'curve' requires 'curve': the edge to follow.", "Get one with se_geometry action 'edges' (also the edge of a se_curve).");
        }

        if (spec.Count < 2)
        {
            throw ToolException.InvalidArgument("'count' must be at least 2 for a pattern along a curve.");
        }

        double spacing = Units.MmToM(Units.RequirePositive(spec.Spacing ?? throw ToolException.InvalidArgument("Type 'curve' requires 'spacing' (mm) between occurrences, measured along the curve."), "spacing"));
        Edge edge = context.Handles.Resolve<Edge>(spec.Curve, "curve");
        object anchor = Late.Get(edge, "StartVertex") ?? throw ToolException.InvalidArgument(
            $"The edge '{spec.Curve}' has no start point (a closed edge such as a full circle).", "Use an open edge.");
        PatternCurveAnchorSideConstants side = spec.Reverse ? PatternCurveAnchorSideConstants.sePatternCurveLeftSide : PatternCurveAnchorSideConstants.sePatternCurveRightSide;
        return (new object[] { edge }, anchor, side, spacing);
    }
}
