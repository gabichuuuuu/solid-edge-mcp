// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum DrawingViewAction
{
    Display,
    SetDisplay,
    Breaks,
    AddBreak,
    SetBroken,
    RemoveBreak,
    BrokenOut,
    To2d,
    Alternates,
    AddAlternate,
    RemoveAlternate,
    Centerlines,
    RemoveCenterlines,
    Activation,
    HideEdges,
    ShowEdges,
    ClearEdges,
}

public enum BreakDirection
{
    Vertical,
    Horizontal,
}

public enum BreakShape
{
    Straight,
    Cylindrical,
    ShortBreak,
    LongBreak,
    ShortCurvedBreak,
}
