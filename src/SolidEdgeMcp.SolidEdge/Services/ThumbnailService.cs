// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// The thumbnail saved in a Solid Edge file (the picture Windows Explorer shows), read without opening the file through
/// SeThumbnail, a component installed with Solid Edge. Verified in Solid Edge 2026: <c>GetThumbnail</c> gives a 128 x 128,
/// 24-bit bitmap for parts, assemblies and drawings, and a null handle for a file without one or that is not a Solid Edge
/// file. Its interface is dual but the object has no IDispatch, so it is called through the interface itself.
/// </summary>
public static class ThumbnailService
{
    private const string ProgId = "SeThumbnail.SeThumbnailExtractor";
    private static readonly string[] Extensions = [".par", ".psm", ".asm", ".dft", ".pwd"];

    public sealed record Thumbnail(string File, byte[] Png, int Width, int Height);

    public static Thumbnail Read(SeContext context, string? path)
    {
        string file = context.Paths.Check(path);
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file does not exist: '{file}'.");
        }

        if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' is not a Solid Edge file ({string.Join(", ", Extensions)}).");
        }

        Type type = Type.GetTypeFromProgID(ProgId) ?? throw new ToolException("component_not_registered",
            $"{ProgId}, installed with Solid Edge, is not registered on this computer.", "Repair the Solid Edge installation.");
        object extractor = Activator.CreateInstance(type)!;
        try
        {
            ((ISeThumbnailExtractor)extractor).GetThumbnail(file, out int handle, Type.Missing);
            if (handle == 0)
            {
                throw new ToolException("no_thumbnail", $"'{Path.GetFileName(file)}' has no thumbnail.",
                    "Solid Edge saves one with the file: open it (se_documents open) and capture the view (se_view capture), or save it again.");
            }

            var bitmap = new IntPtr(handle);
            try
            {
                (byte[] pixels, int width, int height) = Pixels(bitmap);
                return new Thumbnail(file, PngFile.Encode(pixels, width, height), width, height);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        catch (COMException ex)
        {
            throw new ToolException("thumbnail_failed", $"The thumbnail of '{Path.GetFileName(file)}' could not be read: {ex.Message.Trim()}", null, ex);
        }
        finally
        {
            Marshal.FinalReleaseComObject(extractor);
        }
    }

    /// <summary>The pixels of a bitmap as 32-bit blue, green, red, unused, rows from the top.</summary>
    private static (byte[] Pixels, int Width, int Height) Pixels(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BitmapHeader>(), out BitmapHeader header) == 0 || header.Width <= 0 || header.Height <= 0)
        {
            throw new ToolException("thumbnail_failed", "The thumbnail bitmap could not be read.");
        }

        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = header.Width,
            Height = -header.Height,
            Planes = 1,
            BitCount = 32,
        };
        byte[] pixels = new byte[header.Width * header.Height * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)header.Height, pixels, ref info, 0) != header.Height)
            {
                throw new ToolException("thumbnail_failed", "The pixels of the thumbnail could not be read.");
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screen);
        }

        return (pixels, header.Width, header.Height);
    }

    [ComImport]
    [Guid("AC064B8B-E1E3-4287-B103-1441B8D8BAB4")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface ISeThumbnailExtractor
    {
        void GetThumbnail([MarshalAs(UnmanagedType.BStr)] string file, out int bitmap, [MarshalAs(UnmanagedType.Struct)] object sketch);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BitmapHeader header);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
