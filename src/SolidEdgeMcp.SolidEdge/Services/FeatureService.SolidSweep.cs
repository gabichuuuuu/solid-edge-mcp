// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// Solid sweep: a closed construction body (the tool) moved along a path, added to or cut from the design body. Verified in
    /// Solid Edge 2026: Model.SolidSweptCutouts.Add(target body, tool body, path sketch profile, null, null, false, false)
    /// removes the swept volume and SolidSweptProtrusions.Add(..., false, true) adds it; the tool is the Body of the
    /// construction model, the path the sketch profile itself. A Line2d, the construction model or a rotation axis give
    /// E_FAIL or E_NOINTERFACE, and bToolOnPath true gives E_FAIL. The tool stays as a construction body.
    /// </summary>
    public static object SolidSweep(SeContext context, string? documentHandle, string? toolHandle, string? pathHandle, string operation)
    {
        string tool = toolHandle ?? throw ToolException.InvalidArgument("solid needs 'tool': a closed construction surface (se_surface with endCaps, or stitched closed) or a construction body.",
            "Make the tool with se_surface, e.g. revolve a section 360 degrees with endCaps.");
        string path = pathHandle ?? throw ToolException.InvalidArgument("solid needs 'path': a sketch with an open chain of lines and arcs (se_sketch, close with open: true).");
        ModelDocument model = ModelDocument.For(context, documentHandle, tool);
        bool cut = IsCut(operation);
        (_, Body toolBody) = ConstructionOf(model, context.Handles.Resolve(tool, "tool"), tool, "tool");
        if (!toolBody.IsSolid)
        {
            throw ToolException.InvalidArgument($"'{tool}' is an open surface: the tool of a solid sweep is a closed body.",
                "Close it (se_surface revolve or extrude with endCaps, or stitch it closed), or sweep a section with action sweep.");
        }

        if (context.Handles.Resolve(path, "path") is not (Profile or Sketch))
        {
            throw new ToolException("handle_wrong_type", $"'{path}' in 'path' is not a sketch.",
                "Draw the path with se_sketch and close it with 'close' (open: true for an open chain).");
        }

        Profile pathProfile = SketchService.ResolveDefined(context, path);
        bool switched = model.EnsureOrdered();
        Model existing = model.RequireModel();
        object target = model.RequireBody()!;
        double before = model.Volume();
        string what = $"solid sweep {(cut ? "cutout" : "protrusion")}";
        object feature = Create(what, () => cut
            ? existing.SolidSweptCutouts.Add(target, toolBody, pathProfile, null, null, false, false)
            : existing.SolidSweptProtrusions.Add(target, toolBody, pathProfile, null, null, false, true));
        (double after, string? status) = Check(context, model, feature, before, cut ? -1 : 1, null, null,
            hint: "Check that the tool body meets the part along the path and that the path starts at the tool (se_geometry).");
        Hide(pathProfile);
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            warnings = Warnings(switched, status == "warning"),
        };
    }
}
