// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>Physical threads cut into cylindrical faces.</summary>
[McpServerToolType]
public sealed class ThreadTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_thread", Title = "Physical thread", Destructive = false, OpenWorld = false)]
    [Description("""
        Cuts a physical ISO metric thread (60° profile) into a cylindrical face of a part, as a helical cutout: outside on a shaft (depth 0.6134 x pitch,
        the shaft at the nominal diameter) or inside on a hole (the hole at the minor diameter, cut out to the nominal one). mm.
        face = a full cylindrical face (se_geometry faces type cylinder); thread = 'M10' (coarse pitch) or 'M10x1.25', or pitch alone (on the face's own diameter);
        length from the start end (default the whole face); the thread starts at the end with the lower position along the axis direction se_geometry reports,
        reverse = from the other end; hand right (default) | left. For a cosmetic thread use se_hole type tapped.
        The result gives the volume removed and the one expected from the profile (section area x 2π x centroid radius x turns).
        Example: {"face":"face:12","thread":"M10","length":20}.
        """)]
    public Task<CallToolResult> Thread(
        [Description("Cylindrical face handle.")] string face,
        [Description("Metric designation, e.g. 'M10' or 'M10x1.25'.")] string? thread = null,
        [Description("Pitch in mm (instead of a designation).")] double? pitch = null,
        [Description("Threaded length in mm from the start end (default: the whole face).")] double? length = null,
        [Description("Start at the other end of the face.")] bool reverse = false,
        [Description("right (default) or left.")] HelixHand hand = HelixHand.Right,
        [Description("Document handle; default: the face's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_thread", "add", c => FeatureService.Thread(c, document,
            new ThreadSpec(face, thread, pitch, length, reverse, FeatureTools.Snake(hand))), cancellationToken, timeout: TimeSpan.FromMinutes(3));
}
