// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum DimensionAction
{
    Get,
    Tolerance,
    Text,
    Tracker,
    AlignmentSet,
    Arrange,
}

public enum DimensionTolerance
{
    Nominal,
    PlusMinus,
    Limits,
    Fit,
    FitPlusMinus,
    FitLimits,
    Basic,
    Reference,
}
