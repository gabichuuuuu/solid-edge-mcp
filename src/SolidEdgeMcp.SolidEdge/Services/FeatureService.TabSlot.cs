// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Tabs on edges of one sheet body and slots for them in faces of another (mm). <c>Spacing</c> given = fill (as many tabs as
/// fit with that gap between them), otherwise fit (<c>Count</c> tabs spread between the offsets).
/// </summary>
public sealed record TabSlotSpec(string[]? Edges, string[]? Faces, double? Length, int? Count = null, double? Spacing = null,
    double Offset = 0, double Gap = 0, double? Depth = null);

public static partial class FeatureService
{
    private const string TabSlotHint =
        "Use straight edges of one sheet body that lie on the slot faces of another sheet body (make it with se_sheetmetal 'tab' newBody=true), and a shorter length, fewer tabs or smaller offsets.";

    /// <summary>
    /// Tab and slot. Verified in Solid Edge 2026: it builds only in a sheet metal document between two sheet bodies (in a part,
    /// between bodies of a split, the feature is created but fails); the owner is the model of the tab body (the slot body's gives
    /// E_FAIL); it is an ordered feature (in synchronous mode the bodies do not change). Fit spreads 'count' tabs between the
    /// offsets and ignores the spacing; fill ignores the count. Each tab adds length × thickness × depth to its body; each slot
    /// removes (length + 2·gap) × (thickness + 2·gap) through the other.
    /// </summary>
    public static object TabSlot(SeContext context, string? documentHandle, TabSlotSpec spec)
    {
        (ModelDocument model, object[] edges) = ResolveEdges(context, documentHandle, spec.Edges);
        SheetMetalDocument sheet = RequireSheet(model);
        if (spec.Faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("tab_slot requires 'faces': the faces of the other sheet body that get the slots.",
                "Get them with se_geometry 'faces'.");
        }

        object[] faces = spec.Faces.Select(f => (object)context.Handles.Resolve<Face>(f, "faces")).ToArray();
        double length = Required(spec.Length, "length", "tab_slot");
        bool fill = spec.Spacing is not null;
        double spacing = fill ? Required(spec.Spacing, "spacing", "tab_slot") : 0;
        int count = spec.Count ?? 2;
        if (!fill && count < 1)
        {
            throw ToolException.InvalidArgument("'count' must be 1 or more.");
        }

        double offset = Units.MmToM(NonNegative(spec.Offset, "distance"));
        double gap = Units.MmToM(NonNegative(spec.Gap, "gap"));
        double thickness = GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalMaterialThickness);
        double depth = spec.Depth is null ? thickness : Required(spec.Depth, "depth", "tab_slot");

        IReadOnlyList<(Model Model, Body Body)> bodies = model.Bodies();
        int tabBody = BodyOf(bodies, edges.Select(e => ((Edge)e).Body), "edges");
        int slotBody = BodyOf(bodies, faces.Select(f => ((Face)f).Body), "faces");
        if (tabBody == slotBody)
        {
            throw ToolException.InvalidArgument("The edges and the faces are on the same body; the slots go in another sheet body.", TabSlotHint);
        }

        bool switched = model.EnsureOrdered();
        Model owner = bodies[tabBody].Model;
        Model slotted = bodies[slotBody].Model;
        double tabBefore = ((Body)owner.Body).Volume;
        double slotBefore = ((Body)slotted.Body).Volume;
        double before = model.Volume();
        Array tabEdges = edges;
        Array slotFaces = faces;
        object startVertex = ((Edge)edges[0]).StartVertex;
        object feature = Create("tab and slot", () => owner.TabAndSlotFeatures.Add(edges.Length, ref tabEdges, startVertex, faces.Length,
            ref slotFaces, count, length, spacing, offset, offset,
            fill ? TabAndSlotPatternOffsetTypeConstants.TabAndSlotPatternOffsetTypeFill : TabAndSlotPatternOffsetTypeConstants.TabAndSlotPatternOffsetTypeFit,
            TabAndSlotGapTypeConstants.SlotGapTypeSingle, gap, gap, gap, TabAndSlotExtentTypeConstants.TabAndSlotExtentTypeFinite, depth,
            KeyPointExtentConstants.igTangentNormal, null, null, OffsetSideConstants.seOffsetNone, 0,
            TabAndSlotTreatmentTypeConstants.TabAndSlotTreatmentTypeNone, 0, TabAndSlotTreatmentTypeConstants.TabAndSlotTreatmentTypeNone, 0));

        context.ModelChanged(model.Document, "the model changed (new feature)");
        string? status = StatusOf(feature);
        double tabChange = ((Body)owner.Body).Volume - tabBefore;
        double slotChange = ((Body)slotted.Body).Volume - slotBefore;
        string? problem = status == "failed" ? "Solid Edge reports it as failed"
            : tabChange <= VolumeTolerance ? "it added no tabs"
            : slotChange >= -VolumeTolerance ? "it cut no slots"
            : null;
        if (problem is not null)
        {
            string name = Late.TryGetString(feature, "Name") ?? "The tab and slot";
            TryDelete(feature);
            throw new ToolException("feature_failed", $"{name} was deleted because {problem}.", TabSlotHint);
        }

        double perTab = length * thickness * depth;
        double tabs = tabChange / perTab;
        double after = model.Volume();
        return new
        {
            feature = Describe(context, model, feature),
            pattern = fill ? "fill" : "fit",
            tabs = Math.Abs(tabs - Math.Round(tabs)) < 0.01 ? (int?)Math.Round(tabs) : null,
            tabBody = tabBody + 1,
            tabVolumeChange = Geo.Mm3(tabChange),
            slotBody = slotBody + 1,
            slotVolumeChange = Geo.Mm3(slotChange),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>Index of the design body that holds all the given topology, or invalid_argument.</summary>
    private static int BodyOf(IReadOnlyList<(Model Model, Body Body)> bodies, IEnumerable<object> owners, string what)
    {
        int found = -1;
        foreach (object owner in owners)
        {
            int index = -1;
            for (int i = 0; i < bodies.Count && index < 0; i++)
            {
                if (Late.SameObject(bodies[i].Body, owner))
                {
                    index = i;
                }
            }

            if (index < 0 || (found >= 0 && index != found))
            {
                throw ToolException.InvalidArgument(index < 0
                    ? $"One of the {what} is not on a design body of the document."
                    : $"The {what} are on different bodies; give the {what} of one body.", TabSlotHint);
            }

            found = index;
        }

        return found;
    }
}
