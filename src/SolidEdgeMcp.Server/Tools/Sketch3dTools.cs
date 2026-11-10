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

/// <summary>3D sketches of a part: elements drawn in model space, e.g. as paths for sweeps or frames.</summary>
[McpServerToolType]
public sealed class Sketch3dTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_sketch3d", Title = "3D sketches", Destructive = false, OpenWorld = false)]
    [Description("""
        3D sketches of a part or sheet metal part: elements drawn in model space (mm), not on a plane. Each element is read back (ends, center, length, radius) and deleted if it differs.
        list | create (name) | info (elements with handles, relations) |
        line (points [x1,y1,z1,...]: a polyline through them; closed: back to the first) | arc (points = [start, end, center]; normal = the axis it turns about, default from start and end) |
        arc_through (points = [start, a point on the arc, end]) | tangent_arc (elements = [a line], keypoints = [end] (default) or [start]: leaves it along its direction to points = [end]) |
        rectangle (points = [center], normal of its plane, width along direction (default x, or y), height) |
        circle (points = [center], normal, radius) | spline (points it passes through; closed) | point (one per [x,y,z]) |
        equation (equations = [x(t), y(t), z(t)] in mm, range = [t0, t1]; the ends are read back: Solid Edge may stop one sample short) | include (edges = model edges copied in) |
        fillet (elements = 2 that meet, radius: they are trimmed to it) | mirror (elements copied about plane) | split (elements = [one], points = [where]) | trim (elements = [one], points = [on the part to remove, beyond a crossing element]) |
        relation (relation = connect (keypoints [first, second]: start | end | middle | center; default end to start), parallel, perpendicular, collinear, equal, tangent, concentric, coaxial, horizontal_vertical, lock, on_plane (plane), symmetric (2 elements about plane), rigid_set) |
        delete (elements; none = the whole sketch).
        Example: {"action":"line","sketch":"sketch3d:4","points":[0,0,0,100,0,0,100,50,30]}; {"action":"fillet","sketch":"sketch3d:4","elements":["line3d:5","line3d:6"],"radius":10}.
        """)]
    public Task<CallToolResult> Sketch3d(
        Sketch3dAction action,
        [Description("3D sketch handle (create, list).")] string? sketch = null,
        [Description("Model points [x1,y1,z1,...] in mm.")] double[]? points = null,
        [Description("arc, circle: axis [x,y,z]; rectangle: normal of its plane.")] double[]? normal = null,
        [Description("circle, fillet: radius in mm.")] double? radius = null,
        [Description("line, spline: return to the first point.")] bool closed = false,
        [Description("fillet, relation, mirror, split, trim, tangent_arc, delete: element handles (info).")] string[]? elements = null,
        [Description("relation: the relation to apply.")] Sketch3dRelation? relation = null,
        [Description("relation connect: keypoint of each element: start, end, middle or center; tangent_arc: [start] or [end] of the line.")] string[]? keypoints = null,
        [Description("relation on_plane, symmetric, mirror: '1'-'3', 'xy', 'yz', 'xz' or a reference plane handle.")] string? plane = null,
        [Description("create: name of the sketch.")] string? name = null,
        [Description("rectangle: width in mm, along direction.")] double? width = null,
        [Description("rectangle: height in mm, across direction.")] double? height = null,
        [Description("rectangle: [x,y,z] the width runs along (projected onto its plane).")] double[]? direction = null,
        [Description("equation: [x(t), y(t), z(t)] expressions in mm, e.g. [\"100*t\",\"20*sin(t*pi)\",\"0\"].")] string[]? equations = null,
        [Description("equation: [t start, t end].")] double[]? range = null,
        [Description("include: model edge handles (se_geometry edges).")] string[]? edges = null,
        [Description("Document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_sketch3d", Snake(action), c => Sketch3dService.Run(c, document, action,
            new Sketch3dSpec(sketch, points, normal, radius, closed, elements, relation, keypoints, plane, name, width, height, direction, equations, range, edges)),
            cancellationToken);
}
