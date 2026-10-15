// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Writes rows of text as comma-separated values (RFC 4180): a value with a comma, a quote or a line break goes in quotes,
/// with its quotes doubled; lines end in CRLF. UTF-8 with a byte order mark, so spreadsheet programs read accented text.
/// </summary>
public static class CsvFile
{
    public static void Write(string path, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        foreach (IReadOnlyList<string?> row in rows)
        {
            text.Append(string.Join(",", row.Select(Field))).Append("\r\n");
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private static string Field(string? value) =>
        value is null ? "" : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
