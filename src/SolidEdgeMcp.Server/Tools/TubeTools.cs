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

/// <summary>Tubes routed in assemblies (XpresRoute).</summary>
[McpServerToolType]
public sealed class TubeTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_tube", Title = "Assembly tubes", Destructive = false, OpenWorld = false)]
    [Description("""
        Tubes of an assembly (XpresRoute): a tube part routed along a path of straight segments, bent at every corner. mm.
        list (each tube: outer diameter, wall, bend radius, flat length, material, volume, path points) |
        add (points = the path [x1, y1, z1, x2, y2, z2, ...] in assembly coordinates, two points or more; outerDiameter; wall = wall thickness, 0 or omitted = a solid rod; bendRadius, default 2 x outer diameter; material = a name of the material library; path = the .par file of the tube, default '<assembly>_tube<n>.par' next to the assembly; overwrite).
        The assembly must have been saved (se_documents save_as) before a tube is added. The result is read back from the new tube.
        Example: {"action":"add","points":[0,0,0,100,0,0,100,80,0],"outerDiameter":12,"wall":1,"bendRadius":20}.
        """)]
    public Task<CallToolResult> Tube(
        TubeAction action,
        [Description("add: the path of the tube [x1, y1, z1, x2, y2, z2, ...] in mm, assembly coordinates.")] double[]? points = null,
        [Description("add: outer diameter in mm.")] double? outerDiameter = null,
        [Description("add: wall thickness in mm; 0 or omitted = a solid rod.")] double? wall = null,
        [Description("add: bend radius at the corners in mm (default 2 x outer diameter; larger than half the outer diameter).")] double? bendRadius = null,
        [Description("add: material name from the material library (se_material list).")] string? material = null,
        [Description("add: absolute path of the tube's .par file, in an allowed folder (default next to the assembly).")] string? path = null,
        [Description("add: replace the tube file if it exists.")] bool overwrite = false,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_tube";
        string act = Snake(action);
        return action switch
        {
            TubeAction.List => runner.RunAsync(tool, act, c => TubeService.List(c, document), cancellationToken),
            TubeAction.Add => runner.RunAsync(tool, act, c => TubeService.Add(c, document, points, outerDiameter, wall, bendRadius, material, path, overwrite),
                cancellationToken, timeout: TimeSpan.FromMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
