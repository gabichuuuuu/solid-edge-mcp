// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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
