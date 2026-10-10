using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Capa 2: acceso genérico a cualquier objeto del API con su tipo de la interop (reflexión) y, si no
/// se conoce, por IDispatch. Las unidades son las del API (metros y radianes).
/// </summary>
public static class GenericCom
{
    public const string ApplicationTarget = "app";
    public const string ActiveDocumentTarget = "active_document";

    public static object ResolveTarget(SeContext context, string? target)
    {
        return target switch
        {
            null or "" => throw ToolException.InvalidArgument("'target' is required: a handle, 'app' or 'active_document'."),
            ApplicationTarget => context.Application,
            ActiveDocumentTarget => context.GetDocument(null),
            _ => context.Handles.Resolve(target, "target"),
        };
    }

    public static object Describe(SeContext? context, string? target, string? typeName, string? filter, int offset, int limit)
    {
        Type? type;
        string name;
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            type = InteropCatalog.FindByName(typeName) ?? throw ToolException.NotFound(
                $"Unknown interface '{typeName}'.", "Use the full name, e.g. 'SolidEdgePart.Model' (see the 'type' of a handle).");
            name = type.FullName!;
        }
        else
        {
            object value = ResolveTarget(context ?? throw new InvalidOperationException("A Solid Edge context is required."), target);
            type = InteropCatalog.FindInterface(value);
            name = type?.FullName ?? InteropCatalog.GetTypeName(value) ?? "unknown";
            if (type is null)
            {
                return new { type = name, note = "No interop type found for this object; use se_com_get/se_com_invoke by name (IDispatch)." };
            }
        }

