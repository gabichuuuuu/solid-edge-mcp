using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Propiedades del documento por conjuntos (SummaryInformation, ProjectInformation, Custom...).
/// Los cambios quedan en el documento abierto: hay que guardarlo para conservarlos.
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
                // Algunos conjuntos tienen propiedades sin nombre: se identifican por su posición.
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

    /// <summary>Cambia una propiedad existente; en el conjunto Custom la crea si no existe.</summary>
    public static object Set(SeContext context, string? documentHandle, string? set, string name, JsonElement value)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        string setName = string.IsNullOrWhiteSpace(set) ? CustomSet : set;
        Properties properties = FindSet(document, setName);
        object converted = Convert(value);
        Property? existing = Find(properties, name);
        bool created = false;
        if (existing is not null)
        {
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

    private static object? ValueOf(Property property)
    {
        object? value;
        try
        {
            value = property.get_Value();
        }
        catch (COMException)
        {
            return null;
        }

        return value switch
        {
            null or DBNull => null,
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            string or bool or int or double or float or short or long or decimal => value,
            _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static object Convert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out int integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        _ => throw ToolException.InvalidArgument("'value' must be a string, number or boolean."),
    };
}
