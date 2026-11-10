// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum FaceRelationAction
{
    List,
    Coplanar,
    Offset,
    Parallel,
    Perpendicular,
    Tangent,
    Concentric,
    Fix,
    Edit,
    Delete,
    LiveRules,
    SetLiveRules,
    RestoreLiveRules,
}

public enum LiveRule
{
    Concentric,
    Coplanar,
    TangentEdge,
    TangentTouching,
    Parallel,
    Perpendicular,
    Symmetric,
    SymmetricXy,
    SymmetricYz,
    SymmetricZx,
    MaintainRadius,
    OrthoLocking,
    CoplanarAxes,
    CoplanarAxesX,
    CoplanarAxesY,
    CoplanarAxesZ,
    ThicknessChain,
    Offset,
}
