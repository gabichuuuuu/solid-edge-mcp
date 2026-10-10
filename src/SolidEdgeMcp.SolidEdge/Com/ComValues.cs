using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Conversion between JSON and COM values for the generic layer. COM objects come out as handles and
/// go in as <c>{"handle": "x:1"}</c>; paths always go through <see cref="PathGuard"/>.
/// </summary>
public static partial class ComValues
{
    /// <summary>COM value → serializable tree (primitives, lists, dictionaries, handles).</summary>
    public static object? ToJson(SeContext context, object? value, SolidEdgeFramework.SolidEdgeDocument? owner, int depth = 0)
    {
        switch (value)
        {
            case null or DBNull or Missing:
                return null;
            case string or bool or int or short or long or byte or uint or ushort or ulong or sbyte or decimal:
                return value;
            case double d:
                return double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture);
            case float f:
                return float.IsFinite(f) ? f : f.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return dt.ToString("O", CultureInfo.InvariantCulture);
            case Enum e:
                return Enum.IsDefined(e.GetType(), e) ? e.ToString() : Convert.ToInt64(e, CultureInfo.InvariantCulture);
            case Array array when depth < 4:
                var list = new List<object?>();
                int count = 0;
                foreach (object? item in array)
                {
                    if (count++ >= context.MaxListItems)
                    {
                        list.Add($"... truncated: {array.Length} items in total");
                        break;
                    }

                    list.Add(ToJson(context, item, owner, depth + 1));
                }

