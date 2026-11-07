// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Reflection;
using System.Text.Json;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Occurrence Properties (General tab) and Show/Hide Component of an occurrence. Verified in Solid Edge 2026: each one is set
/// and read back; include_in_physical_properties false leaves the occurrence out of the physical properties of the assembly,
/// include_in_bom false and quantity change the bill of materials, reference true also takes it out of the bill of materials
/// (include_in_bom reads false while it is a reference) but not out of the physical properties, and name renames the
/// occurrence. ItemNumber gives E_FAIL, and NongraphicQuantity E_FAIL while HasNongraphicQuantity is false: not offered.
/// </summary>
public static partial class AssemblyService
{
    private static readonly (string Name, string Member)[] OccurrenceProperties =
    [
        ("name", "Name"),
        ("quantity", "Quantity"),
        ("include_in_bom", "IncludeInBom"),
        ("include_in_physical_properties", "IncludeInPhysicalProperties"),
        ("include_in_interference", "IncludeInInterference"),
        ("reference", "ReferenceOnly"),
        ("display_as_reference", "DisplayAsReference"),
        ("display_in_drawings", "DisplayInDrawings"),
        ("display_in_subassembly", "DisplayInSubAssembly"),
        ("locatable", "Locatable"),
        ("use_simplified", "UseSimplified"),
        ("show_sketches", "DisplaySketches"),
        ("show_reference_planes", "DisplayReferencePlanes"),
        ("show_reference_axes", "DisplayReferenceAxes"),
        ("show_coordinate_systems", "DisplayCoordinateSystems"),
        ("show_constructions", "DisplayConstructions"),
        ("show_construction_curves", "DisplayConstrCurves"),
        ("show_dimensions", "DisplayDimensions"),
        ("show_annotations", "DisplayAnnotations"),
        ("show_centerlines", "DisplayCenterline"),
        ("show_design_body", "DisplayDesignBody"),
        ("show_live_sections", "DisplayLiveSections"),
    ];

    /// <summary>The occurrence properties of an occurrence; those Solid Edge does not give are left out.</summary>
    public static object Properties(SeContext context, string? handle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        return new
        {
            occurrence = context.Register(occurrence, "occurrence", (SolidEdgeDocument)occurrence.TopLevelDocument),
            file = occurrence.OccurrenceFileName,
            properties = ReadProperties(occurrence),
        };
    }

    /// <summary>Sets one occurrence property, reads it back and reports the others it changed (reference changes include_in_bom).</summary>
    public static object SetProperty(SeContext context, string? handle, string? name, JsonElement? value)
    {
        Occurrence occurrence = Occurrence(context, handle);
        string key = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("set_property needs 'name': an occurrence property listed by action 'properties'.")
            : name.Trim().ToLowerInvariant();
        (string Name, string Member) entry = OccurrenceProperties.FirstOrDefault(p => p.Name == key);
        if (entry.Name is null)
        {
            throw ToolException.NotFound($"An occurrence has no property '{name}'.{NameMatch.Hint(OccurrenceProperties.Select(p => p.Name), key)}",
                "List them with action 'properties'.");
        }

        JsonElement given = value ?? throw ToolException.InvalidArgument("set_property needs 'value'.");
        if (entry.Name == "quantity" && !(given.ValueKind == JsonValueKind.Number && given.TryGetInt32(out int quantity) && quantity >= 1))
        {
            throw ToolException.InvalidArgument("'quantity' takes a whole number from 1.");
        }

        if (entry.Name == "name" && (given.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(given.GetString())))
        {
            throw ToolException.InvalidArgument("'name' takes the new occurrence name as text.");
        }

        string owner = $"the occurrence '{occurrence.Name}'";
        PropertyInfo property = ComProperties.Find(typeof(Occurrence), entry.Member, owner);
        Dictionary<string, object?> before = ReadProperties(occurrence);
        (object? previous, object? current) = ComProperties.Set(context, occurrence, property, given, owner);
        Dictionary<string, object?> after = ReadProperties(occurrence);
        Dictionary<string, object?> alsoChanged = after
            .Where(p => p.Key != entry.Name && (!before.TryGetValue(p.Key, out object? old) || !Equals(old, p.Value)))
            .ToDictionary(p => p.Key, p => p.Value);
        return new
        {
            occurrence = context.Register(occurrence, "occurrence", (SolidEdgeDocument)occurrence.TopLevelDocument),
            property = entry.Name,
            previous,
            value = current,
            alsoChanged = alsoChanged.Count > 0 ? alsoChanged : null,
            restore = $"Saved with the assembly. To undo, set it back to {JsonSerializer.Serialize(previous)}.",
        };
    }

