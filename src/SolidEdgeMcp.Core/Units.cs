namespace SolidEdgeMcp.Core;

/// <summary>
/// Conversión única entre las unidades de las herramientas (mm, grados) y las internas de
/// Solid Edge (m, rad). Ninguna otra parte del código debe multiplicar por 1000 o por π/180.
/// </summary>
public static class Units
{
    private const double MillimetresPerMetre = 1000.0;
    private const double DegreesPerRadian = 180.0 / Math.PI;

    public static double MmToM(double mm) => mm / MillimetresPerMetre;

    public static double MToMm(double m) => m * MillimetresPerMetre;

    public static double Mm2ToM2(double mm2) => mm2 / (MillimetresPerMetre * MillimetresPerMetre);

    public static double M2ToMm2(double m2) => m2 * MillimetresPerMetre * MillimetresPerMetre;

    public static double M3ToMm3(double m3) => m3 * MillimetresPerMetre * MillimetresPerMetre * MillimetresPerMetre;

    public static double Mm3ToM3(double mm3) => mm3 / (MillimetresPerMetre * MillimetresPerMetre * MillimetresPerMetre);

    /// <summary>Momento de inercia: kg·m² → kg·mm².</summary>
    public static double KgM2ToKgMm2(double kgM2) => kgM2 * MillimetresPerMetre * MillimetresPerMetre;

    public static double DegToRad(double degrees) => degrees / DegreesPerRadian;

    public static double RadToDeg(double radians) => radians * DegreesPerRadian;

    public static double[] MToMm(IEnumerable<double> values) => values.Select(MToMm).ToArray();

    /// <summary>Exige un número finito; los NaN e infinitos nunca deben llegar a COM.</summary>
    public static double RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw ToolException.InvalidArgument($"'{name}' must be a finite number.");
        }

        return value;
    }

    /// <summary>Exige un número finito y estrictamente positivo (longitudes, espesores, radios).</summary>
    public static double RequirePositive(double value, string name)
    {
        RequireFinite(value, name);
        if (value <= 0)
        {
            throw ToolException.InvalidArgument($"'{name}' must be greater than zero (got {value}).");
        }

        return value;
    }
}
