// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using static SolidEdgeMcp.Core.Region2d;

namespace SolidEdgeMcp.Core.Tests;

public sealed class Region2dTests
{
    private const double Join = 1e-9, Margin = 1e-5;

    private static Curve Line(double x1, double y1, double x2, double y2) => new([(x1, y1), (x2, y2)], false);

    private static Curve[] Rectangle(double x1, double y1, double x2, double y2) =>
        [Line(x1, y1, x2, y1), Line(x2, y1, x2, y2), Line(x2, y2, x1, y2), Line(x1, y2, x1, y1)];

    private static Curve Circle(double cx, double cy, double r, int n = 720) =>
        new([.. Enumerable.Range(0, n).Select(i => (cx + (r * Math.Cos(2 * Math.PI * i / n)), cy + (r * Math.Sin(2 * Math.PI * i / n))))], true);

    [Fact]
    public void Rectangle_of_lines_proves_inside_and_outside()
    {
        Curve[] r = Rectangle(0.05, 0.05, 0.11, 0.09);
        Assert.Equal(Place.Inside, Locate(r, (0.055, 0.055), Join, Margin));
        Assert.Equal(Place.Outside, Locate(r, (0.12, 0.07), Join, Margin));
        Assert.Equal(Place.OnElement, Locate(r, (0.08, 0.09), Join, Margin));
    }

    [Fact]
    public void Line_across_the_region_does_not_break_the_proof()
    {
        // Verified in Solid Edge: the point chooses the half of a rectangle split by a line that ends on its sides.
        Curve[] curves = [.. Rectangle(0.05, 0.05, 0.11, 0.09), Line(0.08, 0.05, 0.08, 0.09)];
        Assert.Equal(Place.Inside, Locate(curves, (0.06, 0.07), Join, Margin));
        Assert.Equal(Place.Inside, Locate(curves, (0.1, 0.07), Join, Margin));
    }

    [Fact]
    public void Point_in_the_range_but_outside_a_triangle_is_outside()
    {
        // The flood point that brought Solid Edge 2026 down (RPC_E_SERVERFAULT): inside the range of the triangle, outside it.
        Curve[] t = [Line(0.15, 0.05, 0.21, 0.05), Line(0.21, 0.05, 0.15, 0.09), Line(0.15, 0.09, 0.15, 0.05)];
        Assert.Equal(Place.Outside, Locate(t, (0.205, 0.085), Join, Margin));
        Assert.Equal(Place.Inside, Locate(t, (0.16, 0.06), Join, Margin));
    }

    [Fact]
    public void Open_shapes_have_no_loop()
    {
        Curve[] open = [Line(0, 0, 0.06, 0), Line(0.06, 0, 0.06, 0.04), Line(0.06, 0.04, 0, 0.04)];
        Assert.Equal(Place.NoLoop, Locate(open, (0.03, 0.02), Join, Margin));

        // A gap of 1 µm is not closed.
        Curve[] gap = [Line(0, 0, 0.06, 0), Line(0.06, 0, 0.06, 0.04), Line(0.06, 0.04, 0, 0.04), Line(0, 0.04, 0, 0.000001)];
        Assert.Equal(Place.NoLoop, Locate(gap, (0.03, 0.02), Join, Margin));

        // Four lines crossing like '#': the middle square is closed, but no ends meet, so it is not proved (refused, never guessed).
        Curve[] hash = [Line(0.01, 0, 0.01, 0.04), Line(0.03, 0, 0.03, 0.04), Line(0, 0.01, 0.04, 0.01), Line(0, 0.03, 0.04, 0.03)];
        Assert.Equal(Place.NoLoop, Locate(hash, (0.02, 0.02), Join, Margin));
    }

    [Fact]
    public void Closed_curves_and_cycles_with_a_shared_side()
    {
        Curve c = Circle(0.08, 0.07, 0.01);
        Assert.Equal(Place.Inside, Locate([c], (0.08, 0.07), Join, Margin));
        Assert.Equal(Place.Outside, Locate([c], (0.095, 0.07), Join, Margin));

        // A square with a diagonal from corner to corner: two independent loops (5 sides − 4 corners + 1), both triangles proved.
        Curve[] theta = [.. Rectangle(0, 0, 0.04, 0.04), Line(0, 0, 0.04, 0.04)];
        Assert.Equal(2, Loops(theta, Join).Count);
        Assert.Equal(Place.Inside, Locate(theta, (0.03, 0.01), Join, Margin));
        Assert.Equal(Place.Inside, Locate(theta, (0.01, 0.03), Join, Margin));
        Assert.Equal(Place.Outside, Locate(theta, (0.05, 0.01), Join, Margin));
    }

