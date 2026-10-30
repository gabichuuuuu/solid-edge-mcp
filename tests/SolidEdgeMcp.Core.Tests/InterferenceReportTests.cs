// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class InterferenceReportTests
{
    // Real report from Solid Edge 2026 (block 100×60×20 and two Ø10×40 pins going through it), with all options.
    private const string Full = """
        Report Name: C:\temp\intf2.txt
        Report Time: 10/10/2026 15:47:09
        Report Options:
            Part Names
            Part CG's
            Interference CG's
            Interference Volumes

        Select Set #1:
            blk.par:1
            pin.par:1
            pin.par:2

        Interferences:
            1 of 2
            blk.par:1
            Located at:     50.00 mm     30.00 mm     10.00 mm

            pin.par:1
            Located at:     30.00 mm     30.00 mm     20.00 mm

            Interference Volume 1570.796 mm^3
            Located at:     30.00 mm     30.00 mm     10.00 mm


            2 of 2
            blk.par:1
            Located at:     50.00 mm     30.00 mm     10.00 mm

            pin.par:2
            Located at:     70.00 mm     30.00 mm     20.00 mm

            Interference Volume 1570.796 mm^3
            Located at:     70.00 mm     30.00 mm     10.00 mm


            2 Interferences Detected
        """;

    [Fact]
    public void Reads_pairs_and_volumes_skipping_locations()
    {
        IReadOnlyList<InterferenceReport.Pair> pairs = InterferenceReport.Parse(Full);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(new InterferenceReport.Pair("blk.par:1", "pin.par:1", 1570.796), pairs[0]);
        Assert.Equal(new InterferenceReport.Pair("blk.par:1", "pin.par:2", 1570.796), pairs[1]);
    }

    [Fact]
    public void Reads_names_only_report_with_two_sets_and_translated_labels()
    {
        // Translated labels, decimal comma and volume in cm³: the parsing does not depend on the label text.
        const string text = "Informe\r\nConjunto 1:\r\n    1.par:1\r\n\r\nInterferencias:\r\n    1 de 1\r\n    1.par:1\r\n"
            + "    blk.par:1\r\n    Volumen de interferencia 1,5708 cm^3\r\n\r\n    1 interferencias detectadas\r\n";

        IReadOnlyList<InterferenceReport.Pair> pairs = InterferenceReport.Parse(text);

        InterferenceReport.Pair pair = Assert.Single(pairs);
        Assert.Equal("1.par:1", pair.Occurrence1);
        Assert.Equal("blk.par:1", pair.Occurrence2);
        Assert.Equal(1570.8, pair.VolumeMm3!.Value, 6);
    }

    // Real report from Solid Edge 2026 Foundation (block and one pin), where the document shows values without their unit.
    private const string Unlabelled = """
        Report Name: C:\temp\interference.txt
        Report Time: 24/10/2026 2:22:57
        Report Options:
            Part Names
            Interference Volumes

        Select Set #1:
            blk.par:1
            pin.par:1

        Interferences:
            1 of 1
            blk.par:1
            pin.par:1
            Interference Volume 1.571

            1 Interferences Detected
        """;

    [Theory]
    [InlineData(1000, 1571)]
    [InlineData(1, 1.571)]
    public void Reads_a_volume_without_unit_in_the_unit_of_the_document(double volumeUnitMm3, double expected)
    {
        InterferenceReport.Pair pair = Assert.Single(InterferenceReport.Parse(Unlabelled, volumeUnitMm3));

        Assert.Equal(new InterferenceReport.Pair("blk.par:1", "pin.par:1", expected), pair);
    }

    [Fact]
    public void Leaves_a_volume_without_unit_unread_without_the_unit_of_the_document()
    {
        Assert.Equal(new InterferenceReport.Pair("blk.par:1", "pin.par:1", null), Assert.Single(InterferenceReport.Parse(Unlabelled)));
    }

    [Fact]
    public void No_interference_gives_no_pairs()
    {
        const string text = "Report Options:\n    Part Names\n\nSelect Set #1:\n    blk.par:1\n    pin.par:1\n\n    0 Interferences Detected\n";

        Assert.Empty(InterferenceReport.Parse(text));
    }
}
