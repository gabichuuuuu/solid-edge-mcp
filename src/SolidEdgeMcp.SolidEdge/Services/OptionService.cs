using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Opciones de Solid Edge (cuadro «Opciones de Solid Edge» y ajustes globales de cada tipo de documento) por
/// Get/SetGlobalParameter, y unidades del documento (UnitsOfMeasure). Las opciones de aplicación son del usuario y
/// persisten entre sesiones: cada cambio devuelve el valor anterior para poder restaurarlo.
/// </summary>
public static class OptionService
{
    public static readonly IReadOnlyList<string> Scopes = ["application", "document"];

    /// <summary>Enumeración de parámetros del ámbito, objeto que los atiende y prefijos que se quitan del nombre.</summary>
    private sealed record Catalog(Type Enum, object Target, string Owner, string[] Prefixes);

    public static object List(SeContext context, string scope, string? documentHandle, string? filter, int offset, int limit)
    {
        Catalog catalog = CatalogOf(context, scope, documentHandle);
        var matches = Entries(catalog)
            .Where(e => string.IsNullOrWhiteSpace(filter) || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = matches.Skip(start).Take(take).Select(e => Read(catalog, e.Name, e.Value)).ToList();
        return new
        {
            scope = catalog.Owner,
            count = matches.Count,
            offset = start,
            returned = items.Count,
            more = start + items.Count < matches.Count,
            options = items,
        };
    }

    public static object Get(SeContext context, string scope, string? documentHandle, string name)
    {
        Catalog catalog = CatalogOf(context, scope, documentHandle);
        (string Name, int Value) entry = Find(catalog, name);
        return Read(catalog, entry.Name, entry.Value);
    }

    /// <summary>Cambia una opción con el tipo de su valor actual y comprueba que Solid Edge lo ha aceptado.</summary>
    public static object Set(SeContext context, string scope, string? documentHandle, string name, JsonElement value)
    {
        Catalog catalog = CatalogOf(context, scope, documentHandle);
        (string Name, int Value) entry = Find(catalog, name);
        object? previous = ReadRaw(catalog, entry.Value, out string? readError);
        if (readError is not null)
        {
            throw new ToolException("option_failed", $"Solid Edge cannot read the option '{entry.Name}': {readError}",
                "Only options that can be read can be changed safely; use se_com_invoke on SetGlobalParameter if you must.");
        }

        object converted = ConvertValue(value, previous, IsColor(entry.Name));
        try
        {
            Late.Call(catalog.Target, "SetGlobalParameter", entry.Value, converted);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw new ToolException("option_failed", $"Solid Edge rejected the value for '{entry.Name}': {ComErrors.Unwrap(ex).Message}",
                "Check the type and range of the current value (se_options get).", ex);
        }

        object? current = ReadRaw(catalog, entry.Value, out _);
        bool applied = SameValue(current, converted);
        if (!applied && SameValue(current, previous) && !SameValue(previous, converted))
        {
            throw new ToolException("option_not_applied",
                $"Solid Edge kept '{entry.Name}' at {Describe(previous)} instead of {Describe(converted)}.",
                "The value may be out of range or the option may be read-only.");
        }

        return new
        {
            scope = catalog.Owner,
            name = entry.Name,
            previous = Format(previous),
            value = Format(current),
            color = IsColor(entry.Name) ? Hex(current) : null,
            note = applied ? null : "Solid Edge adjusted the value (for example to its allowed range).",
            restore = $"To undo, set it back to {Describe(previous)}.",
        };
    }

    /// <summary>Unidades del documento: tipo, unidad de lectura y precisión.</summary>
    public static object Units(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        var items = new List<object>();
        foreach (UnitOfMeasure unit in UnitsOf(document))
        {
            items.Add(DescribeUnit(unit));
        }

        return new
        {
            document = context.DocumentHandle(document),
            units = items,
            note = "Display units only: the se_* tools always take and return mm, degrees and kg.",
        };
    }

    /// <summary>Cambia la unidad de lectura y la precisión de un tipo de unidad del documento.</summary>
    public static object SetUnits(SeContext context, string? documentHandle, string type, string? unit, int? precision)
    {
        if (unit is null && precision is null)
        {
            throw ToolException.InvalidArgument("Action 'set_units' requires 'unit' and/or 'precision'.");
        }

        SolidEdgeDocument document = context.GetDocument(documentHandle);
        UnitOfMeasure target = UnitsOf(document).FirstOrDefault(u => string.Equals(TypeName(u.Type), Key(type), StringComparison.Ordinal))
            ?? throw ToolException.NotFound($"The document has no unit type '{type}'.", "List them with se_options action 'units'.");
        object before = DescribeUnit(target);
        if (unit is not null)
        {
            int wanted = UnitValue(target.Type, unit);
            target.Units = wanted;
            if (target.Units != wanted)
            {
                throw new ToolException("option_not_applied", $"Solid Edge kept the {type} unit at {UnitName(target.Type, target.Units)}.");
            }
        }

