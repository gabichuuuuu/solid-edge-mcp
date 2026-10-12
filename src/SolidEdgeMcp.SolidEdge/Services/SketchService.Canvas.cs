// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeDraft;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class SketchService
{
    /// <summary>
    /// Where 2D geometry is drawn: the profile of a sketch or a drawing sheet (2D drafting, in mm from the lower-left corner of
    /// the sheet). Both expose the same SolidEdgeFrameworkSupport collections; only a sketch has a 3D frame, is validated and
    /// becomes the profile of a feature.
    /// </summary>
    internal sealed class Canvas2d
    {
        private Canvas2d(Profile? profile, Sheet? sheet)
        {
            Profile = profile;
            Sheet = sheet;
        }

        public Profile? Profile { get; }

        public Sheet? Sheet { get; }

        public Lines2d Lines2d => Profile?.Lines2d ?? Sheet!.Lines2d;

        public Circles2d Circles2d => Profile?.Circles2d ?? Sheet!.Circles2d;

        public Arcs2d Arcs2d => Profile?.Arcs2d ?? Sheet!.Arcs2d;

        public Ellipses2d Ellipses2d => Profile?.Ellipses2d ?? Sheet!.Ellipses2d;

        public EllipticalArcs2d EllipticalArcs2d => Profile?.EllipticalArcs2d ?? Sheet!.EllipticalArcs2d;

        public BSplineCurves2d BSplineCurves2d => Profile?.BSplineCurves2d ?? Sheet!.BsplineCurves2d;

        public Points2d Points2d => Profile?.Points2d ?? Sheet!.Points2d;

        public Relations2d Relations2d => Profile is { } p ? (Relations2d)p.Relations2d : Sheet!.Relations2d;

        public Dimensions Dimensions => (Dimensions)(Profile is { } p ? p.Dimensions : Sheet!.Dimensions);

        public Boundaries2d Boundaries2d => Profile?.Boundaries2d ?? Sheet!.Boundaries2d;

        public AreaPropertiesCollection AreaPropertiesCollection => Profile?.AreaPropertiesCollection ?? Sheet!.AreaPropertiesCollection;

        public string Name => Profile is { } p ? NameOf(p) : Sheet!.Name;

        public static Canvas2d Of(Profile profile) => new(profile, null);

        public static Canvas2d Of(Sheet sheet) => new(null, sheet);
    }

    /// <summary>Sketch profile or drawing sheet given by its handle (a drawing document handle means its active sheet).</summary>
    internal static Canvas2d ResolveCanvas(SeContext context, string sketch) => context.Handles.Resolve(sketch, "sketch") switch
    {
        Sheet sheet => Canvas2d.Of(sheet),
        DraftDocument draft => Canvas2d.Of(draft.ActiveSheet),
        Profile or Sketch => Canvas2d.Of(Resolve(context, sketch)),
        _ => throw new ToolException("handle_wrong_type", $"Handle '{sketch}' in 'sketch' does not refer to a sketch or a drawing sheet.",
            "Pass a sketch (se_sketch 'create' or se_feature_tree) or a drawing sheet (se_drawing 'sheets')."),
    };
}
