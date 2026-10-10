using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Access by name (IDispatch) to members common to heterogeneous objects: operations of
/// different types share Name, Status or Suppress but not an interface.
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
    /// Call through IDispatch with by-reference (ref/out) parameters in <paramref name="byRef"/>; returns the
    /// arguments after the call. It is useful when the call through the interop types fails (e.g. DISP_E_BADCALLEE
    /// with enumeration parameters in MatTable).
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

    /// <summary>Reads a property; returns <c>null</c> if the object does not have it or it fails.</summary>
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

    /// <summary>Items of a COM collection (Count + Item, 1-based).</summary>
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