    [Fact]
    public void Interior_point_lies_inside_the_loop()
    {
        Curve[] l = [Line(0, 0, 0.06, 0), Line(0.06, 0, 0.06, 0.02), Line(0.06, 0.02, 0.02, 0.02), Line(0.02, 0.02, 0.02, 0.04),
            Line(0.02, 0.04, 0, 0.04), Line(0, 0.04, 0, 0)];
        (double X, double Y)? p = InteriorPoint(l, Join, Margin);
        Assert.NotNull(p);
        Assert.Equal(Place.Inside, Locate(l, p!.Value, Join, Margin));
        Assert.Null(InteriorPoint([Line(0, 0, 0.06, 0)], Join, Margin));
    }

    [Fact]
    public void Holes_follow_the_parallel_axis_theorem()
    {
        // 60 x 40 rectangle minus its left half = the right half: A 1200, centroid (45, 20), Ix = 30·40³/12, Iy = 40·30³/12.
        var outer = new Moments(2400, 200, 30, 20, 60 * 40 * 40 * 40 / 12.0, 40 * 60 * 60 * 60 / 12.0, 0);
        var left = new Moments(1200, 140, 15, 20, 30 * 40 * 40 * 40 / 12.0, 40 * 30 * 30 * 30 / 12.0, 0);
        Moments half = outer.Minus([left]);
        Assert.Equal(1200, half.Area, 9);
        Assert.Equal(45, half.Cx, 9);
        Assert.Equal(20, half.Cy, 9);
        Assert.Equal(160_000, half.Ix, 6);
        Assert.Equal(90_000, half.Iy, 6);
        Assert.Equal(0, half.Ixy, 6);

        // L shape: the 60 x 40 rectangle minus its 40 x 20 top-right corner, against the sum of its two rectangles by hand:
        // A 1600, centroid (25, 15), Ix 173 333.3, Iy 493 333.3, Ixy = 1200·5·(−5) + 400·(−15)·15 = −120 000.
        var corner = new Moments(800, 120, 40, 30, 40 * 20 * 20 * 20 / 12.0, 20 * 40 * 40 * 40 / 12.0, 0);
        Moments shape = outer.Minus([corner]);
        Assert.Equal(1600, shape.Area, 9);
        Assert.Equal(25, shape.Cx, 9);
        Assert.Equal(15, shape.Cy, 9);
        Assert.Equal(173_333.333333, shape.Ix, 5);
        Assert.Equal(493_333.333333, shape.Iy, 5);
        Assert.Equal(-120_000, shape.Ixy, 6);
        Assert.Equal(320, shape.Perimeter, 9);
        Assert.Throws<ToolException>(() => outer.Minus([outer]));
    }

    [Fact]
    public void Principal_moments_match_solid_edge()
    {
        // Solid Edge 2026, right triangle b 60 h 40 mm (m⁴): dMIX 1.0667e-7, dMIY 2.4e-7, dMIXY -8e-8 → dPrincipalMIX
        // 2.7747e-7, dPrincipalMIY 6.9197e-8, dAngleOfOrientation 1.1327673014958 rad; the mirrored triangle -1.1327673014958.
        (double max, double min, double angle) = new Moments(0.0012, 0, 0, 0, 1.0666666666666666e-7, 2.4e-7, -8e-8).Principal();
        Assert.Equal(2.774699956787553e-7, max, 15);
        Assert.Equal(6.919667098791124e-8, min, 15);
        Assert.Equal(1.1327673014958, angle, 12);
        Assert.Equal(-1.1327673014958, new Moments(0.0012, 0, 0, 0, 1.0666666666666666e-7, 2.4e-7, 8e-8).Principal().Angle, 12);

        // 60 x 40 rectangle: the axis of the maximum is y (90°); a circle: every axis is principal (0°).
        Assert.Equal(Math.PI / 2, new Moments(0.0024, 0, 0, 0, 3.2e-7, 7.2e-7, 0).Principal().Angle, 12);
        Assert.Equal(0, new Moments(3.14e-4, 0, 0, 0, 7.853981633974483e-9, 7.853981633974483e-9, -8.27e-25).Principal().Angle, 12);
    }
}
