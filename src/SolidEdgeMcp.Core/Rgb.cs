// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Colours as the tools give them ('#RRGGBB'), as the API stores them (a COLORREF, 0x00BBGGRR) and as channels
/// from 0 to 1.
/// </summary>
public static class Rgb
{
    /// <summary>0xRRGGBB of '#RRGGBB'. Spaces around the text are ignored and the '#' may be missing.</summary>
    public static bool TryParse(string? text, out int rgb)
    {
        rgb = 0;
        ReadOnlySpan<char> digits = text.AsSpan().Trim();
        if (digits.StartsWith("#"))
        {
            digits = digits[1..];
        }

        return digits.Length == 6 && int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    public static int Parse(string text, string? argument = null) => TryParse(text, out int rgb)
        ? rgb
        : throw ToolException.InvalidArgument($"{(argument is null ? "" : $"'{argument}': ")}'{text}' is not a #RRGGBB colour.",
            "Example: '#FF8000' (orange).");

    public static string Hex(int rgb) => $"#{rgb & 0xFFFFFF:X6}";

    public static string Hex(float red, float green, float blue) => $"#{Channel(red):X2}{Channel(green):X2}{Channel(blue):X2}";

    /// <summary>Channels from 0 to 1 of '#RRGGBB'.</summary>
    public static (float Red, float Green, float Blue) Channels(string text, string? argument = null)
    {
        int rgb = Parse(text, argument);
        return (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }

    /// <summary>COLORREF (0x00BBGGRR) of '#RRGGBB'.</summary>
    public static int ColorRef(string text, string? argument = null) => Swap(Parse(text, argument));

    /// <summary>'#RRGGBB' of a COLORREF (0x00BBGGRR).</summary>
    public static string HexOfColorRef(int colorRef) => Hex(Swap(colorRef));

    private static int Swap(int value) => ((value & 0xFF) << 16) | (value & 0xFF00) | ((value >> 16) & 0xFF);

    private static int Channel(float value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
