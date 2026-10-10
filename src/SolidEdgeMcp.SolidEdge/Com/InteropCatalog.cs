using System.Reflection;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Índice de los tipos de la interop generada: interfaz por IID y enumeraciones. Permite tratar
/// cualquier objeto COM devuelto por Solid Edge con su tipo real (reflexión) en lugar de adivinar.
/// </summary>
public static class InteropCatalog
{
    private static readonly Lazy<Index> Data = new(Build);

    public static IReadOnlyList<Assembly> Assemblies => Data.Value.Assemblies;

    public static IReadOnlyList<Type> Enums => Data.Value.Enums;

    /// <summary>Tipo de la interop que describe el objeto, a partir de su información de tipos COM.</summary>
    public static Type? FindInterface(object comObject)
    {
        (Guid guid, string _)? info = GetTypeInfoIdentity(comObject);
        return info is null ? null : FindInterface(info.Value.guid);
    }

    public static Type? FindInterface(Guid iid) =>
        Data.Value.InterfacesByGuid.TryGetValue(iid, out Type? type) ? type : null;

    public static Type? FindByName(string fullName) =>
        Data.Value.InterfacesByName.TryGetValue(fullName, out Type? type) ? type : null;

    /// <summary>Nombre del tipo COM del objeto (librería.tipo), o <c>null</c> si no expone información de tipos.</summary>
    public static string? GetTypeName(object comObject)
    {
        Type? type = FindInterface(comObject);
        if (type is not null)
        {
            return type.FullName;
        }

        return GetTypeInfoIdentity(comObject)?.name;
    }

    /// <summary>Todos los miembros públicos de la interfaz y de las que hereda.</summary>
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
            // La interfaz «coclass» (PartDocument) comparte IID con la real (_IPartDocument...) y hereda
            // de ella: se prefiere por su nombre; GetMembers incluye los miembros heredados.
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
