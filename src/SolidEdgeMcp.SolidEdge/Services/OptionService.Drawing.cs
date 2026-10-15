// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Preferences saved in a drawing (DraftDocument.FilePreferences, the drawing pages of the Options dialog while a draft is
/// active): projection angle, drawing view defaults, thread display, grid, bend notes and the line and drawing view styles
/// that each kind of edge and view takes (listed as DVEdgeStyleName[HiddenEdge], DVStyleNameFromTable[SectionViews]). Also
/// the drawing's own sketching toggles (DraftDocument), each the state of a ribbon button: verified in Solid Edge 2026,
/// Maintain Relationships (10514), Alignment Indicator (11302), Relationship Handles (10210) and Locate Background (11594)
/// toggle the property of the same meaning, and Maintain Alignment Set is the button of the Dimension group.
/// </summary>
public static partial class OptionService
{
    private sealed record DrawingEntry(string Name, PropertyInfo Property, int? Index, string? Styles = null, bool OnDocument = false);

    /// <summary>Sketching toggles of the drawing itself (DraftDocument), read and set like the preferences.</summary>
    private static readonly string[] DrawingToggles =
        ["MaintainRelationships", "DisplayAlignmentIndicator", "DisplayRelationshipHandles", "LocateBackgroundSheetGeometry", "MaintainAlignmentSet"];

    /// <summary>Indexed preferences and the style collection their names come from.</summary>
    private static readonly (string Property, Type Index, string Prefix, string Styles)[] DrawingIndexed =
    [
        ("DVEdgeStyleName", typeof(SolidEdgeDraft.DrawingViewEdgeStyleMappingEdgeType), "seDVEdgeStyleMapping_", "LinearStyles"),
        ("DVStyleNameFromTable", typeof(SolidEdgeDraft.DrawingViewStyleMappingElementType), "seDVStyleMapping_", "DrawingViewStyles"),
    ];

    private static readonly Lazy<List<DrawingEntry>> DrawingCatalog = new(() =>
    {
        Type iface = typeof(SolidEdgeDraft.DraftFilePreferences);
        var entries = ComProperties.Scalars(iface).Select(p => new DrawingEntry(p.Name, p, null)).ToList();
        foreach ((string property, Type index, string prefix, string styles) in DrawingIndexed)
        {
            PropertyInfo info = iface.GetProperty(property) ?? throw new InvalidOperationException($"{property} is not in the interop.");
            entries.AddRange(Enum.GetValues(index).Cast<object>().Select(v =>
                new DrawingEntry($"{property}[{Enum.GetName(index, v)![prefix.Length..]}]", info, Convert.ToInt32(v, CultureInfo.InvariantCulture), styles)));
        }

        Type draft = typeof(SolidEdgeDraft.DraftDocument);
        entries.AddRange(DrawingToggles.Select(name => new DrawingEntry(name,
            draft.GetProperty(name) ?? throw new InvalidOperationException($"{name} is not in the interop."), null, OnDocument: true)));
        return entries;
    });

