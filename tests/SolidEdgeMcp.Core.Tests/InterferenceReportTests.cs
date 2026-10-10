using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class InterferenceReportTests
{
    // Informe real de Solid Edge 2026 (bloque 100×60×20 y dos pasadores Ø10×40 que lo atraviesan), con todas las opciones.
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
        // Rótulos traducidos, coma decimal y volumen en cm³: la lectura no depende del texto de los rótulos.
        const string text = "Informe\r\nConjunto 1:\r\n    1.par:1\r\n\r\nInterferencias:\r\n    1 de 1\r\n    1.par:1\r\n"
            + "    blk.par:1\r\n    Volumen de interferencia 1,5708 cm^3\r\n\r\n    1 interferencias detectadas\r\n";

        IReadOnlyList<InterferenceReport.Pair> pairs = InterferenceReport.Parse(text);

        InterferenceReport.Pair pair = Assert.Single(pairs);
        Assert.Equal("1.par:1", pair.Occurrence1);
        Assert.Equal("blk.par:1", pair.Occurrence2);
        Assert.Equal(1570.8, pair.VolumeMm3!.Value, 6);
    }

    [Fact]
    public void No_interference_gives_no_pairs()
    {
        const string text = "Report Options:\n    Part Names\n\nSelect Set #1:\n    blk.par:1\n    pin.par:1\n\n    0 Interferences Detected\n";

        Assert.Empty(InterferenceReport.Parse(text));
    }
}
