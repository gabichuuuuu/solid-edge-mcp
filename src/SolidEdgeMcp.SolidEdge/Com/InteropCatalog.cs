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
    public static Type? FindInterface(object comObject) => Identify(comObject).Type;

    public static Type? FindInterface(Guid iid) =>
        Data.Value.InterfacesByGuid.TryGetValue(iid, out Type? type) ? type : null;

    public static Type? FindByName(string fullName) =>
        Data.Value.InterfacesByName.TryGetValue(fullName, out Type? type) ? type : null;

    /// <summary>COM type name of the object (library.type), or <c>null</c> if it does not expose type information.</summary>
    public static string? GetTypeName(object comObject)
    {
        (Type? type, string? name) = Identify(comObject);
        return type?.FullName ?? name;
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

    /// <summary>
    /// Interop type and COM type name (library.type) of an object, from its type information. Every call to the type
    /// information is a round trip to Solid Edge, so only the IID is read when the interop declares it in one library. The
    /// part library declares the draft's DrawingViews and block interfaces again under its own names (SketchDrawingViews,
    /// SketchBlocks...) with the same IIDs: for those, and for types the interop does not have, the name is read too and the
    /// library it names decides.
    /// </summary>
    private static (Type? Type, string? Name) Identify(object comObject)
    {
        if (!Marshal.IsComObject(comObject) || comObject is not IDispatchTypeInfo dispatch
            || dispatch.GetTypeInfo(0, 0, out ComTypes.ITypeInfo? typeInfo) < 0 || typeInfo is null)
        {
            return (null, null);
        }

        typeInfo.GetTypeAttr(out IntPtr attrPointer);
        Guid guid;
        try
        {
            guid = Marshal.PtrToStructure<ComTypes.TYPEATTR>(attrPointer).guid;
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(attrPointer);
        }

        Type? type = FindInterface(guid);
        if (type is not null && !Data.Value.SharedIids.Contains(guid))
        {
            return (type, type.FullName);
        }

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

        string qualified = string.IsNullOrEmpty(library) ? name : $"{library}.{name}";
        return (type is not null && !qualified.StartsWith(type.Namespace + ".", StringComparison.Ordinal)
            && FindByName(qualified) is { } named && named.GUID == guid ? named : type, qualified);
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
        var namespaces = new Dictionary<Guid, string?>();
        var shared = new HashSet<Guid>();
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
            if (!namespaces.TryAdd(type.GUID, type.Namespace) && namespaces[type.GUID] != type.Namespace)
            {
                shared.Add(type.GUID);
            }

            // The "coclass" interface (PartDocument) shares its IID with the real one (_IPartDocument...) and inherits
            // from it, so the one matched by name is preferred; GetMembers includes inherited members.
            if (!byGuid.TryGetValue(type.GUID, out Type? existing)
                || (existing.GetCustomAttribute<CoClassAttribute>() is null && type.GetCustomAttribute<CoClassAttribute>() is not null))
            {
                byGuid[type.GUID] = type;
            }
        }

        return new Index(assemblies, byGuid, byName, enums.OrderBy(e => e.FullName, StringComparer.Ordinal).ToList(), shared);
    }

    /// <param name="SharedIids">IIDs that interfaces of more than one library declare.</param>
    private sealed record Index(
        List<Assembly> Assemblies,
        Dictionary<Guid, Type> InterfacesByGuid,
        Dictionary<string, Type> InterfacesByName,
        List<Type> Enums,
        HashSet<Guid> SharedIids);
}
