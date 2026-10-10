using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class UnitsTests
{
    [Fact]
    public void Length_area_volume_round_trip()
    {
        Assert.Equal(0.1, Units.MmToM(100), 15);
        Assert.Equal(100, Units.MToMm(0.1), 12);
        // Block 100 × 50 × 20 mm = 1.0·10⁻⁴ m³ = 100,000 mm³.
        Assert.Equal(100_000, Units.M3ToMm3(1e-4), 9);
        Assert.Equal(1e-4, Units.Mm3ToM3(100_000), 15);
        Assert.Equal(16_000, Units.M2ToMm2(0.016), 9);
        Assert.Equal(0.016, Units.Mm2ToM2(16_000), 15);
        Assert.Equal(1e6, Units.KgM2ToKgMm2(1), 6);
    }

    [Fact]
    public void Angles()
    {
        Assert.Equal(Math.PI / 2, Units.DegToRad(90), 15);
        Assert.Equal(180, Units.RadToDeg(Math.PI), 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RequireFinite_rejects_non_finite(double value) =>
        Assert.Throws<ToolException>(() => Units.RequireFinite(value, "x"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequirePositive_rejects_zero_and_negative(double value) =>
        Assert.Throws<ToolException>(() => Units.RequirePositive(value, "x"));
}
