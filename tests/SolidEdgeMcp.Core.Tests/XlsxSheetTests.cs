// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;
using System.Xml.Linq;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class XlsxSheetTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Writes_the_rows_as_numeric_cells_of_one_sheet()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xlsx-{Guid.NewGuid():N}.xlsx");
        try
        {
            XlsxSheet.Write(path, [[0, 0, 0], [30, -2.5, 0], [30, 25, 1e-3]]);
            using ZipArchive zip = ZipFile.OpenRead(path);
            Assert.Equal(
                ["[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/workbook.xml", "xl/worksheets/sheet1.xml"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            XDocument sheet;
            using (Stream stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
            {
                sheet = XDocument.Load(stream);
            }

            XElement[] rows = [.. sheet.Descendants(Main + "row")];
            Assert.Equal(3, rows.Length);
            Assert.Equal(["B2", "-2.5"], [(string)rows[1].Elements(Main + "c").ElementAt(1).Attribute("r")!, rows[1].Elements(Main + "c").ElementAt(1).Value]);
            Assert.Equal("0.001", rows[2].Elements(Main + "c").Last().Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Writes_texts_as_inline_strings_and_numbers_as_numbers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xlsx-{Guid.NewGuid():N}.xlsx");
        try
        {
            object?[] wide = [.. Enumerable.Range(0, 28).Select(i => (object?)i)];
            XlsxSheet.Write(path, [["Item", "Name <A&B>", "Quantity"], [1, null, 2.5], wide]);
            using ZipArchive zip = ZipFile.OpenRead(path);
            XDocument sheet;
            using (Stream stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
            {
                sheet = XDocument.Load(stream);
            }

            XElement[] rows = [.. sheet.Descendants(Main + "row")];
            XElement name = rows[0].Elements(Main + "c").ElementAt(1);
            Assert.Equal(["inlineStr", "Name <A&B>"], [(string)name.Attribute("t")!, name.Value]);
            Assert.Equal(["A2", "C2"], rows[1].Elements(Main + "c").Select(c => (string)c.Attribute("r")!));
            Assert.Null(rows[1].Elements(Main + "c").Last().Attribute("t"));
            Assert.Equal(["Z3", "AA3", "AB3"], rows[2].Elements(Main + "c").Skip(25).Select(c => (string)c.Attribute("r")!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Refuses_values_that_are_not_finite()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xlsx-{Guid.NewGuid():N}.xlsx");
        Assert.Throws<ArgumentException>(() => XlsxSheet.Write(path, [[0, double.NaN, 0]]));
        Assert.False(File.Exists(path));
    }
}
