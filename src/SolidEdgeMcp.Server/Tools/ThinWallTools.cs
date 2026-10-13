// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum ThinWallAction
{
    MountingBoss,
}

public enum DecalAction
{
    List,
    Add,
    Delete,
}

/// <summary>Thin-wall features of plastic parts and decals.</summary>
[McpServerToolType]
public sealed class ThinWallTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_thin_wall", Title = "Thin-wall features (mounting bosses)", Destructive = false, OpenWorld = false)]
    [Description("""
        Thin-wall features of plastic parts (ordered modelling). mm.
        mounting_boss (plane = where the bosses start, e.g. a reference plane at the open face of a shelled part; centers = [x1, y1, x2, y2, ...] on that plane; diameter; holeDiameter, holeDepth = a mounting hole in each boss): the bosses grow from the plane to the next face; the volume change is read back.
        Shell: se_shell; lip, rib, web network: se_part_feature. Vents do not work through the API (E_FAIL).
        Example: {"action":"mounting_boss","plane":"refplane:11","centers":[30,30,70,30],"diameter":8,"holeDiameter":3}.
        """)]
    public Task<CallToolResult> ThinWall(
        ThinWallAction action,
        [Description("mounting_boss: reference plane ('1'-'3', 'xy'/'yz'/'xz', refplane handle) or planar face where the bosses start.")] string? plane = null,
        [Description("mounting_boss: boss centres [x1, y1, x2, y2, ...] in mm, in the coordinates of the plane's sketch.")] double[]? centers = null,
        [Description("mounting_boss: boss diameter in mm.")] double? diameter = null,
        [Description("mounting_boss: diameter of a mounting hole in each boss, in mm.")] double? holeDiameter = null,
        [Description("mounting_boss: depth of the mounting hole in mm (default Solid Edge's).")] double? holeDepth = null,
        [Description("Part document handle; default: the document of the plane or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        action switch
        {
            ThinWallAction.MountingBoss => runner.RunAsync("se_thin_wall", Snake(action), c => ThinWallService.MountingBoss(c, document, plane, centers, diameter, holeDiameter, holeDepth), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    [McpServerTool(Name = "se_decal", Title = "Decals", Destructive = false, OpenWorld = false)]
    [Description("""
        Decals of a part: a picture wrapped on a face. mm.
        list (name, mapping, status, faces, offset) |
        add (image = a png/jpg/bmp/tif file in an allowed folder, e.g. from se_view capture 'path'; face = the face it is wrapped on; plane = the plane of the picture's sketch, default the face; offset = [x, y]; name) |
        delete (decal = name or handle).
        Projection onto several faces is not offered: it left Solid Edge unstable.
        Example: {"action":"add","image":"C:\\Work\\logo.png","face":"face:12","name":"Logo"}.
        """)]
    public Task<CallToolResult> Decal(
        DecalAction action,
        [Description("add: picture file (absolute path in an allowed folder).")] string? image = null,
        [Description("add: face handle the picture is wrapped on.")] string? face = null,
        [Description("add: plane of the picture's sketch ('1'-'3', refplane or planar face handle); default the face.")] string? plane = null,
        [Description("add: offset of the picture [x, y] in mm.")] double[]? offset = null,
        [Description("add: name of the decal.")] string? name = null,
        [Description("delete: decal name or handle.")] string? decal = null,
        [Description("Part document handle; default: the document of the face or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_decal";
        string act = Snake(action);
        return action switch
        {
            DecalAction.List => runner.RunAsync(tool, act, c => DecalService.List(c, document), cancellationToken),
            DecalAction.Add => runner.RunAsync(tool, act, c => DecalService.Add(c, document, image, face, plane, offset, name), cancellationToken),
            DecalAction.Delete => runner.RunAsync(tool, act, c => DecalService.Delete(c, document, decal), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
