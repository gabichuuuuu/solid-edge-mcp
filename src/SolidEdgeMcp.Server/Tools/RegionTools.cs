// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum RegionAction
{
    Fill,
    Fills,
    Style,
    Delete,
    Area,
}

/// <summary>Closed regions of a drawing sheet or a sketch: fills and area properties, as the Fill and Area commands do.</summary>
[McpServerToolType]
public sealed class RegionTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_region", Title = "Fills and area properties", Destructive = true, OpenWorld = false)]
    [Description("""
        Closed regions of a drawing sheet or a sketch, as the Fill and Area commands do; mm and degrees. sketch: a sheet, drawing document (its active sheet) or sketch handle; elements: handles of the 2D elements that bound the region (se_sketch draw actions, info).
        fill (elements + point [x,y] inside the region, which may be omitted when the elements form one closed loop; or points = the corners of a polygon [x1,y1,...]; optional style = fill style name from se_styles list type=fill, color #RRGGBB, angle, spacing) | fills (list: area, perimeter, centroid, style) | style (fill + style, color, angle, spacing) | delete (fill) |
        area (elements + point; holes = closed elements or chains inside the region, subtracted): area, perimeter, centroid, second moments about the centroid (ix, iy, ixy = ∫(x−cx)(y−cy)dA, polar), principal moments and angle, radii of gyration, elastic section moduli and range; nothing stays in the document.
        A region is bounded by closed elements (circle, ellipse, closed spline) or chains whose ends meet; other elements may cross it to split it (a rectangle with a line across: the point chooses the side). The point is checked before Solid Edge looks for the region, because a point outside every region brings Solid Edge down. Solid Edge cannot fill round an island (a closed loop inside the region): draw a line across the hole and fill each side; for the area pass the hole as 'holes'.
        Example: {"action":"area","sketch":"sheet:2","elements":["line2d:3","line2d:4","line2d:5","line2d:6"],"point":[55,55],"holes":["circle2d:7"]}.
        """)]
    public Task<CallToolResult> Region(
        RegionAction action,
        [Description("Drawing sheet, drawing document or sketch handle (fill, fills, area).")] string? sketch = null,
        [Description("fill, area: handles of the elements that bound the region.")] string[]? elements = null,
        [Description("fill, area: a point [x, y] in mm inside the region.")] double[]? point = null,
        [Description("fill: corners of a polygon [x1, y1, x2, y2, ...] in mm, instead of elements.")] double[]? points = null,
        [Description("area: handles of closed elements or chains inside the region to subtract.")] string[]? holes = null,
        [Description("style, delete: fill handle.")] string? fill = null,
        [Description("fill, style: fill style name of the document (se_styles list type=fill), e.g. 'ANSI31(Iron)'.")] string? style = null,
        [Description("fill, style: colour #RRGGBB.")] string? color = null,
        [Description("fill, style: hatch angle in degrees.")] double? angle = null,
        [Description("fill, style: hatch spacing in mm.")] double? spacing = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_region";
        string act = Snake(action);
        var look = new SketchService.FillLook(style, color, angle, spacing);
        return action switch
        {
            RegionAction.Fill => runner.RunAsync(tool, act, c => SketchService.Fill(c, ToolRunner.Require(sketch, "sketch", act), elements, point, points, look), cancellationToken),
            RegionAction.Fills => runner.RunAsync(tool, act, c => SketchService.Fills(c, ToolRunner.Require(sketch, "sketch", act)), cancellationToken),
            RegionAction.Style => runner.RunAsync(tool, act, c => SketchService.Restyle(c, ToolRunner.Require(fill, "fill", act), look), cancellationToken),
            RegionAction.Delete => runner.RunAsync(tool, act, c => SketchService.DeleteFill(c, ToolRunner.Require(fill, "fill", act)), cancellationToken),
            RegionAction.Area => runner.RunAsync(tool, act, c => SketchService.Area(c, ToolRunner.Require(sketch, "sketch", act), elements, point, holes), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
