// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// Removes what lies on one side of a plane: an extruded cutout through all, to that side, of a rectangle drawn on the plane
    /// around the whole body, as it is done by hand. Verified: the Boolean feature with a reference plane as the tool
    /// (seBooleanPlaneFront or Back) gives E_INVALIDARG whatever the side.
    /// </summary>
    private static object CutByPlane(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string plane = spec.Plane ?? throw ToolException.InvalidArgument("cut_by_plane requires 'plane': '1'-'3', a reference plane or a planar face handle.");
        if (spec.Side is not (PartFeatureSide.Normal or PartFeatureSide.Reverse))
        {
            throw ToolException.InvalidArgument("cut_by_plane requires 'side': normal removes the material on the side the plane's sketch normal points to (se_sketch create shows it), reverse the other side.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, plane);
        List<SolidEdgeGeometry.Body> bodies = [.. model.Bodies().Select(b => b.Body)];
        if (bodies.Count == 0)
        {
            throw new ToolException("no_model", $"'{model.Document.Name}' has no solid to cut yet.", "Model it first.");
        }

        (double[] min, double[] max) = InspectService.Range(bodies);
        bool switched = model.EnsureOrdered();
        Profile profile = model.Sketches.Add().Profiles.Add(model.ResolvePlane(context, plane));
        string sketch = context.Register(profile, "sketch", model.Document);
        try
        {
            double[] normal = SketchService.RectangleAround(context, sketch, [.. min.Select(Units.MmToM)], [.. max.Select(Units.MmToM)]);
            object cut = Extrude(context, null, sketch, FeatureOperation.Cut, ExtrudeExtent.ThroughAll,
                spec.Side == PartFeatureSide.Normal ? FeatureDirection.Normal : FeatureDirection.Reverse, null);
            return new
            {
                cut,
                removedToward = spec.Side == PartFeatureSide.Normal ? normal : [.. normal.Select(n => -n + 0.0)],
                note = switched ? "The document was switched to ordered modelling." : null,
            };
        }
        catch (ToolException)
        {
            SketchService.Delete(context, sketch);
            throw;
        }
    }
}
