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

public enum AssemblyPatternAction
{
    List,
    Create,
    Duplicate,
    Delete,
    Dissolve,
}

public enum AssemblyFeatureAction
{
    List,
    Cut,
    Revolve,
    Hole,
    Mirror,
    Pattern,
    Protrusion,
    RevolvedProtrusion,
    FilletWeld,
    LabelWeld,
    Delete,
}

public enum AssemblyConfigAction
{
    List,
    Save,
    Apply,
    Delete,
    Explode,
    Exploded,
}

public enum ExplodeLevel
{
    Part,
    Subassembly,
}

public enum BomStructure
{
    Top,
    Flat,
}

/// <summary>Assemblies: occurrences, relations, interferences and bill of materials.</summary>
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

    [McpServerTool(Name = "se_assembly_pattern", Title = "Component patterns", Destructive = true, OpenWorld = false)]
    [Description("""
        Component patterns that follow a pattern of a part, as the assembly Pattern command does: place the occurrences on the first instance (e.g. a pin in the first hole), then repeat them on every instance of the part pattern. Also duplicate patterns, as the Duplicate command does.
        list | create (occurrences = the ones to repeat; source = the occurrence whose part has the pattern; pattern = its name, optional if the part has one; reference = the feature the occurrences sit on, default the first feature the pattern repeats; name optional) |
        duplicate (occurrences = the ones to copy, placed on source; targets = the occurrences to copy them onto, at the same relative position and orientation, e.g. the bolts of one flange onto the other flanges; name optional) |
        delete (name: removes the pattern and its copies) | dissolve (name: the copies become ordinary occurrences).
        The pattern follows the part: editing the part pattern updates the copies. Component mirrors and patterns along a curve cannot be created through the API.
        Example: {"action":"create","occurrences":["occurrence:5"],"source":"occurrence:3","pattern":"Pattern_1"}.
        """)]
    public Task<CallToolResult> ComponentPattern(
        AssemblyPatternAction action,
        [Description("create, duplicate: occurrence handles to repeat.")] string[]? occurrences = null,
        [Description("create: occurrence whose part has the pattern to follow; duplicate: occurrence the repeated ones are placed on.")] string? source = null,
        [Description("duplicate: occurrences to copy them onto (same relative position as on source).")] string[]? targets = null,
        [Description("create: name of the part pattern (se_feature_tree of the part, e.g. 'Pattern_1').")] string? pattern = null,
        [Description("create: name of the part feature the occurrences sit on (default: the first feature the pattern repeats).")] string? reference = null,
        [Description("create: name of the new pattern (default 'Pattern N'); delete/dissolve: the pattern name.")] string? name = null,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_assembly_pattern";
        string act = Snake(action);
        return action switch
        {
            AssemblyPatternAction.List => runner.RunAsync(tool, act, c => AssemblyPatternService.List(c, document), cancellationToken),
            AssemblyPatternAction.Create => runner.RunAsync(tool, act, c => AssemblyPatternService.Create(c, occurrences, source, pattern, reference, name), cancellationToken),
            AssemblyPatternAction.Duplicate => runner.RunAsync(tool, act, c => AssemblyPatternService.Duplicate(c, occurrences, source, targets, name), cancellationToken),
            AssemblyPatternAction.Delete => runner.RunAsync(tool, act, c => AssemblyPatternService.Delete(c, document, name), cancellationToken),
            AssemblyPatternAction.Dissolve => runner.RunAsync(tool, act, c => AssemblyPatternService.Dissolve(c, document, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_assembly_feature", Title = "Assembly features", Destructive = true, OpenWorld = false)]
    [Description("""
        Assembly features: made in the assembly (the part files do not change), from a sketch drawn in the assembly (se_sketch create with plane '1'-'3' of the assembly, draw, close).
        list (status, the faces left on each part, volumes) | cut (extruded cutout: sketch; occurrences = the parts to cut; depth mm, default through all; side auto (along the plane normal, then against it) | normal | reverse | symmetric) |
        revolve (revolved cutout: sketch and its 'axis' from se_sketch 'axis' before 'close'; angle degrees, default 360; side) |
        hole (plane '1'-'3' or an assembly plane; centers [x,y,z,...] mm; diameter; type simple | countersink; extent through_all | finite with depth; direction auto | normal | reverse; counterbore and tapped: se_hole in the part) |
        mirror (features = names of cutouts; plane) | pattern (features = names of cutouts or holes; plane whose sketch axes the copies follow; count [columns, rows]; spacing [mm, mm]; angle turns the rows, degrees) |
        protrusion (sketch; depth mm; side normal | reverse | symmetric: a new assembly body) | revolved_protrusion (sketch, axis, angle, side) |
        fillet_weld (weldment only, se_documents new kind 'weldment': faces = base faces, targetFaces, read with se_geometry 'faces' and source = the occurrence; leg mm on the base faces, leg2 on the target faces, default leg) |
        label_weld (weldment only: edges from se_geometry 'edges') | delete (name).
        'name' names the new feature. Each feature is checked (faces left on every part, body volume, weld bead) and removed if it fails.
        Example: {"action":"cut","sketch":"sketch:8","occurrences":["occurrence:2"]}; {"action":"pattern","features":["Cutout 1"],"plane":"1","count":[3],"spacing":[30]};
        {"action":"fillet_weld","faces":["face:19"],"targetFaces":["face:20"],"leg":5}.
        """)]
    public Task<CallToolResult> AssemblyFeature(
        AssemblyFeatureAction action,
        [Description("cut, revolve, protrusion, revolved_protrusion: closed sketch handle made in the assembly.")] string? sketch = null,
        [Description("cut, revolve, hole: occurrence handles of the parts to cut.")] string[]? occurrences = null,
        [Description("cut, hole, protrusion: depth in mm from the sketch plane (cut: default through all; hole: with extent finite).")] double? depth = null,
        PartFeatureSide side = PartFeatureSide.Auto,
        [Description("revolve, revolved_protrusion: axis handle from se_sketch 'axis'.")] string? axis = null,
        [Description("revolve, revolved_protrusion: angle in degrees (default 360); pattern: rotation of the rows in degrees (default 0).")] double? angle = null,
        [Description("hole, mirror, pattern: assembly plane '1'-'3' or an assembly reference plane handle.")] string? plane = null,
        [Description("mirror: names of the cutouts to mirror; pattern: names of the cutouts or holes to repeat (action 'list').")] string[]? features = null,
        [Description("pattern: [columns] or [columns, rows], the original included.")] int[]? count = null,
        [Description("pattern: [between columns, between rows] in mm along the sketch axes of the plane.")] double[]? spacing = null,
        [Description("fillet_weld: base face handles (se_geometry 'faces' with source = the occurrence).")] string[]? faces = null,
        [Description("fillet_weld: target face handles, meeting the base faces along an edge.")] string[]? targetFaces = null,
        [Description("fillet_weld: leg in mm on the base faces.")] double? leg = null,
        [Description("fillet_weld: leg in mm on the target faces (default leg).")] double? leg2 = null,
        [Description("label_weld: edge handles (se_geometry 'edges' of an occurrence face).")] string[]? edges = null,
        [Description("hole: centers [x1,y1,z1,...] in mm in the assembly, projected onto the plane.")] double[]? centers = null,
        [Description("hole: diameter in mm.")] double? diameter = null,
        HoleType type = HoleType.Simple,
        [Description("hole countersink: diameter in mm.")] double? countersinkDiameter = null,
        [Description("hole countersink: angle in degrees (default 90).")] double countersinkAngle = 90,
        ExtrudeExtent extent = ExtrudeExtent.ThroughAll,
        HoleDirection direction = HoleDirection.Auto,
        [Description("Name of the new feature; delete: the feature name.")] string? name = null,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_assembly_feature";
        string act = Snake(action);
        return action switch
        {
            AssemblyFeatureAction.List => runner.RunAsync(tool, act, c => AssemblyFeatureService.List(c, document), cancellationToken),
            AssemblyFeatureAction.Cut => runner.RunAsync(tool, act, c => AssemblyFeatureService.Cut(c, sketch, occurrences, depth, Snake(side), name), cancellationToken),
            AssemblyFeatureAction.Revolve => runner.RunAsync(tool, act, c => AssemblyFeatureService.Revolve(c, sketch, axis, occurrences, angle, Snake(side), name), cancellationToken),
            AssemblyFeatureAction.Hole => runner.RunAsync(tool, act, c => AssemblyFeatureService.Hole(c, document, occurrences,
                new HoleSpec(plane ?? "", centers, diameter, Snake(type), null, null, countersinkDiameter, countersinkAngle,
                    null, Snake(extent), depth, Snake(direction)), name), cancellationToken),
            AssemblyFeatureAction.Mirror => runner.RunAsync(tool, act, c => AssemblyFeatureService.Mirror(c, document, features, plane, name), cancellationToken),
            AssemblyFeatureAction.Pattern => runner.RunAsync(tool, act, c => AssemblyFeatureService.Pattern(c, document, features, plane, count, spacing, angle, name), cancellationToken),
            AssemblyFeatureAction.Protrusion => runner.RunAsync(tool, act, c => AssemblyFeatureService.Protrusion(c, document, sketch, depth, Snake(side), name), cancellationToken),
            AssemblyFeatureAction.RevolvedProtrusion => runner.RunAsync(tool, act, c => AssemblyFeatureService.RevolvedProtrusion(c, document, sketch, axis, angle, Snake(side), name), cancellationToken),
            AssemblyFeatureAction.FilletWeld => runner.RunAsync(tool, act, c => AssemblyFeatureService.FilletWeld(c, document, faces, targetFaces, leg, leg2, name), cancellationToken),
            AssemblyFeatureAction.LabelWeld => runner.RunAsync(tool, act, c => AssemblyFeatureService.LabelWeld(c, document, edges, name), cancellationToken),
            AssemblyFeatureAction.Delete => runner.RunAsync(tool, act, c => AssemblyFeatureService.Delete(c, document, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_assembly_config", Title = "Assembly display configurations and exploded views", Destructive = true, OpenWorld = false)]
    [Description("""
        Display configurations of an assembly: named states of which components are shown (hide them with se_assembly hide first).
        list (configurations, the active one and the hidden components) | save (name: the current display) | apply (name: shows and hides the components as saved) | delete (name).
        The assembly must have been saved (se_documents save_as): Solid Edge keeps the configurations in a .cfg file next to it.
        Exploded views: explode (automatic explode along the relations; spread = distance in mm between components, default Solid Edge's minimum clearance; by part | subassembly)
        | exploded (exploded placement of each component next to its assembly placement). The assembly placements do not change; there is no call to collapse
        the explode: close the assembly without saving, or use the Explode environment.
        Example: {"action":"save","name":"Without covers"}; {"action":"explode","spread":50}.
        """)]
    public Task<CallToolResult> AssemblyConfig(
        AssemblyConfigAction action,
        [Description("save/apply/delete: configuration name.")] string? name = null,
        [Description("explode: distance between components in mm (default: Solid Edge's minimum clearance).")] double? spread = null,
        ExplodeLevel by = ExplodeLevel.Part,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_assembly_config";
        string act = Snake(action);
        return action switch
        {
            AssemblyConfigAction.List => runner.RunAsync(tool, act, c => AssemblyConfigService.List(c, document), cancellationToken),
            AssemblyConfigAction.Save => runner.RunAsync(tool, act, c => AssemblyConfigService.Save(c, document, name), cancellationToken),
            AssemblyConfigAction.Apply => runner.RunAsync(tool, act, c => AssemblyConfigService.Apply(c, document, name), cancellationToken),
            AssemblyConfigAction.Delete => runner.RunAsync(tool, act, c => AssemblyConfigService.Delete(c, document, name), cancellationToken),
            AssemblyConfigAction.Explode => runner.RunAsync(tool, act, c => AssemblyExplodeService.Explode(c, document, spread, Snake(by)), cancellationToken),
            AssemblyConfigAction.Exploded => runner.RunAsync(tool, act, c => AssemblyExplodeService.Exploded(c, document), cancellationToken),
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
