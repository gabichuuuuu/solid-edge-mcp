// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.Core;

/// <summary>
/// Matching of names that each installation defines (materials, libraries, styles, library parts): another installation may
/// use other names or another language, so a name that is not found is answered with the closest names it does have.
/// </summary>
public static class NameMatch
{
    /// <summary>The name itself if it exists, otherwise the only one that differs just in case; null if none or ambiguous.</summary>
    public static string? Resolve(IEnumerable<string> names, string wanted)
    {
        string[] all = [.. names];
        if (all.Contains(wanted, StringComparer.Ordinal))
        {
            return wanted;
        }

        string[] folded = [.. all.Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal)];
        return folded.Length == 1 ? folded[0] : null;
    }

    /// <summary>Up to <paramref name="max"/> names closest to <paramref name="wanted"/>: those that contain it or are contained in it first, then by edit distance.</summary>
    public static IReadOnlyList<string> Closest(IEnumerable<string> names, string wanted, int max = 5)
    {
        string target = Normalize(wanted);
        return [.. names.Distinct(StringComparer.Ordinal)
            .Select(n => (Name: n, Key: Normalize(n)))
            .Where(n => n.Key.Length > 0)
            .Select(n => (n.Name, Contains: target.Length > 0 && (n.Key.Contains(target, StringComparison.Ordinal) || target.Contains(n.Key, StringComparison.Ordinal)),
                Distance: Distance(n.Key, target) / (double)Math.Max(Math.Max(n.Key.Length, target.Length), 1)))
            .Where(n => n.Contains || n.Distance <= 0.5)
            .OrderBy(n => n.Contains ? 0 : 1).ThenBy(n => n.Distance).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(max, 1))
            .Select(n => n.Name)];
    }

    /// <summary>A sentence with the closest names, or an empty string when none is close.</summary>
    public static string Hint(IEnumerable<string> names, string wanted)
    {
        IReadOnlyList<string> closest = Closest(names, wanted);
        return closest.Count == 0 ? "" : $" Closest names here: {string.Join(", ", closest.Select(n => $"'{n}'"))}.";
    }

    private static string Normalize(string value) =>
        new([.. value.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static int Distance(string a, string b)
    {
        int[] previous = [.. Enumerable.Range(0, b.Length + 1)];
        int[] current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
