// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Writes a one-sheet Excel workbook (.xlsx) of numbers with no header, for the Solid Edge commands that read their input from
/// Excel (pattern by table). Only the parts a reader needs are written (content types, relationships, workbook and sheet), so no
/// Excel installation is needed.
/// </summary>
public static class XlsxSheet
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static void Write(string path, IReadOnlyList<double[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Any(r => r.Length > 26 || r.Any(v => !double.IsFinite(v))))
        {
            throw new ArgumentException("Every row needs at most 26 finite values.", nameof(rows));
        }

        using FileStream file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Put(zip, "[Content_Types].xml", Header +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");
        Put(zip, "_rels/.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        Put(zip, "xl/workbook.xml", Header +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Put(zip, "xl/_rels/workbook.xml.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>");
        var sheet = new StringBuilder(Header).Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{(char)('A' + c)}{r + 1}\"><v>{rows[r][c].ToString("R", CultureInfo.InvariantCulture)}</v></c>");
            }

            sheet.Append("</row>");
        }

        Put(zip, "xl/worksheets/sheet1.xml", sheet.Append("</sheetData></worksheet>").ToString());
    }

    private static void Put(ZipArchive zip, string entry, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }
}
