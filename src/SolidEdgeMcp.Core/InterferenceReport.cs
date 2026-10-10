using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Lectura del informe de texto de <c>AssemblyDocument.CheckInterference</c>. Solid Edge 2026 no rellena por el API
/// las listas de piezas que interfieren (los parámetros de salida vuelven vacíos, comprobado), pero sí las escribe en
/// el informe. El análisis se basa en la estructura y no en los rótulos, que pueden estar traducidos: cada
/// interferencia empieza con una línea «k of N» seguida de los nombres de las dos ocurrencias, y la línea con una
/// unidad cúbica es el volumen común.
/// </summary>
public static partial class InterferenceReport
{
    public sealed record Pair(string Occurrence1, string Occurrence2, double? VolumeMm3);

    public static IReadOnlyList<Pair> Parse(string text)
    {
        string[] lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var pairs = new List<Pair>();
        int? total = null;
        for (int i = 0; i < lines.Length; i++)
        {
            Match counter = Counter().Match(lines[i]);
            if (!counter.Success)
            {
                continue;
            }

            // Solo cuenta una numeración correlativa con el mismo total (evita confundirla con un nombre).
            int index = int.Parse(counter.Groups[1].Value, CultureInfo.InvariantCulture);
            int count = int.Parse(counter.Groups[2].Value, CultureInfo.InvariantCulture);
            if (index != pairs.Count + 1 || (total is { } expected && expected != count))
            {
                continue;
            }

            total = count;
            var names = new List<string>(2);
            double? volume = null;
            int j = i + 1;
            for (; j < lines.Length && !IsNextCounter(lines[j], pairs.Count + 2); j++)
            {
                string line = lines[j].Trim();
                if (line.Length == 0 || Location().IsMatch(line))
                {
                    continue;
                }

                if (Volume().Match(line) is { Success: true } match)
                {
                    volume ??= ToMm3(match.Groups["value"].Value, match.Groups["unit"].Value);
                }
                else if (names.Count < 2)
                {
                    names.Add(line);
                }
            }

            if (names.Count == 2)
            {
                pairs.Add(new Pair(names[0], names[1], volume));
            }

            i = j - 1;
        }

        return pairs;
    }

    private static bool IsNextCounter(string line, int expected) =>
        Counter().Match(line) is { Success: true } match && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == expected;

    private static double? ToMm3(string value, string unit)
    {
        // Separador decimal: el último de '.' o ',' (el informe sigue el formato regional).
        int dot = value.LastIndexOf('.');
        int comma = value.LastIndexOf(',');
        char separator = comma > dot ? ',' : '.';
        string plain = new(value.Where(c => char.IsAsciiDigit(c) || c == separator || c is '-' or '+').ToArray());
        if (!double.TryParse(plain.Replace(separator, '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return null;
        }

        double? factor = unit.ToLowerInvariant() switch
        {
            "mm" => 1,
            "cm" => 1e3,
            "m" => 1e9,
            "in" => 16_387.064,
            "ft" => 28_316_846.592,
            _ => null,
        };
        return factor is { } f ? Math.Round(number * f, 6) : null;
    }

    [GeneratedRegex(@"^\s*(\d+)\s+\S+\s+(\d+)\s*$")]
    private static partial Regex Counter();

    [GeneratedRegex(@"(?<value>[-+]?\d[\d.,]*)\s*(?<unit>mm|cm|m|in|ft)(\^3|³)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Volume();

    /// <summary>Línea de centro de gravedad: tres números con unidad al final.</summary>
    [GeneratedRegex(@"[-+]?\d[\d.,]*\s*\p{L}+\s+[-+]?\d[\d.,]*\s*\p{L}+\s+[-+]?\d[\d.,]*\s*\p{L}+\s*$")]
    private static partial Regex Location();
}
