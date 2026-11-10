// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.IO.Compression;
using System.Text;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Encodes a picture as a PNG file (8-bit RGB, no filtering, zlib-compressed), from pixels in the order Windows bitmaps
/// keep them: blue, green, red and one unused byte per pixel, rows from the top.
/// </summary>
public static class PngFile
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(ReadOnlySpan<byte> bgrx, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgrx.Length < width * height * 4)
        {
            throw new ArgumentException($"{bgrx.Length} bytes do not hold a {width} x {height} picture of 4 bytes per pixel.", nameof(bgrx));
        }

        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] row = new byte[1 + width * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int source = (y * width + x) * 4;
                    row[1 + x * 3] = bgrx[source + 2];
                    row[2 + x * 3] = bgrx[source + 1];
                    row[3 + x * 3] = bgrx[source];
                }

                zlib.Write(row);
            }
        }

        byte[] header = new byte[13];
        BigEndian(header, 0, width);
        BigEndian(header, 4, height);
        header[8] = 8;
        header[9] = 2;
        using var png = new MemoryStream();
        png.Write(Signature);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", pixels.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Width and height of a PNG file, read from its header.</summary>
    public static (int Width, int Height) Size(ReadOnlySpan<byte> png) =>
        png.Length >= 24 && png[..8].SequenceEqual(Signature)
            ? (ReadBigEndian(png[16..]), ReadBigEndian(png[20..]))
            : throw new ArgumentException("Not a PNG file.", nameof(png));

    /// <summary>
    /// Pixels of an 8-bit RGB PNG that is not interlaced, the kind Solid Edge writes, as red, green and blue bytes with rows
    /// from the top; null for any other kind or a damaged file.
    /// </summary>
    public static (int Width, int Height, byte[] Rgb)? Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
        {
            return null;
        }

        int width = 0, height = 0;
        using var compressed = new MemoryStream();
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            int length = ReadBigEndian(png[offset..]);
            if (length < 0 || length > png.Length - offset - 12)
            {
                return null;
            }

            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                // Bit depth 8, colour type 2 (RGB), compression and filter method 0, no interlace.
                if (length < 13 || data[8] != 8 || data[9] != 2 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    return null;
                }

                (width, height) = (ReadBigEndian(data), ReadBigEndian(data[4..]));
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        long size = (3L * width + 1) * height;
        if (width <= 0 || height <= 0 || size > Array.MaxLength)
        {
            return null;
        }

        byte[] filtered = new byte[size];
        try
        {
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            zlib.ReadExactly(filtered);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }

        // Each row starts with its filter type; the filters predict a byte from the one of the pixel to its left (a), above (b)
        // and above-left (c), already decoded.
        int stride = 3 * width;
        byte[] rgb = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = filtered[y * (stride + 1)];
            ReadOnlySpan<byte> line = filtered.AsSpan(y * (stride + 1) + 1, stride);
            Span<byte> row = rgb.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> above = y > 0 ? rgb.AsSpan((y - 1) * stride, stride) : new byte[stride];
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 3 ? row[x - 3] : 0;
                int b = above[x];
                int c = x >= 3 ? above[x - 3] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => -1,
                };
                if (predicted < 0)
                {
                    return null;
                }

                row[x] = (byte)(line[x] + predicted);
            }
        }

        return (width, height, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>CRC-32 of the PNG specification (ISO 3309), over the chunk type and data.</summary>
    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BigEndian(length, 0, data.Length);
        stream.Write(length);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typed);
        byte[] crc = new byte[4];
        BigEndian(crc, 0, unchecked((int)Crc(typed)));
        stream.Write(crc);
    }

    private static void BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> bytes) => bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3];
}
