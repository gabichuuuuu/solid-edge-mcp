// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Layer 2: generic access to any API object through its interop type (reflection) and, if it is not
/// known, through IDispatch. The units are the API ones (metres and radians).
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
                CheckIndex(value, type, property.Name, indexArgs);
                result = property.GetValue(value, indexArgs.Length == 0 ? null : indexArgs);
                break;
            case MethodInfo method:
                // Some "Item" or "GetX" members are read as methods.
                RefuseUnsafe(type, method.Name);
                indexArgs = BindArguments(context, method.Name, method.GetParameters(), index);
                RefuseUnsafeArguments(type, method.Name, indexArgs);
                CheckIndex(value, type, method.Name, indexArgs);
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
                if (type?.Name == "Sensor" && property.Name == "Description" && converted is string text && string.IsNullOrWhiteSpace(text))
                {
                    throw EmptySensorDescription();
                }

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

    /// <summary>
    /// Methods verified to bring Solid Edge 2026 down or leave it unstable: refused, because Solid Edge may hold the
    /// person's unsaved work.
    /// </summary>
    private static readonly Dictionary<string, (string Effect, string? Instead)> Unsafe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RadialMenu.DumpPallets"] = ("closes Solid Edge (access violation in control.dll)", null),
        ["RadialMenu.SavePallets"] = ("closes Solid Edge (access violation in control.dll)", null),
        ["ViewStyle.BeginPropertyBuffer"] = ("leaves Solid Edge unstable (RPC_E_SERVERFAULT)", null),
        ["ViewStyle.FlushPropertyBuffer"] = ("leaves Solid Edge unstable (RPC_E_SERVERFAULT)", null),
        ["Relations3d.AddPlanarCam"] = ("leaves Solid Edge unstable (RPC_E_SERVERFAULT, with the faces of the cam and the follower as assembly references)", null),
        ["Relations3d.AddPath"] = ("leaves Solid Edge unstable (RPC_E_SERVERFAULT, with an edge, a circular edge or a vertex as the follower)", null),
        ["Rounds.AddBlend"] = ("closed Solid Edge (with one face set and two radii) and otherwise leaves the blend failed", "Round the edge the faces share with se_round."),
        ["SheetMetalSensors.AddMinimumDistanceSensor"] = ("fails with E_INVALIDARG after leaving an empty sensor that stops the document from saving (sensors cannot be deleted either)", "Use a variable sensor, or a distance sensor in an assembly that holds the part."),
        ["Sensor.Delete"] = ("breaks every sensor of the document, also in the saved file (each later call on Sensors fails with 0x80040225)", "Keep the sensor and change its condition with se_sensor set."),
        ["EndCaps.Add"] = ("leaves Solid Edge unstable (RPC_E_SERVERFAULT on the next call on the assembly's occurrences) after capping the members of a frame", "Use the End Cap command of the frame in Solid Edge."),
        ["SmartFrame2d.CreateLink"] = ("closed Solid Edge (a part file linked into a smart frame of a drawing)", "Place a view of the part with se_drawing base_view, or a picture with se_sheet_content image."),
        ["ResizeHoles.Add"] =("leaves Solid Edge unstable (RPC_E_SERVERFAULT) when ThreadHoleData is not a HoleData", "Use se_part_feature resize_holes."),
        ["AutoSimplifyFeatures.AddAutoSimplify"] = ("closed Solid Edge (a part of an assembly, not edited in place, simplifying another occurrence)", "Simplify an assembly with se_simplify (create, save_as)."),
        ["AutoSimplifyFeatures.AddAutoSimplifyEx"] = ("is the same call as AddAutoSimplify, which closed Solid Edge", "Simplify an assembly with se_simplify (create, save_as)."),
        ["Points3D.GetPointByPosition"] = ("leaves Solid Edge unstable (RPC_E_SERVERFAULT, with the position of a point of the 3D sketch)", "Keep the positions given to se_sketch3d point."),
        ["NamedViews.Create"] = ("makes every later save of the document fail with RPC_E_SERVERFAULT", "Save the current view of the window with se_view save_view."),
    };

    private static void RefuseUnsafe(Type? type, string member)
    {
        if (type is not null && Unsafe.TryGetValue($"{type.Name}.{member.Trim()}", out (string Effect, string? Instead) found))
        {
            throw new ToolException("unsafe_member", $"{type.Name}.{member.Trim()} {found.Effect} in Solid Edge 2026, so it is not called.",
                found.Instead ?? "Do it in the Solid Edge interface instead.");
        }
    }

    /// <summary>
    /// Arguments verified to bring Solid Edge 2026 down: SplitCurves.Add and AreCurvesValid with a curve feature in the curves
    /// array (instead of its edges) fail with RPC_E_SERVERFAULT.
    /// </summary>
    private static void RefuseUnsafeArguments(Type? type, string member, object?[] args)
    {
        int description = type?.Name is not ("Sensors" or "SheetMetalSensors") ? -1 : member.ToUpperInvariant() switch
        {
            "ADDVARIABLESENSOR" => 2,
            "ADDMINIMUMDISTANCESENSOR" or "ADDSURFACEAREASENSOR" => 3,
            _ => -1,
        };
        if (description >= 0 && args.Length > description && args[description] is string text && string.IsNullOrWhiteSpace(text))
        {
            throw EmptySensorDescription();
        }

        if (type?.Name == "SplitCurves"
            && (member.Equals("Add", StringComparison.OrdinalIgnoreCase) || member.Equals("AreCurvesValid", StringComparison.OrdinalIgnoreCase))
            && args.Length > 1 && args[1] is Array curves && (curves.Length > 1 || curves.Cast<object?>().Any(c => c is not SolidEdgeGeometry.Edge)))
        {
            throw new ToolException("unsafe_member",
                $"SplitCurves.{member} with anything but one edge in the curves array can bring Solid Edge 2026 down (RPC_E_SERVERFAULT: the curve feature, or several edges with one of them not crossed), so it is not called.",
                "Pass one edge of the curve per call (se_geometry edges with source = the curve), or use se_curve split.");
        }
    }

    /// <summary>Verified in Solid Edge 2026: a sensor with an empty description makes the document fail to save (DISP_E_EXCEPTION).</summary>
    private static ToolException EmptySensorDescription() => new("unsafe_member",
        "A sensor with an empty description makes the document fail to save in Solid Edge 2026 (SaveAs: DISP_E_EXCEPTION, leaving a broken file), so it is not called.",
        "Give the sensor a description, or use se_sensor.");

    /// <summary>
    /// Checks a numeric Item/ItemEx index against the collection's Count. Verified in Solid Edge 2026: ModelMembers.Item
    /// beyond Count fails with RPC_E_SERVERFAULT, which leaves Solid Edge unstable.
    /// </summary>
    private static void CheckIndex(object collection, Type? type, string member, object?[] args)
    {
        if (type is null || args.Length == 0
            || !(member.Equals("Item", StringComparison.OrdinalIgnoreCase) || member.Equals("ItemEx", StringComparison.OrdinalIgnoreCase))
            || args[0] is null or string or bool || args[0] is not IConvertible number
            || InteropCatalog.FindMember(type, "Count") is not PropertyInfo { CanRead: true })
        {
            return;
        }

        double index = number.ToDouble(CultureInfo.InvariantCulture);
        int count;
        try
        {
            count = CollectionCount(collection, type);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            return;
        }

        if (index != Math.Floor(index) || index < 1 || index > count)
        {
            throw new ToolException("index_out_of_range",
                $"{type.Name} has {count} item(s), so there is no item {index.ToString(CultureInfo.InvariantCulture)}.",
                "Collections in Solid Edge start at 1.");
        }
    }

    public static object Invoke(SeContext context, string? target, string method, JsonElement? arguments)
    {
        object obj = ResolveTarget(context, target);
        SolidEdgeFramework.SolidEdgeDocument? owner = OwnerOf(context, obj, target);
        Type? type = InteropCatalog.FindInterface(obj);
        RefuseUnsafe(type, method);
        MemberInfo? info = type is null ? null : InteropCatalog.FindMember(type, method);
        switch (info)
        {
            case MethodInfo methodInfo:
                ParameterInfo[] parameters = methodInfo.GetParameters();
                object?[] args = BindArguments(context, methodInfo.Name, parameters, arguments);
                RefuseUnsafeArguments(type, methodInfo.Name, args);
                CheckIndex(obj, type, methodInfo.Name, args);
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

    /// <summary>Items of a COM collection (Count + Item, 1-based), with their name when they have one.</summary>
    public static object Items(SeContext context, string? target, string? member, int offset, int limit)
    {
        object collection = ResolveTarget(context, target);
        SolidEdgeFramework.SolidEdgeDocument? owner = OwnerOf(context, collection, target);
        if (!string.IsNullOrWhiteSpace(member))
        {
            collection = ToObject(Get(context, target, member, null), context)
                ?? throw new ToolException("not_a_collection", $"'{member}' is not an object.");
        }

        Type? type = InteropCatalog.FindInterface(collection);
        int count = CollectionCount(collection, type);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object?>();
        for (int i = Math.Max(0, offset) + 1; i <= count && items.Count < take; i++)
        {
            object? item = CollectionItem(collection, type, i);
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
    /// Count and Item through the interop interface when it declares them, by name otherwise. By name, a collection with
    /// named members answers with them first: an AttributeSet with an attribute called "Count" returns that attribute.
    /// </summary>
    private static int CollectionCount(object collection, Type? type) =>
        Convert.ToInt32(type is not null && InteropCatalog.FindMember(type, "Count") is PropertyInfo { CanRead: true } count
                && count.GetIndexParameters().Length == 0
            ? count.GetValue(collection)
            : InvokeByName(collection, "Count", BindingFlags.GetProperty, []), CultureInfo.InvariantCulture);

    private static object? CollectionItem(object collection, Type? type, int index) =>
        (type is null ? null : InteropCatalog.FindMember(type, "Item")) switch
        {
            MethodInfo method when method.GetParameters().Length == 1 => method.Invoke(collection, [index]),
            PropertyInfo property when property.GetIndexParameters().Length == 1 => property.GetValue(collection, [index]),
            _ => InvokeByName(collection, "Item", BindingFlags.InvokeMethod, [index]),
        };

    /// <summary>
    /// Searches the enumerations. The same enumeration appears in several libraries: it is grouped by
    /// short name and the libraries it is in are listed.
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

    /// <summary>Converts the JSON arguments (positional array) according to the real parameters.</summary>
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
                    // An IDispatch parameter does not accept Type.Missing (it is not a COM object): it is omitted with null.
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
                    // Explicit null for a required object parameter: Nothing, as in VB (e.g. the plane of
                    // the extension of a loft that has no extension).
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
    /// Calls the method through the interop or, if an optional argument with a type (enumeration, number...) is missing, through
    /// IDispatch: reflection only accepts <see cref="Type.Missing"/> in VARIANT parameters, whereas
    /// IDispatch lets Solid Edge apply its default value. By-reference parameters are marked
    /// to receive the output values in <paramref name="args"/>. If IDispatch rejects the call with DISP_E_TYPEMISMATCH (it
    /// happens with by-reference arrays, e.g. ConvToSMs.Add with only the face), it is retried through the interop with the
    /// missing typed arguments set to empty arrays and zero. An argument whose given type does not fit its parameter (e.g. an
    /// array for a number) is refused by reflection before Solid Edge is called: that is the caller's argument error.
    /// </summary>
    private static object? CallMethod(object target, MethodInfo method, object?[] args)
    {
        try
        {
            return CallMethodCore(target, method, args);
        }
        catch (ArgumentException ex)
        {
            throw ToolException.InvalidArgument($"An argument of '{method.Name}' does not fit its parameter ({ex.Message}).",
                "Use se_com_describe to see the signature: the types and order of the parameters.");
        }
    }

    private static object? CallMethodCore(object target, MethodInfo method, object?[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        bool outArrays = false;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].IsOut && ElementType(parameters[i]) == typeof(Array))
            {
                // A typed call ignores what an output argument holds; a call through IDispatch needs an array there (below).
                outArrays = true;
                args[i] = args[i] is null || args[i] == Type.Missing ? Array.Empty<object>() : args[i];
            }
        }

        bool typedMissing = parameters.Where((p, i) => args[i] == Type.Missing && ElementType(p) != typeof(object)).Any();
        if (!typedMissing && !outArrays || !Marshal.IsComObject(target))
        {
            return method.Invoke(target, args);
        }

        var modifier = new ParameterModifier(parameters.Length);
        for (int i = 0; i < parameters.Length; i++)
        {
            modifier[i] = parameters[i].ParameterType.IsByRef;
        }

        try
        {
            return typedMissing
                ? target.GetType().InvokeMember(method.Name, BindingFlags.InvokeMethod, null, target, args, [modifier],
                    CultureInfo.InvariantCulture, null)
                : method.Invoke(target, args);
        }
        catch (Exception ex) when (typedMissing && ComErrors.Unwrap(ex) is COMException { ErrorCode: ComErrors.DispETypeMismatch })
        {
            FillMissing(parameters, args);
            return method.Invoke(target, args);
        }
        catch (Exception ex) when (outArrays && ComErrors.Unwrap(ex) is NullReferenceException)
        {
            // E_POINTER (raised by .NET as NullReferenceException): the method writes into an output array that must already
            // exist, which only a call through IDispatch passes by reference. Verified: GetTopLevelAssemblyFileNames and
            // FindSEDocumentsContainingText with an empty array of variants.
            FillMissing(parameters, args);
            return target.GetType().InvokeMember(method.Name, BindingFlags.InvokeMethod, null, target, args, [modifier],
                CultureInfo.InvariantCulture, null);
        }
    }

    /// <summary>Gives each omitted typed argument an empty value (an empty array, zero or null).</summary>
    private static void FillMissing(ParameterInfo[] parameters, object?[] args)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = ElementType(parameters[i]);
            if (args[i] == Type.Missing && type != typeof(object))
            {
                args[i] = type == typeof(Array) ? Array.Empty<object>()
                    : type.IsArray ? Array.CreateInstance(type.GetElementType()!, 0)
                    : type.IsValueType ? Activator.CreateInstance(type)
                    : null;
            }
        }
    }

    private static Type ElementType(ParameterInfo parameter) =>
        parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;

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

    /// <summary>Document the object belongs to (so its handles expire when it is closed).</summary>
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
