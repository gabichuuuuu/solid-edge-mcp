using SolidEdgeAssembly;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Cara o arista de la pieza de una ocurrencia, vista desde el conjunto. Todas las ocurrencias de una pieza comparten
/// sus objetos de topología, así que el handle lleva también la ocurrencia para no confundirlas.
/// </summary>
public sealed record OccurrenceElement(Occurrence Occurrence, object Element) : ICompositeHandleValue
{
    /// <summary>Referencia del elemento en el conjunto, la que esperan las relaciones, medidas y la selección.</summary>
    public object Reference(AssemblyDocument assembly) => assembly.CreateReference(Occurrence, Element);
}

/// <summary>
/// Posición de una ocurrencia en su conjunto. <c>Occurrence.GetMatrix</c> da una matriz 4×4 por columnas con el
/// convenio de vector fila (p' = p·M): filas 0-2 = ejes X, Y, Z de la pieza en el conjunto y fila 3 = origen
/// (comprobado: una pieza insertada en (0,2; 0; 0) devuelve la traslación en los elementos 12-14). Metros.
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

    /// <summary>Copia de los 16 valores, en el orden de <c>GetMatrix</c>/<c>PutMatrix</c>.</summary>
    public double[] Matrix => (double[])_m.Clone();

    public double[] Origin => [_m[12], _m[13], _m[14]];

    public double[] XAxis => [_m[0], _m[1], _m[2]];

    public double[] YAxis => [_m[4], _m[5], _m[6]];

    public double[] ZAxis => [_m[8], _m[9], _m[10]];

    /// <summary>Punto de la pieza (m) en coordenadas del conjunto (m).</summary>
    public double[] Point(IReadOnlyList<double> p) =>
    [
        (p[0] * _m[0]) + (p[1] * _m[4]) + (p[2] * _m[8]) + _m[12],
        (p[0] * _m[1]) + (p[1] * _m[5]) + (p[2] * _m[9]) + _m[13],
        (p[0] * _m[2]) + (p[1] * _m[6]) + (p[2] * _m[10]) + _m[14],
    ];

    /// <summary>Dirección de la pieza en coordenadas del conjunto (solo giro).</summary>
    public double[] Vector(IReadOnlyList<double> v) =>
    [
        (v[0] * _m[0]) + (v[1] * _m[4]) + (v[2] * _m[8]),
        (v[0] * _m[1]) + (v[1] * _m[5]) + (v[2] * _m[9]),
        (v[0] * _m[2]) + (v[1] * _m[6]) + (v[2] * _m[10]),
    ];

    /// <summary>
    /// Matriz de la ocurrencia girada <paramref name="angle"/> radianes (regla de la mano derecha) alrededor del eje que
    /// pasa por <paramref name="point"/> con dirección <paramref name="direction"/> (m, coordenadas del conjunto).
    /// </summary>
    public double[] Rotated(IReadOnlyList<double> point, IReadOnlyList<double> direction, double angle)
    {
        double length = Math.Sqrt((direction[0] * direction[0]) + (direction[1] * direction[1]) + (direction[2] * direction[2]));
        double[] u = [direction[0] / length, direction[1] / length, direction[2] / length];
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        // Fórmula de Rodrigues: v' = v·cos + (u×v)·sin + u·(u·v)·(1 − cos).
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

    /// <summary>Caja alineada con los ejes del conjunto que contiene la caja de la pieza (sus 8 esquinas giradas).</summary>
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
