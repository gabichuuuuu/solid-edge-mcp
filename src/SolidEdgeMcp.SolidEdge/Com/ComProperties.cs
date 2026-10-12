using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Scalar properties of a COM object read and written by name, typed by its interop interface: enumerations by name (also
/// the end of the name, such as 'gradient' for seBackgroundTypeGradient), colours (Int32 properties named *Color*) as
/// #RRGGBB, everything else in API units (metres, radians).
/// </summary>
public static class ComProperties
{
    /// <summary>Properties without an index whose value is a number, text, boolean or enumeration.</summary>
    public static IReadOnlyList<PropertyInfo> Scalars(Type iface) =>
        [.. InteropCatalog.GetMembers(iface).OfType<PropertyInfo>()
            .Where(p => p.GetIndexParameters().Length == 0 && IsScalar(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)];

    public static PropertyInfo Find(Type iface, string name, string owner)
    {
        IReadOnlyList<PropertyInfo> all = Scalars(iface);
        return all.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"{owner} has no property '{name}'.{NameMatch.Hint(all.Select(p => p.Name), name)}",
                "List the properties with action 'get'.");
    }

    /// <summary>Every readable scalar property as a value ready for JSON; the ones Solid Edge does not give are listed apart.</summary>
    public static (Dictionary<string, object?> Values, List<string> ReadOnly, List<string> Unreadable) ReadAll(object target, Type iface)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var readOnly = new List<string>();
        var unreadable = new List<string>();
        foreach (PropertyInfo property in Scalars(iface))
        {
            try
            {
                values[property.Name] = Format(property, Late.Get(target, property.Name));
                if (!property.CanWrite)
                {
                    readOnly.Add(property.Name);
                }
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
            {
                unreadable.Add(property.Name);
            }
        }

        return (values, readOnly, unreadable);
    }

    public static object? Read(object target, PropertyInfo property) => Format(property, Late.Get(target, property.Name));

    /// <summary>Sets a property and reads it back; the value must stay as given (a colour, enumeration or number within 1e-9).</summary>
    public static (object? Previous, object? Value) Set(SeContext context, object target, PropertyInfo property, JsonElement value, string owner)
    {
        object? previous = TryRead(target, property);
        object? written = Write(context, target, property, value, owner);
        object? current = Read(target, property);
        if (!Same(current, written))
        {
            throw new ToolException("value_rejected", $"Solid Edge kept '{property.Name}' of {owner} at {JsonSerializer.Serialize(current)} instead of {value.GetRawText()}.",
                "The value may be out of range, or the property may depend on another one.");
        }

        return (previous, current);
    }

    /// <summary>
    /// Converts and sets a property without reading it back, for objects that do not report what was set (the caller
    /// measures the effect); returns the value written, formatted like a read.
    /// </summary>
    public static object? Write(SeContext context, object target, PropertyInfo property, JsonElement value, string owner)
    {
        if (!property.CanWrite)
        {
            throw new ToolException("read_only_property", $"'{property.Name}' of {owner} is read-only.");
        }

        object? converted = Convert(context, property, value);
        try
        {
            Late.Set(target, property.Name, converted);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("value_rejected", $"Solid Edge rejected {value.GetRawText()} for '{property.Name}' of {owner}: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the type and range of the current value (action 'get').", ex);
        }

        return Format(property, converted);
    }

    public static object? TryRead(object target, PropertyInfo property)
    {
        try
        {
            return Read(target, property);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            return null;
        }
    }

    public static bool IsColor(PropertyInfo property) =>
        property.PropertyType == typeof(int) && property.Name.Contains("Color", StringComparison.OrdinalIgnoreCase);

    /// <summary>#RRGGBB of a COLORREF (0x00BBGGRR).</summary>
    public static string Hex(int colorRef) =>
        $"#{colorRef & 0xFF:X2}{(colorRef >> 8) & 0xFF:X2}{(colorRef >> 16) & 0xFF:X2}";

    /// <summary>COLORREF (0x00BBGGRR) of #RRGGBB.</summary>
    public static int ColorRef(string hex, string argument)
    {
        string text = hex.Trim();
        if (text.Length != 7 || text[0] != '#' || !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            throw ToolException.InvalidArgument($"'{argument}': '{hex}' is not a #RRGGBB colour.");
        }

        return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
    }

    /// <summary>Value of an enumeration by its name, the end of its name ('gradient') or its number.</summary>
    public static object EnumValue(Type type, JsonElement value, string argument)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return Enum.ToObject(type, number);
        }

        string text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : value.GetRawText();
        static string Compact(string s) => s.Replace("_", "", StringComparison.Ordinal);
        string[] names = Enum.GetNames(type);
        string? match = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => string.Equals(Compact(n), Compact(text), StringComparison.OrdinalIgnoreCase));
        if (match is null && text.Length > 0)
        {
            // The end of the name from the start of a word: 'bold' is igDimStyleFontBold and igDimStyleFontItalicBold, and
            // the shortest one is the plain value.
            string ending = Compact(text);
            string[] endings = [.. names.Where(n => EndsAtWord(Compact(n), ending))];
            string[] shortest = [.. endings.Where(n => n.Length == endings.Min(e => e.Length))];
            match = shortest.Length == 1 ? shortest[0] : null;
        }

        return match is not null
            ? Enum.Parse(type, match)
            : throw ToolException.InvalidArgument($"'{argument}': '{text}' is not a value of {type.Name}. Values: {string.Join(", ", names)}.");
    }

    private static bool EndsAtWord(string name, string ending)
    {
        if (ending.Length == 0 || !name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int start = name.Length - ending.Length;
        return start == 0 || char.IsUpper(name[start]) || char.IsDigit(name[start]) || !char.IsLetter(name[start - 1]);
    }

    private static object? Convert(SeContext context, PropertyInfo property, JsonElement value)
    {
        Type type = property.PropertyType;
        if (IsColor(property) && value.ValueKind == JsonValueKind.String)
        {
            return ColorRef(value.GetString()!, property.Name);
        }

        if (type.IsEnum)
        {
            return EnumValue(type, value, property.Name);
        }

        if (type == typeof(int) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            // Many style switches are Int32 flags (0 or 1).
            return value.ValueKind == JsonValueKind.True ? 1 : 0;
        }

        return ComValues.FromJson(context, value, type, property.Name, ComValues.IsFileMember(property.Name, property.Name));
    }

    private static object? Format(PropertyInfo property, object? raw) => raw switch
    {
        null or DBNull => null,
        _ when property.PropertyType.IsEnum && raw is not string =>
            Enum.IsDefined(property.PropertyType, Enum.ToObject(property.PropertyType, raw))
                ? Enum.GetName(property.PropertyType, Enum.ToObject(property.PropertyType, raw))
                : System.Convert.ToInt64(raw, CultureInfo.InvariantCulture),
        int color when IsColor(property) => Hex(color),
        double d => double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture),
        float f => float.IsFinite(f) ? (double)f : f.ToString(CultureInfo.InvariantCulture),
        _ => raw,
    };

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase),
        (double x, double y) => Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y))),
        (not null, not null) when IsNumber(a) && IsNumber(b) =>
            Math.Abs(System.Convert.ToDouble(a, CultureInfo.InvariantCulture) - System.Convert.ToDouble(b, CultureInfo.InvariantCulture)) <= 1e-6,
        _ => Equals(a, b),
    };

    private static bool IsNumber(object value) => value is int or short or long or byte or uint or ushort or double or float;

    private static bool IsScalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string);
}
