// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SketchEditAction
{
    Move,
    Rotate,
    Scale,
    Mirror,
    Trim,
    Extend,
    Split,
    Corner,
    Offset,
    Construction,
    PatternRectangular,
    PatternCircular,
    Delete,
    Order,
    Group,
    Ungroup,
}

public enum OffsetCap
{
    Arc,
    Line,
    None,
}

public enum DrawOrder
{
    Front,
    Back,
    Forward,
    Backward,
}
