// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Writes a table read from Solid Edge (a bill of materials, the cells of a drawing table) to a .csv or .xlsx file of an
/// allowed folder, with its headers in the first row. Solid Edge has no call that writes them: a parts list only copies
/// itself to the clipboard (CopyToClipboard), and the Reports dialog has no object in the type libraries.
/// </summary>
internal static class TableFile
{
    internal static string Write(SeContext context, string path, bool overwrite, IReadOnlyList<string?> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        string target = context.Paths.Check(path);
        string extension = Path.GetExtension(target).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx"))
        {
            throw ToolException.InvalidArgument($"Cannot write a table to '{extension}'.", "Use a .csv or .xlsx file.");
        }

        if (File.Exists(target) && !overwrite)
        {
            throw new ToolException("file_exists", $"'{target}' already exists.", "Pass overwrite=true to replace it.");
        }

        List<IReadOnlyList<object?>> all = [[.. headers.Cast<object?>()], .. rows];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (extension == ".csv")
            {
                CsvFile.Write(target, [.. all.Select(row => (IReadOnlyList<string?>)[.. row.Select(v => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture))])]);
            }
            else
            {
                XlsxSheet.Write(target, all);
            }
        }
        catch (IOException ex)
        {
            throw new ToolException("write_failed", $"Could not write '{target}': {ex.Message}", "Close the file if another program has it open.", ex);
        }

        return target;
    }
}
