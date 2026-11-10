// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum Sketch3dAction
{
    List,
    Create,
    Info,
    Line,
    Arc,
    ArcThrough,
    TangentArc,
    Rectangle,
    Circle,
    Spline,
    Point,
    Fillet,
    Relation,
    Mirror,
    Split,
    Trim,
    Equation,
    Include,
    Delete,
}

public enum Sketch3dRelation
{
    Connect,
    Parallel,
    Perpendicular,
    Collinear,
    Equal,
    Tangent,
    Concentric,
    Coaxial,
    HorizontalVertical,
    Lock,
    OnPlane,
    Symmetric,
    RigidSet,
}
