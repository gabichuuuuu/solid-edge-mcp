// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Document properties by sets (SummaryInformation, ProjectInformation, Custom...).
/// Changes stay in the open document: it must be saved to keep them.
/// </summary>
public static class PropertyService
{
    public const string CustomSet = "Custom";

    public static object List(SeContext context, string? documentHandle, string? set)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        var sets = (PropertySets)document.Properties;
        var result = new List<object>();
        int budget = context.MaxListItems;
        for (int i = 1; i <= sets.Count; i++)
        {
            Properties properties = sets.Item(i);
            if (set is not null && !string.Equals(properties.Name, set, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int j = 1; j <= properties.Count && budget > 0; j++, budget--)
            {
                Property property = properties.Item(j);
                // Some sets have unnamed properties: they are identified by their position.
                values[string.IsNullOrEmpty(property.Name) ? $"#{j}" : property.Name] = ValueOf(property);
            }

            result.Add(new { set = properties.Name, count = properties.Count, properties = values });
        }

        if (set is not null && result.Count == 0)
        {
            throw ToolException.NotFound($"There is no property set named '{set}'.", "Omit 'set' to list all sets.");
        }

        return new { document = context.DocumentHandle(document), sets = result, truncated = budget <= 0 ? true : (bool?)null };
    }

    /// <summary>Changes an existing property; in the Custom set it creates it if it does not exist.</summary>
    public static object Set(SeContext context, string? documentHandle, string? set, string name, JsonElement value)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        // Without a set, the name of a standard property (Title, Subject, Comments, Project Name...) goes to the set that holds
        // it, as in File > Properties; any other name is a custom property. Found in an end-to-end run: 'Title' without a set
        // had made a custom property and left the document title empty.
        (Properties Set, Property Property)? standard = string.IsNullOrWhiteSpace(set) ? StandardProperty(document, name) : null;
        Properties properties = standard?.Set ?? FindSet(document, string.IsNullOrWhiteSpace(set) ? CustomSet : set);
        object converted = ConvertValue(value);
        Property? existing = standard?.Property ?? Find(properties, name);
        bool created = false;
        if (existing is not null)
        {
            // Verified in Solid Edge 2026: 'Last Print Date' written in an open document disappears from its
            // SummaryInformation set, and saving the document then failed with RPC_E_SERVERFAULT. The dates of that set
            // (Total Editing Time, Last Print Date, Origination Date, Last Save Date: ids 10 to 13) are kept by Solid Edge;
            // the file property library writes them in the closed file without harm.
            if (string.Equals(properties.Name, "SummaryInformation", StringComparison.OrdinalIgnoreCase) && (IdOf(existing) is >= 10 and <= 13 || (IdOf(existing) is null && KeptDates.Contains(existing.Name, StringComparer.OrdinalIgnoreCase))))
            {
                throw new ToolException("not_supported", $"Solid Edge keeps '{existing.Name}' itself: written in an open document it is removed from the document.",
                    "Close the document and write it to the file: se_properties set with 'path'.");
            }

            converted = ForProperty(converted, IsDate(existing), existing.Name);
            existing.set_Value(ref converted);
        }
        else if (string.Equals(properties.Name, CustomSet, StringComparison.OrdinalIgnoreCase))
        {
            existing = properties.Add(name, converted);
            created = true;
        }
        else
        {
            throw ToolException.NotFound($"'{name}' does not exist in the '{properties.Name}' set.",
                $"Only the '{CustomSet}' set accepts new properties; list the existing ones with action 'list'.");
        }

        return new
        {
            set = properties.Name,
            name = existing.Name,
            value = ValueOf(existing),
            created,
            note = "Save the document (se_documents 'save') to keep the change.",
        };
    }

    public static object Delete(SeContext context, string? documentHandle, string name)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        Properties properties = FindSet(document, CustomSet);
        Property property = Find(properties, name) ?? throw ToolException.NotFound($"There is no custom property '{name}'.");
        property.Delete();
        return new { deleted = name, set = CustomSet, note = "Save the document to keep the change." };
    }

    /// <summary>The property of that name in a set other than the custom one, if any.</summary>
    private static (Properties Set, Property Property)? StandardProperty(SolidEdgeDocument document, string name)
    {
        var sets = (PropertySets)document.Properties;
        for (int i = 1; i <= sets.Count; i++)
        {
            var candidate = (Properties)sets.Item(i);
            if (!string.Equals(candidate.Name, CustomSet, StringComparison.OrdinalIgnoreCase) && Find(candidate, name) is { } property)
            {
                return (candidate, property);
            }
        }

        return null;
    }

    private static Properties FindSet(SolidEdgeDocument document, string name)
    {
        var sets = (PropertySets)document.Properties;
        for (int i = 1; i <= sets.Count; i++)
        {
            Properties properties = sets.Item(i);
            if (string.Equals(properties.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return properties;
            }
        }

        throw ToolException.NotFound($"There is no property set named '{name}'.", "List the sets with action 'list'.");
    }

    private static Property? Find(Properties properties, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' is required.");
        }

        for (int i = 1; i <= properties.Count; i++)
        {
            Property property = properties.Item(i);
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private static object? ValueOf(Property property) => Normalize(RawValue(property));

    /// <summary>VT_FILETIME, the type of the date properties (Property.Type; verified for the dates of both summary sets).</summary>
    private const int FileTimeType = 64;

    private static readonly string[] KeptDates = ["Total Editing Time", "Last Print Date", "Origination Date", "Last Save Date"];

    private static bool IsDate(Property property)
    {
        try
        {
            return Convert.ToInt32(property.Type, CultureInfo.InvariantCulture) == FileTimeType || RawValue(property) is DateTime;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException)
        {
            return RawValue(property) is DateTime;
        }
    }

    private static int? IdOf(Property property)
    {
        try
        {
            return Convert.ToInt32(property.Id(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    private static object? RawValue(Property property)
    {
        try
        {
            return property.get_Value();
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>A property value as the tools return it: dates in ISO 8601, numbers and texts as they are.</summary>
    internal static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        string or bool or int or double or float or short or long or decimal => value,
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The value to write to a property (<paramref name="isDate"/> when it holds a date): a text for a date property is
    /// read as a date (ISO 8601, "2026-11-20" or "2026-11-20T10:30"), which the property takes and a text is not.
    /// </summary>
    internal static object ForProperty(object converted, bool isDate, string name) =>
        isDate && converted is string text
            ? DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime date)
                ? DateTime.SpecifyKind(date, DateTimeKind.Unspecified)
                : throw ToolException.InvalidArgument($"'{name}' is a date: '{text}' is not one.", "Give it in ISO 8601, e.g. 2026-11-20 or 2026-11-20T10:30.")
            : converted;

    internal static object ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out int integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        _ => throw ToolException.InvalidArgument("'value' must be a string, number or boolean."),
    };
}
