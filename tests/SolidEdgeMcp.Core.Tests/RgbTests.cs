// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class RgbTests
{
    [Theory]
    [InlineData("#FF8000", 0xFF8000)]
    [InlineData("#ff8000", 0xFF8000)]
    [InlineData(" #FF8000 ", 0xFF8000)]
    [InlineData("FF8000", 0xFF8000)]
    [InlineData("#000000", 0)]
    public void Parse_reads_six_hexadecimal_digits_with_or_without_the_hash(string text, int expected) =>
        Assert.Equal(expected, Rgb.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("#FF800")]
    [InlineData("#FF80000")]
    [InlineData("# FF800")]
    [InlineData("##FF8000")]
    [InlineData("#GG8000")]
    [InlineData("#+F8000")]
    [InlineData("orange")]
    public void Parse_rejects_anything_else_and_names_the_argument(string text)
    {
        ToolException error = Assert.Throws<ToolException>(() => Rgb.Parse(text, "color"));

        Assert.Equal("invalid_argument", error.Code);
        Assert.Contains("'color'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorRef_swaps_red_and_blue_and_comes_back()
    {
        // COLORREF is 0x00BBGGRR: orange #FF8000 is 0x0080FF.
        Assert.Equal(0x0080FF, Rgb.ColorRef("#FF8000"));
        Assert.Equal("#FF8000", Rgb.HexOfColorRef(0x0080FF));
    }

    [Fact]
    public void Channels_and_Hex_round_trip()
    {
        (float red, float green, float blue) = Rgb.Channels("#FF8000");

        Assert.Equal(1f, red);
        Assert.Equal(128 / 255f, green);
        Assert.Equal(0f, blue);
        Assert.Equal("#FF8000", Rgb.Hex(red, green, blue));
        Assert.Equal("#FF0000", Rgb.Hex(2f, -1f, 0f));
    }
}
