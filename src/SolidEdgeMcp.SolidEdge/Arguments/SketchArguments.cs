// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SketchAction
{
    Create,
    Line,
    Rectangle,
    Polyline,
    Polygon,
    Circle,
    Arc,
    Ellipse,
    Spline,
    Conic,
    Point,
    Fillet,
    Chamfer,
    Project,
    Axis,
    Close,
    Info,
    Delete,
    TearOff,
    Blocks,
    CreateBlock,
    PlaceBlock,
    DeleteBlock,
}

public enum SketchConstraintAction
{
    Relation,
    Dimension,
    List,
    Delete,
    Autoscale,
}

public enum SketchRelation
{
    Horizontal,
    Vertical,
    Fix,
    Parallel,
    Perpendicular,
    Equal,
    Tangent,
    Concentric,
    Colinear,
    Connect,
    Symmetric,
}

public enum SketchDimension
{
    Length,
    Radius,
    Diameter,
    Distance,
    Angle,
}

public enum RefPlaneAction
{
    List,
    Offset,
    Angle,
    ThreePoints,
    Mid,
    NormalToEdge,
    Tangent,
    CoordinateSystem,
    CoordinateSystems,
    Show,
    Hide,
    Rename,
    Delete,
}

public enum EdgeEnd
{
    Start,
    End,
}
