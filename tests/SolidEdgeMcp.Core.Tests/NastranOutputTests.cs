// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class NastranOutputTests
{
    // Layout of the tables as Simcenter Nastran prints them in the .f06 of a Solid Edge study (values of a test cantilever).
    private const string Modal = """
        1    SOLID EDGE MODAL ANALYSIS                                              PAGE     7

                                                      R E A L   E I G E N V A L U E S
           MODE    EXTRACTION      EIGENVALUE            RADIANS             CYCLES            GENERALIZED         GENERALIZED
            NO.       ORDER                                                                       MASS              STIFFNESS
                1         1        2.640940E+07        5.139008E+03        8.178985E+02        1.000000E+00        2.640940E+07
                2         2        1.003666E+08        1.001831E+04        1.594464E+03        1.000000E+00        1.003666E+08
        1    SOLID EDGE MODAL ANALYSIS                                              PAGE     8
        """;

    private const string Static = """
             RESULTANTS ABOUT ORIGIN OF SUPERELEMENT BASIC COORDINATE SYSTEM IN SUPERELEMENT BASIC SYSTEM COORDINATES.
        0                                                  OLOAD    RESULTANT
          SUBCASE/    LOAD
          DAREA ID    TYPE       T1            T2            T3            R1            R2            R3
        0        1     FX   -5.773398E-14     ----          ----          ----      -5.773398E-16  4.136473E-16
                       FZ       ----          ----       2.000000E+02  2.000000E+00 -1.000000E+01     ----
                     TOTALS -5.773398E-14 -7.818313E-15  2.000000E+02  2.000000E+00 -1.000000E+01  1.511953E-16
        0                                                  SPCFORCE RESULTANT
          SUBCASE/    LOAD
          DAREA ID    TYPE       T1            T2            T3            R1            R2            R3
        0        1     FX   -2.142144E-10     ----          ----          ----       1.000000E+01  1.923223E-11
                     TOTALS -2.142144E-10  2.070273E-10 -2.000000E+02 -2.000000E+00  1.000000E+01  1.923223E-11
         *** USER FATAL MESSAGE 9050 (SEKRRS)
         *** USER FATAL MESSAGE 9050 (SEKRRS)
        """;

    [Fact]
    public void Reads_the_modes_and_their_frequencies()
    {
        IReadOnlyList<NastranEigenvalue> modes = NastranOutput.Eigenvalues(Modal);
        Assert.Equal(2, modes.Count);
        Assert.Equal(new NastranEigenvalue(1, 2.640940E+07, 8.178985E+02), modes[0]);
        Assert.Equal(1594.464, modes[1].Cycles, 3);
    }

    [Fact]
    public void Reads_the_modes_of_a_table_that_goes_on_over_a_page_break()
    {
        string paged = Modal + "\n" + """
                                                      R E A L   E I G E N V A L U E S
           MODE    EXTRACTION      EIGENVALUE            RADIANS             CYCLES            GENERALIZED         GENERALIZED
            NO.       ORDER                                                                       MASS              STIFFNESS
                3         3        3.000000E+08        1.732051E+04        2.756644E+03        1.000000E+00        3.000000E+08
        1    SOLID EDGE MODAL ANALYSIS                                              PAGE     9

                                                      R E A L   E I G E N V A L U E S
           MODE    EXTRACTION      EIGENVALUE            RADIANS             CYCLES            GENERALIZED         GENERALIZED
            NO.       ORDER                                                                       MASS              STIFFNESS
                1         1        9.000000E+09        9.486833E+04        1.509860E+04        1.000000E+00        9.000000E+09
        """;
        IReadOnlyList<NastranEigenvalue> modes = NastranOutput.Eigenvalues(paged);
        Assert.Equal([1, 2, 3], modes.Select(m => m.Mode));
        Assert.Equal(2756.644, modes[2].Cycles, 3);
    }

    [Fact]
    public void Reads_the_totals_of_each_resultant()
    {
        NastranResultant? load = NastranOutput.Resultant(Static, "OLOAD");
        NastranResultant? reaction = NastranOutput.Resultant(Static, "SPCFORCE");
        Assert.NotNull(load);
        Assert.NotNull(reaction);
        Assert.Equal(200, load.Force[2], 9);
        Assert.Equal(-10, load.Moment[1], 9);
        Assert.Equal(-200, reaction.Force[2], 9);
        Assert.Equal(10, reaction.Moment[1], 9);
    }

    [Fact]
    public void Gives_nothing_for_what_the_file_does_not_have()
    {
        Assert.Empty(NastranOutput.Eigenvalues(Static));
        Assert.Null(NastranOutput.Resultant(Modal, "SPCFORCE"));
        Assert.Empty(NastranOutput.FatalMessages(Modal));
        Assert.Equal(["*** USER FATAL MESSAGE 9050 (SEKRRS)"], NastranOutput.FatalMessages(Static));
    }
}
