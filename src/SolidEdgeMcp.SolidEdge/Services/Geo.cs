using System.Globalization;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Conversión de vectores y puntos del API (metros) a los de las herramientas (mm).</summary>
internal static class Geo
{
    /// <summary>Redondeo para la salida: evita ruido numérico (1e-9 mm) sin perder precisión útil.</summary>
    public static double R(double value) => Math.Round(value, 9);

    public static double Mm(double metres) => R(Units.MToMm(metres));

    public static double Mm2(double squareMetres) => Math.Round(Units.M2ToMm2(squareMetres), 6);

    /// <summary>mm³ redondeados; sumar 0,0 convierte -0 en 0.</summary>
    public static double Mm3(double cubicMetres) => Math.Round(Units.M3ToMm3(cubicMetres), 6) + 0.0;

    public static double[] Mm(Array values) => Doubles(values).Select(Mm).ToArray();

    public static double[] Plain(Array values) => Doubles(values).Select(R).ToArray();

    public static IEnumerable<double> Doubles(Array values)
    {
        foreach (object? value in values)
        {
            yield return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Punto 2D en mm desde un argumento [x, y].</summary>
    public static (double X, double Y) Point2(double[]? values, string name)
    {
        if (values is not { Length: 2 })
        {
            throw ToolException.InvalidArgument($"'{name}' must be [x, y] in mm.");
        }

        return (Units.MmToM(Units.RequireFinite(values[0], name)), Units.MmToM(Units.RequireFinite(values[1], name)));
    }

    /// <summary>Lista plana [x1, y1, x2, y2, ...] en mm → puntos en metros.</summary>
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

    /// <summary>Lista plana [x1, y1, z1, x2, ...] en mm (coordenadas del modelo) → puntos en metros.</summary>
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

    public static double Length(double[] v) => Math.Sqrt(v.Sum(c => c * c));
}
