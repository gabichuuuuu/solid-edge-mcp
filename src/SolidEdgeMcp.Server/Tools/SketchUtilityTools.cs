// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>Sketch tools of the ribbon that are not drawing or editing: clean, convert to curve, origin, image, section sketches, total length, copy sketch.</summary>
[McpServerToolType]
public sealed class SketchUtilityTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_sketch_tools", Title = "Sketch tools", Destructive = true, OpenWorld = false)]
    [Description("""
        Sketch tools of the ribbon; mm and degrees. sketch = a handle from se_sketch create (clean, convert and length also take a drawing sheet).
        clean (Clean Sketch: duplicate, overlapping and small elements deleted; tolerance = the smallest length kept, default 0.01; points, splines, identical, small = what is cleaned, default all; on a sheet, elements = what to clean, default all) |
        convert (Convert to Curve: elements = lines, arcs… become B-spline curves of the same shape) |
        origin (Reposition / Zero Origin: origin [x,y] and angle of the sketch's local x axis; none = back to the plane's own) |
        image (path = .png/.jpg/.bmp placed in a sketch; position [x,y] = its lower-left corner; width, the height follows; link) |
        sections (Section Sketches of a part: a sketch of the bodies' section on plane = '1'-'3' or a reference plane and on count more planes 'spacing' apart on side normal | both | reverse; bodies default all) |
        length (Total Length of elements, default every curve) |
        copy (Copy Sketch in an assembly: sketch = a layout (se_sketch create layout=true) copied into the part of 'occurrence'; associative).
        Each result is read back: counts before and after, the new curves and their length, the axis, the image size, the new sketches.
        Example: {"action":"clean","sketch":"sketch:3"}; {"action":"sections","plane":"1","count":3,"spacing":5}.
        """)]
    public Task<CallToolResult> SketchUtility(
        SketchUtilityAction action,
        [Description("Sketch handle (se_sketch create); clean, convert and length also a drawing sheet.")] string? sketch = null,
        [Description("convert, length; clean on a sheet: element handles.")] string[]? elements = null,
        [Description("clean: the smallest length kept, mm (default 0.01).")] double? tolerance = null,
        [Description("clean: delete stray points.")] bool points = true,
        [Description("clean: clean B-spline curves.")] bool splines = true,
        [Description("clean: delete identical (duplicate, overlapping) elements.")] bool identical = true,
        [Description("clean: delete elements shorter than the tolerance.")] bool small = true,
        [Description("origin: the new origin [x,y] in mm in the sketch plane; omitted with angle = the plane's own origin.")] double[]? origin = null,
        [Description("origin: direction of the local x axis in degrees from the plane's x axis (default 0).")] double? angle = null,
        [Description("image: the picture file (allowed folders).")] string? path = null,
        [Description("image: lower-left corner [x,y] in mm.")] double[]? position = null,
        [Description("image: width in mm (the height keeps the aspect ratio).")] double? width = null,
        [Description("image: link the file instead of embedding it.")] bool link = false,
        [Description("sections: '1'-'3' or a reference plane handle.")] string? plane = null,
        [Description("sections: design body handles (se_body list); default all.")] string[]? bodies = null,
        [Description("sections: number of planes besides the reference plane (default 1).")] int? count = null,
        [Description("sections: distance between the planes, mm.")] double? spacing = null,
        [Description("sections: which side of the plane the planes go.")] SectionSide side = SectionSide.Normal,
        [Description("copy: the part occurrence that receives the layout.")] string? occurrence = null,
        [Description("copy: keep the copy linked to the layout.")] bool associative = false,
        [Description("sections: part document handle; default the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_sketch_tools";
        string act = Snake(action);
        string Sketch() => sketch ?? throw ToolException.InvalidArgument($"{act} needs 'sketch': a sketch handle (se_sketch create).");
        return action switch
        {
            SketchUtilityAction.Clean => runner.RunAsync(tool, act, c => SketchService.Clean(c, Sketch(), elements, tolerance, points, splines, identical, small), cancellationToken),
            SketchUtilityAction.Convert => runner.RunAsync(tool, act, c => SketchService.ConvertToCurve(c, Sketch(), elements), cancellationToken),
            SketchUtilityAction.Origin => runner.RunAsync(tool, act, c => SketchService.Origin(c, Sketch(), origin, angle), cancellationToken),
            SketchUtilityAction.Image => runner.RunAsync(tool, act, c => SketchService.Image(c, Sketch(), path, position, width, link), cancellationToken,
                precheck: path is null ? null : () => guard.Check(path)),
            SketchUtilityAction.Sections => runner.RunAsync(tool, act, c => SketchService.Sections(c, document, plane, bodies, count, spacing, side), cancellationToken),
            SketchUtilityAction.Length => runner.RunAsync(tool, act, c => SketchService.TotalLength(c, Sketch(), elements), cancellationToken),
            SketchUtilityAction.Copy => runner.RunAsync(tool, act, c => SketchService.CopyToPart(c, Sketch(), occurrence, associative), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
