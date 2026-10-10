using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum AssemblyAction
{
    List,
    Insert,
    Move,
    Rotate,
    Ground,
    Unground,
    Replace,
    Show,
    Hide,
    Suppress,
    Unsuppress,
    Delete,
}

public enum RelationAction
{
    List,
    Add,
    Delete,
}

public enum RelationType
{
    Planar,
    Axial,
    Insert,
    Parallel,
    Tangent,
    Angle,
    RigidSet,
}

public enum BomStructure
{
    Top,
    Flat,
}

/// <summary>Conjuntos: ocurrencias, relaciones, interferencias y lista de materiales.</summary>
[McpServerToolType]
public sealed class AssemblyTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_assembly", Title = "Assembly occurrences", Destructive = true, OpenWorld = false)]
    [Description("""
        Occurrences (parts and subassemblies) of an assembly. Positions in mm and angles in degrees, assembly coordinates.
        list (recursive: also the parts inside subassemblies) | insert (path of a saved .par/.psm/.asm, optional origin [x,y,z] and angles [ax,ay,az]; grounded by default) |
        move (delta [dx,dy,dz], or absolute origin and/or angles) | rotate (axis [x1,y1,z1, x2,y2,z2] through two points, angle) |
        ground | unground | replace (path; all = every occurrence of the same file) | show | hide |
        suppress (unloads it but keeps it in the assembly; returns a 'suppressed' handle) | unsuppress (that handle) | delete (removes the occurrence, not its file).
        move/rotate are checked: an occurrence held by its relations is left where it was and reported. se_relations frees (ungrounds) the occurrence it positions.
        Faces for relations: se_geometry with 'source' = the occurrence handle.
        Example: {"action":"insert","path":"D:\\CAD\\pin.par","origin":[50,30,0],"ground":false}.
        """)]
    public Task<CallToolResult> Assembly(
        AssemblyAction action,
        [Description("Occurrence handle (move, rotate, ground, unground, replace, show, hide, suppress, delete); 'suppressed' handle for unsuppress.")] string? occurrence = null,
        [Description("insert/replace: absolute path of the part or assembly file (allowed folders).")] string? path = null,
        [Description("insert/move: position [x,y,z] in mm.")] double[]? origin = null,
        [Description("insert/move: rotation angles [ax,ay,az] in degrees about the assembly X, Y, Z axes.")] double[]? angles = null,
        [Description("move: relative displacement [dx,dy,dz] in mm.")] double[]? delta = null,
        [Description("rotate: axis through two points [x1,y1,z1, x2,y2,z2] in mm.")] double[]? axis = null,
        [Description("rotate: angle in degrees (right hand about the axis direction).")] double? angle = null,
        [Description("insert: fix the occurrence where it is placed (default true).")] bool ground = true,
        [Description("replace: replace every occurrence of the same file.")] bool all = false,
        [Description("list: include the parts of subassemblies.")] bool recursive = false,
        int offset = 0,
        int limit = 100,
        [Description("Assembly document handle; default: the occurrence's assembly or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_assembly";
        string name = Snake(action);
        Action? precheck = path is null ? null : () => guard.Check(path);
        return action switch
        {
            AssemblyAction.List => runner.RunAsync(tool, name, c => AssemblyService.List(c, document, recursive, offset, limit), cancellationToken),
            AssemblyAction.Insert => runner.RunAsync(tool, name, c => AssemblyService.Insert(c, document,
                ToolRunner.Require(path, "path", "insert"), origin, angles, ground), cancellationToken, precheck: precheck),
            AssemblyAction.Move => runner.RunAsync(tool, name, c => AssemblyService.Move(c, occurrence, delta, origin, angles), cancellationToken),
            AssemblyAction.Rotate => runner.RunAsync(tool, name, c => AssemblyService.Rotate(c, occurrence, axis, angle), cancellationToken),
            AssemblyAction.Ground => runner.RunAsync(tool, name, c => AssemblyService.Ground(c, occurrence, true), cancellationToken),
            AssemblyAction.Unground => runner.RunAsync(tool, name, c => AssemblyService.Ground(c, occurrence, false), cancellationToken),
            AssemblyAction.Replace => runner.RunAsync(tool, name, c => AssemblyService.Replace(c, occurrence,
                ToolRunner.Require(path, "path", "replace"), all), cancellationToken, precheck: precheck),
            AssemblyAction.Show => runner.RunAsync(tool, name, c => AssemblyService.SetVisible(c, occurrence, true), cancellationToken),
            AssemblyAction.Hide => runner.RunAsync(tool, name, c => AssemblyService.SetVisible(c, occurrence, false), cancellationToken),
            AssemblyAction.Suppress => runner.RunAsync(tool, name, c => AssemblyService.Suppress(c, occurrence), cancellationToken),
            AssemblyAction.Unsuppress => runner.RunAsync(tool, name, c => AssemblyService.Unsuppress(c, occurrence), cancellationToken),
            AssemblyAction.Delete => runner.RunAsync(tool, name, c => AssemblyService.Delete(c, occurrence), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_relations", Title = "Assembly relations", Destructive = true, OpenWorld = false)]
    [Description("""
        Assembly relations (mates). list (optionally of one 'occurrence') | add | delete (relation handle).
        add moves 'occurrence1' onto 'occurrence2' (or onto an assembly base plane '1'-'3' when occurrence2 is omitted); occurrence1 is ungrounded first.
        type: planar (element1/element2 = planar faces or reference planes; align false = faces against each other (mate), true = same direction (flush); offset mm) |
        axial (element1/element2 = cylindrical/conical faces or straight/circular edges; keeps the current orientation, flip reverses it) |
        insert (planar mate element1/element2 + axial axis1/axis2 with rotation locked, e.g. a pin into a hole) |
        parallel (element1/element2 = axes as for axial; offset = distance in mm between them, 0 = free) |
        tangent (element1 = cylindrical face; element2 = planar face (cylinder on its outer side) or cylindrical face (outside); offset mm) |
        angle (element1/element2 = planar faces; angle = degrees between their outward normals, 0-180; plane = measurement plane perpendicular to the rotation axis, default the base plane perpendicular to both faces) |
        rigid_set (occurrences = 2+ occurrence handles that then move as one).
        The result is checked geometrically (normals, distance, coaxiality, angle); a relation that fails is removed and the occurrence put back.
        Faces and edges: se_geometry action 'faces'/'edges' with 'source' = the occurrence handle (assembly coordinates).
        Gear, cam, path, center-plane and point relations: se_com_invoke on the document's Relations3d.
        Example: {"action":"add","type":"angle","element1":"face:31","element2":"face:12","angle":30}.
        """)]
    public Task<CallToolResult> Relations(
        RelationAction action,
        RelationType? type = null,
        [Description("add: occurrence to move; optional when element1 is a face/edge handle from se_geometry with source = that occurrence.")] string? occurrence1 = null,
        [Description("add: face, edge or reference plane of occurrence1's part.")] string? element1 = null,
        [Description("add: occurrence it is related to; optional when element2 is a face/edge handle read from it; omit it and use '1'-'3' in element2 for an assembly base plane.")] string? occurrence2 = null,
        [Description("add: face, edge or reference plane of occurrence2's part, or '1'-'3' / an assembly plane handle without occurrence2.")] string? element2 = null,
        [Description("insert: cylindrical face or circular edge of occurrence1 (its axis).")] string? axis1 = null,
        [Description("insert: cylindrical face or circular edge of occurrence2.")] string? axis2 = null,
        [Description("planar/insert: distance in mm between the faces, along the outward normal of element2; parallel: distance between the axes (0 = free); tangent: gap in mm (default 0).")] double offset = 0,
        [Description("planar: true = flush (normals in the same direction); default false = mate (faces against each other).")] bool align = false,
        [Description("axial/insert: reverse the axis orientation.")] bool flip = false,
        [Description("angle: degrees between the outward normals of element1 and element2 (0-180).")] double? angle = null,
        [Description("rigid_set: occurrence handles (2 or more).")] string[]? occurrences = null,
        [Description("angle: measurement plane, '1'-'3' (assembly base planes XY, YZ, XZ) or an assembly plane handle, perpendicular to the rotation axis; required when the faces are parallel.")] string? plane = null,
        [Description("delete: relation handle.")] string? relation = null,
        [Description("list: only the relations of this occurrence.")] string? occurrence = null,
        [Description("list: number of relations to skip.")] int skip = 0,
        int limit = 100,
        [Description("Assembly document handle; default: the occurrence's assembly or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_relations";
        string name = Snake(action);
        return action switch
        {
            RelationAction.List => runner.RunAsync(tool, name, c => RelationService.List(c, document, occurrence, skip, limit), cancellationToken),
            RelationAction.Add => runner.RunAsync(tool, name, c => RelationService.Add(c, document,
                new RelationSpec(Snake(ToolRunner.RequireValue(type, "type", "add")), occurrence1, element1, occurrence2, element2, axis1, axis2,
                    offset, align, flip, angle, occurrences, plane)), cancellationToken),
            RelationAction.Delete => runner.RunAsync(tool, name, c => RelationService.Delete(c, relation), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_interference", Title = "Interference check", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Checks interference (overlapping material) between occurrences: 'occurrences' among themselves, or against 'against'; with neither, all top-level occurrences.
        status: none | confirmed | probable | confirmed_and_probable | incomplete, with the interfering pairs. Nothing is added to the assembly.
        """)]
    public Task<CallToolResult> Interference(
        [Description("Occurrence handles to check (default: all top-level occurrences).")] string[]? occurrences = null,
        [Description("Occurrence handles to check against (default: the first set among itself).")] string[]? against = null,
        [Description("Assembly document handle; default: the occurrences' assembly or the active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_interference", "check", c => AssemblyService.Interference(c, document, occurrences, against), cancellationToken);

    [McpServerTool(Name = "se_bom", Title = "Bill of materials", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Bill of materials of an assembly, grouped by file. structure: top (top-level items; a subassembly is one line) | flat (only parts, quantities multiplied through subassemblies).
        Occurrences excluded from the BOM in Solid Edge are left out.
        """)]
    public Task<CallToolResult> Bom(
        BomStructure structure = BomStructure.Top,
        [Description("Assembly document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_bom", Snake(structure), c => AssemblyService.Bom(c, document, Snake(structure)), cancellationToken);
}
