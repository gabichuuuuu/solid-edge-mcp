// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Component groups of an assembly (the groups of PathFinder), kept in the assembly file. Verified in Solid Edge 2026:
/// <c>AssemblyGroups.Add</c> names a group "Assembly Group_N"; with a component that is already in a group it fails with
/// E_FAIL and leaves an empty group behind, and <c>AddToGroup</c> with such a component moves it and nests the target
/// group inside its old group, so components that are already grouped are refused here. <c>Delete</c> removes the group
/// AND its components; <c>UnGroup</c> keeps them. <c>AddPacked</c> groups every ungrouped occurrence of the same file
/// as "&lt;file&gt; (xN)"; <c>Unpack</c> undoes it. <c>Visible</c> (write only) shows or hides the components. A group
/// emptied with <c>RemoveFromGroup</c> stays. Names are unique (a duplicate gives 0x807B003F).
/// </summary>
public static class ComponentGroupService
{
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        List<AssemblyGroup> groups = Groups(assembly);
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            count = groups.Count,
            groups = groups.Select(g => Describe(context, assembly, g, groups)).ToArray(),
        };
    }

    public static object Create(SeContext context, string? documentHandle, string[]? occurrences, string? name)
    {
        Occurrence[] members = Members(context, occurrences, "group");
        AssemblyDocument assembly = documentHandle is null ? members[0].TopLevelDocument : Assembly(context, documentHandle);
        RequireTopLevel(assembly, members);
        List<AssemblyGroup> groups = Groups(assembly);
        string? title = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (title is not null)
        {
            RequireFreeName(groups, title, null);
        }

        RefuseGrouped(groups, members);
        Array components = members.Cast<object>().ToArray();
        AssemblyGroup group;
        try
        {
            group = assembly.AssemblyGroups.Add(members.Length, ref components);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // A failed Add can leave an empty group behind.
            DiscardNew(assembly, groups);
            throw new ToolException("group_failed", $"Solid Edge did not group the components: {ex.Message}",
                "Check that they are top-level components of this assembly and not in a group (se_component_group list).", ex);
        }

        object[] got = Components(group);
        if (assembly.AssemblyGroups.Count != groups.Count + 1 || got.Length != members.Length || members.Any(m => !Contains(got, m)))
        {
            Discard(group);
            throw new ToolException("group_failed", "Solid Edge did not put exactly those components in the new group; it was removed.",
                "Check the components with se_assembly list and se_component_group list.");
        }

        if (title is not null)
        {
            Rename(group, title, () => Discard(group));
        }

        return Describe(context, assembly, group, Groups(assembly));
    }

    public static object Add(SeContext context, string? documentHandle, string? groupName, string[]? occurrences)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, "add");
        Occurrence[] members = Members(context, occurrences, "add");
        RequireTopLevel(assembly, members);
        RefuseGrouped(Groups(assembly), members);
        Array components = members.Cast<object>().ToArray();
        try
        {
            group.AddToGroup(members.Length, ref components);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            TakeOut(group, members);
            throw new ToolException("group_failed", $"Solid Edge did not add the components to '{group.Name}': {ex.Message}",
                "Check them with se_component_group list.", ex);
        }

        if (members.Any(m => !Contains(Components(group), m)))
        {
            TakeOut(group, members);
            throw new ToolException("group_failed", $"Solid Edge did not add the components to '{group.Name}'; those it added were taken out again.",
                "Check them with se_component_group list.");
        }

        return Describe(context, assembly, group, Groups(assembly));
    }

    public static object Remove(SeContext context, string? documentHandle, string? groupName, string[]? occurrences)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, "remove");
        Occurrence[] members = Members(context, occurrences, "remove");
        object[] before = Components(group);
        if (members.FirstOrDefault(m => !Contains(before, m)) is { } stranger)
        {
            throw ToolException.InvalidArgument($"'{stranger.Name}' is not in the group '{group.Name}'.",
                $"Its components: {string.Join(", ", before.Select(NameOf))}.");
        }

        Array components = members.Cast<object>().ToArray();
        try
        {
            group.RemoveFromGroup(members.Length, ref components);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            PutBack(group, members);
            throw new ToolException("group_failed", $"Solid Edge did not remove the components from '{group.Name}': {ex.Message}",
                "Check them with se_component_group list.", ex);
        }

        object[] after = Components(group);
        if (members.Any(m => Contains(after, m)))
        {
            PutBack(group, members);
            throw new ToolException("group_failed", $"Solid Edge did not remove the components from '{group.Name}'; those it removed were put back.",
                "Check them with se_component_group list.");
        }

        Dictionary<string, object?> result = Describe(context, assembly, group, Groups(assembly));
        if (after.Length == 0)
        {
            result["note"] = "The group is empty and stays in the assembly; ungroup removes it.";
        }

        return result;
    }

    public static object Rename(SeContext context, string? documentHandle, string? groupName, string? name)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, "rename");
        string title = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' (the new group name) is required for rename.")
            : name.Trim();
        RequireFreeName(Groups(assembly), title, group);
        Rename(group, title, null);
        return Describe(context, assembly, group, Groups(assembly));
    }

    /// <summary>Shows or hides the components of a group (those of nested groups too); on a failure they are put back as they were.</summary>
    public static object SetVisible(SeContext context, string? documentHandle, string? groupName, bool visible)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, visible ? "show" : "hide");
        List<Occurrence> members = AllOccurrences(group);
        bool[] before = members.Select(m => m.Visible).ToArray();
        try
        {
            group.Visible = visible;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            RestoreVisibility(members, before);
            throw new ToolException("group_failed", $"Solid Edge did not {(visible ? "show" : "hide")} the group '{group.Name}': {ex.Message}",
                "Show or hide its components with se_assembly show/hide.", ex);
        }

        if (members.FirstOrDefault(m => m.Visible != visible) is { } other)
        {
            string state = other.Visible ? "shown" : "hidden";
            RestoreVisibility(members, before);
            throw new ToolException("group_failed", $"Solid Edge left '{other.Name}' {state}; the components were put back as they were.",
                "Show or hide it with se_assembly show/hide.");
        }

        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            group = group.Name,
            visible,
            components = members.Select(m => m.Name).ToArray(),
        };
    }

    /// <summary>Removes the group and keeps its components in the assembly.</summary>
    public static object Ungroup(SeContext context, string? documentHandle, string? groupName)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, "ungroup");
        string title = group.Name;
        string[] names = Components(group).Select(NameOf).ToArray();
        int groups = assembly.AssemblyGroups.Count;
        int occurrences = assembly.Occurrences.Count;
        Call(group.UnGroup, $"ungroup '{title}'");
        if (assembly.AssemblyGroups.Count != groups - 1 || assembly.Occurrences.Count != occurrences)
        {
            throw new ToolException("group_failed", $"Solid Edge did not ungroup '{title}' as expected.",
                "Check the assembly with se_component_group list and se_assembly list.");
        }

        Forget(context, group, "the group was ungrouped");
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), ungrouped = title, components = names };
    }

    /// <summary>Groups every ungrouped occurrence of the same file as the given one (PathFinder's Pack).</summary>
    public static object Pack(SeContext context, string? handle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        RequireTopLevel(assembly, [occurrence]);
        List<AssemblyGroup> groups = Groups(assembly);
        RefuseGrouped(groups, [occurrence]);
        AssemblyGroup group;
        try
        {
            group = assembly.AssemblyGroups.AddPacked(occurrence);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            DiscardNew(assembly, groups);
            throw new ToolException("group_failed", $"Solid Edge did not pack the occurrences of '{occurrence.Name}': {ex.Message}",
                "Check the groups with se_component_group list.", ex);
        }

        if (assembly.AssemblyGroups.Count != groups.Count + 1 || !group.Packed() || !Contains(Components(group), occurrence))
        {
            DiscardNew(assembly, groups);
            throw new ToolException("group_failed", $"Solid Edge did not pack the occurrences of '{occurrence.Name}'; the groups it made were removed.",
                "Check the groups with se_component_group list.");
        }

        return Describe(context, assembly, group, Groups(assembly));
    }

    public static object Unpack(SeContext context, string? documentHandle, string? groupName)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, "unpack");
        if (!group.Packed())
        {
            throw ToolException.InvalidArgument($"'{group.Name}' is not a packed group.", "Use ungroup to remove an ordinary group.");
        }

        string title = group.Name;
        string[] names = Components(group).Select(NameOf).ToArray();
        int groups = assembly.AssemblyGroups.Count;
        Call(group.Unpack, $"unpack '{title}'");
        if (assembly.AssemblyGroups.Count != groups - 1)
        {
            throw new ToolException("group_failed", $"Solid Edge did not unpack '{title}'.", "Check the groups with se_component_group list.");
        }

        Forget(context, group, "the group was unpacked");
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), unpacked = title, components = names };
    }

    /// <summary>Deletes the group together with its components, as Solid Edge does; it needs an explicit confirmation.</summary>
    public static object Delete(SeContext context, string? documentHandle, string? groupName, bool withComponents)
    {
        (AssemblyDocument assembly, AssemblyGroup group) = Group(context, documentHandle, groupName, "delete");
        string title = group.Name;
        List<Occurrence> members = AllOccurrences(group);
        string[] names = members.Select(m => m.Name).ToArray();
        if (!withComponents)
        {
            throw ToolException.InvalidArgument(
                $"Deleting the group '{title}' also deletes its {names.Length} components from the assembly ({string.Join(", ", names)}).",
                "Pass withComponents=true to delete them too, or use ungroup to keep them.");
        }

        List<AssemblyGroup> inner = [group, .. NestedGroups(group)];
        string[] innerNames = inner.Select(g => g.Name).ToArray();
        int groups = assembly.AssemblyGroups.Count;
        int occurrences = assembly.Occurrences.Count;
        COMException? failure = null;
        try
        {
            group.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            failure = ex;
        }

        // Forget what is gone, also when Solid Edge did only part of it.
        HashSet<string> present = Items<Occurrence>(assembly.Occurrences).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> groupsLeft = Groups(assembly).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        int left = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (present.Contains(names[i]))
            {
                left++;
            }
            else
            {
                Forget(context, members[i], "the occurrence was deleted with its group");
            }
        }

        for (int i = 0; i < inner.Count; i++)
        {
            if (!groupsLeft.Contains(innerNames[i]))
            {
                Forget(context, inner[i], "the group was deleted");
            }
        }

        if (left < members.Count || present.Count != occurrences)
        {
            context.ModelChanged((SolidEdgeDocument)assembly, "components were deleted with their group");
        }

        if (failure is not null)
        {
            throw new ToolException("group_failed", $"Solid Edge did not delete '{title}': {failure.Message}",
                "Check the assembly with se_component_group list and se_assembly list.", failure);
        }

        if (assembly.AssemblyGroups.Count >= groups || left > 0)
        {
            throw new ToolException("group_failed", $"Solid Edge did not delete '{title}' with its components ({left} left).",
                "Check the assembly with se_assembly list.");
        }

        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            deleted = title,
            components = names,
            occurrences = assembly.Occurrences.Count,
            removed = occurrences - assembly.Occurrences.Count,
        };
    }

    private static Dictionary<string, object?> Describe(SeContext context, AssemblyDocument assembly, AssemblyGroup group, List<AssemblyGroup> all)
    {
        var document = (SolidEdgeDocument)assembly;
        object[] components = Components(group);
        var result = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(group, "assemblygroup", document),
            ["name"] = group.Name,
            ["packed"] = group.Packed(),
            ["count"] = components.Length,
            ["components"] = components.Select(c => c switch
            {
                Occurrence o => (object)new { handle = context.Register(o, "occurrence", document), name = o.Name },
                AssemblyGroup g => new { handle = context.Register(g, "assemblygroup", document), group = g.Name },
                _ => new { name = NameOf(c) },
            }).ToArray(),
        };
        if (all.FirstOrDefault(other => !Late.SameObject(other, group) && Contains(Components(other), group)) is { } parent)
        {
            result["parent"] = parent.Name;
        }

        return result;
    }

    private static (AssemblyDocument Assembly, AssemblyGroup Group) Group(SeContext context, string? documentHandle, string? group, string action)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            throw ToolException.InvalidArgument($"'group' is required for {action}: a group name or handle (se_component_group list).");
        }

        if (group.StartsWith("assemblygroup:", StringComparison.OrdinalIgnoreCase))
        {
            AssemblyGroup resolved = context.Handles.Resolve<AssemblyGroup>(group, "group");
            return (resolved.Parent as AssemblyDocument ?? Assembly(context, documentHandle), resolved);
        }

        AssemblyDocument assembly = Assembly(context, documentHandle);
        string title = group.Trim();
        return Groups(assembly).FirstOrDefault(g => string.Equals(g.Name, title, StringComparison.OrdinalIgnoreCase)) is { } found
            ? (assembly, found)
            : throw new ToolException("not_found", $"The assembly has no group '{title}'.", "List the groups with se_component_group list.");
    }

    private static Occurrence[] Members(SeContext context, string[]? handles, string action)
    {
        if (handles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"'occurrences' must list the occurrence handles to {action} (se_assembly list).");
        }

        return handles.Distinct(StringComparer.Ordinal).Select(h => Occurrence(context, h, "occurrences")).ToArray();
    }

    private static void RequireTopLevel(AssemblyDocument assembly, Occurrence[] members)
    {
        Occurrence[] top = Items<Occurrence>(assembly.Occurrences).ToArray();
        if (members.FirstOrDefault(m => !top.Any(t => Late.SameObject(t, m))) is { } other)
        {
            throw ToolException.InvalidArgument($"'{other.Name}' is not a top-level component of this assembly.",
                "Group the top-level occurrences listed by se_assembly list; a part inside a subassembly is grouped in that subassembly.");
        }
    }

    private static void RefuseGrouped(List<AssemblyGroup> groups, IEnumerable<Occurrence> members)
    {
        foreach (Occurrence member in members)
        {
            if (groups.FirstOrDefault(g => Contains(Components(g), member)) is { } group)
            {
                throw ToolException.InvalidArgument($"'{member.Name}' is already in the group '{group.Name}'.",
                    $"A component belongs to one group: remove it from '{group.Name}' first (action remove) or ungroup it.");
            }
        }
    }

    private static void RequireFreeName(List<AssemblyGroup> groups, string name, AssemblyGroup? self)
    {
        if (groups.FirstOrDefault(g => (self is null || !Late.SameObject(g, self)) && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            throw ToolException.InvalidArgument($"The assembly already has a group named '{other.Name}'.", "Pass another name.");
        }
    }

    private static void Rename(AssemblyGroup group, string name, Action? undo)
    {
        try
        {
            group.Name = name;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            undo?.Invoke();
            throw new ToolException("group_failed", $"Solid Edge did not accept the group name '{name}': {ex.Message}", "Pass another name.", ex);
        }

        if (!string.Equals(group.Name, name, StringComparison.Ordinal))
        {
            undo?.Invoke();
            throw new ToolException("group_failed", $"Solid Edge kept the name '{group.Name}' instead of '{name}'.", "Pass another name.");
        }
    }

    private static List<AssemblyGroup> Groups(AssemblyDocument assembly) => Items<AssemblyGroup>(assembly.AssemblyGroups).ToList();

    /// <summary>Direct components of a group: occurrences and nested groups.</summary>
    private static object[] Components(AssemblyGroup group)
    {
        int count = group.NumComponents;
        if (count == 0)
        {
            return [];
        }

        Array components = new object[count];
        group.GetComponents(ref components);
        return components.Cast<object?>().OfType<object>().ToArray();
    }

    private static List<Occurrence> AllOccurrences(AssemblyGroup group)
    {
        var result = new List<Occurrence>();
        foreach (object component in Components(group))
        {
            switch (component)
            {
                case Occurrence occurrence:
                    result.Add(occurrence);
                    break;
                case AssemblyGroup nested:
                    result.AddRange(AllOccurrences(nested));
                    break;
            }
        }

        return result;
    }

    /// <summary>The groups nested in a group, at any depth.</summary>
    private static List<AssemblyGroup> NestedGroups(AssemblyGroup group) =>
        Components(group).OfType<AssemblyGroup>().SelectMany(g => NestedGroups(g).Prepend(g)).ToList();

    private static bool Contains(object[] components, object value) => components.Any(c => Late.SameObject(c, value));

    private static string NameOf(object component) => component switch
    {
        Occurrence o => o.Name,
        AssemblyGroup g => g.Name,
        _ => Late.TryGetString(component, "Name") ?? "?",
    };

    /// <summary>Runs a group operation; Solid Edge's refusal becomes group_failed.</summary>
    private static void Call(Action operation, string what)
    {
        try
        {
            operation();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("group_failed", $"Solid Edge did not {what}: {ex.Message}",
                "Check the assembly with se_component_group list and se_assembly list.", ex);
        }
    }

    /// <summary>Removes a group that this server just made, keeping its components (Delete would delete them).</summary>
    private static void Discard(AssemblyGroup group)
    {
        try
        {
            group.UnGroup();
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original failure.
        }
    }

    /// <summary>Removes the groups made since <paramref name="before"/> was read, keeping their components.</summary>
    private static void DiscardNew(AssemblyDocument assembly, List<AssemblyGroup> before)
    {
        try
        {
            foreach (AssemblyGroup group in Groups(assembly).Where(g => !before.Any(old => Late.SameObject(old, g))))
            {
                Discard(group);
            }
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original failure.
        }
    }

    /// <summary>Undoes a partial add: takes the given components that are in the group back out of it.</summary>
    private static void TakeOut(AssemblyGroup group, Occurrence[] members)
    {
        try
        {
            object[] got = Components(group);
            Array added = members.Where(m => Contains(got, m)).Cast<object>().ToArray();
            if (added.Length > 0)
            {
                group.RemoveFromGroup(added.Length, ref added);
            }
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original failure.
        }
    }

    /// <summary>Undoes a partial remove: puts the given components that left the group back in it.</summary>
    private static void PutBack(AssemblyGroup group, Occurrence[] members)
    {
        try
        {
            object[] got = Components(group);
            Array removed = members.Where(m => !Contains(got, m)).Cast<object>().ToArray();
            if (removed.Length > 0)
            {
                group.AddToGroup(removed.Length, ref removed);
            }
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original failure.
        }
    }

    private static void RestoreVisibility(List<Occurrence> members, bool[] before)
    {
        for (int i = 0; i < members.Count; i++)
        {
            try
            {
                if (members[i].Visible != before[i])
                {
                    members[i].Visible = before[i];
                }
            }
            catch (COMException)
            {
                // Best effort: the caller reports the original failure.
            }
        }
    }

    private static void Forget(SeContext context, object value, string reason)
    {
        if (context.Handles.TryGetId(value, out string? id) && id is not null)
        {
            context.Handles.Remove(id, reason);
        }
    }
}
