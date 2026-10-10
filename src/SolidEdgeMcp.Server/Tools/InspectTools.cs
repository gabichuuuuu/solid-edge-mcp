using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum GeometryAction
{
    Summary,
    Faces,
    Edges,
}

public enum MeasureAction
{
    Distance,
    Angle,
}

/// <summary>Consultas de solo lectura del modelo: geometría, medidas y propiedades físicas (mm).</summary>
[McpServerToolType]
public sealed class InspectTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_geometry", Title = "Model geometry", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Geometry of a part or sheet metal body, in mm. summary (volume mm3, bounding box, face/edge/vertex counts) |
        faces (handle, type, area mm2, bounding box, outward normal of planes, radius and axis of cylinders) |
        edges (handle, type, start/end points, length, direction of lines, centre and radius of circles).
        'source' limits faces/edges to a feature (handle from se_feature_tree) or, for edges, to a face; 'direction' selects by orientation (e.g. [0,0,1]: top faces / vertical edges).
        In an assembly, 'source' = an occurrence handle (se_assembly) lists the faces/edges of its part in assembly coordinates: use them for se_relations.
        Face and edge handles expire when the model changes.
        """)]
    public Task<CallToolResult> Geometry(
        GeometryAction action = GeometryAction.Summary,
        [Description("Feature handle (faces/edges of that feature), face handle (its edges) or assembly occurrence handle (its part, in assembly coordinates); default: whole body.")] string? source = null,
        [Description("Filter. Faces: all | plane | cylinder | cone | sphere | torus. Edges: all | line | circle | spline.")] string type = "all",
        [Description("Filter [x,y,z]: faces = planar faces whose outward normal points this way; edges = straight edges parallel to it.")] double[]? direction = null,
        int offset = 0,
        int limit = 100,
        [Description("Document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_geometry";
        string name = action.ToString();
        return action switch
        {
            GeometryAction.Summary => runner.RunAsync(tool, name, c => InspectService.Summary(c, document), cancellationToken),
            GeometryAction.Faces => runner.RunAsync(tool, name, c => InspectService.Faces(c, document, source, type, direction, offset, limit), cancellationToken),
            GeometryAction.Edges => runner.RunAsync(tool, name, c => InspectService.Edges(c, document, source, type, direction, offset, limit), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_measure", Title = "Measure", ReadOnly = true, OpenWorld = false)]
    [Description("distance: minimum distance in mm between two elements (faces, edges, vertices, bodies, planes) with the closest points | angle: degrees between planar faces / reference planes (normals) or straight edges (directions).")]
    public Task<CallToolResult> Measure(
        MeasureAction action,
        [Description("First element handle.")] string first,
        [Description("Second element handle.")] string second,
        [Description("Document handle; default: the first element's document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        action switch
        {
            MeasureAction.Distance => runner.RunAsync("se_measure", "distance", c => InspectService.Distance(c, document, first, second), cancellationToken),
            MeasureAction.Angle => runner.RunAsync("se_measure", "angle", c => InspectService.Angle(c, first, second), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    [McpServerTool(Name = "se_physical", Title = "Physical properties", ReadOnly = true, OpenWorld = false)]
    [Description("Volume (mm3), area (mm2) and centre of volume of the part; with a density (argument in kg/m3, or the document material) also mass (kg), centre of gravity (mm), inertia about the global axes through the origin (kg*mm2; products of inertia as +integral of xy dm) and principal moments about the centre of gravity.")]
    public Task<CallToolResult> Physical(
        [Description("Density in kg/m3 (e.g. 7850 for steel); default: the document's material density.")] double? density = null,
        [Description("Document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_physical", "compute", c => InspectService.Physical(c, document, density), cancellationToken);
}