        List<string> members = InteropCatalog.GetMembers(type)
            .Where(m => string.IsNullOrWhiteSpace(filter) || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m is MethodInfo ? 1 : 0)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(FormatMember)
            .ToList();
        int take = Math.Clamp(limit, 1, context?.MaxListItems ?? 2000);
        return new
        {
            type = name,
            total = members.Count,
            offset,
            members = members.Skip(Math.Max(0, offset)).Take(take).ToList(),
            units = "COM level: lengths in metres, angles in radians; collections start at 1.",
        };
    }

    public static object? Get(SeContext context, string? target, string member, JsonElement? index)
    {
        object value = ResolveTarget(context, target);
        SolidEdgeFramework.SolidEdgeDocument? owner = OwnerOf(context, value, target);
        Type? type = InteropCatalog.FindInterface(value);
        MemberInfo? info = type is null ? null : InteropCatalog.FindMember(type, member);
        object?[] indexArgs = [];
        object? result;
        switch (info)
        {
            case PropertyInfo property:
                indexArgs = BindArguments(context, property.Name, property.GetIndexParameters(), index);
                result = property.GetValue(value, indexArgs.Length == 0 ? null : indexArgs);
                break;
            case MethodInfo method:
                // Algunos «Item» o «GetX» se leen como método.
                indexArgs = BindArguments(context, method.Name, method.GetParameters(), index);
                result = CallMethod(value, method, indexArgs);
                break;
            case null when type is not null:
                throw new ToolException("member_not_found", $"{type.FullName} has no member '{member}'.",
                    "Use se_com_describe to list the members.");
            default:
                result = InvokeDispatch(value, member, BindingFlags.GetProperty, InferArguments(context, member, index));
                break;
        }

        return ComValues.ToJson(context, result, owner);
    }

    public static object? Set(SeContext context, string? target, string member, JsonElement value, JsonElement? index)
    {
        object obj = ResolveTarget(context, target);
        Type? type = InteropCatalog.FindInterface(obj);
        MemberInfo? info = type is null ? null : InteropCatalog.FindMember(type, member);
        bool fileProperty = ComValues.IsFileMember(member, member);
        switch (info)
        {
            case PropertyInfo { CanWrite: true } property:
                object?[] indexArgs = BindArguments(context, property.Name, property.GetIndexParameters(), index);
                object? converted = ComValues.FromJson(context, value, property.PropertyType, member, fileProperty);
                property.SetValue(obj, converted, indexArgs.Length == 0 ? null : indexArgs);
                return ComValues.ToJson(context, property.CanRead ? property.GetValue(obj, indexArgs.Length == 0 ? null : indexArgs) : null, OwnerOf(context, obj, target));
            case PropertyInfo:
                throw new ToolException("read_only_property", $"'{member}' is read-only.");
            case null when type is not null:
                throw new ToolException("member_not_found", $"{type.FullName} has no property '{member}'.",
                    "Use se_com_describe to list the members.");
            case not null:
                throw new ToolException("not_a_property", $"'{member}' is a method; use se_com_invoke.");
            default:
                object?[] args = [.. InferArguments(context, member, index), ComValues.FromJson(context, value, typeof(object), member, fileProperty)];
                InvokeDispatch(obj, member, BindingFlags.SetProperty, args);
                return null;
        }
    }

    public static object Invoke(SeContext context, string? target, string method, JsonElement? arguments)
    {
        object obj = ResolveTarget(context, target);
        SolidEdgeFramework.SolidEdgeDocument? owner = OwnerOf(context, obj, target);
        Type? type = InteropCatalog.FindInterface(obj);
        MemberInfo? info = type is null ? null : InteropCatalog.FindMember(type, method);
        switch (info)
        {
            case MethodInfo methodInfo:
                ParameterInfo[] parameters = methodInfo.GetParameters();
                object?[] args = BindArguments(context, methodInfo.Name, parameters, arguments);
                object? result = CallMethod(obj, methodInfo, args);
                var outputs = new Dictionary<string, object?>();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType.IsByRef)
                    {
                        outputs[parameters[i].Name ?? $"arg{i}"] = ComValues.ToJson(context, args[i], owner);
                    }
                }

                return outputs.Count == 0
                    ? new { result = ComValues.ToJson(context, result, owner) }
                    : new { result = ComValues.ToJson(context, result, owner), outputs };
            case PropertyInfo:
                throw new ToolException("not_a_method", $"'{method}' is a property; use se_com_get or se_com_set.");
            case null when type is not null:
                throw new ToolException("member_not_found", $"{type.FullName} has no method '{method}'.",
                    "Use se_com_describe to list the members.");
            default:
                object? dispatchResult = InvokeDispatch(obj, method, BindingFlags.InvokeMethod, InferArguments(context, method, arguments));
                return new { result = ComValues.ToJson(context, dispatchResult, owner) };
        }
    }

    /// <summary>Elementos de una colección COM (Count + Item, base 1), con su nombre si lo tienen.</summary>
    public static object Items(SeContext context, string? target, string? member, int offset, int limit)
    {
        object collection = ResolveTarget(context, target);
        SolidEdgeFramework.SolidEdgeDocument? owner = OwnerOf(context, collection, target);
        if (!string.IsNullOrWhiteSpace(member))
        {
            collection = ToObject(Get(context, target, member, null), context)
                ?? throw new ToolException("not_a_collection", $"'{member}' is not an object.");
        }

        int count = Convert.ToInt32(InvokeByName(collection, "Count", BindingFlags.GetProperty, []), CultureInfo.InvariantCulture);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object?>();
        for (int i = Math.Max(0, offset) + 1; i <= count && items.Count < take; i++)
        {
            object? item = InvokeByName(collection, "Item", BindingFlags.InvokeMethod, [i]);
            string? name = null;
            if (item is not null && Marshal.IsComObject(item))
            {
                name = TryGetString(item, "Name") ?? TryGetString(item, "DisplayName");
            }

            items.Add(new { index = i, name, value = ComValues.ToJson(context, item, owner) });
        }

        return new { count, offset, items };
    }

    /// <summary>
    /// Busca en las enumeraciones. Una misma enumeración aparece en varias librerías: se agrupa por
    /// nombre corto y se indica en qué librerías está.
    /// </summary>
    public static object Constants(string query, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw ToolException.InvalidArgument("'query' is required (an enum name such as 'FeaturePropertyConstants' or part of a value name).");
        }

        var groups = new List<object>();
        int values = 0;
        int skipped = 0;
        int take = Math.Clamp(limit, 1, 2000);
        foreach (IGrouping<string, Type> group in InteropCatalog.Enums.GroupBy(e => e.Name, StringComparer.Ordinal))
        {
            Type type = group.First();
            bool typeMatch = type.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var members = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string name in Enum.GetNames(type))
            {
                if (typeMatch || name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (skipped < offset)
                    {
                        skipped++;
                        continue;
                    }

                    if (values >= take)
                    {
                        break;
                    }

                    members[name] = Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture);
                    values++;
                }
            }

            if (members.Count > 0)
            {
                groups.Add(new { @enum = type.Name, libraries = group.Select(t => t.Namespace).Distinct().ToArray(), values = members });
            }

            if (values >= take)
            {
                break;
            }
        }

        return new { offset, returned = values, more = values >= take, enums = groups };
    }
    internal static string FormatMember(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo property:
                ParameterInfo[] index = property.GetIndexParameters();
                string indexText = index.Length == 0 ? "" : $"[{string.Join(", ", index.Select(FormatParameter))}]";
                string access = property.CanWrite ? "get/set" : "get";
                return $"P {TypeName(property.PropertyType)} {property.Name}{indexText} {{{access}}}";
            case MethodInfo method:
                return $"M {TypeName(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(FormatParameter))})";
            default:
                return member.Name;
        }
    }

    private static string FormatParameter(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        string prefix = !type.IsByRef ? "" : parameter.IsOut && !parameter.IsIn ? "out " : "ref ";
        string text = $"{prefix}{TypeName(type.IsByRef ? type.GetElementType()! : type)} {parameter.Name}";
        return parameter.IsOptional ? "[opt] " + text : text;
    }

    internal static string TypeName(Type type) => type switch
    {
        _ when type == typeof(void) => "void",
        _ when type == typeof(object) => "object",
        _ when type == typeof(string) => "string",
        _ when type == typeof(int) => "int",
        _ when type == typeof(short) => "short",
        _ when type == typeof(double) => "double",
        _ when type == typeof(float) => "float",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(Array) => "Array",
        _ when type.IsArray => TypeName(type.GetElementType()!) + "[]",
        _ when type.Namespace?.StartsWith("SolidEdge", StringComparison.Ordinal) == true => $"{type.Namespace}.{type.Name}",
        _ => type.Name,
    };

    /// <summary>Convierte los argumentos JSON (array posicional) según los parámetros reales.</summary>
    private static object?[] BindArguments(SeContext context, string memberName, ParameterInfo[] parameters, JsonElement? json)
    {
        JsonElement[] supplied = ArgumentArray(json);
        if (supplied.Length > parameters.Length)
        {
            throw ToolException.InvalidArgument(
                $"'{memberName}' takes {parameters.Length} argument(s); {supplied.Length} were given.",
                "Use se_com_describe to see the signature.");
        }

        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            string name = parameter.Name ?? $"arg{i}";
            bool outOnly = parameter.ParameterType.IsByRef && parameter.IsOut && !parameter.IsIn;
            bool missing = i >= supplied.Length || supplied[i].ValueKind == JsonValueKind.Null;
            if (missing)
            {
                if (outOnly)
                {
                    args[i] = null;
                }
                else if (parameter.IsOptional)
                {
                    // Un parámetro IDispatch no admite Type.Missing (no es un objeto COM): se omite con null.
                    args[i] = parameter.HasDefaultValue && parameter.DefaultValue is not DBNull ? parameter.DefaultValue
                        : parameter.GetCustomAttribute<MarshalAsAttribute>()?.Value == UnmanagedType.IDispatch ? null
                        : Type.Missing;
                }
                else if (parameter.ParameterType.IsByRef)
                {
                    args[i] = null;
                }
                else if (i < supplied.Length && !parameter.ParameterType.IsValueType)
                {
                    // null explícito en un parámetro de objeto obligatorio: Nothing, como en VB (p. ej. el plano de
                    // extensión de un solevado sin extensión).
                    args[i] = null;
                }
                else
                {
                    throw ToolException.InvalidArgument($"'{memberName}' requires argument {i + 1} ('{name}').",
                        "Use se_com_describe to see the signature; pass null explicitly for an object argument that is not used (Nothing).");
                }

                continue;
            }

            args[i] = ComValues.FromJson(context, supplied[i], parameter.ParameterType, name, ComValues.IsFileMember(memberName, name));
        }

        return args;
    }

    private static object?[] InferArguments(SeContext context, string memberName, JsonElement? json) =>
        ArgumentArray(json)
            .Select((element, i) => ComValues.FromJson(context, element, typeof(object), $"arg{i}", ComValues.IsFileMember(memberName, memberName)))
            .ToArray();

    private static JsonElement[] ArgumentArray(JsonElement? json) => json switch
    {
        null => [],
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => [],
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().ToArray(),
        var single => [single.Value],
    };

    /// <summary>
    /// Llama al método por la interop o, si falta un argumento opcional con tipo (enumeración, número...), por
    /// IDispatch: la reflexión solo acepta <see cref="Type.Missing"/> en parámetros VARIANT, mientras que
    /// IDispatch deja que Solid Edge aplique su valor por defecto. Los parámetros por referencia se marcan
    /// para recibir los valores de salida en <paramref name="args"/>.
    /// </summary>
    private static object? CallMethod(object target, MethodInfo method, object?[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        bool typedMissing = parameters.Where((p, i) => args[i] == Type.Missing
            && (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) != typeof(object)).Any();
        if (!typedMissing || !Marshal.IsComObject(target))
        {
            return method.Invoke(target, args);
        }

        var modifier = new ParameterModifier(parameters.Length);
        for (int i = 0; i < parameters.Length; i++)
        {
            modifier[i] = parameters[i].ParameterType.IsByRef;
        }

        return target.GetType().InvokeMember(method.Name, BindingFlags.InvokeMethod, null, target, args, [modifier],
            CultureInfo.InvariantCulture, null);
    }

    private static object? InvokeDispatch(object target, string member, BindingFlags flags, object?[] args)
    {
        if (!Marshal.IsComObject(target))
        {
            throw new ToolException("not_a_com_object", "The target is not a Solid Edge object.");
        }

        return InvokeByName(target, member, flags, args);
    }

    private static object? InvokeByName(object target, string member, BindingFlags flags, object?[] args) =>
        target.GetType().InvokeMember(member, flags, null, target, args, CultureInfo.InvariantCulture);

    private static string? TryGetString(object target, string property)
    {
        try
        {
            return InvokeByName(target, property, BindingFlags.GetProperty, []) as string;
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException)
        {
            return null;
        }
    }

    private static object? ToObject(object? json, SeContext context) =>
        json is Dictionary<string, object?> dict && dict.TryGetValue("handle", out object? handle) && handle is string id
            ? context.Handles.Resolve(id)
            : null;

    /// <summary>Documento al que pertenece el objeto (para caducar sus handles al cerrarlo).</summary>
    private static SolidEdgeFramework.SolidEdgeDocument? OwnerOf(SeContext context, object value, string? target)
    {
        if (value is SolidEdgeFramework.SolidEdgeDocument document)
        {
            return document;
        }

        if (target is null || target == ApplicationTarget || target == ActiveDocumentTarget)
        {
            return null;
        }

        string? scope = context.Handles.GetScope(target);
        try
        {
            return scope is null ? null : context.Handles.Resolve(scope) as SolidEdgeFramework.SolidEdgeDocument;
        }
        catch (ToolException)
        {
            return null;
        }
    }
}
