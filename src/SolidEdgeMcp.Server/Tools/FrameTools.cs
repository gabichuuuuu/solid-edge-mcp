using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum FrameAction
{
    Library,
    List,
    Frame,
    Pipe,
    Delete,
}

public enum FrameLibrary
{
    Frames,
    Pipes,
}

public enum FrameEndCondition
{
    Miter,
    Butt1,
    Butt2,
    None,
}

/// <summary>Structural frames and pipes of an assembly.</summary>
[McpServerToolType]
public sealed class FrameTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_frame", Title = "Structural frames and pipes", Destructive = true, OpenWorld = false)]
    [Description("""
        Structural frames and pipes in an assembly, along a polyline of points (mm, assembly coordinates; one line segment per pair) or along 'lines' drawn with se_sketch line in the assembly.
        library (kind frames: section parts installed with Solid Edge, e.g. 'DIN/Square Tubing/SquareTubing 20x20x1.6'; kind pipes: pipes and fittings, e.g. 'DIN/Pipe/Pipe_100_mm'; filter) |
        list (frames with their cut length and members, pipes with their segments and fittings) |
        frame (section = a library name or the absolute path of a part of your own; one member per segment, each an occurrence of the section; endCondition at the corners miter | butt1 | butt2 | none) |
        pipe (diameter = outside diameter mm, e.g. 114.3 for DIN 100; pipe part; elbow at corners, tee, cross, coupling) | delete (name of a frame or pipe, with its members or segments).
        Library parts are copied into the work folder and used from there. Members and segments report their boundingBox in the assembly; se_geometry with a member occurrence gives its real faces (e.g. for se_assembly_feature fillet_weld).
        Checked: a frame or pipe that makes no member or segment is removed.
        Example: {"action":"frame","section":"DIN/Square Tubing/SquareTubing 20x20x1.6","points":[0,0,0,500,0,0,500,300,0]}; {"action":"pipe","diameter":114.3,"pipe":"DIN/Pipe/Pipe_100_mm","elbow":"DIN/Fitting/Butt/Butt_Elbow90_100_mm","points":[0,0,0,500,0,0,500,0,500]}.
        """)]
    public Task<CallToolResult> Frame(
        FrameAction action,
        [Description("library: frames (sections) or pipes (pipes and fittings).")] FrameLibrary kind = FrameLibrary.Frames,
        [Description("library: only names containing this text.")] string? filter = null,
        [Description("frame: section part, a library name or an absolute path.")] string? section = null,
        [Description("frame, pipe: polyline [x1,y1,z1, x2,y2,z2, ...] in mm (at least 2 points).")] double[]? points = null,
        [Description("frame, pipe: line handles from se_sketch line in the assembly, instead of points.")] string[]? lines = null,
        [Description("frame: end treatment where members meet (default Solid Edge's).")] FrameEndCondition? endCondition = null,
        [Description("pipe: outside diameter in mm.")] double? diameter = null,
        [Description("pipe: pipe part (library name or absolute path).")] string? pipe = null,
        [Description("pipe: elbow part, needed where the path turns.")] string? elbow = null,
        [Description("pipe: tee part.")] string? tee = null,
        [Description("pipe: cross part.")] string? cross = null,
        [Description("pipe: coupling part.")] string? coupling = null,
        [Description("frame, pipe: name of the new frame or pipe; delete: the name.")] string? name = null,
        int offset = 0,
        int limit = 100,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_frame";
        string act = Snake(action);
        (string Name, string? Value)[] files = action == FrameAction.Frame
            ? [("section", section)]
            : [("pipe", pipe), ("elbow", elbow), ("tee", tee), ("cross", cross), ("coupling", coupling)];
        Action precheck = () =>
        {
            foreach ((string argument, string? file) in files)
            {
                if (file is not null && Path.IsPathFullyQualified(file))
                {
                    guard.Check(file, argument);
                }
            }
        };
        return action switch
        {
            FrameAction.Library => runner.RunAsync(tool, act, c => FrameService.Library(c, Snake(kind), filter, offset, limit), cancellationToken),
            FrameAction.List => runner.RunAsync(tool, act, c => FrameService.List(c, document), cancellationToken),
            FrameAction.Frame => runner.RunAsync(tool, act, c => FrameService.Frame(c, document, section, points, lines,
                endCondition is { } e ? Snake(e) : null, name), cancellationToken, precheck: precheck),
            FrameAction.Pipe => runner.RunAsync(tool, act, c => FrameService.Pipe(c, document, diameter, points, lines,
                new PipeParts(pipe, elbow, tee, cross, coupling), name), cancellationToken, precheck: precheck),
            FrameAction.Delete => runner.RunAsync(tool, act, c => FrameService.Delete(c, document, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
