// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Welds modelled in a part document (weld beads as design bodies).</summary>
public static partial class FeatureService
{
    /// <summary>
    /// Fillet weld between base faces and target faces of the part, with legs in mm. Verified (ordered, part document):
    /// Model.PartFilletWelds.Add(n, base faces, n, target faces, PartDocument.LabelWeldDataCollection.Add(), equal setback,
    /// leg, leg) between the top of a plate and the side of a rib standing on it, legs 5 mm, 100 mm long: a new design body
    /// of 1250 mm³ (5 x 5 / 2 x 100). Model.PartLabelWelds fails (E_NOINTERFACE) in a part document.
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
}
