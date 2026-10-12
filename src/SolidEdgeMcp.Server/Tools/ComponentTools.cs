using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum ComponentGroupAction
{
    List,
    Create,
    Add,
    Remove,
    Rename,
    Show,
    Hide,
    Ungroup,
    Pack,
    Unpack,
    Delete,
}

public enum ComponentQueryAction
{
    Find,
    Zone,
    Run,
    List,
    Rename,
    Delete,
}

public enum ComponentProperty
{
    Name,
    Title,
    Subject,
    Author,
    Manager,
    Company,
    Category,
    Keywords,
    Comments,
    DocumentNumber,
    Revision,
    Project,
    Material,
    Custom,
}

public enum ComponentCondition
{
    Contains,
    Is,
    IsNot,
}

public enum ComponentScope
{
    All,
    Shown,
    Hidden,
    Selected,
}

public enum ComponentDisplay
{
    Select,
    ShowOnly,
    Hide,
    Show,
}

/// <summary>One criterion of a component query.</summary>
public sealed record ComponentCriterionArgument(
    [property: JsonRequired, Description("name (occurrence name, e.g. 'bolt.par:2') or a property of the component file: title, subject, author, manager, company, category, keywords, comments, document_number, revision, project, material, custom.")]
    ComponentProperty Property,
    [property: JsonRequired, Description("Text to compare.")] string Value,
    [property: Description("contains (default; ignores case) | is | is_not.")] ComponentCondition Condition = ComponentCondition.Contains,
    [property: Description("custom: the custom property name, e.g. 'Supplier'.")] string? Custom = null);

