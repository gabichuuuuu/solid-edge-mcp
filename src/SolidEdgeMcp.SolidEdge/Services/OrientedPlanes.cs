using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Base planes chosen by their orientation ('xy', 'yz', 'xz') instead of their place in the template: a template of another
/// installation may order or name its base planes differently, while the plane through the origin normal to z is xy in any
/// of them. Works for part reference planes and assembly reference planes (both have GetNormal and GetRootPoint).
/// </summary>
internal static class OrientedPlanes
{
    /// <summary>Axis of the normal for 'xy' (z = 2), 'yz' (x = 0) and 'xz' (y = 1); false for anything else.</summary>
    public static bool TryAxis(string plane, out int axis)
    {
        axis = plane.Trim().ToLowerInvariant() switch
        {
            "xy" or "yx" => 2,
            "yz" or "zy" => 0,
            "xz" or "zx" => 1,
            _ => -1,
        };
        return axis >= 0;
    }

    /// <summary>The first plane through the origin whose normal is along <paramref name="axis"/>.</summary>
    public static object Find(IEnumerable<object> planes, int axis, string plane)
    {
        foreach (object candidate in planes)
        {
            if (Read(candidate) is ({ } normal, { } root) && Math.Abs(Math.Abs(normal[axis]) - 1) < 1e-6 && Math.Abs(root[axis]) < 1e-9)
            {
                return candidate;
            }
        }

        throw ToolException.NotFound($"There is no reference plane '{plane}' through the origin in this document.",
            "List the planes and their normals with se_ref_plane action 'list', or pass a plane index or handle.");
    }

    private static (double[]? Normal, double[]? Root) Read(object plane)
    {
        Array normal = new double[3];
        Array root = new double[3];
        try
        {
            switch (plane)
            {
                case SolidEdgePart.RefPlane part:
                    part.GetNormal(ref normal);
                    part.GetRootPoint(ref root);
                    break;
                case SolidEdgeAssembly.AsmRefPlane assembly:
                    assembly.GetNormal(ref normal);
                    assembly.GetRootPoint(ref root);
                    break;
                default:
                    return (null, null);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return (null, null);
        }

        return ([.. Geo.Doubles(normal)], [.. Geo.Doubles(root)]);
    }
}