        if (precision is { } p)
        {
            target.Precision = p;
            if (target.Precision != p)
            {
                throw new ToolException("option_not_applied", $"Solid Edge kept the {type} precision at {target.Precision} instead of {p}.",
                    "Precision is an index (distance: 0 ones, 1 tenths, 2 hundredths, 3 thousandths, 4 ten-thousandths...).");
            }
        }

        return new { document = context.DocumentHandle(document), previous = before, current = DescribeUnit(target), note = "Save the document to keep the change." };
    }

    private static Catalog CatalogOf(SeContext context, string scope, string? documentHandle)
    {
        if (scope == "application")
        {
            return new Catalog(typeof(ApplicationGlobalConstants), context.Application, "application", ["seApplicationGlobal"]);
        }

        if (scope != "document")
        {
            throw ToolException.InvalidArgument($"Unknown scope '{scope}'.", $"Use one of: {string.Join(", ", Scopes)}.");
        }

        SolidEdgeDocument document = context.GetDocument(documentHandle);
        return document switch
        {
            SolidEdgePart.WeldmentDocument => new Catalog(typeof(SolidEdgePart.WeldmentGlobalConstants), document, "weldment", ["seWeldmentGlobal", "seWeldment"]),
            SolidEdgePart.SheetMetalDocument => new Catalog(typeof(SolidEdgePart.SheetMetalGlobalConstants), document, "sheet_metal", ["seSheetMetalGlobal", "seSheetMetal"]),
            SolidEdgePart.PartDocument => new Catalog(typeof(SolidEdgePart.PartGlobalConstants), document, "part", ["sePartGlobal", "sePart"]),
            SolidEdgeAssembly.AssemblyDocument => new Catalog(typeof(SolidEdgeAssembly.AssemblyGlobalConstants), document, "assembly", ["seAssemblyGlobal", "seAssembly"]),
            SolidEdgeDraft.DraftDocument => new Catalog(typeof(SolidEdgeDraft.DraftGlobalConstants), document, "draft", ["seDraftGlobal", "seDraft"]),
            _ => throw new ToolException("wrong_document_type", "This document type has no document options.", "Use scope 'application'."),
        };
    }

    private static IEnumerable<(string Name, int Value)> Entries(Catalog catalog) =>
        Enum.GetNames(catalog.Enum)
            .Select(n => (Name: Strip(n, catalog.Prefixes), Value: Convert.ToInt32(Enum.Parse(catalog.Enum, n), CultureInfo.InvariantCulture)))
            .OrderBy(e => e.Value);

    private static (string Name, int Value) Find(Catalog catalog, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' is required.");
        }

