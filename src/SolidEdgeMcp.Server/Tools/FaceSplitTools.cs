using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum FaceSplitAction
{
    Split,
    Parting,
    OffsetEdge,
}

/// <summary>Faces of the design body split into more faces, the volume unchanged: by tools, at a parting line or along an edge.</summary>
[McpServerToolType]
public sealed class FaceSplitTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_face_split", Title = "Split faces", Destructive = false, OpenWorld = false)]
    [Description("""
        Splits faces of the design body of a part into more faces without changing its volume, e.g. to draft, colour or relate only part of a face. mm.
        split ('faces' of one body; 'tools' = reference planes ('1'-'3', 'xy', 'yz', 'xz' or handles), surfaces or faces of surfaces, curves or edges that cross them; faces of the body itself are not tools) |
        parting ('faces' split along their silhouette seen along the normal of 'plane', e.g. a sphere at its equator; flat faces have none) |
        offset_edge ('faces' = one face, 'edge' = an edge of it, 'distance' mm: a line on the face at that distance from the edge splits it).
        Each split is checked: the body gets more faces and keeps its volume; otherwise it is deleted. Returns the new faces (handles, areas mm²).
        Example: {"action":"split","faces":["face:12"],"tools":["yz"]}; {"action":"offset_edge","faces":["face:12"],"edge":"edge:40","distance":10}.
        """)]
    public Task<CallToolResult> FaceSplit(
        FaceSplitAction action,
        [Description("Face handles of the design body (se_geometry faces); offset_edge: one face.")] string[]? faces = null,
        [Description("split: reference planes ('1'-'3', 'xy', 'yz', 'xz' or handles), surfaces, faces of surfaces, curves or edges.")] string[]? tools = null,
        [Description("parting: '1'-'3', 'xy', 'yz', 'xz' or a reference plane handle; the silhouette is seen along its normal.")] string? plane = null,
        [Description("offset_edge: an edge of the face (se_geometry edges).")] string? edge = null,
        [Description("offset_edge: distance in mm from the edge, inside the face.")] double? distance = null,
        [Description("Part document handle; default: the document of the faces or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        string act = Snake(action);
        var spec = new FaceSplitSpec(act, faces, tools, plane, edge, distance);
        return runner.RunAsync("se_face_split", act, c => FeatureService.FaceSplit(c, document, spec), cancellationToken);
    }
}
