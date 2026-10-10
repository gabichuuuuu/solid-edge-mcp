using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Acceso por nombre (IDispatch) a miembros comunes de objetos heterogéneos: operaciones de
/// distintos tipos comparten Name, Status o Suppress pero no una interfaz.
/// </summary>
public static class Late
{
    public static object? Get(object target, string property, params object?[] index) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, index, CultureInfo.InvariantCulture);

    public static void Set(object target, string property, object? value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);

    public static object? Call(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    /// <summary>
    /// Llamada por IDispatch con parámetros por referencia (ref/out) en <paramref name="byRef"/>; devuelve los
    /// argumentos tras la llamada. Sirve cuando la llamada con tipos de la interop falla (p. ej. DISP_E_BADCALLEE
    /// con parámetros de enumeración en MatTable).
    /// </summary>
    public static object?[] CallByRef(object target, string method, object?[] args, params int[] byRef)
    {
        var modifier = new ParameterModifier(args.Length);
        foreach (int position in byRef)
        {
            modifier[position] = true;
        }

        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, [modifier], CultureInfo.InvariantCulture, null);
        return args;
    }

    /// <summary>Lee una propiedad; devuelve <c>null</c> si el objeto no la tiene o falla.</summary>
    public static object? TryGet(object target, string property, params object?[] index)
    {
        try
        {
            return Get(target, property, index);
        }
        catch (Exception ex) when (IsMemberFailure(ex))
        {
            return null;
        }
    }

    public static string? TryGetString(object target, string property) => TryGet(target, property) as string;

    public static bool IsMemberFailure(Exception ex) =>
        ComErrors.Unwrap(ex) is COMException or MissingMemberException or NotSupportedException or InvalidCastException
            or ArgumentException;

    /// <summary>Elementos de una colección COM (Count + Item, base 1).</summary>
    public static IEnumerable<object> Items(object collection, int max = int.MaxValue)
    {
        int count = Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
        for (int i = 1; i <= count && i <= max; i++)
        {
            if (Call(collection, "Item", i) is { } item)
            {
                yield return item;
            }
        }
    }

    public static int Count(object collection) => Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
}
