// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SheetMetalAction
{
    Settings,
    SetSettings,
    Gauge,
    Tab,
    ContourFlange,
    Flange,
    Hem,
    BreakCorner,
    Flatten,
    BendAngle,
    BendRadius,
    Convert,
    Transform,
    MatchFace,
    Louver,
    TabSlot,
    Unbend,
    Gusset,
    BendTable,
}

public enum GussetShape
{
    Round,
    Square,
}

public enum SheetMetalSide
{
    Inside,
    Outside,
    BendOutside,
}

public enum FlangeDimension
{
    Inside,
    Outside,
}

public enum HemType
{
    Closed,
    Open,
    SFlange,
    ClosedLoop,
}

public enum CornerType
{
    Round,
    Chamfer,
}

public enum LouverEnd
{
    Lanced,
    Formed,
}

public enum ChainSide
{
    Right,
    Left,
}

public enum SheetFormAction
{
    Dimple,
    DrawnCutout,
    NormalCutout,
    Etch,
    Jog,
    Bend,
    Bead,
}

public enum FormDirection
{
    Auto,
    Normal,
    Reverse,
}

public enum MovingSide
{
    Right,
    Left,
}

/// <summary>Side of the thickness for the tab or contour flange; 'symmetric' does not exist in sheet metal.</summary>
public enum SheetDirection
{
    Normal,
    Reverse,
}

public enum BendPosition
{
    Inside,
    Outside,
    Symmetric,
}

public enum BeadType
{
    U,
    V,
    Circular,
}

public enum CutoutExtent
{
    ThroughAll,
    ThroughNext,
    Finite,
    FromTo,
}

public enum BeadEnd
{
    Lanced,
    Punched,
    Formed,
}

public enum CutoutMethod
{
    Thickness,
    MidPlane,
    Clearance,
}
