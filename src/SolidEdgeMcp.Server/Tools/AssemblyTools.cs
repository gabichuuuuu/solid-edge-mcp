// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Text.Json;
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
    Activate,
    Inactivate,
    Bundle,
    Transfer,
    Disperse,
    CopyAssembly,
    Mirror,
    Adjustable,
    SaveModel,
    Properties,
    SetProperty,
    Reorder,
}

public enum ReplaceSource
{
    File,
    Copy,
    New,
}

public enum RelationAction
{
    List,
    Add,
    Edit,
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
    CenterPlane,
    Connect,
    CoordinateSystem,
    Gear,
}

public enum EdgePoint
{
    Start,
    End,
    Mid,
    Center,
}

public enum GearRatio
{
    Teeth,
    Turns,
}

public enum AssemblyPatternAction
{
    List,
    Create,
    Duplicate,
    Curve,
    Delete,
    Dissolve,
    Clone,
}

public enum CloneRelation
{
    Ground,
    None,
    Repair,
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

public enum AssemblyFamilyAction
{
    List,
    Create,
    Add,
    Activate,
    Rename,
    Delete,
    SaveMember,
    Exclude,
    Alternates,
    AddAlternate,
    RemoveAlternate,
}

public enum AssemblyFamilyKind
{
    AlternatePositions,
    Family,
}

public enum AssemblyConfigAction
{
    List,
    Save,
    Apply,
    Delete,
    Explode,
    Exploded,
    Performance,
}

public enum ExplodeLevel
{
    Part,
    Subassembly,
}

public enum InterferenceCompare
{
    Auto,
    Others,
    Visible,
}

public enum BomStructure
{
    Top,
    Flat,
    Indented,
    Exploded,
    Statistics,
}

/// <summary>Assemblies: occurrences, relations, interferences and bill of materials.</summary>
[McpServerToolType]
public sealed class AssemblyTools(ToolRunner runner, PathGuard guard)
{
    [McpServerTool(Name = "se_assembly", Title = "Assembly occurrences", Destructive = true, OpenWorld = false)]
    [Description("""
        Occurrences (parts and subassemblies) of an assembly. mm, degrees, assembly coordinates.
        list (recursive: also the parts of subassemblies) | insert (path of a saved .par/.psm/.asm, optional origin [x,y,z] and angles [ax,ay,az]; grounded by default;
        create = a new empty file made there from the template first; adjustable = as an adjustable part) |
        move (delta [dx,dy,dz], or absolute origin and/or angles) | rotate (axis [x1,y1,z1, x2,y2,z2] through two points, angle; a gear drives its gear relations) |
        ground | unground | replace (path; all = every occurrence of the same file; 'source' copy or new) | show | hide (all = every occurrence; show with an occurrence and all = Show Only) |
        suppress (unloads it, kept in the assembly; returns a 'suppressed' handle) | unsuppress (that handle) | delete (removes the occurrence, not its file) |
        activate (loads it for editing; all = every component) | inactivate (lighter, still measurable) | adjustable (an adjustable part: adjustable here) |
        save_model (occurrence, path: a new .par with a linked copy of its part; save the assembly first) |
        bundle (occurrences → a new subassembly saved at path (.asm, must not exist; template optional), keeping their positions) |
        transfer (occurrences → into the existing subassembly occurrence 'subassembly') | disperse (subassembly occurrence: its components move up a level; all = every occurrence of it) |
        copy_assembly (path of another saved .asm: its components copied in, kept in step; plane = mirrored about it) |
        mirror (occurrence, plane: a new occurrence of the same file as its mirror image) |
        properties (occurrence: Occurrence Properties and Show/Hide Component) | set_property (occurrence, name, value) | reorder (occurrence, target: placed after it, or before).
        bundle/transfer give the moved occurrences new handles. move/rotate are checked: an occurrence held by relations stays and is reported.
        Faces for relations: se_geometry source = occurrence.
        Example: {"action":"insert","path":"D:\\CAD\\pin.par","origin":[50,30,0],"ground":false}.
        """)]
    public Task<CallToolResult> Assembly(
        AssemblyAction action,
        [Description("Occurrence handle (move, rotate, ground, unground, replace, show, hide, suppress, delete, activate, inactivate, disperse, adjustable, save_model); 'suppressed' handle for unsuppress.")] string? occurrence = null,
        [Description("insert/replace: absolute path of the part or assembly file (allowed folders); bundle: the new subassembly file (.asm); copy_assembly: the assembly to copy; save_model: the new .par.")] string? path = null,
        [Description("insert/move: position [x,y,z] in mm.")] double[]? origin = null,
        [Description("insert/move: rotation angles [ax,ay,az] in degrees about the assembly X, Y, Z axes.")] double[]? angles = null,
        [Description("move: relative displacement [dx,dy,dz] in mm.")] double[]? delta = null,
        [Description("rotate: axis through two points [x1,y1,z1, x2,y2,z2] in mm.")] double[]? axis = null,
        [Description("rotate: angle in degrees (right hand about the axis direction).")] double? angle = null,
        [Description("insert: fix the occurrence where it is placed (default true).")] bool ground = true,
        [Description("replace: every occurrence of the same file; activate: every component; disperse: every occurrence of the subassembly; show/hide: every occurrence.")] bool all = false,
        [Description("list: include the parts of subassemblies.")] bool recursive = false,
        [Description("bundle, transfer: occurrence handles to move into the subassembly.")] string[]? occurrences = null,
        [Description("transfer: the subassembly occurrence to move them into.")] string? subassembly = null,
        [Description("bundle, insert create, replace new: template of the new file (default: Solid Edge's default template of its kind).")] string? template = null,
        [Description("insert: create the file at path, empty, from the template (a new part to model).")] bool create = false,
        [Description("insert: place the part as an adjustable part (it needs adjustable variables, se_variables adjustable).")] bool adjustable = false,
        [Description("replace: file (default: path is an existing file) | copy (path gets a copy of the occurrence's file) | new (path gets an empty part from the template).")] ReplaceSource source = ReplaceSource.File,
        [Description("save_model: replace an existing file.")] bool overwrite = false,
        [Description("copy_assembly, mirror: assembly plane to mirror about ('1'-'3', 'xy'/'yz'/'xz' or a reference plane handle).")] string? plane = null,
        [Description("set_property: property name from 'properties' (name, quantity, include_in_bom, include_in_physical_properties, include_in_interference, reference, display_in_drawings, locatable, show_*...).")] string? name = null,
        [Description("set_property: new value (true/false; quantity a whole number from 1; name text).")] JsonElement? value = null,
        [Description("reorder: the occurrence to place it next to.")] string? target = null,
        [Description("reorder: place it before 'target' (default after).")] bool before = false,
        int offset = 0,
        int limit = 100,
        [Description("Assembly document handle; default: the occurrence's assembly or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_assembly";
        string actionName = Snake(action);
        Action? precheck = path is null ? null : () => guard.Check(path);
        return action switch
        {
            AssemblyAction.List => runner.RunAsync(tool, actionName, c => AssemblyService.List(c, document, recursive, offset, limit), cancellationToken),
            AssemblyAction.Insert => runner.RunAsync(tool, actionName, c => create
                ? AssemblyService.InsertNew(c, document, ToolRunner.Require(path, "path", "insert"), template, origin, angles, ground)
                : adjustable
                    ? AssemblyService.InsertAdjustable(c, document, ToolRunner.Require(path, "path", "insert"), origin, angles, ground)
                    : AssemblyService.Insert(c, document, ToolRunner.Require(path, "path", "insert"), origin, angles, ground), cancellationToken, precheck: precheck),
            AssemblyAction.Move => runner.RunAsync(tool, actionName, c => AssemblyService.Move(c, occurrence, delta, origin, angles), cancellationToken),
            AssemblyAction.Rotate => runner.RunAsync(tool, actionName, c => AssemblyService.Rotate(c, occurrence, axis, angle), cancellationToken),
            AssemblyAction.Mirror => runner.RunAsync(tool, actionName, c => AssemblyService.Mirror(c, occurrence, plane), cancellationToken),
            AssemblyAction.Ground => runner.RunAsync(tool, actionName, c => AssemblyService.Ground(c, occurrence, true), cancellationToken),
            AssemblyAction.Unground => runner.RunAsync(tool, actionName, c => AssemblyService.Ground(c, occurrence, false), cancellationToken),
            AssemblyAction.Replace => runner.RunAsync(tool, actionName, c => AssemblyService.ReplaceWith(c, occurrence,
                ToolRunner.Require(path, "path", "replace"), Snake(source), all, template), cancellationToken, precheck: precheck),
            AssemblyAction.Adjustable => runner.RunAsync(tool, actionName, c => AssemblyService.MakeAdjustable(c, occurrence), cancellationToken),
            AssemblyAction.Properties => runner.RunAsync(tool, actionName, c => AssemblyService.Properties(c, occurrence), cancellationToken),
            AssemblyAction.SetProperty => runner.RunAsync(tool, actionName, c => AssemblyService.SetProperty(c, occurrence, name, value), cancellationToken),
            AssemblyAction.SaveModel => runner.RunAsync(tool, actionName, c => AssemblyService.SaveModel(c, occurrence,
                ToolRunner.Require(path, "path", "save_model"), overwrite), cancellationToken, precheck: precheck),
            AssemblyAction.Show => runner.RunAsync(tool, actionName, c => all
                ? AssemblyService.SetVisibleAll(c, document, occurrence, true)
                : AssemblyService.SetVisible(c, occurrence, true), cancellationToken),
            AssemblyAction.Hide => runner.RunAsync(tool, actionName, c => all
                ? AssemblyService.SetVisibleAll(c, document, occurrence, false)
                : AssemblyService.SetVisible(c, occurrence, false), cancellationToken),
            AssemblyAction.Reorder => runner.RunAsync(tool, actionName, c => AssemblyService.Reorder(c, occurrence, target, before), cancellationToken),
            AssemblyAction.Suppress => runner.RunAsync(tool, actionName, c => AssemblyService.Suppress(c, occurrence), cancellationToken),
            AssemblyAction.Unsuppress => runner.RunAsync(tool, actionName, c => AssemblyService.Unsuppress(c, occurrence), cancellationToken),
            AssemblyAction.Delete => runner.RunAsync(tool, actionName, c => AssemblyService.Delete(c, occurrence), cancellationToken),
            AssemblyAction.Activate => runner.RunAsync(tool, actionName, c => AssemblyService.Activate(c, document, occurrence, true, all), cancellationToken),
            AssemblyAction.Inactivate => runner.RunAsync(tool, actionName, c => AssemblyService.Activate(c, document, occurrence, false, false), cancellationToken),
            AssemblyAction.Bundle => runner.RunAsync(tool, actionName, c => AssemblyService.Bundle(c, occurrences,
                ToolRunner.Require(path, "path", "bundle"), template), cancellationToken, precheck: precheck),
            AssemblyAction.Transfer => runner.RunAsync(tool, actionName, c => AssemblyService.Transfer(c, occurrences, subassembly), cancellationToken),
            AssemblyAction.CopyAssembly => runner.RunAsync(tool, actionName, c => AssemblyService.CopyAssembly(c, document,
                ToolRunner.Require(path, "path", "copy_assembly"), plane), cancellationToken, precheck: precheck),
            AssemblyAction.Disperse => runner.RunAsync(tool, actionName, c => AssemblyService.Disperse(c, occurrence, all), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_relations", Title = "Assembly relations", Destructive = true, OpenWorld = false)]
    [Description("""
        Assembly relations (mates). list (optional 'occurrence') | add | edit (relation: new offset mm or angle degrees, or suppress; the parts follow, gears too) | delete (relation).
        add moves occurrence1 onto occurrence2 (or, without it, an assembly plane 'xy'/'yz'/'xz'), ungrounding it first.
        type: planar (planar faces or reference planes; align false = mate (faces against each other), true = flush; offset mm) |
        axial (cylindrical/conical faces or straight/circular edges; flip reverses the orientation; lockRotation false lets it turn) |
        insert (planar mate element1/element2 + axial axis1/axis2, rotation locked: a pin into a hole) |
        parallel (axes as for axial; offset = distance in mm between them, 0 = free) |
        tangent (element1 = cylindrical face; element2 = planar or cylindrical face, the cylinder outside it; offset mm) |
        angle (planar faces; angle = degrees between outward normals, 0-180; plane = measurement plane, normal to the axis) |
        center_plane (centered = 1 face of occurrence1, or 2 whose mid-plane is centred; between = the 2 parallel faces or planes) |
        connect (point1 = start|end|mid|center of edge element1, onto point2 of edge element2, or onto a planar face, part plane or straight edge) |
        coordinate_system (element1 = coordinate system name in occurrence1's part, onto element2 in occurrence2's part or the assembly) |
        gear (element1 = cylindrical face or circular edge of the driver; element2 = of the driven gear, or a rack's straight edge; ratio [teeth1, teeth2], or ratioBy turns: [turns1, turns2 or rack mm]; same sense unless flip; both free to turn; drive it by editing an angle relation) |
        rigid_set (occurrences = 2+ handles that then move as one).
        Results are checked geometrically; a failed relation is removed and the occurrence put back. Cam and path relations crash Solid Edge 2026 via the API.
        Faces/edges: se_geometry with 'source' = the occurrence (assembly coordinates).
        Example: {"action":"add","type":"gear","element1":"face:31","element2":"face:44","ratio":[40,20]}.
        """)]
    public Task<CallToolResult> Relations(
        RelationAction action,
        RelationType? type = null,
        [Description("add: occurrence to move; optional when element1 is a face/edge handle from se_geometry with source = that occurrence.")] string? occurrence1 = null,
        [Description("add: face, edge or reference plane of occurrence1's part; 'xy'/'yz'/'xz' (or an index) = a base plane of that part.")] string? element1 = null,
        [Description("add: occurrence it is related to; optional when element2 is a face/edge handle read from it; omit it and use 'xy'/'yz'/'xz' or '1'-'3' in element2 for an assembly base plane.")] string? occurrence2 = null,
        [Description("add: face, edge or reference plane of occurrence2's part ('xy'/'yz'/'xz' = its base plane), or without occurrence2 'xy'/'yz'/'xz', '1'-'3' or a handle of an assembly plane.")] string? element2 = null,
        [Description("insert: cylindrical face or circular edge of occurrence1 (its axis).")] string? axis1 = null,
        [Description("insert: cylindrical face or circular edge of occurrence2.")] string? axis2 = null,
        [Description("planar/insert: distance in mm between the faces, along the outward normal of element2; parallel: distance between the axes (0 = free); tangent: gap in mm (default 0). edit: the new offset, as list shows it.")] double? offset = null,
        [Description("planar: true = flush (normals in the same direction); default false = mate (faces against each other).")] bool align = false,
        [Description("axial/insert: reverse the axis orientation; gear: the driven part turns the opposite way.")] bool flip = false,
        [Description("angle: degrees between the outward normals of element1 and element2 (0-180); edit: the new angle of an angle relation.")] double? angle = null,
        [Description("rigid_set: occurrence handles (2 or more).")] string[]? occurrences = null,
        [Description("angle: measurement plane, 'xy'/'yz'/'xz' ('1'-'3') or an assembly plane handle, perpendicular to the rotation axis; required when the faces are parallel.")] string? plane = null,
        [Description("center_plane: 1 face or plane of occurrence1 (it is centred) or 2 (the plane midway between them is centred).")] string[]? centered = null,
        [Description("center_plane: the 2 parallel faces or planes (of other occurrences, or assembly planes 'xy'/'yz'/'xz') it is centred between.")] string[]? between = null,
        [Description("connect: point of edge element1 that is connected.")] EdgePoint? point1 = null,
        [Description("connect: point of edge element2 it meets; omit it to put point1 on element2 (a planar face, plane or straight edge).")] EdgePoint? point2 = null,
        [Description("gear: [teeth of element1, teeth of element2]; with ratioBy turns: [turns of element1, turns of element2 or mm a rack moves].")] double[]? ratio = null,
        [Description("gear: what 'ratio' counts (default teeth; a rack needs turns).")] GearRatio? ratioBy = null,
        [Description("axial/insert: false lets the part turn about the axis (gears need it); default locked, as Solid Edge makes it.")] bool? lockRotation = null,
        [Description("edit, delete: relation handle.")] string? relation = null,
        [Description("edit: true suppresses the relation, false restores it.")] bool? suppress = null,
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
                    offset ?? 0, align, flip, angle, occurrences, plane, centered, between, point1 is { } p1 ? Snake(p1) : null, point2 is { } p2 ? Snake(p2) : null,
                    ratio, ratioBy is { } by ? Snake(by) : null, lockRotation)), cancellationToken),
            RelationAction.Edit => runner.RunAsync(tool, name, c => RelationService.Edit(c, relation, offset, angle, suppress), cancellationToken),
            RelationAction.Delete => runner.RunAsync(tool, name, c => RelationService.Delete(c, relation), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_assembly_pattern", Title = "Component patterns", Destructive = true, OpenWorld = false)]
    [Description("""
        Component patterns that follow a pattern of a part, as the assembly Pattern command does: place the occurrences on the first instance (e.g. a pin in the first hole), then repeat them on every instance of the part pattern. Also duplicate patterns, as the Duplicate command does.
        list | create (occurrences = the ones to repeat; source = the occurrence whose part has the pattern; pattern = its name, optional if the part has one; reference = the feature the occurrences sit on, default the first feature the pattern repeats; name optional) |
        duplicate (occurrences = the ones to copy, placed on source; targets = the occurrences to copy them onto, at the same relative position and orientation, e.g. the bolts of one flange onto the other flanges; name optional) |
        curve (occurrences = the ones to repeat; curve = a line, arc or spline of a closed assembly layout, se_sketch create layout=true; count in all, originals included; spacing mm along the curve from its start, or its end with reverse; the copies are moved, not turned) |
        delete (name: removes the pattern and its copies) | dissolve (name: the copies become ordinary occurrences) |
        clone (Clone Component: occurrences = the ones to clone; faces = the faces they sit on, se_geometry with source = their occurrence, e.g. a hole; targets = the occurrences searched for matching faces: a clone goes on each match; relations ground (default) | none | repair; exact = exact match instead of automatic; group = the clones form a component group).
        A part pattern follows the part: editing it updates the copies. Component mirrors cannot be created through the API.
        Example: {"action":"create","occurrences":["occurrence:5"],"source":"occurrence:3","pattern":"Pattern_1"}.
        """)]
    public Task<CallToolResult> ComponentPattern(
        AssemblyPatternAction action,
        [Description("create, duplicate, curve, clone: occurrence handles to repeat.")] string[]? occurrences = null,
        [Description("create: occurrence whose part has the pattern to follow; duplicate: occurrence the repeated ones are placed on.")] string? source = null,
        [Description("duplicate: occurrences to copy them onto (same relative position as on source); clone: occurrences searched for matching faces.")] string[]? targets = null,
        [Description("create: name of the part pattern (se_feature_tree of the part, e.g. 'Pattern_1').")] string? pattern = null,
        [Description("create: name of the part feature the occurrences sit on (default: the first feature the pattern repeats).")] string? reference = null,
        [Description("create, duplicate, curve: name of the new pattern (default 'Pattern N'); delete/dissolve: the pattern name.")] string? name = null,
        [Description("curve: handle of a line, arc or spline drawn on an assembly layout (se_sketch create layout=true, then close).")] string? curve = null,
        [Description("curve: number of occurrences along the curve, the originals included (at least 2).")] int? count = null,
        [Description("curve: distance in mm between consecutive occurrences, measured along the curve.")] double? spacing = null,
        [Description("curve: true starts from the end of the curve instead of its start.")] bool reverse = false,
        [Description("clone: faces the cloned occurrences sit on (se_geometry 'faces' with source = their occurrence).")] string[]? faces = null,
        [Description("clone: relations of the clones.")] CloneRelation relations = CloneRelation.Ground,
        [Description("clone: exact match of the faces (default: automatic).")] bool exact = false,
        [Description("clone: put the clones in a component group.")] bool group = false,
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
            AssemblyPatternAction.Curve => runner.RunAsync(tool, act, c => AssemblyPatternService.AlongCurve(c, occurrences, curve, count, spacing, reverse, name), cancellationToken),
            AssemblyPatternAction.Delete => runner.RunAsync(tool, act, c => AssemblyPatternService.Delete(c, document, name), cancellationToken),
            AssemblyPatternAction.Dissolve => runner.RunAsync(tool, act, c => AssemblyPatternService.Dissolve(c, document, name), cancellationToken),
            AssemblyPatternAction.Clone => runner.RunAsync(tool, act, c => AssemblyPatternService.Clone(c, occurrences, faces, targets, Snake(relations), exact, group), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_assembly_feature", Title = "Assembly features", Destructive = true, OpenWorld = false)]
    [Description("""
        Assembly features: made in the assembly (the part files do not change), from a sketch drawn in the assembly (se_sketch create with plane '1'-'3' of the assembly, draw, close).
        list (status, the faces left on each part, volumes) | cut (extruded cutout: sketch; occurrences = the parts to cut; depth mm, default through all; side auto (along the plane normal, then against it) | normal | reverse | symmetric) |
        revolve (revolved cutout: sketch and its 'axis' from se_sketch 'axis' before 'close'; angle degrees, default 360; side) |
        hole (plane '1'-'3' or an assembly plane; centers [x,y,z,...] mm; diameter; type simple | countersink | counterbore (counterboreDiameter, counterboreDepth: the hole plus a cutout "<name> counterbore"); extent through_all | finite with depth; direction auto | normal | reverse; tapped: se_hole in the part) |
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
        [Description("hole counterbore: diameter in mm, larger than the hole.")] double? counterboreDiameter = null,
        [Description("hole counterbore: depth in mm from the plane.")] double? counterboreDepth = null,
        SimpleExtent extent = SimpleExtent.ThroughAll,
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
                new HoleSpec(plane ?? "", centers, diameter, Snake(type), counterboreDiameter, counterboreDepth, countersinkDiameter, countersinkAngle,
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

    [McpServerTool(Name = "se_assembly_family", Title = "Alternate positions and families of assemblies", Destructive = true, OpenWorld = false)]
    [Description("""
        Alternate positions and families of assemblies: one assembly file with named members. In alternate positions every member has the same
        components in its own positions (move them with se_assembly or se_relations while the member is active); in a family each member can leave
        components out. Drawings show a member with se_drawing base_view 'member', and the other positions as phantoms with se_drawing_view 'alternate'.
        list (kind none | alternate_positions | family, the active member, the members) |
        create (kind; members = two names: turns the saved assembly into one with two members) | add (name: a new member made from the active one, then active) |
        activate (member) | rename (member, newName; a new member must be saved first) | delete (member: not the active one, two members stay) |
        save_member (member, path = a .asm file: that member as a plain assembly) | exclude (family: member, occurrences left out of it).
        Alternate components (Define Alternate Components, in any assembly): alternates (occurrence: the files it may be swapped for) |
        add_alternate (occurrence, path = a file of the same kind) | remove_alternate (occurrence; path = one of them, default all).
        Example: {"action":"create","kind":"alternate_positions","members":["Closed","Open"]}.
        """)]
    public Task<CallToolResult> AssemblyFamily(
        AssemblyFamilyAction action,
        [Description("create: alternate_positions (default) or family.")] AssemblyFamilyKind kind = AssemblyFamilyKind.AlternatePositions,
        [Description("create: the names of the two first members.")] string[]? members = null,
        [Description("add: the new member's name.")] string? name = null,
        [Description("activate, rename, delete, save_member, exclude: member name.")] string? member = null,
        [Description("rename: the new name.")] string? newName = null,
        [Description("save_member: absolute path of the .asm file to write (allowed folders); add_alternate, remove_alternate: the alternate file.")] string? path = null,
        [Description("exclude: occurrence handles to leave out of the member.")] string[]? occurrences = null,
        [Description("alternates, add_alternate, remove_alternate: occurrence handle.")] string? occurrence = null,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_assembly_family";
        string act = Snake(action);
        return action switch
        {
            AssemblyFamilyAction.List => runner.RunAsync(tool, act, c => AssemblyFamilyService.List(c, document), cancellationToken),
            AssemblyFamilyAction.Create => runner.RunAsync(tool, act, c => AssemblyFamilyService.Create(c, document, Snake(kind), members), cancellationToken),
            AssemblyFamilyAction.Add => runner.RunAsync(tool, act, c => AssemblyFamilyService.Add(c, document, name), cancellationToken),
            AssemblyFamilyAction.Activate => runner.RunAsync(tool, act, c => AssemblyFamilyService.Activate(c, document, member), cancellationToken),
            AssemblyFamilyAction.Rename => runner.RunAsync(tool, act, c => AssemblyFamilyService.Rename(c, document, member, newName), cancellationToken),
            AssemblyFamilyAction.Delete => runner.RunAsync(tool, act, c => AssemblyFamilyService.Delete(c, document, member), cancellationToken),
            AssemblyFamilyAction.SaveMember => runner.RunAsync(tool, act, c => AssemblyFamilyService.SaveMember(c, document, member, path), cancellationToken,
                precheck: path is null ? null : () => guard.Check(path)),
            AssemblyFamilyAction.Exclude => runner.RunAsync(tool, act, c => AssemblyFamilyService.Exclude(c, document, member, occurrences), cancellationToken),
            AssemblyFamilyAction.Alternates => runner.RunAsync(tool, act, c => AssemblyService.Alternates(c, occurrence), cancellationToken),
            AssemblyFamilyAction.AddAlternate => runner.RunAsync(tool, act, c => AssemblyService.AddAlternate(c, occurrence, path), cancellationToken,
                precheck: path is null ? null : () => guard.Check(path)),
            AssemblyFamilyAction.RemoveAlternate => runner.RunAsync(tool, act, c => AssemblyService.RemoveAlternate(c, occurrence, path), cancellationToken),
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
        the explode: close the assembly without saving, or use the Explode environment. Saving the exploded assembly keeps the explode as the explode configuration
        'explode,Solid Edge' (se_drawing base_view takes it for an exploded view); a display configuration does not keep it.
        performance: Large Assembly Performance mode of the assembly, read, or set with enabled = true | false.
        Example: {"action":"save","name":"Without covers"}; {"action":"explode","spread":50}.
        """)]
    public Task<CallToolResult> AssemblyConfig(
        AssemblyConfigAction action,
        [Description("save/apply/delete: configuration name.")] string? name = null,
        [Description("explode: distance between components in mm (default: Solid Edge's minimum clearance).")] double? spread = null,
        ExplodeLevel by = ExplodeLevel.Part,
        [Description("performance: turn the performance mode on or off (omit to read it).")] bool? enabled = null,
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
            AssemblyConfigAction.Performance => runner.RunAsync(tool, act, c => AssemblyService.Performance(c, document, enabled), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_interference", Title = "Interference check", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Checks interference (overlapping material) between occurrences: 'occurrences' among themselves, or against 'against'; with neither, all top-level occurrences included in interference analysis (se_assembly properties); compare others | visible: 'occurrences' against all the rest or the visible ones.
        status: none | confirmed | probable | confirmed_and_probable | incomplete, with the interfering pairs. Nothing is added to the assembly.
        """)]
    public Task<CallToolResult> Interference(
        [Description("Occurrence handles to check (default: all top-level occurrences).")] string[]? occurrences = null,
        [Description("Occurrence handles to check against (default: the first set among itself).")] string[]? against = null,
        [Description("auto (default: 'against', or 'occurrences' among themselves) | others ('occurrences' against every other occurrence) | visible (against the visible ones).")] InterferenceCompare compare = InterferenceCompare.Auto,
        [Description("Assembly document handle; default: the occurrences' assembly or the active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_interference", "check", c => AssemblyService.Interference(c, document, occurrences, against, Snake(compare)), cancellationToken);

    [McpServerTool(Name = "se_bom", Title = "Bill of materials and assembly reports", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Bill of materials and reports of an assembly (the Reports and Assembly Statistics dialogs). structure: top (parts list: top-level items grouped by file; a subassembly is one line) | flat (summary of atomic parts: only parts, quantities multiplied through subassemblies) |
        indented (bill of materials: each level under its subassembly, each part once per subassembly; item numbers 1, 1.1…) | exploded (exploded bill of materials: every occurrence on its own line, with its name) |
        statistics (parts and subassemblies in total and unique, levels of nesting, and the unique files with their number of occurrences).
        Occurrences excluded from the BOM in Solid Edge are left out of the bills; statistics counts every occurrence.
        """)]
    public Task<CallToolResult> Bom(
        BomStructure structure = BomStructure.Top,
        [Description("Assembly document handle; default: active document.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_bom", Snake(structure), c => structure is BomStructure.Top or BomStructure.Flat
            ? AssemblyService.Bom(c, document, Snake(structure))
            : AssemblyService.Report(c, document, Snake(structure)), cancellationToken);
}