/// <summary>Component groups, queries and zones of an assembly (the PathFinder groups and Select Tools).</summary>
[McpServerToolType]
public sealed class ComponentTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_component_group", Title = "Component groups", Destructive = true, OpenWorld = false)]
    [Description("""
        Component groups of an assembly (the groups of PathFinder), saved with the assembly.
        list (groups with their components, nested groups and packed state) | create (occurrences; name optional, default 'Assembly Group_N') |
        add (group, occurrences) | remove (group, occurrences; an emptied group stays until ungrouped) | rename (group, name) |
        show | hide (group: shows or hides its components) | ungroup (group: removes the group and keeps the components) |
        pack (occurrence: groups every ungrouped occurrence of the same file as '<file> (xN)') | unpack (a group made by pack) |
        delete (group, withComponents=true: Solid Edge deletes the group AND its components; ungroup keeps them).
        A component belongs to one group: components already in a group are refused (remove them or ungroup first).
        group: the group name or its handle from list. occurrences: top-level occurrence handles from se_assembly list.
        Example: {"action":"create","occurrences":["occurrence:2","occurrence:3"],"name":"Fasteners"}; {"action":"hide","group":"Fasteners"}.
        """)]
    public Task<CallToolResult> ComponentGroup(
        ComponentGroupAction action,
        [Description("Group name or handle (add, remove, rename, show, hide, ungroup, unpack, delete).")] string? group = null,
        [Description("create, add, remove: top-level occurrence handles.")] string[]? occurrences = null,
        [Description("pack: an occurrence of the file whose occurrences are packed.")] string? occurrence = null,
        [Description("create: name of the new group (optional); rename: the new name.")] string? name = null,
        [Description("delete: true to confirm that the components are deleted with the group.")] bool withComponents = false,
        [Description("Assembly document handle; default: the occurrences' assembly or the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_component_group";
        string act = Snake(action);
        return action switch
        {
            ComponentGroupAction.List => runner.RunAsync(tool, act, c => ComponentGroupService.List(c, document), cancellationToken),
            ComponentGroupAction.Create => runner.RunAsync(tool, act, c => ComponentGroupService.Create(c, document, occurrences, name), cancellationToken),
            ComponentGroupAction.Add => runner.RunAsync(tool, act, c => ComponentGroupService.Add(c, document, group, occurrences), cancellationToken),
            ComponentGroupAction.Remove => runner.RunAsync(tool, act, c => ComponentGroupService.Remove(c, document, group, occurrences), cancellationToken),
            ComponentGroupAction.Rename => runner.RunAsync(tool, act, c => ComponentGroupService.Rename(c, document, group, name), cancellationToken),
            ComponentGroupAction.Show => runner.RunAsync(tool, act, c => ComponentGroupService.SetVisible(c, document, group, true), cancellationToken),
            ComponentGroupAction.Hide => runner.RunAsync(tool, act, c => ComponentGroupService.SetVisible(c, document, group, false), cancellationToken),
            ComponentGroupAction.Ungroup => runner.RunAsync(tool, act, c => ComponentGroupService.Ungroup(c, document, group), cancellationToken),
            ComponentGroupAction.Pack => runner.RunAsync(tool, act, c => ComponentGroupService.Pack(c, occurrence), cancellationToken),
            ComponentGroupAction.Unpack => runner.RunAsync(tool, act, c => ComponentGroupService.Unpack(c, document, group), cancellationToken),
            ComponentGroupAction.Delete => runner.RunAsync(tool, act, c => ComponentGroupService.Delete(c, document, group, withComponents), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [McpServerTool(Name = "se_component_query", Title = "Component queries and zones", Destructive = true, OpenWorld = false)]
    [Description("""
        Finds the components of an assembly as the Select Tools of PathFinder do; saved queries and zones are kept in the assembly.
        find (criteria, all of which must hold; scope all | shown | hidden | selected; subassemblies: also the parts inside subassemblies, default true) |
        zone (box [x1,y1,z1, x2,y2,z2] mm: the components inside that region; overlap: also those partly inside) |
        run (name: a saved query or zone) | list (saved queries and zones) | rename (name, newName) | delete (name).
        find and zone with 'name' save the query or zone in the assembly (kept when it is saved); without it nothing is kept.
        The matches are left selected (se_selection get) and listed: top-level components with their occurrence handle, parts
        inside subassemblies with their path ('frame.asm:1/bolt.par:2') and the top-level occurrence that holds them.
        display: select (default) | show_only (the matches, a matched subassembly whole; hides every other component, also inside
        subassemblies) | hide | show; it lists the components whose visibility changed and undoes them if Solid Edge refuses one.
        Criterion: {"property": name (the occurrence name) | title | subject | author | manager | company | category | keywords | comments |
        document_number | revision | project | material | custom (with "custom": the custom property name), "condition": contains (default, ignores case) | is | is_not, "value": text}.
        The file properties are those of each component's file (se_properties, se_material).
        Example: {"action":"find","criteria":[{"property":"material","condition":"is","value":"Steel"}],"display":"show_only"};
        {"action":"zone","box":[0,-50,0,200,50,100],"overlap":true,"name":"Front"}.
        """)]
    public Task<CallToolResult> ComponentQuery(
        ComponentQueryAction action,
        [Description("find: the criteria (objects with property, condition, value and custom).")] ComponentCriterionArgument[]? criteria = null,
        ComponentScope scope = ComponentScope.All,
        [Description("find: also search the parts inside subassemblies (default true).")] bool subassemblies = true,
        [Description("zone: two opposite corners [x1,y1,z1, x2,y2,z2] in mm, assembly coordinates.")] double[]? box = null,
        [Description("zone: also the components partly inside the box (default false: only those fully inside).")] bool overlap = false,
        [Description("find/zone: save under this name; run, rename, delete: the saved query or zone.")] string? name = null,
        [Description("rename: the new name.")] string? newName = null,
        ComponentDisplay display = ComponentDisplay.Select,
        [Description("Maximum number of matches listed (default 100).")] int limit = 100,
        [Description("Assembly document handle; default: the active document.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_component_query";
        string act = Snake(action);
        string shown = Snake(display);
        return action switch
        {
            ComponentQueryAction.Find => runner.RunAsync(tool, act, c => ComponentQueryService.Find(c, document,
                criteria?.Select(k => new ComponentCriterion(Snake(k.Property), Snake(k.Condition), k.Value, k.Custom)).ToArray(),
                Snake(scope), subassemblies, name, shown, limit), cancellationToken),
            ComponentQueryAction.Zone => runner.RunAsync(tool, act, c => ComponentQueryService.Zone(c, document, box, overlap, name, shown, limit), cancellationToken),
            ComponentQueryAction.Run => runner.RunAsync(tool, act, c => ComponentQueryService.Run(c, document, name, shown, limit), cancellationToken),
            ComponentQueryAction.List => runner.RunAsync(tool, act, c => ComponentQueryService.List(c, document), cancellationToken),
            ComponentQueryAction.Rename => runner.RunAsync(tool, act, c => ComponentQueryService.Rename(c, document, name, newName), cancellationToken),
            ComponentQueryAction.Delete => runner.RunAsync(tool, act, c => ComponentQueryService.Delete(c, document, name), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