        string wanted = Strip(name.Trim(), catalog.Prefixes);
        foreach ((string Name, int Value) entry in Entries(catalog))
        {
            if (string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        throw ToolException.NotFound($"There is no {catalog.Owner} option named '{name}'.", "Search with se_options action 'list' and 'filter'.");
    }

    private static string Strip(string name, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    private static object Read(Catalog catalog, string name, int parameter)
    {
        object? raw = ReadRaw(catalog, parameter, out string? error);
        return new
        {
            name,
            value = Format(raw),
            color = error is null && IsColor(name) ? Hex(raw) : null,
            error,
        };
    }

    private static object? ReadRaw(Catalog catalog, int parameter, out string? error)
    {
        error = null;
        try
        {
            return Late.CallByRef(catalog.Target, "GetGlobalParameter", [parameter, null], 1)[1];
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            error = ComErrors.Unwrap(ex) is COMException com ? $"0x{com.HResult:X8} {com.Message}" : ComErrors.Unwrap(ex).Message;
            return null;
        }
    }

    private static object? Format(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or int or double or float or short or long or uint or ushort or byte or sbyte or ulong => value,
        Array array => array.Cast<object?>().Select(Format).ToArray(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Describe(object? value) => value switch
    {
        null => "(empty)",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static bool IsColor(string name) => name.Contains("Color", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Opacity", StringComparison.OrdinalIgnoreCase) && !name.Contains("Scheme", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Theme", StringComparison.OrdinalIgnoreCase) && !name.Contains("Mode", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("ColorManager", StringComparison.OrdinalIgnoreCase);

    /// <summary>Color OLE (0x00BBGGRR) como #RRGGBB.</summary>
    private static string? Hex(object? value)
    {
        long? bgr = value switch
        {
            int i when i >= 0 => i,
            uint u => u,
            long l when l >= 0 => l,
            double d when d >= 0 && d == Math.Floor(d) && d <= 0xFFFFFF => (long)d,
            _ => null,
        };
        return bgr is { } c && c <= 0xFFFFFF ? $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}" : null;
    }

    private static object ConvertValue(JsonElement value, object? current, bool color)
    {
        if (color && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.StartsWith('#'))
        {
            if (text.Length != 7 || !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                throw ToolException.InvalidArgument($"'{text}' is not a #RRGGBB colour.");
            }

            int bgr = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
            return current switch
            {
                uint => (uint)bgr,
                double => (double)bgr,
                _ => bgr,
            };
        }

        try
        {
            return current switch
            {
                bool => value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => value.GetDouble() != 0,
                    JsonValueKind.String => bool.Parse(value.GetString() ?? ""),
                    _ => throw new FormatException(),
                },
                int => Convert.ToInt32(Number(value), CultureInfo.InvariantCulture),
                short => Convert.ToInt16(Number(value), CultureInfo.InvariantCulture),
                uint => Convert.ToUInt32(Number(value), CultureInfo.InvariantCulture),
                long => Convert.ToInt64(Number(value), CultureInfo.InvariantCulture),
                double => Number(value),
                float => (float)Number(value),
                string => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText(),
                _ => value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when value.TryGetInt32(out int i) => i,
                    JsonValueKind.Number => value.GetDouble(),
                    _ => throw new FormatException(),
                },
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            throw ToolException.InvalidArgument($"The value {value.GetRawText()} does not fit the option type ({current?.GetType().Name ?? "unknown"}).",
                "Pass a value of the same type as the current one (se_options get); colours accept #RRGGBB.");
        }
    }

    private static double Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => double.Parse(value.GetString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => throw new FormatException(),
    };

    private static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is bool || b is bool)
        {
            return Convert.ToBoolean(a, CultureInfo.InvariantCulture) == Convert.ToBoolean(b, CultureInfo.InvariantCulture);
        }

        if (a is string || b is string)
        {
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        double x = Convert.ToDouble(a, CultureInfo.InvariantCulture);
        double y = Convert.ToDouble(b, CultureInfo.InvariantCulture);
        return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x));
    }

    private static IEnumerable<UnitOfMeasure> UnitsOf(SolidEdgeDocument document)
    {
        UnitsOfMeasure units = document.UnitsOfMeasure;
        for (int i = 1; i <= units.Count; i++)
        {
            yield return units.Item(i);
        }
    }

    private static object DescribeUnit(UnitOfMeasure unit) => new
    {
        type = TypeName(unit.Type),
        unit = UnitName(unit.Type, unit.Units),
        unitValue = unit.Units,
        precision = unit.Precision,
    };

    /// <summary>igUnitLinearVelocity → linear_velocity.</summary>
    private static string TypeName(UnitTypeConstants type) =>
        Snake(type.ToString().Replace("igUnit", "", StringComparison.Ordinal));

    private static string Key(string text) => text.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>Enumeración de unidades de lectura del tipo (SolidEdgeConstants.UnitOfMeasure*ReadoutConstants), si existe.</summary>
    private static Type? ReadoutEnum(UnitTypeConstants type)
    {
        string name = type.ToString().Replace("igUnit", "", StringComparison.Ordinal);
        string[] candidates = name switch
        {
            "Distance" => ["Length"],
            "CoefficientOfThermalExpansion" => ["CoefOfThermalExpansion"],
            "FilmCoefficient" => ["HeatTransferCoefficient"],
            "MassPerLength" => ["LinearDensity"],
            "HeatFluxPerArea" => ["HeatFlux"],
            "HeatCapacity" => ["SpecificHeat"],
            _ => [name],
        };
        return candidates
            .Select(c => InteropCatalog.Enums.FirstOrDefault(t => t.FullName == $"SolidEdgeConstants.UnitOfMeasure{c}ReadoutConstants"))
            .FirstOrDefault(t => t is not null);
    }

    /// <summary>Nombre corto de la unidad (seLengthMillimeter → millimeter), o <c>null</c> si no hay enumeración.</summary>
    private static string? UnitName(UnitTypeConstants type, int value)
    {
        Type? readout = ReadoutEnum(type);
        if (readout is null)
        {
            return null;
        }

        string? name = Enum.GetName(readout, Enum.ToObject(readout, value));
        return name is null || name.Length <= UnitPrefix(readout).Length ? null : Snake(name[UnitPrefix(readout).Length..]);
    }

    private static int UnitValue(UnitTypeConstants type, string unit)
    {
        if (int.TryParse(unit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
        {
            return raw;
        }

        Type readout = ReadoutEnum(type)
            ?? throw ToolException.InvalidArgument($"Unit names are not known for '{TypeName(type)}'.", "Pass the numeric unit value instead.");
        string prefix = UnitPrefix(readout);
        string wanted = unit.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        foreach (string name in Enum.GetNames(readout))
        {
            if (string.Equals(name[prefix.Length..], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt32(Enum.Parse(readout, name), CultureInfo.InvariantCulture);
            }
        }

        throw ToolException.InvalidArgument($"Unknown {TypeName(type)} unit '{unit}'.",
            $"Use one of: {string.Join(", ", Enum.GetNames(readout).Select(n => Snake(n[prefix.Length..])))}.");
    }

    /// <summary>Prefijo común de los nombres de la enumeración, cortado en un límite de palabra (seLength, seMass...).</summary>
    private static string UnitPrefix(Type readout)
    {
        string[] names = Enum.GetNames(readout);
        int length = names.Min(n => n.Length);
        int common = 0;
        while (common < length && names.All(n => n[common] == names[0][common]))
        {
            common++;
        }

        while (common > 0 && !names.All(n => n.Length > common && char.IsUpper(n[common])))
        {
            common--;
        }

        return names[0][..common];
    }
}
