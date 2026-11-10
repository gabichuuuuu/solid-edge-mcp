// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SensorAction
{
    List,
    AddVariable,
    AddDistance,
    AddArea,
    AddChecker,
    Update,
    Set,
}

public enum SensorOperator
{
    GreaterThan,
    LessThan,
    EqualTo,
    NotEqualTo,
    Between,
    NotBetween,
}

public enum SensorDisplay
{
    Range,
    TrueFalse,
}

public enum SensorUpdate
{
    Automatic,
    Manual,
}
