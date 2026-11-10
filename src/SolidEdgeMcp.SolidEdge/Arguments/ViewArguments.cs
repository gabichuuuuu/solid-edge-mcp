// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum ViewAction
{
    Capture,
    Fit,
    Orient,
    RenderMode,
    NamedViews,
    SaveView,
    ApplyView,
    DeleteView,
    Style,
    Background,
    Camera,
    SetCamera,
    Zoom,
    Rotate,
}

public enum ViewOrientation
{
    Iso,
    Front,
    Back,
    Top,
    Bottom,
    Left,
    Right,
    Dimetric,
    Trimetric,
}

public enum BackgroundKind
{
    Solid,
    Gradient,
    Image,
}

public enum GradientShape
{
    Horizontal,
    Vertical,
    DiagonalUp,
    DiagonalDown,
    SquareSpot,
    CircularSpot,
}
