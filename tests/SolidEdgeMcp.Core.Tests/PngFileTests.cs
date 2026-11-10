// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

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

    [Fact]
    public void Reads_back_the_pixels_it_writes()
    {
        // 3 x 2: red, green, blue / white, black, grey (blue, green, red, unused per pixel).
        byte[] bgrx = [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 0, 0, 0, 0, 128, 128, 128, 0];

        (int width, int height, byte[] rgb) = PngFile.Decode(PngFile.Encode(bgrx, 3, 2))!.Value;

        Assert.Equal((3, 2), (width, height));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 128, 128, 128 }, rgb);
    }

    [Fact]
    public void Undoes_each_row_filter()
    {
        // 2 x 4 pixels; each row filtered as the PNG specification defines, by hand: Sub, Up, Average and Paeth.
        byte[] filtered =
        [
            1, 10, 20, 30, 40, 50, 60,
            2, 2, 4, 6, 10, 10, 10,
            3, 194, 88, 32, 20, 70, 95,
            4, 61, 150, 78, 2, 9, 85,
        ];

        byte[] rgb = PngFile.Decode(Png(2, 4, colourType: 2, filtered))!.Value.Rgb;

        Assert.Equal(new byte[]
        {
            10, 20, 30, 50, 70, 90,
            12, 24, 36, 60, 80, 100,
            200, 100, 50, 150, 160, 170,
            5, 250, 128, 7, 3, 255,
        }, rgb);
    }

    [Fact]
    public void Reads_only_8_bit_rgb_that_is_not_interlaced()
    {
        byte[] row = [0, 1, 2, 3, 4, 5, 6];

        Assert.NotNull(PngFile.Decode(Png(2, 1, colourType: 2, row)));
        Assert.Null(PngFile.Decode(Png(2, 1, colourType: 6, row)));
        Assert.Null(PngFile.Decode(Png(2, 2, colourType: 2, row)));
        Assert.Null(PngFile.Decode(Png(2, 1, colourType: 2, [5, 1, 2, 3, 4, 5, 6])));
        Assert.Null(PngFile.Decode("not a png"u8));
    }

    /// <summary>A PNG with the given rows (each with its filter byte) as its only image data.</summary>
    private static byte[] Png(int width, int height, byte colourType, byte[] rows)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        byte[] header = [.. BigEndian(width), .. BigEndian(height), 8, colourType, 0, 0, 0];
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header), .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];

        static byte[] Chunk(string type, byte[] data)
        {
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            return [.. BigEndian(data.Length), .. typed, .. BigEndian(unchecked((int)PngFile.Crc(typed)))];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

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
