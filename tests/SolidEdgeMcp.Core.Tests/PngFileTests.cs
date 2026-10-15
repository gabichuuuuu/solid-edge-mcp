// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;
using System.Text;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class PngFileTests
{
    [Fact]
    public void Writes_the_pixels_as_rgb_rows_with_valid_chunks()
    {
        // 2 x 2: red, green / blue, white (blue, green, red, unused per pixel).
        byte[] bgrx = [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
        byte[] png = PngFile.Encode(bgrx, 2, 2);

        Assert.Equal((2, 2), PngFile.Size(png));
        var chunks = Chunks(png);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
        Assert.Equal(new byte[] { 8, 2, 0, 0, 0 }, chunks[0].Data[8..]);

        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255 }, inflated.ToArray());
    }

    [Fact]
    public void Refuses_too_few_bytes() =>
        Assert.Throws<ArgumentException>(() => PngFile.Encode(new byte[15], 2, 2));

    [Fact]
    public void Crc_matches_the_iend_chunk_of_every_png() =>
        Assert.Equal(0xAE426082u, PngFile.Crc(Encoding.ASCII.GetBytes("IEND")));

    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        int at = 8;
        while (at < png.Length)
        {
            int length = png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3];
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            byte[] data = png[(at + 8)..(at + 8 + length)];
            uint crc = (uint)(png[at + 8 + length] << 24 | png[at + 9 + length] << 16 | png[at + 10 + length] << 8 | png[at + 11 + length]);
            Assert.Equal(PngFile.Crc(png.AsSpan(at + 4, 4 + length)), crc);
            chunks.Add((type, data));
            at += 12 + length;
        }

        return chunks;
    }
}
