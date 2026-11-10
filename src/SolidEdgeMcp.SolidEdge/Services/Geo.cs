// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Conversion of API vectors and points (metres) to the tool ones (mm), and vector algebra.</summary>
internal static class Geo
{
    /// <summary>Volume change (m³) under which an operation did nothing: 1e-6 mm³, the precision volumes are reported with.</summary>
    public const double VolumeTolerance = 1e-15;

    /// <summary>Volume difference (m³) reported as a change of a body an edit should have left as it was: 0.001 mm³.</summary>
    public const double VolumeDrift = 1e-12;

    /// <summary>Rounding for the output: avoids numerical noise (1e-9 mm) without losing useful precision.</summary>
    public static double R(double value) => Math.Round(value, 9);

    public static double Mm(double metres) => R(Units.MToMm(metres));

    public static double Mm2(double squareMetres) => Math.Round(Units.M2ToMm2(squareMetres), 6);

    /// <summary>Rounded mm³; adding 0.0 turns -0 into 0.</summary>
    public static double Mm3(double cubicMetres) => Math.Round(Units.M3ToMm3(cubicMetres), 6) + 0.0;

    /// <summary>Second moment of area in mm⁴ (from m⁴), rounded; adding 0.0 turns -0 into 0.</summary>
    public static double Mm4(double metres4) => Math.Round(metres4 * 1e12, 6) + 0.0;

    public static double[] Mm(Array values) => Doubles(values).Select(Mm).ToArray();

    public static double[] Plain(Array values) => Doubles(values).Select(R).ToArray();

    public static IEnumerable<double> Doubles(Array values)
    {
        foreach (object? value in values)
        {
            yield return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>2D point in mm from an argument [x, y].</summary>
    public static (double X, double Y) Point2(double[]? values, string name)
    {
        if (values is not { Length: 2 })
        {
            throw ToolException.InvalidArgument($"'{name}' must be [x, y] in mm.");
        }

        return (Units.MmToM(Units.RequireFinite(values[0], name)), Units.MmToM(Units.RequireFinite(values[1], name)));
    }

    /// <summary>Flat list [x1, y1, x2, y2, ...] in mm → points in metres.</summary>
    public static List<(double X, double Y)> Points2(double[]? values, string name, int minPoints)
    {
        if (values is null || values.Length % 2 != 0 || values.Length < minPoints * 2)
        {
            throw ToolException.InvalidArgument(
                $"'{name}' must be a flat list [x1, y1, x2, y2, ...] in mm with at least {minPoints} points.");
        }

        var points = new List<(double X, double Y)>(values.Length / 2);
        for (int i = 0; i < values.Length; i += 2)
        {
            points.Add((Units.MmToM(Units.RequireFinite(values[i], name)), Units.MmToM(Units.RequireFinite(values[i + 1], name))));
        }

        return points;
    }

    /// <summary>Flat list [x1, y1, z1, x2, ...] in mm (model coordinates) → points in metres.</summary>
    public static List<(double X, double Y, double Z)> Points3(double[]? values, string name, int minPoints)
    {
        if (values is null || values.Length % 3 != 0 || values.Length < minPoints * 3)
        {
            throw ToolException.InvalidArgument(
                $"'{name}' must be a flat list [x1, y1, z1, x2, y2, z2, ...] in mm (model coordinates) with at least {minPoints} point(s).");
        }

        var points = new List<(double X, double Y, double Z)>(values.Length / 3);
        for (int i = 0; i < values.Length; i += 3)
        {
            points.Add((Units.MmToM(Units.RequireFinite(values[i], name)), Units.MmToM(Units.RequireFinite(values[i + 1], name)),
                Units.MmToM(Units.RequireFinite(values[i + 2], name))));
        }

        return points;
    }

    public static double[] Cross(double[] a, double[] b) =>
        [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    public static double Dot(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();

    public static double[] Minus(double[] a, double[] b) => [.. a.Zip(b, (x, y) => x - y)];

    public static double[] Scale(double[] v, double factor) => [.. v.Select(c => c * factor)];

    /// <summary>The direction of a vector, or <c>null</c> when it is too short to have one (under 1e-12).</summary>
    public static double[]? TryUnit(double[] v)
    {
        double length = Length(v);
        return length < 1e-12 ? null : [.. v.Select(c => c / length)];
    }

    /// <summary>The direction of a vector; one too short to have a direction is returned as it is.</summary>
    public static double[] Unit(double[] v) => TryUnit(v) ?? v;

    public static double Length(double[] v) => Math.Sqrt(v.Sum(c => c * c));

    public static double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
}
