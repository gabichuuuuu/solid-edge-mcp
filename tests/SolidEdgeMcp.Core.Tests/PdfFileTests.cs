// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;
using System.Text;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class PdfFileTests
{
    [Fact]
    public void Inspect_reads_pages_embedded_files_and_3D_inside_compressed_object_streams()
    {
        byte[] pdf = Pdf(
            "1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n",
            "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>> <</Type/Filespec/F (part.step)/UF <FEFF0070006100720074002E006A0074>/EF <</F 7 0 R>>>> "
            + "<</Type/Filespec/F (my \\(1\\).stp)>> <</Type/Annot/Subtype/3D/3DD 8 0 R/F 4>> "
            + "<</Type/3DView/XN (Default)/IN (Default)>> <</Type/3DView/XN <FEFF00490073006F>>> <</Type/3DView/XN (Default)>>");

        PdfFile.Info info = PdfFile.Inspect(pdf);

        Assert.Equal(2, info.Pages);
        Assert.Equal(["my (1).stp", "part.jt", "part.step"], info.EmbeddedFiles);
        Assert.True(info.Has3D);
        Assert.Equal(["Default", "Iso"], info.Views3D);
        Assert.Equal(0, info.Widgets);
    }

    [Fact]
    public void Inspect_takes_the_root_count_of_a_nested_page_tree_and_ignores_other_counts()
    {
        byte[] pdf = Pdf(
            "1 0 obj <</Type/Pages/Kids[2 0 R 5 0 R]/Count 5>> endobj\n2 0 obj <</Type/Pages/Parent 1 0 R/Count 3>> endobj\n"
            + "9 0 obj <</Type/Outlines/Count 12>> endobj\n",
            "<</Type/Annot/Subtype/Widget/F 4>>");

        PdfFile.Info info = PdfFile.Inspect(pdf);

        Assert.Equal(5, info.Pages);
        Assert.Empty(info.EmbeddedFiles);
        Assert.False(info.Has3D);
        Assert.Empty(info.Views3D);
        Assert.Equal(1, info.Widgets);
    }

    [Fact]
    public void Inspect_reads_the_distinct_page_sizes_in_mm()
    {
        // A4 landscape twice (as Microsoft Print to PDF writes it) and A3 portrait inside an object stream.
        byte[] pdf = Pdf(
            "3 0 obj <</Type/Page/MediaBox [ 0.0 0.0 841.92004 595.32001 ]>> endobj\n4 0 obj <</Type/Page/MediaBox[0 0 841.92004 595.32001]>> endobj\n",
            "<</Type/Page/MediaBox [0 0 841.92 1190.52]>>");

        PdfFile.Info info = PdfFile.Inspect(pdf);

        Assert.Collection(info.PageSizes,
            size => Assert.Equal([297.0, 210.0], size),
            size => Assert.Equal([297.0, 420.0], size));
    }

    [Fact]
    public void IsPdf_checks_the_header()
    {
        string folder = Directory.CreateTempSubdirectory("pdffile").FullName;
        try
        {
            string pdf = Path.Combine(folder, "a.pdf");
            string text = Path.Combine(folder, "b.pdf");
            File.WriteAllBytes(pdf, Pdf("", ""));
            File.WriteAllText(text, "not a pdf");
            File.WriteAllText(Path.Combine(folder, "c.pdf"), "%P");

            Assert.True(PdfFile.IsPdf(pdf));
            Assert.False(PdfFile.IsPdf(text));
            Assert.False(PdfFile.IsPdf(Path.Combine(folder, "c.pdf")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A small PDF: <paramref name="plain"/> as written, <paramref name="packed"/> in a Flate-compressed object stream.</summary>
    private static byte[] Pdf(string plain, string packed)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.Latin1.GetBytes(packed));
        }

        using var file = new MemoryStream();
        void Write(string text) => file.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.7\n" + plain);
        Write($"5 0 obj <</Type/ObjStm/N 1/First 0/Filter/FlateDecode/Length {compressed.Length}>>\nstream\r\n");
        file.Write(compressed.ToArray());
        Write("\r\nendstream\nendobj\n%%EOF\n");
        return file.ToArray();
    }
}
