// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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
        // Decimal separator: the last of '.' or ',' (the report follows the regional format).
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

    /// <summary>Center of gravity line: three numbers with a unit at the end.</summary>
    [GeneratedRegex(@"[-+]?\d[\d.,]*\s*\p{L}+\s+[-+]?\d[\d.,]*\s*\p{L}+\s+[-+]?\d[\d.,]*\s*\p{L}+\s*$")]
    private static partial Regex Location();
}
