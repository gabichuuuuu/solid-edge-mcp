// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeAssembly;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Face or edge of an occurrence's part, seen from the assembly. All occurrences of a part share
/// their topology objects, so the handle also carries the occurrence to tell them apart.
/// </summary>
public sealed record OccurrenceElement(Occurrence Occurrence, object Element) : ICompositeHandleValue
{
    /// <summary>Reference of the element in the assembly, the one expected by relations, measurements and selection.</summary>
    public object Reference(AssemblyDocument assembly) => assembly.CreateReference(Occurrence, Element);
}

/// <summary>
/// Position of an occurrence in its assembly. <c>Occurrence.GetMatrix</c> gives a 4×4 matrix by columns, with the
/// row-vector convention (p' = p·M): rows 0-2 = X, Y, Z axes of the part in the assembly and row 3 = origin
/// (verified: a part inserted at (0.2; 0; 0) returns the translation in elements 12-14). Metres.
/// </summary>
public sealed class Placement
{
    private readonly double[] _m;

    private Placement(double[] matrix) => _m = matrix;

    public static Placement Of(Occurrence occurrence)
    {
        Array matrix = new double[16];
        occurrence.GetMatrix(ref matrix);
        return new Placement(Geo.Doubles(matrix).ToArray());
    }

    /// <summary>Copy of the 16 values, in the order of <c>GetMatrix</c>/<c>PutMatrix</c>.</summary>
    public double[] Matrix => (double[])_m.Clone();

    public double[] Origin => [_m[12], _m[13], _m[14]];

    public double[] XAxis => [_m[0], _m[1], _m[2]];

    public double[] YAxis => [_m[4], _m[5], _m[6]];

    public double[] ZAxis => [_m[8], _m[9], _m[10]];

    /// <summary>Point of the part (m) in assembly coordinates (m).</summary>
    public double[] Point(IReadOnlyList<double> p) =>
    [
        (p[0] * _m[0]) + (p[1] * _m[4]) + (p[2] * _m[8]) + _m[12],
        (p[0] * _m[1]) + (p[1] * _m[5]) + (p[2] * _m[9]) + _m[13],
        (p[0] * _m[2]) + (p[1] * _m[6]) + (p[2] * _m[10]) + _m[14],
    ];

    /// <summary>Point of the assembly (m) in the coordinates of the part (m): the inverse of <see cref="Point"/>.</summary>
    public double[] Local(IReadOnlyList<double> p)
    {
        double[] d = [p[0] - _m[12], p[1] - _m[13], p[2] - _m[14]];
        return
        [
            (d[0] * _m[0]) + (d[1] * _m[1]) + (d[2] * _m[2]),
            (d[0] * _m[4]) + (d[1] * _m[5]) + (d[2] * _m[6]),
            (d[0] * _m[8]) + (d[1] * _m[9]) + (d[2] * _m[10]),
        ];
    }

    /// <summary>Direction of the part in assembly coordinates (rotation only).</summary>
    public double[] Vector(IReadOnlyList<double> v) =>
    [
        (v[0] * _m[0]) + (v[1] * _m[4]) + (v[2] * _m[8]),
        (v[0] * _m[1]) + (v[1] * _m[5]) + (v[2] * _m[9]),
        (v[0] * _m[2]) + (v[1] * _m[6]) + (v[2] * _m[10]),
    ];

    /// <summary>
    /// Matrix of the occurrence rotated by <paramref name="angle"/> radians (right-hand rule) about the axis that
    /// passes through <paramref name="point"/> with direction <paramref name="direction"/> (m, assembly coordinates).
    /// </summary>
    public double[] Rotated(IReadOnlyList<double> point, IReadOnlyList<double> direction, double angle)
    {
        double length = Math.Sqrt((direction[0] * direction[0]) + (direction[1] * direction[1]) + (direction[2] * direction[2]));
        double[] u = [direction[0] / length, direction[1] / length, direction[2] / length];
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        // Rodrigues' formula: v' = v·cos + (u×v)·sin + u·(u·v)·(1 − cos).
        double[] Turn(double x, double y, double z)
        {
            double dot = (u[0] * x) + (u[1] * y) + (u[2] * z);
            return
            [
                (x * cos) + (((u[1] * z) - (u[2] * y)) * sin) + (u[0] * dot * (1 - cos)),
                (y * cos) + (((u[2] * x) - (u[0] * z)) * sin) + (u[1] * dot * (1 - cos)),
                (z * cos) + (((u[0] * y) - (u[1] * x)) * sin) + (u[2] * dot * (1 - cos)),
            ];
        }

        double[] result = Matrix;
        for (int row = 0; row < 3; row++)
        {
            double[] axis = Turn(_m[row * 4], _m[(row * 4) + 1], _m[(row * 4) + 2]);
            Array.Copy(axis, 0, result, row * 4, 3);
        }

        double[] origin = Turn(_m[12] - point[0], _m[13] - point[1], _m[14] - point[2]);
        result[12] = origin[0] + point[0];
        result[13] = origin[1] + point[1];
        result[14] = origin[2] + point[2];
        return result;
    }

    /// <summary>Box aligned with the assembly axes that contains the part's box (its 8 rotated corners).</summary>
    public (double[] Min, double[] Max) Box(IReadOnlyList<double> min, IReadOnlyList<double> max)
    {
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        for (int corner = 0; corner < 8; corner++)
        {
            double[] p = Point([(corner & 1) == 0 ? min[0] : max[0], (corner & 2) == 0 ? min[1] : max[1], (corner & 4) == 0 ? min[2] : max[2]]);
            for (int i = 0; i < 3; i++)
            {
                lo[i] = Math.Min(lo[i], p[i]);
                hi[i] = Math.Max(hi[i], p[i]);
            }
        }

        return (lo, hi);
    }
}
