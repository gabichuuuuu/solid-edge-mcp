// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum PmiAction
{
    List,
    Set,
    Lock,
    Unlock,
    Delete,
    Display,
    SetDisplay,
    Copy,
    Size,
    Plane,
    Axis,
    Dimension,
    Move,
}

public enum PmiDimensionType
{
    Linear,
    Diameter,
    Radius,
    Angle,
}

public enum PmiCopyKind
{
    All,
    Dimensions,
    Annotations,
}

public enum PmiDirection
{
    Measure,
    Origin,
    Symmetric,
}
