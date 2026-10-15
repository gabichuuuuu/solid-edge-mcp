// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>
/// STL files, binary or ASCII, which carry no unit: their triangle count and box, and a copy with every coordinate scaled.
/// Solid Edge reads and writes them in the unit of its STL translator options (inches by default), the tools in mm.
/// </summary>
public static partial class StlFile
{
    /// <summary>Contents of an STL file: triangles, whether it is binary, and the box of its vertices (in the file's unit).</summary>
    public sealed record Info(int Triangles, bool Binary, double[] Min, double[] Max);

    public static Info Inspect(string path) => Read(File.ReadAllBytes(path), 1, out _);

    /// <summary>Writes <paramref name="target"/> (it may be the source) with every vertex multiplied by the factor; returns its contents.</summary>
    public static Info Scale(string source, string target, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The scale factor must be a positive number.");
        }

        Info info = Read(File.ReadAllBytes(source), factor, out byte[] scaled);
        File.WriteAllBytes(target, scaled);
        return info;
    }

    /// <summary>Reads the file with every vertex multiplied by the factor; <paramref name="scaled"/> is the file written that way.</summary>
    public static Info Read(byte[] data, double factor, out byte[] scaled)
    {
        ArgumentNullException.ThrowIfNull(data);
        var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var max = new[] { double.MinValue, double.MinValue, double.MinValue };
        void Add(int axis, double value)
        {
            min[axis] = Math.Min(min[axis], value);
            max[axis] = Math.Max(max[axis], value);
        }

        int triangles;
        bool binary = data.Length >= 84 && 84L + 50L * BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80)) == data.Length;
        if (binary)
        {
            // Header (80 bytes), count, then per triangle a normal and three vertices (floats) and 2 attribute bytes.
            scaled = (byte[])data.Clone();
            triangles = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
            for (int t = 0; t < triangles; t++)
            {
                for (int k = 0; k < 9; k++)
                {
                    Span<byte> at = scaled.AsSpan(84 + 50 * t + 12 + 4 * k, 4);
                    float value = (float)(BinaryPrimitives.ReadSingleLittleEndian(at) * factor);
                    BinaryPrimitives.WriteSingleLittleEndian(at, value);
                    Add(k % 3, value);
                }
            }
        }
        else
        {
            string text = Encoding.ASCII.GetString(data);
            if (!text.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase) || !Vertex().IsMatch(text))
            {
                throw new InvalidDataException("The file is not an STL file (neither a binary STL nor ASCII facets).");
            }

            triangles = Facet().Count(text);
            string result = Vertex().Replace(text, m =>
            {
                var values = new string[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    double value = double.Parse(m.Groups[axis + 2].Value, NumberStyles.Float, CultureInfo.InvariantCulture) * factor;
                    Add(axis, value);
                    values[axis] = value.ToString("E6", CultureInfo.InvariantCulture);
                }

                return $"{m.Groups[1].Value}{string.Join(' ', values)}";
            });
            scaled = Encoding.ASCII.GetBytes(result);
        }

        return triangles == 0 || min[0] == double.MaxValue
            ? new Info(triangles, binary, [0, 0, 0], [0, 0, 0])
            : new Info(triangles, binary, min, max);
    }

    [GeneratedRegex(@"(vertex\s+)(\S+)\s+(\S+)\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex Vertex();

    [GeneratedRegex(@"\bfacet\s+normal\b", RegexOptions.IgnoreCase)]
    private static partial Regex Facet();
}