                return list;
            case not null when Marshal.IsComObject(value):
                string type = InteropCatalog.GetTypeName(value) ?? "object";
                string kind = type[(type.LastIndexOf('.') + 1)..];
                return new Dictionary<string, object?>
                {
                    ["handle"] = context.Register(value, kind, owner),
                    ["type"] = type,
                };
            default:
                return value.ToString();
        }
    }

    /// <summary>JSON → value of the parameter's .NET type (including <c>object</c>/VARIANT and <c>Array</c>).</summary>
    public static object? FromJson(SeContext context, JsonElement json, Type targetType, string argumentName, bool fileArgument)
    {
        Type type = targetType.IsByRef ? targetType.GetElementType()! : targetType;
        Type? nullable = Nullable.GetUnderlyingType(type);
        type = nullable ?? type;

        if (json.ValueKind == JsonValueKind.Null || json.ValueKind == JsonValueKind.Undefined)
        {
            return type.IsValueType && nullable is null ? Activator.CreateInstance(type) : null;
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            return FromObject(context, json, type, argumentName, fileArgument);
        }

        if (type == typeof(string))
        {
            return CheckString(context, json.ValueKind == JsonValueKind.String ? json.GetString()! : json.GetRawText(), argumentName, fileArgument);
        }

        if (type == typeof(bool))
        {
            return json.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(json.GetString(), out bool b) => b,
                _ => throw Mismatch(argumentName, "a boolean"),
            };
        }

        if (type.IsEnum)
        {
            if (json.ValueKind == JsonValueKind.Number)
            {
                return Enum.ToObject(type, json.GetInt64());
            }

            string? name = json.GetString();
            if (name is not null && Enum.TryParse(type, name, ignoreCase: true, out object? parsed))
            {
                return parsed;
            }

            throw ToolException.InvalidArgument($"'{argumentName}': '{name}' is not a value of {type.FullName}.",
                $"Use se_com_constants with query '{type.Name}' to list the valid names.");
        }

        if (IsNumeric(type))
        {
            double number = json.ValueKind switch
            {
                JsonValueKind.Number => json.GetDouble(),
                JsonValueKind.String when double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
                _ => throw Mismatch(argumentName, "a number"),
            };
            Units.RequireFinite(number, argumentName);
            try
            {
                return Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                throw ToolException.InvalidArgument($"'{argumentName}': {number} is out of range for {type.Name}.");
            }
        }

        if (type == typeof(Array) || type.IsArray)
        {
            if (json.ValueKind != JsonValueKind.Array)
            {
                throw Mismatch(argumentName, "an array");
            }

            return type.IsArray && type != typeof(Array)
                ? TypedArray(context, json, type.GetElementType()!, argumentName, fileArgument)
                : InferArray(context, json, argumentName, fileArgument);
        }

        if (type == typeof(object))
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => CheckString(context, json.GetString()!, argumentName, fileArgument),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when json.TryGetInt32(out int i) => i,
                JsonValueKind.Number => json.GetDouble(),
                JsonValueKind.Array => InferArray(context, json, argumentName, fileArgument),
                _ => throw Mismatch(argumentName, "a value"),
            };
        }

        if (type.IsInterface)
        {
            throw ToolException.InvalidArgument($"'{argumentName}' expects a Solid Edge object ({type.FullName}).",
                "Pass it as {\"handle\": \"...\"} with a handle returned by another tool.");
        }

        throw ToolException.InvalidArgument($"'{argumentName}': parameters of type {type.FullName} are not supported by the generic layer.");
    }

    /// <summary>Does the parameter or property receive a file or folder name?</summary>
    public static bool IsFileMember(string memberName, string parameterName) =>
        StrongFileParameter().IsMatch(parameterName)
        || (WeakFileParameter().IsMatch(parameterName) && FileMember().IsMatch(memberName));

    private static object? FromObject(SeContext context, JsonElement json, Type type, string argumentName, bool fileArgument)
    {
        if (json.TryGetProperty("handle", out JsonElement handle))
        {
            string? id = handle.GetString();
            object resolved = id is GenericCom.ApplicationTarget or GenericCom.ActiveDocumentTarget
                ? GenericCom.ResolveTarget(context, id)
                : context.Handles.Resolve(id, argumentName);
            if (type != typeof(object) && !type.IsInstanceOfType(resolved))
            {
                throw new ToolException("handle_wrong_type",
                    $"'{argumentName}' expects {type.FullName}; the handle '{handle.GetString()}' is a {InteropCatalog.GetTypeName(resolved) ?? "different object"}.");
            }

            return resolved;
        }

        if (json.TryGetProperty("type", out JsonElement typeName) && json.TryGetProperty("value", out JsonElement inner))
        {
            Type explicitType = typeName.GetString() switch
            {
                "int" => typeof(int),
                "short" => typeof(short),
                "double" => typeof(double),
                "float" => typeof(float),
                "bool" => typeof(bool),
                "string" => typeof(string),
                "int[]" => typeof(int[]),
                "short[]" => typeof(short[]),
                "double[]" => typeof(double[]),
                "string[]" => typeof(string[]),
                "object[]" => typeof(object[]),
                "bool[]" => typeof(bool[]),
                var other => throw ToolException.InvalidArgument($"'{argumentName}': unknown explicit type '{other}'.",
                    "Use int, short, double, float, bool, string or the same names with [] for arrays."),
            };
            return FromJson(context, inner, explicitType, argumentName, fileArgument);
        }

        throw ToolException.InvalidArgument($"'{argumentName}': objects must be {{\"handle\": \"...\"}} or {{\"type\": \"double[]\", \"value\": [...]}}.");
    }

    private static Array TypedArray(SeContext context, JsonElement json, Type elementType, string argumentName, bool fileArgument)
    {
        var array = Array.CreateInstance(elementType, json.GetArrayLength());
        int i = 0;
        foreach (JsonElement item in json.EnumerateArray())
        {
            array.SetValue(FromJson(context, item, elementType, $"{argumentName}[{i}]", fileArgument), i);
            i++;
        }

        return array;
    }

    /// <summary>Numbers → double[]; all strings → string[]; anything else → object[].</summary>
    private static Array InferArray(SeContext context, JsonElement json, string argumentName, bool fileArgument)
    {
        JsonElement[] items = json.EnumerateArray().ToArray();
        if (items.Length > 0 && items.All(i => i.ValueKind == JsonValueKind.Number))
        {
            return TypedArray(context, json, typeof(double), argumentName, fileArgument);
        }

        if (items.Length > 0 && items.All(i => i.ValueKind == JsonValueKind.String))
        {
            return TypedArray(context, json, typeof(string), argumentName, fileArgument);
        }

        return TypedArray(context, json, typeof(object), argumentName, fileArgument);
    }

    private static string CheckString(SeContext context, string value, string argumentName, bool fileArgument)
    {
        if (fileArgument && value.Length > 0 || PathGuard.LooksLikePath(value))
        {
            return context.Paths.Check(value, argumentName);
        }

        return value;
    }

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(double) || type == typeof(short) || type == typeof(long)
        || type == typeof(float) || type == typeof(uint) || type == typeof(ushort) || type == typeof(byte)
        || type == typeof(sbyte) || type == typeof(ulong) || type == typeof(decimal);

    private static ToolException Mismatch(string argumentName, string expected) =>
        ToolException.InvalidArgument($"'{argumentName}' must be {expected}.");

    [GeneratedRegex("open|save|export|import|file|path|folder|directory|template|macro|image|print|publish|insert|replace|convert|load|link|attach|copy|bitmap|picture", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileMember();

    [GeneratedRegex("file|path|folder|directory|template|macro", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongFileParameter();

    [GeneratedRegex("^name$|newname|output|destination|location", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeakFileParameter();
}
