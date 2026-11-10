// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tests;

public sealed class GeoTests
{
    [Fact]
    public void Vector_algebra_of_a_3_4_5_triangle()
    {
        double[] a = [3, 0, 0];
        double[] b = [0, 4, 0];

        Assert.Equal(0, Geo.Dot(a, b));
        Assert.Equal(9, Geo.Dot(a, a));
        Assert.Equal([3, -4, 0], Geo.Minus(a, b));
        Assert.Equal([6, 0, 0], Geo.Scale(a, 2));
        Assert.Equal([0, 0, 12], Geo.Cross(a, b));
        Assert.Equal(5, Geo.Length(Geo.Minus(a, b)));
        Assert.Equal(5, Geo.Distance(a, b));
    }

    [Fact]
    public void Unit_gives_the_direction()
    {
        Assert.Equal([0.6, 0.8, 0], Geo.Unit([3, 4, 0]));
        Assert.Equal([0.6, 0.8, 0], Assert.IsType<double[]>(Geo.TryUnit([3, 4, 0])));
    }

    [Fact]
    public void A_vector_too_short_has_no_direction()
    {
        double[] zero = [0, 0, 0];

        Assert.Null(Geo.TryUnit(zero));
        Assert.Null(Geo.TryUnit([1e-13, 0, 0]));
        Assert.Same(zero, Geo.Unit(zero));
    }
}
