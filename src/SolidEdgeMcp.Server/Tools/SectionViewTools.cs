// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>3D section views of parts, sheet metal and assemblies, as the Section command of the View tab makes them.</summary>
[McpServerToolType]
public sealed class SectionViewTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_section_view", Title = "3D section views", Destructive = true, OpenWorld = false)]
    [Description("""
        3D section views of a part, sheet metal or assembly (View > Section): the model is shown cut by a sketch profile pushed along the normal of its plane; the model itself does not change. mm.
        list | add (sketch = a sketch of the model, drawn and closed with se_sketch (open=true for a single line), or plane = a plane that cuts it (direction front|back = the side cut away; with depth only that far; both + depth = a slab around the plane); frontNormal is reported; keep = the part that stays: inside|outside of a closed profile, or left|right of an open one, the side of its drawing direction as the sketch frame shows it (x right, y up); depth = how far the cut goes along the sketch normal, default without limit; direction = both (with depth: symmetric, depth is the total width)|front (along the frame's normal)|back; caption; cutHardware = assemblies: cut the fasteners too; display, opacity, showPlane as in edit) |
        show | hide | edit (caption, display = what is drawn, opacity of the cutting plane 0-1, showPlane, cutHardware) | delete.
        A new section view repeats the display and showPlane last set in its document unless add gives them.
        section = handle, name or caption from list or add.
        Delete also deletes the profile sketch and any local plane that only it used (Solid Edge does it, and it cannot be undone): the result lists removed planes; planes made with se_ref_plane stay. Hide the section instead to keep the sketch.
        See the cut with se_view capture. Example: {"action":"add","sketch":"sketch:7","keep":"inside","caption":"Half"}.
        """)]
    public Task<CallToolResult> SectionView(
        SectionViewAction action,
        [Description("add: handle of the profile sketch (se_sketch create on any plane of the model, draw, close).")] string? sketch = null,
        [Description("add: instead of a sketch, the plane that cuts the model: '1'-'3', a reference plane or planar face handle.")] string? plane = null,
        [Description("add: the part that stays (default inside; not used with plane).")] SectionKeep keep = SectionKeep.Inside,
        [Description("add: depth of the cut in mm along the sketch normal (default: without limit).")] double? depth = null,
        [Description("add: both sides of the sketch plane (default), front = along the normal of the sketch frame, back = against it.")] SectionDirection direction = SectionDirection.Both,
        [Description("add, edit: caption of the section view.")] string? caption = null,
        [Description("show, hide, edit, delete: section view handle, name (e.g. 'Section 1') or caption.")] string? section = null,
        [Description("add, edit: what is drawn: only_cut_faces, cut_faces_and_cut_bodies (default of a new document), cut_faces_with_original_bodies, only_original_bodies.")] SectionDisplay? display = null,
        [Description("add, edit: opacity of the cutting plane, 0 (transparent) to 1.")] double? opacity = null,
        [Description("add, edit: show the cutting plane.")] bool? showPlane = null,
        [Description("add, edit: in assemblies, also cut fasteners (hardware parts).")] bool? cutHardware = null,
        [Description("Part, sheet metal or assembly document handle; default: the document of the sketch or section, or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_section_view";
        string act = Snake(action);
        return action switch
        {
            SectionViewAction.List => runner.RunAsync(tool, act, c => SectionViewService.List(c, document), cancellationToken),
            SectionViewAction.Add => runner.RunAsync(tool, act, c => SectionViewService.Add(c, document, sketch, keep, depth, direction,
                caption, cutHardware ?? false, display, opacity, showPlane, plane), cancellationToken),
            SectionViewAction.Show => runner.RunAsync(tool, act, c => SectionViewService.Show(c, document, section, true), cancellationToken),
            SectionViewAction.Hide => runner.RunAsync(tool, act, c => SectionViewService.Show(c, document, section, false), cancellationToken),
            SectionViewAction.Edit => runner.RunAsync(tool, act, c => SectionViewService.Edit(c, document, section, caption,
                display, opacity, showPlane, cutHardware), cancellationToken),
            SectionViewAction.Delete => runner.RunAsync(tool, act, c => SectionViewService.Delete(c, document, section), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
