using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>
/// What a PDF file holds, read without a PDF library to check what Solid Edge wrote: the page count of its page tree, the
/// names of embedded files and whether it has 3D annotations. Dictionaries are looked for in the file and inside its
/// Flate-compressed streams (object streams), where Solid Edge 2026 keeps most of them.
/// </summary>
public static partial class PdfFile
{
    /// <summary>Largest stream that is decompressed, so a damaged or hostile file cannot exhaust memory.</summary>
    private const int MaxStream = 64 * 1024 * 1024;

    public sealed record Info(int? Pages, IReadOnlyList<string> EmbeddedFiles, bool Has3D);

    /// <summary>Whether the file starts with the PDF header.</summary>
    public static bool IsPdf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[5];
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.SequenceEqual("%PDF-"u8);
    }

    public static Info Inspect(string path) => Inspect(File.ReadAllBytes(path));

    public static Info Inspect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<string> texts = [Encoding.Latin1.GetString(data), .. Streams(data)];
        int? pages = null;
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool has3D = false;
        foreach (string text in texts)
        {
            foreach (Match match in PagesCount().Matches(text))
            {
                // The root of the page tree counts every page; intermediate nodes count fewer.
                int count = int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                pages = Math.Max(pages ?? 0, count);
            }

            if (text.Contains("/Filespec", StringComparison.Ordinal) || text.Contains("/EmbeddedFiles", StringComparison.Ordinal))
            {
                foreach (Match match in FileName().Matches(text))
                {
                    string name = Decode(match.Groups["name"].Value).TrimEnd('\0').Trim();
                    if (name.Length > 0)
                    {
                        files.Add(name);
                    }
                }
            }

            has3D |= ThreeD().IsMatch(text);
        }

        return new Info(pages, [.. files], has3D);
    }

    /// <summary>Contents of the streams that decompress with zlib (FlateDecode); others are skipped.</summary>
    private static IEnumerable<string> Streams(byte[] data)
    {
        string raw = Encoding.Latin1.GetString(data);
        int index = 0;
        while ((index = raw.IndexOf("stream", index, StringComparison.Ordinal)) >= 0)
        {
            int start = index + "stream".Length;
            index = start;
            if (start > "stream".Length && raw[start - "stream".Length - 1] == 'd')
            {
                continue; // "endstream"
            }

            if (start < raw.Length && raw[start] == '\r')
            {
                start++;
            }

            if (start >= raw.Length || raw[start] != '\n')
            {
                continue;
            }

            start++;
            int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            string? text = Inflate(data, start, end - start);
            if (text is not null)
            {
                yield return text;
            }

            index = end + "endstream".Length;
        }
    }

    private static string? Inflate(byte[] data, int offset, int length)
    {
        if (length < 2 || (data[offset] & 0x0F) != 8)
        {
            return null; // not a zlib header (deflate method 8)
        }

        try
        {
            using var input = new MemoryStream(data, offset, length, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxStream)
                {
                    return null;
                }
            }

            return Encoding.Latin1.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A PDF string: a literal "(…)" with escapes or a hex "&lt;…&gt;", in PDFDocEncoding or UTF-16BE with a BOM.</summary>
    private static string Decode(string value)
    {
        byte[] bytes;
        if (value.StartsWith('<'))
        {
            string hex = new([.. value.Trim('<', '>').Where(Uri.IsHexDigit)]);
            if (hex.Length % 2 == 1)
            {
                hex += "0";
            }

            bytes = Convert.FromHexString(hex);
        }
        else
        {
            bytes = Encoding.Latin1.GetBytes(Unescape(value[1..^1]));
        }

        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.Latin1.GetString(bytes);
    }

    private static string Unescape(string literal)
    {
        var text = new StringBuilder(literal.Length);
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c != '\\' || i + 1 >= literal.Length)
            {
                text.Append(c);
                continue;
            }

            char next = literal[++i];
            switch (next)
            {
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case >= '0' and <= '7':
                    int value = next - '0';
                    for (int k = 0; k < 2 && i + 1 < literal.Length && literal[i + 1] is >= '0' and <= '7'; k++)
                    {
                        value = (value * 8) + (literal[++i] - '0');
                    }

                    text.Append((char)(value & 0xFF));
                    break;
                case '\r' or '\n': break; // line continuation
                default: text.Append(next); break;
            }
        }

        return text.ToString();
    }

    [GeneratedRegex(@"/Type\s*/Pages\b[^>]*?/Count\s+(?<n>\d+)|/Count\s+(?<n>\d+)[^<>]*?/Type\s*/Pages\b", RegexOptions.CultureInvariant)]
    private static partial Regex PagesCount();

    [GeneratedRegex(@"/(?:UF|F)\s*(?<name>\((?:\\.|[^\\)]){1,400}\)|<[0-9A-Fa-f\s]{2,1600}>)", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    [GeneratedRegex(@"/Subtype\s*/3D\b", RegexOptions.CultureInvariant)]
    private static partial Regex ThreeD();
}
