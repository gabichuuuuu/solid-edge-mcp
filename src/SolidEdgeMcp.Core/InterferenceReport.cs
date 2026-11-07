// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Parses the text report of <c>AssemblyDocument.CheckInterference</c>. Solid Edge 2026 does not fill,
/// through the API, the lists of interfering parts (the output parameters come back empty, verified), but it does write them to
/// the report. The parsing relies on the structure rather than on the labels, which may be translated: each
/// interference starts with a line "k of N" followed by the names of the two occurrences, and the line with a
/// cubic unit is the common volume.
/// </summary>
public static partial class InterferenceReport
{
    public sealed record Pair(string Occurrence1, string Occurrence2, double? VolumeMm3);

    /// <param name="text">Contents of the report file, in any language of the installation.</param>
    /// <param name="volumeUnitMm3">mm³ in the volume unit of the document. The report writes values as the document
    /// formats them, and where it formats them without their unit the volume line is a label and a number alone
    /// (verified with Solid Edge 2026 Foundation: "Interference Volume 1570.796", and 1.571 with the volume in cm³, where
    /// the Community Edition wrote "1570.796 mm^3"). Without it such a volume is not read.</param>
    public static IReadOnlyList<Pair> Parse(string text, double? volumeUnitMm3 = null)
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

            // Only counts a consecutive numbering with the same total (avoids confusing it with a name).
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
                    volume ??= ToMm3(match.Groups["value"].Value, UnitMm3(match.Groups["unit"].Value));
                }
                else if (names.Count == 2 && volumeUnitMm3 is { } unit && BareVolume().Match(line) is { Success: true } bare)
                {
                    volume ??= ToMm3(bare.Groups["value"].Value, unit);
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

    private static double? ToMm3(string value, double? mm3PerUnit)
    {
        // Decimal separator: the last of '.' or ',' (the report follows the regional format).
        int dot = value.LastIndexOf('.');
        int comma = value.LastIndexOf(',');
        char separator = comma > dot ? ',' : '.';
        string plain = new(value.Where(c => char.IsAsciiDigit(c) || c == separator || c is '-' or '+').ToArray());
        if (!double.TryParse(plain.Replace(separator, '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return null;
        }

        return mm3PerUnit is { } f ? Math.Round(number * f, 6) : null;
    }

    private static double? UnitMm3(string unit) => unit.ToLowerInvariant() switch
    {
        "mm" => 1,
        "cm" => 1e3,
        "m" => 1e9,
        "in" => 16_387.064,
        "ft" => 28_316_846.592,
        _ => null,
    };

    [GeneratedRegex(@"^\s*(\d+)\s+\S+\s+(\d+)\s*$")]
    private static partial Regex Counter();

    [GeneratedRegex(@"(?<value>[-+]?\d[\d.,]*)\s*(?<unit>mm|cm|m|in|ft)(\^3|³)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Volume();

    /// <summary>A label without digits and a number alone at the end: a volume written without its unit.</summary>
    [GeneratedRegex(@"^\D*\s(?<value>[-+]?\d[\d.,]*)\s*$")]
    private static partial Regex BareVolume();

    /// <summary>Center of gravity line: three numbers with a unit at the end.</summary>
    [GeneratedRegex(@"[-+]?\d[\d.,]*\s*\p{L}+\s+[-+]?\d[\d.,]*\s*\p{L}+\s+[-+]?\d[\d.,]*\s*\p{L}+\s*$")]
    private static partial Regex Location();
}
