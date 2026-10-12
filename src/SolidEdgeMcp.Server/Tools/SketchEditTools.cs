// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum SketchEditAction
{
    Move,
    Rotate,
    Scale,
    Mirror,
    Trim,
    Extend,
    Split,
    Corner,
    Offset,
    Construction,
    PatternRectangular,
    PatternCircular,
    Delete,
}

/// <summary>Editing of sketch and drawing sheet geometry, as the sketch editing commands do.</summary>
[McpServerToolType]
public sealed class SketchEditTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_sketch_edit", Title = "Sketch editing", Destructive = true, OpenWorld = false)]
    [Description("""
        Edits the 2D elements of a sketch (before 'close') or of a drawing sheet, as the sketch editing commands do; mm and degrees (counter-clockwise positive). elements: handles from se_sketch (draw actions, info).
        move (delta [dx,dy]) | rotate (center, angle) | scale (center, factor) | mirror (line [x1,y1,x2,y2] = two points of the mirror line): copy=true edits copies (default for mirror) and returns their handles, connected like the originals (and horizontal / vertical where they still are); elements tied by relations move as a set, keeping the relations among them (a quarter turn makes horizontal lines vertical), so include every element they are tied to (a rectangle: its four lines) |
        trim (element, at = a point on the part to remove, bounds = 1-2 elements that cut it there) | extend (element, at = a point near the end to extend, bounds = [the element to reach]) | split (element, at = the point) | corner (elements = 2, keep [x1,y1,x2,y2] = a point on the part of each one to keep: both are trimmed or extended to meet) |
        offset (at = a point on a chain, side = a point on the side of the copy, distance: the whole connected chain is offset) | construction (elements, value: true = construction geometry, not part of the profile) |
        pattern_rectangular (elements, count = columns, rows, spacing [dx,dy], negative = to the left / down, angle of the columns) | pattern_circular (elements, center, count = occurrences with the original, angle = sweep from the original: default 360 = evenly round, negative = clockwise) | delete (elements).
        construction and patterns: sketches only. Each result is measured; edited and new elements come back with their geometry (a trimmed circle becomes an arc with a new handle).
        Example: {"action":"mirror","sketch":"sketch:5","elements":["line2d:7","arc2d:8"],"line":[0,0,0,10]}.
        """)]
    public Task<CallToolResult> SketchEdit(
        SketchEditAction action,
        [Description("Sketch handle, or a drawing sheet / drawing document handle.")] string? sketch = null,
        [Description("Handles of the elements to edit (corner: the two elements).")] string[]? elements = null,
        [Description("trim, extend, split: handle of the element.")] string? element = null,
        [Description("trim: 1-2 elements that cut the element; extend: the element to reach.")] string[]? bounds = null,
        [Description("move: displacement [dx, dy] in mm.")] double[]? delta = null,
        [Description("rotate, scale, pattern_circular: centre [x, y] in mm.")] double[]? center = null,
        [Description("rotate: degrees, counter-clockwise positive; pattern_circular: sweep in degrees (default 360); pattern_rectangular: direction of the columns in degrees (default 0).")] double? angle = null,
        [Description("scale: factor (> 0) about 'center'.")] double? factor = null,
        [Description("mirror: two points of the mirror line [x1, y1, x2, y2] in mm.")] double[]? line = null,
        [Description("move, rotate, scale, mirror: edit copies and keep the originals (default false; mirror: true).")] bool? copy = null,
        [Description("trim: a point on the part to remove; extend: a point near the end to extend; split: the point; offset: a point on the chain. [x, y] in mm.")] double[]? at = null,
        [Description("corner: a point on the part of each element to keep [x1, y1, x2, y2] in mm.")] double[]? keep = null,
        [Description("offset: a point on the side of the copy [x, y] in mm.")] double[]? side = null,
        [Description("offset: distance in mm.")] double? distance = null,
        [Description("pattern_rectangular: columns; pattern_circular: occurrences round the centre; both with the original.")] int? count = null,
        [Description("pattern_rectangular: rows (default 1).")] int? rows = null,
        [Description("pattern_rectangular: [dx, dy] in mm between columns and between rows.")] double[]? spacing = null,
        [Description("construction: true = construction geometry, false = normal geometry (default true).")] bool? value = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sketch_edit";
        string act = Snake(action);
        string handle = ToolRunner.Require(sketch, "sketch", act);
        return action switch
        {
            SketchEditAction.Move => runner.RunAsync(tool, act, c => SketchService.Move(c, handle, elements, delta, copy ?? false), cancellationToken),
            SketchEditAction.Rotate => runner.RunAsync(tool, act, c => SketchService.Rotate(c, handle, elements, center, angle, copy ?? false), cancellationToken),
            SketchEditAction.Scale => runner.RunAsync(tool, act, c => SketchService.Scale(c, handle, elements, center, factor, copy ?? false), cancellationToken),
            SketchEditAction.Mirror => runner.RunAsync(tool, act, c => SketchService.Mirror(c, handle, elements, line, copy ?? true), cancellationToken),
            SketchEditAction.Trim => runner.RunAsync(tool, act, c => SketchService.Trim(c, handle,
                ToolRunner.Require(element, "element", act), at, bounds), cancellationToken),
            SketchEditAction.Extend => runner.RunAsync(tool, act, c => SketchService.Extend(c, handle,
                ToolRunner.Require(element, "element", act), at, bounds), cancellationToken),
            SketchEditAction.Split => runner.RunAsync(tool, act, c => SketchService.Split(c, handle,
                ToolRunner.Require(element, "element", act), at), cancellationToken),
            SketchEditAction.Corner => runner.RunAsync(tool, act, c => SketchService.Corner(c, handle, elements, keep), cancellationToken),
            SketchEditAction.Offset => runner.RunAsync(tool, act, c => SketchService.Offset(c, handle, at, side, distance), cancellationToken),
            SketchEditAction.Construction => runner.RunAsync(tool, act, c => SketchService.Construction(c, handle, elements, value ?? true), cancellationToken),
            SketchEditAction.PatternRectangular => runner.RunAsync(tool, act, c => SketchService.RectangularPattern(c, handle, elements, count, rows, spacing, angle), cancellationToken),
            SketchEditAction.PatternCircular => runner.RunAsync(tool, act, c => SketchService.CircularPattern(c, handle, elements, center, count, angle), cancellationToken),
            SketchEditAction.Delete => runner.RunAsync(tool, act, c => SketchService.DeleteElements(c, handle, elements), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