    /// <summary>
    /// Shows or hides every top-level occurrence or, with <paramref name="only"/>, shows that one and hides the others (Show Only
    /// in PathFinder); each change is read back.
    /// </summary>
    public static object SetVisibleAll(SeContext context, string? documentHandle, string? only, bool visible)
    {
        Occurrence? keep = string.IsNullOrWhiteSpace(only) ? null : Occurrence(context, only);
        if (keep is not null && !visible)
        {
            throw ToolException.InvalidArgument("hide with 'all' hides every occurrence: leave out 'occurrence'.",
                "To show one occurrence alone, use show with 'occurrence' and 'all' (Show Only).");
        }

        AssemblyDocument assembly = keep?.TopLevelDocument ?? Assembly(context, documentHandle);
        int changed = 0;
        var refused = new List<string>();
        foreach (Occurrence occurrence in Items<Occurrence>(assembly.Occurrences))
        {
            bool wanted = keep is null ? visible : occurrence.OccurrenceID == keep.OccurrenceID;
            if (occurrence.Visible != wanted)
            {
                occurrence.Visible = wanted;
                changed++;
                if (occurrence.Visible != wanted)
                {
                    refused.Add(occurrence.Name);
                }
            }
        }

        if (refused.Count > 0)
        {
            throw new ToolException("visibility_not_applied", $"Solid Edge kept the visibility of {string.Join(", ", refused)}.",
                "Check that those occurrences are loaded (se_assembly list: status).");
        }

        List<Occurrence> all = [.. Items<Occurrence>(assembly.Occurrences)];
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            shown = keep is null ? (visible ? all.Count : 0) : 1,
            hidden = keep is null ? (visible ? 0 : all.Count) : all.Count - 1,
            changed,
            only = keep?.Name,
        };
    }

    /// <summary>
    /// Moves an occurrence next to another in the order of the assembly (dragged in PathFinder). Verified in Solid Edge 2026:
    /// Occurrences.ReorderOccurrence places it after the target with AfterTarget false and before it with true, the opposite
    /// of what the argument's name says; the order is read back.
    /// </summary>
    public static object Reorder(SeContext context, string? handle, string? targetHandle, bool before)
    {
        Occurrence occurrence = Occurrence(context, handle);
        Occurrence target = Occurrence(context, string.IsNullOrWhiteSpace(targetHandle)
            ? throw ToolException.InvalidArgument("reorder needs 'target': the occurrence to place it after (or before, with 'before').")
            : targetHandle, "target");
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        if (!Late.SameObject(assembly, target.TopLevelDocument) || occurrence.OccurrenceID == target.OccurrenceID)
        {
            throw ToolException.InvalidArgument("'target' must be another occurrence of the same assembly.");
        }

        assembly.Occurrences.ReorderOccurrence(occurrence, target, before);
        List<string> order = [.. Items<Occurrence>(assembly.Occurrences).Select(o => o.Name)];
        int at = order.IndexOf(occurrence.Name);
        int next = order.IndexOf(target.Name);
        if (at != (before ? next - 1 : next + 1))
        {
            throw new ToolException("reorder_failed", $"Solid Edge did not place '{occurrence.Name}' {(before ? "before" : "after")} '{target.Name}'.",
                "List the order with se_assembly list and try another target.");
        }

        return new { moved = occurrence.Name, before = before ? target.Name : null, after = before ? null : target.Name, order };
    }

    private static Dictionary<string, object?> ReadProperties(Occurrence occurrence)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string name, string member) in OccurrenceProperties)
        {
            if (ComProperties.TryRead(occurrence, ComProperties.Find(typeof(Occurrence), member, "an occurrence")) is { } value)
            {
                values[name] = value;
            }
        }

        return values;
    }
}
