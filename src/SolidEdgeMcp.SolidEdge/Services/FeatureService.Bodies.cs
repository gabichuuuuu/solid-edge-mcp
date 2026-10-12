// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// Splits a body by a reference plane into separate design bodies. Verified (ordered): Splits.Add(1, [body], 1, [plane], 0, 0)
    /// on a 100 x 60 x 20 box at x = 30 leaves two models of 36000 and 84000 mm³; the total volume does not change, so the check
    /// is that there are more bodies.
    /// </summary>
    private static object SplitBody(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        string plane = string.IsNullOrWhiteSpace(spec.Plane)
            ? throw ToolException.InvalidArgument("split requires 'plane': a reference plane ('1'-'3' or a refplane handle from se_ref_plane).",
                "Make an offset plane where the body must be cut, e.g. se_ref_plane offset.")
            : spec.Plane;
        ModelDocument model = int.TryParse(plane, out _) ? ModelDocument.Get(context, documentHandle) : ModelDocument.For(context, documentHandle, plane);
        if (model.ResolvePlaneSource(context, plane, "plane") is not RefPlane refPlane)
        {
            throw ToolException.InvalidArgument("split cuts with a reference plane, not a face.", "Make a plane on the face with se_ref_plane (offset 0) and pass it.");
        }

        bool switched = model.EnsureOrdered();
        IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> bodies = RequireBodies(model, 1);
        (Model owner, object[] targets) = spec.Bodies is { Length: > 0 } handles
            ? Owned(context, bodies, handles)
            : (bodies[0].Model, [bodies[0].Body]);
        double before = model.Volume();
        int count = bodies.Count;
        Array targetArray = targets;
        Array tools = new object[] { refPlane };
        object feature = Create("split", () => owner.Splits.Add(targets.Length, ref targetArray, 1, ref tools, 0, 0));
        (double after, string? status) = Check(context, model, feature, before, 0, null, null, () => model.Bodies().Count > count);
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            bodies = InspectService.DescribeBodies(context, model),
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>
    /// Unites design bodies (default: all of them). Verified: Unions.Add(1, [target], n, [tools], 0, 0) with Body objects; the
    /// tool bodies keep an empty model (its Body fails), which the measurements skip. The target is always the body of the
    /// earliest model given: emptying the first model would leave the part without a readable first body.
    /// </summary>
    private static object UniteBodies(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        ModelDocument model = spec.Bodies is { Length: > 0 } list
            ? ModelDocument.For(context, documentHandle, list[0])
            : ModelDocument.Get(context, documentHandle);
        bool switched = model.EnsureOrdered();
        IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> bodies = RequireBodies(model, 2);
        (Model owner, object[] chosen) = spec.Bodies is { Length: > 0 } handles
            ? Owned(context, bodies, handles)
            : (bodies[0].Model, bodies.Select(b => (object)b.Body).ToArray());
        if (chosen.Length < 2)
        {
            throw ToolException.InvalidArgument("unite needs at least two different bodies in 'bodies'.",
                "Omit 'bodies' to unite all of them, or list two or more body handles from se_geometry summary.");
        }

        double before = model.Volume();
        int count = bodies.Count;
        Array target = new[] { chosen[0] };
        Array tools = chosen.Skip(1).ToArray();
        object feature = Create("union", () => owner.Unions.Add(1, ref target, tools.Length, ref tools, 0, 0));
        (double after, string? status) = Check(context, model, feature, before, 0, null, null, () => model.Bodies().Count < count);
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            bodies = InspectService.DescribeBodies(context, model),
            warnings = Warnings(switched, status == "warning"),
        };
    }

    private static IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> RequireBodies(ModelDocument model, int minimum)
    {
        IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> bodies = model.Bodies();
        if (bodies.Count == 0)
        {
            throw new ToolException("no_model", $"'{model.Document.Name}' has no solid yet.", "Create a base feature first (se_extrude...).");
        }

        if (bodies.Count < minimum)
        {
            throw ToolException.InvalidArgument($"'{model.Document.Name}' has only {bodies.Count} body.", "Split it first (se_part_feature split).");
        }

        return bodies;
    }

    /// <summary>The bodies given by handle in model order, and the model of the earliest one (the one that owns the feature).</summary>
    private static (Model Owner, object[] Bodies) Owned(SeContext context, IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> bodies, string[] handles)
    {
        int[] indexes = [.. handles.Select(h =>
        {
            SolidEdgeGeometry.Body body = context.Handles.Resolve<SolidEdgeGeometry.Body>(h, "bodies");
            int index = bodies.ToList().FindIndex(b => Late.SameObject(b.Body, body));
            return index >= 0
                ? index
                : throw ToolException.InvalidArgument($"'{h}' is not a design body of this document.", "Take the body handles from se_geometry summary ('bodies').");
        }).Distinct().Order()];
        return (bodies[indexes[0]].Model, [.. indexes.Select(i => (object)bodies[i].Body)]);
    }
}