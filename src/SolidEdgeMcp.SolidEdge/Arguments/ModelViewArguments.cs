// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum ModelViewAction
{
    List,
    Add,
    Set,
    Include,
    Exclude,
    Apply,
    Update,
    Delete,
}

public enum ModelViewOrientation
{
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom,
    Dimetric,
    Isometric,
    Trimetric,
}

public enum ModelViewRender
{
    None,
    VisibleEdges,
    VisibleAndHiddenEdges,
    Shaded,
    ShadedWithVisibleEdges,
}
