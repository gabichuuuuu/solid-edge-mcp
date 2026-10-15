// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Text;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class StlFileTests
{
    [Fact]
    public void Binary_file_is_scaled_and_its_box_read_in_the_new_unit()
    {
        // Two triangles of a 1 x 2 inch rectangle at z = 0.5 in, written in inches.
        byte[] stl = Binary(
            [0, 0, 0.5f, 1, 0, 0.5f, 1, 2, 0.5f],
            [0, 0, 0.5f, 1, 2, 0.5f, 0, 2, 0.5f]);

        StlFile.Info info = StlFile.Read(stl, 25.4, out byte[] scaled);

        Assert.True(info.Binary);
        Assert.Equal(2, info.Triangles);
        Assert.Equal([0, 0, 12.7], info.Min, Tolerance);
        Assert.Equal([25.4, 50.8, 12.7], info.Max, Tolerance);
        Assert.Equal(stl.Length, scaled.Length);
        Assert.Equal(stl.AsSpan(0, 84).ToArray(), scaled.AsSpan(0, 84).ToArray());
        // The normal stays a unit vector; the second vertex of the first triangle is now (25.4, 0, 12.7).
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(scaled.AsSpan(84 + 8)));
        Assert.Equal(25.4f, BinaryPrimitives.ReadSingleLittleEndian(scaled.AsSpan(84 + 24)), 4);

        StlFile.Info again = StlFile.Read(scaled, 1, out _);
        Assert.Equal(info.Max, again.Max, Tolerance);
    }

    [Fact]
    public void Ascii_file_is_scaled_and_its_facets_counted()
    {
        string text = "solid part\n facet normal 0 0 1\n  outer loop\n   vertex 0 0 0\n   vertex 10 0 0\n   vertex 10 -5.5e1 2\n"
            + "  endloop\n endfacet\nendsolid part\n";

        StlFile.Info info = StlFile.Read(Encoding.ASCII.GetBytes(text), 0.1, out byte[] scaled);

        Assert.False(info.Binary);
        Assert.Equal(1, info.Triangles);
        Assert.Equal([0, -5.5, 0], info.Min, Tolerance);
        Assert.Equal([1, 0, 0.2], info.Max, Tolerance);
        string result = Encoding.ASCII.GetString(scaled);
        Assert.Contains("vertex 1.000000E+000 -5.500000E+000 2.000000E-001", result, StringComparison.Ordinal);
        Assert.Contains("facet normal 0 0 1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_files_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => StlFile.Read(Encoding.ASCII.GetBytes("ISO-10303-21;"), 1, out _));
    }

    private static readonly IEqualityComparer<double> Tolerance = EqualityComparer<double>.Create((a, b) => Math.Abs(a - b) < 1e-5, v => 0);

    private static byte[] Binary(params float[][] triangles)
    {
        byte[] data = new byte[84 + 50 * triangles.Length];
        Encoding.ASCII.GetBytes("binary test").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), (uint)triangles.Length);
        for (int t = 0; t < triangles.Length; t++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(84 + 50 * t + 8), 1f);
            for (int k = 0; k < 9; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(84 + 50 * t + 12 + 4 * k), triangles[t][k]);
            }
        }

        return data;
    }
}
