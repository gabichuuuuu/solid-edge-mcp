// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Reflection;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Index of the types in the generated interop: interface by IID and enumerations. It makes it possible to handle
/// any COM object returned by Solid Edge with its real type (reflection) rather than guessing.
/// </summary>
public static class InteropCatalog
{
    private static readonly Lazy<Index> Data = new(Build);

    public static IReadOnlyList<Assembly> Assemblies => Data.Value.Assemblies;

    public static IReadOnlyList<Type> Enums => Data.Value.Enums;

    /// <summary>Interop type that describes the object, based on its COM type information.</summary>
    public static Type? FindInterface(object comObject)
    {
        (Guid guid, string name)? info = GetTypeInfoIdentity(comObject);
        if (info is null)
        {
            return null;
        }

        // The part library declares the draft's DrawingViews and block interfaces again under its own names
        // (SketchDrawingViews, SketchBlocks...) with the same IIDs, so the library named by the type information decides.
        Type? type = FindInterface(info.Value.guid);
        return type is not null && !info.Value.name.StartsWith(type.Namespace + ".", StringComparison.Ordinal)
            && FindByName(info.Value.name) is { } named && named.GUID == info.Value.guid
                ? named
                : type;
    }

    public static Type? FindInterface(Guid iid) =>
        Data.Value.InterfacesByGuid.TryGetValue(iid, out Type? type) ? type : null;

    public static Type? FindByName(string fullName) =>
        Data.Value.InterfacesByName.TryGetValue(fullName, out Type? type) ? type : null;

    /// <summary>COM type name of the object (library.type), or <c>null</c> if it does not expose type information.</summary>
    public static string? GetTypeName(object comObject)
    {
        Type? type = FindInterface(comObject);
        if (type is not null)
        {
            return type.FullName;
        }

        return GetTypeInfoIdentity(comObject)?.name;
    }

    /// <summary>All public members of the interface and of the interfaces it inherits from.</summary>
    public static IEnumerable<MemberInfo> GetMembers(Type type) =>
        new[] { type }.Concat(type.GetInterfaces())
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m is PropertyInfo || (m is MethodInfo method && !method.IsSpecialName))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

    public static MemberInfo? FindMember(Type type, string name) =>
        GetMembers(type).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static (Guid guid, string name)? GetTypeInfoIdentity(object comObject)
    {
        if (!Marshal.IsComObject(comObject) || comObject is not IDispatchTypeInfo dispatch)
        {
            return null;
        }

        if (dispatch.GetTypeInfoCount(out uint count) < 0 || count == 0
            || dispatch.GetTypeInfo(0, 0, out ComTypes.ITypeInfo? typeInfo) < 0 || typeInfo is null)
        {
            return null;
        }

        typeInfo.GetTypeAttr(out IntPtr attrPointer);
        try
        {
            ComTypes.TYPEATTR attr = Marshal.PtrToStructure<ComTypes.TYPEATTR>(attrPointer);
            typeInfo.GetDocumentation(-1, out string name, out _, out _, out _);
            string library = "";
            try
            {
                typeInfo.GetContainingTypeLib(out ComTypes.ITypeLib lib, out _);
                lib.GetDocumentation(-1, out library, out _, out _, out _);
            }
            catch (COMException)
            {
            }

            return (attr.guid, string.IsNullOrEmpty(library) ? name : $"{library}.{name}");
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(attrPointer);
        }
    }

    private static Index Build()
    {
        Assembly anchor = typeof(SolidEdgeFramework.Application).Assembly;
        string directory = Path.GetDirectoryName(anchor.Location)!;
        var assemblies = new List<Assembly>();
        foreach (string name in new[]
                 {
                     "Interop.SolidEdgeFramework", "Interop.SolidEdgeFrameworkSupport", "Interop.SolidEdgeConstants",
                     "Interop.SolidEdgeGeometry", "Interop.SolidEdgePart", "Interop.SolidEdgeAssembly", "Interop.SolidEdgeDraft",
                 })
        {
            try
            {
                assemblies.Add(Assembly.Load(name));
            }
            catch (FileNotFoundException)
            {
                string path = Path.Combine(directory, name + ".dll");
                if (File.Exists(path))
                {
                    assemblies.Add(Assembly.LoadFrom(path));
                }
            }
        }

        var byGuid = new Dictionary<Guid, Type>();
        var byName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var enums = new List<Type>();
        foreach (Type type in assemblies.SelectMany(a => a.GetExportedTypes()))
        {
            if (type.IsEnum)
            {
                enums.Add(type);
                continue;
            }

            if (!type.IsInterface || type.Name.EndsWith("_Event", StringComparison.Ordinal)
                || type.Name.EndsWith("_SinkHelper", StringComparison.Ordinal))
            {
                continue;
            }

            byName.TryAdd(type.FullName!, type);
            // The "coclass" interface (PartDocument) shares its IID with the real one (_IPartDocument...) and inherits
            // from it, so the one matched by name is preferred; GetMembers includes inherited members.
            if (!byGuid.TryGetValue(type.GUID, out Type? existing)
                || (existing.GetCustomAttribute<CoClassAttribute>() is null && type.GetCustomAttribute<CoClassAttribute>() is not null))
            {
                byGuid[type.GUID] = type;
            }
        }

        return new Index(assemblies, byGuid, byName, enums.OrderBy(e => e.FullName, StringComparer.Ordinal).ToList());
    }

    private sealed record Index(
        List<Assembly> Assemblies,
        Dictionary<Guid, Type> InterfacesByGuid,
        Dictionary<string, Type> InterfacesByName,
        List<Type> Enums);
}
