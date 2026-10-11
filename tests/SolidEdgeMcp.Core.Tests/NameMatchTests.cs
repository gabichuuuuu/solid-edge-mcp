using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class NameMatchTests
{
    private static readonly string[] Materials = ["Steel", "Stainless Steel", "Aluminum, 6061-T6", "Copper", "ABS Plastic"];

    [Fact]
    public void Resolve_takes_the_exact_name_or_the_only_one_that_differs_in_case()
    {
        Assert.Equal("Steel", NameMatch.Resolve(Materials, "Steel"));
        Assert.Equal("Copper", NameMatch.Resolve(Materials, "copper"));
        Assert.Null(NameMatch.Resolve(Materials, "Acero"));
        Assert.Null(NameMatch.Resolve(["Red", "RED"], "red"));
    }

    [Fact]
    public void Closest_puts_names_that_contain_the_text_first_then_near_spellings()
    {
        Assert.Equal(["Steel", "Stainless Steel"], NameMatch.Closest(Materials, "steel"));
        Assert.Equal("Aluminum, 6061-T6", NameMatch.Closest(Materials, "aluminium 6061").First());
        Assert.Contains("Copper", NameMatch.Closest(Materials, "Coper"));
        Assert.Empty(NameMatch.Closest(Materials, "Zzzz"));
    }

    [Fact]
    public void Hint_is_empty_when_nothing_is_close()
    {
        Assert.Equal("", NameMatch.Hint(Materials, "Zzzz"));
        Assert.Contains("'Steel'", NameMatch.Hint(Materials, "Stel"));
    }
}
