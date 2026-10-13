// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using WeldType = SolidEdgeFrameworkSupport.DimWeldTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Welds modelled in a part document (weld beads as design bodies).</summary>
public static partial class FeatureService
{
    /// <summary>
    /// Fillet weld between base faces and target faces of the part, with legs in mm. Verified (ordered, part document):
    /// Model.PartFilletWelds.Add(n, base faces, n, target faces, PartDocument.LabelWeldDataCollection.Add(), equal setback,
    /// leg, leg) between the top of a plate and the side of a rib standing on it, legs 5 mm, 100 mm long: a new design body
    /// of 1250 mm³ (5 x 5 / 2 x 100).
    /// </summary>
    private static object FilletWeld(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (spec.Faces is not { Length: > 0 } || spec.TargetFaces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("fillet_weld requires 'faces' (the base faces) and 'targetFaces' (the faces they meet along an edge).",
                "Get them with se_geometry action 'faces', e.g. the top face of a plate and the side face of a rib standing on it.");
        }

        double baseLeg = Units.RequirePositive(spec.Leg ?? throw ToolException.InvalidArgument("fillet_weld requires 'leg': the weld leg in mm on the base faces."), "leg");
        double targetLeg = Units.RequirePositive(spec.Leg2 ?? baseLeg, "leg2");
        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Faces[0]);
        if (model.Document is not PartDocument part)
        {
            throw ToolException.InvalidArgument("fillet_weld works in part documents only.", "Weld sheet metal parts in a weldment assembly: se_assembly_feature fillet_weld.");
        }

        object[] bases = spec.Faces.Select(h => (object)context.Handles.Resolve<Face>(h, "faces")).ToArray();
        object[] targets = spec.TargetFaces.Select(h => (object)context.Handles.Resolve<Face>(h, "targetFaces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        LabelWeldData data = part.LabelWeldDataCollection.Add();
        object feature = Create("fillet weld", () =>
        {
            Array baseArray = bases;
            Array targetArray = targets;
            return target.PartFilletWelds.Add(bases.Length, ref baseArray, targets.Length, ref targetArray, data,
                Math.Abs(baseLeg - targetLeg) < 1e-9 ? FilletWeldSetbackConstants.seFilletWeldEqualSetback : FilletWeldSetbackConstants.seFilletWeldUnequalSetback,
                Units.MmToM(baseLeg), Units.MmToM(targetLeg));
        });
        return Verify(context, model, feature, before, 1, switched,
            hint: "The base and target faces must meet along an edge with an angle between them (e.g. the top face of a plate and the side face of a rib standing on it), and the legs must fit on them.");
    }

    /// <summary>
    /// Label weld: marks edges of the part as welded, without a bead, optionally with a weld symbol (the type on the arrow
    /// side, the other side or both, a size note, all around, field weld). Verified (ordered, part document, typed calls):
    /// Model.PartLabelWelds.Add(n, edges, PartDocument.LabelWeldDataCollection.Add()) on one or two edges of a box, with the
    /// symbol set on the data; an edge that another label weld already has leaves the weld failed, a face instead of an
    /// edge makes Solid Edge throw a server fault (the handles are checked to be edges first), and the optional
    /// cross-section area argument gives E_NOINTERFACE. Through IDispatch the call cannot convert the edge array.
    /// </summary>
    private static object LabelWeld(SeContext context, string? documentHandle, PartFeatureSpec spec)
    {
        if (spec.Edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("label_weld requires 'edges': the welded edges of the part.", "Get them with se_geometry action 'edges'.");
        }

        bool onArrow = spec.WeldSide is "arrow" or "both";
        bool onOther = spec.WeldSide is "other" or "both";
        if (!onArrow && !onOther)
        {
            throw ToolException.InvalidArgument($"Unknown weldSide '{spec.WeldSide}'.", $"Use one of: {string.Join(", ", DrawingService.WeldSides)}.");
        }

        WeldType? arrow = spec.Weld is null ? null
            : DrawingService.WeldTypes.TryGetValue(spec.Weld, out WeldType type) ? type
            : throw ToolException.InvalidArgument($"Unknown weld '{spec.Weld}'.", $"Use one of: {string.Join(", ", DrawingService.WeldTypes.Keys)}.");
        if (arrow is null && spec.Note is not null)
        {
            throw ToolException.InvalidArgument("'note' is the size of a weld symbol: give 'weld' too.", "For example weld 'fillet' and note 'a5'.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Edges[0]);
        if (model.Document is not PartDocument part)
        {
            throw ToolException.InvalidArgument("label_weld works in part documents only.", "Label welds of a weldment: se_assembly_feature label_weld.");
        }

        object[] edges = spec.Edges.Select(h => (object)context.Handles.Resolve<Edge>(h, "edges")).ToArray();
        bool switched = model.EnsureOrdered();
        PartLabelWelds labels = model.RequireModel().PartLabelWelds;
        int count = labels.Count;
        double before = model.Volume();
        LabelWeldData data = part.LabelWeldDataCollection.Add();
        if (arrow is { } symbol)
        {
            if (onArrow)
            {
                data.BottomType = symbol;
                data.BottomNote1 = spec.Note ?? "";
            }

            if (onOther)
            {
                data.TopType = symbol - DrawingService.WeldOtherSide;
                data.TopNote1 = spec.Note ?? "";
            }
        }

        data.WeldAllAround = spec.AllAround;
        data.WeldInField = spec.Field;
        var label = (PartLabelWeld)Create("label weld", () =>
        {
            Array edgeArray = edges;
            return labels.Add(edges.Length, ref edgeArray, data);
        });
        const string Hint = "Pass edges of the part read with se_geometry 'edges' (read them again after a change) that no other label weld has.";
        (_, string? status) = Check(context, model, label, before, 0, null, null, () => labels.Count > count, hint: Hint);
        int labelled = ((SolidEdgeGeometry.Edges)label.Edges[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]).Count;
        if (labelled != edges.Length)
        {
            TryDelete(label);
            throw new ToolException("feature_failed", $"The label weld took {labelled} of the {edges.Length} edges and was deleted.", Hint);
        }

        label.GetLabelWeldData(out object kept);
        var read = (LabelWeldData)kept;
        WeldType shown = read.BottomType != WeldType.igDimWeldTypeNone ? read.BottomType : read.TopType + DrawingService.WeldOtherSide;
        bool keptArrow = read.BottomType != WeldType.igDimWeldTypeNone;
        bool keptOther = read.TopType != WeldType.igDimWeldTypeNone;
        return new
        {
            feature = Describe(context, model, label),
            edges = labelled,
            weld = keptArrow || keptOther ? DrawingService.WeldTypes.FirstOrDefault(w => w.Value == shown).Key : null,
            weldSide = keptArrow && keptOther ? "both" : keptOther ? "other" : keptArrow ? "arrow" : null,
            note = NoteText(keptArrow ? read.BottomNote1 : keptOther ? read.TopNote1 : null),
            allAround = read.WeldAllAround,
            field = read.WeldInField,
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>A weld note as Solid Edge returns it, which ends with a padding character; null when empty.</summary>
    private static string? NoteText(string? note) =>
        note is null ? null : new string(note.Where(c => !char.IsControl(c)).ToArray()).Trim() is { Length: > 0 } text ? text : null;
}
