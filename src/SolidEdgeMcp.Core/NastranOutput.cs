// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>One row of the real eigenvalue table: the eigenvalue and, for normal modes, the frequency in Hz (CYCLES).</summary>
public sealed record NastranEigenvalue(int Mode, double Eigenvalue, double Cycles);

/// <summary>
/// The TOTALS row of a resultant table (OLOAD, SPCFORCE...) of a Nastran .f06 file: forces T1-T3 and moments R1-R3 about the
/// origin, in the units of the model.
/// </summary>
public sealed record NastranResultant(double[] Force, double[] Moment);

/// <summary>
/// Reads the printed output (.f06) of Simcenter Nastran, which Solid Edge Simulation writes in its scratch folder. The API of
/// Solid Edge gives neither the natural frequencies nor the buckling load factors nor the total reaction, and the .f06 has them.
/// </summary>
public static partial class NastranOutput
{
    /// <summary>
    /// The rows of the first real eigenvalue table (modal and buckling solutions), also when it goes on over a page break;
    /// empty when there is none.
    /// </summary>
    public static IReadOnlyList<NastranEigenvalue> Eigenvalues(string text)
    {
        const string Title = "R E A L   E I G E N V A L U E S";
        var rows = new List<NastranEigenvalue>();
        for (int start = text.IndexOf(Title, StringComparison.Ordinal); start >= 0; start = text.IndexOf(Title, start + Title.Length, StringComparison.Ordinal))
        {
            // The two header lines come first, then one row per mode until the first line that is not a row. A title with the
            // next mode number continues the table on a new page; any other number is another table.
            int before = rows.Count;
            foreach (string line in text[start..].Split('\n').Skip(1))
            {
                Match match = EigenvalueRow().Match(line);
                if (match.Success)
                {
                    int mode = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (rows.Count > 0 && mode != rows[^1].Mode + 1)
                    {
                        return rows;
                    }

                    rows.Add(new NastranEigenvalue(mode, Number(match.Groups[2].Value), Number(match.Groups[3].Value)));
                }
                else if (rows.Count > before)
                {
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>The TOTALS row of the first table called <paramref name="name"/> RESULTANT (e.g. OLOAD, SPCFORCE), or null.</summary>
    public static NastranResultant? Resultant(string text, string name)
    {
        Match header = Regex.Match(text, $@"\b{Regex.Escape(name)}\s+RESULTANT\b", RegexOptions.CultureInvariant);
        if (!header.Success)
        {
            return null;
        }

        foreach (string line in text[header.Index..].Split('\n').Skip(1).Take(12))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("TOTALS", StringComparison.Ordinal))
            {
                continue;
            }

            string[] values = trimmed["TOTALS".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 6)
            {
                return null;
            }

            double[] numbers = values.Take(6).Select(Number).ToArray();
            return new NastranResultant(numbers[..3], numbers[3..]);
        }

        return null;
    }

    /// <summary>The fatal messages Nastran printed (one line each), or none.</summary>
    public static IReadOnlyList<string> FatalMessages(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("FATAL MESSAGE", StringComparison.Ordinal)).Distinct().ToArray();

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\s+(\d+)\s+\d+\s+(-?[\d.]+E[+-]\d+)\s+-?[\d.]+E[+-]\d+\s+(-?[\d.]+E[+-]\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex EigenvalueRow();
}