    private static object DrawingList(SeContext context, string? documentHandle, string? filter, int offset, int limit)
    {
        (SolidEdgeDocument document, object preferences) = DrawingPreferences(context, documentHandle);
        var matches = DrawingCatalog.Value.Where(e => string.IsNullOrWhiteSpace(filter) || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        int start = Math.Max(offset, 0);
        var items = matches.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems)).Select(e => DrawingRead(document, preferences, e)).ToList();
        return new
        {
            scope = "drawing",
            document = context.DocumentHandle(document),
            count = matches.Count,
            offset = start,
            returned = items.Count,
            more = start + items.Count < matches.Count,
            options = items,
        };
    }

    private static object DrawingGet(SeContext context, string? documentHandle, string name)
    {
        (SolidEdgeDocument document, object preferences) = DrawingPreferences(context, documentHandle);
        return DrawingRead(document, preferences, DrawingFind(name));
    }

    private static object DrawingSet(SeContext context, string? documentHandle, string name, JsonElement value)
    {
        (SolidEdgeDocument document, object preferences) = DrawingPreferences(context, documentHandle);
        DrawingEntry entry = DrawingFind(name);
        string owner = $"the drawing '{SeContext.SafeName(document)}'";
        object? previous;
        object? current;
        if (entry.Index is not { } index)
        {
            (previous, current) = ComProperties.Set(context, entry.OnDocument ? document : preferences, entry.Property, value, owner);
        }
        else
        {
            string given = value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text.Trim()
                : throw ToolException.InvalidArgument($"'{entry.Name}' takes the name of a style (se_styles list).");

            // Verified in Solid Edge 2026: an unknown name is not refused but replaced by another style, so it is checked first.
            string[] names = Late.TryGet(document, entry.Styles!) is { } collection
                ? [.. Late.Items(collection).Select(s => Late.TryGetString(s, "Name")).OfType<string>()]
                : [];
            string style = names.Length == 0
                ? given
                : names.FirstOrDefault(n => string.Equals(n, given, StringComparison.OrdinalIgnoreCase))
                    ?? throw ToolException.NotFound($"The drawing has no {(entry.Styles == "LinearStyles" ? "line" : "drawing view")} style '{given}'.{NameMatch.Hint(names, given)}",
                        "List them with se_styles action 'list'.");
            previous = Late.TryGet(preferences, entry.Property.Name, index);
            try
            {
                preferences.GetType().InvokeMember(entry.Property.Name, BindingFlags.SetProperty, null, preferences, [index, style], CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                throw new ToolException("value_rejected", $"Solid Edge rejected '{style}' for '{entry.Name}': {ComErrors.Unwrap(ex).Message.Trim()}",
                    "Use the name of a line style (DVEdgeStyleName) or drawing view style (DVStyleNameFromTable) of the drawing (se_styles list).", ex);
            }

            current = Late.TryGet(preferences, entry.Property.Name, index);
            if (!string.Equals(current as string, style, StringComparison.OrdinalIgnoreCase))
            {
                if (previous is string before)
                {
                    preferences.GetType().InvokeMember(entry.Property.Name, BindingFlags.SetProperty, null, preferences, [index, before], CultureInfo.InvariantCulture);
                }

                throw new ToolException("value_rejected", $"Solid Edge set '{entry.Name}' to '{current}' instead of '{style}'; it was set back to '{previous}'.",
                    "Use the name of a style the drawing has (se_styles list).");
            }
        }

        return new
        {
            scope = "drawing",
            document = context.DocumentHandle(document),
            name = entry.Name,
            previous,
            value = current,
            restore = $"{(entry.OnDocument ? "Set on this drawing" : "Saved with the drawing")}. To undo, set it back to {JsonSerializer.Serialize(previous)}.",
        };
    }

    private static (SolidEdgeDocument Document, object Preferences) DrawingPreferences(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        return document is SolidEdgeDraft.DraftDocument draft
            ? (document, draft.FilePreferences)
            : throw new ToolException("wrong_document_type", $"'{SeContext.SafeName(document)}' is not a drawing.",
                "Scope 'drawing' is for draft documents; use scope 'document' for the others.");
    }

    private static DrawingEntry DrawingFind(string name)
    {
        string wanted = (name ?? "").Trim();
        return DrawingCatalog.Value.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"There is no drawing option named '{name}'.{NameMatch.Hint(DrawingCatalog.Value.Select(e => e.Name), wanted)}",
                "Search with se_options action 'list', scope 'drawing' and 'filter'.");
    }

    private static object DrawingRead(object document, object preferences, DrawingEntry entry)
    {
        try
        {
            object? value = entry.Index is { } index
                ? Late.Get(preferences, entry.Property.Name, index)
                : ComProperties.Read(entry.OnDocument ? document : preferences, entry.Property);
            return new { name = entry.Name, value, error = (string?)null };
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            return new { name = entry.Name, value = (object?)null, error = (string?)ComErrors.Unwrap(ex).Message.Trim() };
        }
    }
}
