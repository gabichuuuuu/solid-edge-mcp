// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum AssemblyAction
{
    List,
    Insert,
    Move,
    Rotate,
    Ground,
    Unground,
    Replace,
    Show,
    Hide,
    Suppress,
    Unsuppress,
    Delete,
    Activate,
    Inactivate,
    Bundle,
    Transfer,
    Disperse,
    CopyAssembly,
    Mirror,
    Adjustable,
    SaveModel,
    Properties,
    SetProperty,
    Reorder,
}

public enum ReplaceSource
{
    File,
    Copy,
    New,
}

public enum RelationAction
{
    List,
    Add,
    Edit,
    Delete,
}

public enum RelationType
{
    Planar,
    Axial,
    Insert,
    Parallel,
    Tangent,
    Angle,
    RigidSet,
    CenterPlane,
    Connect,
    CoordinateSystem,
    Gear,
}

public enum EdgePoint
{
    Start,
    End,
    Mid,
    Center,
}

public enum GearRatio
{
    Teeth,
    Turns,
}

public enum AssemblyPatternAction
{
    List,
    Create,
    Duplicate,
    Curve,
    Delete,
    Dissolve,
    Clone,
}

public enum CloneRelation
{
    Ground,
    None,
    Repair,
}

public enum AssemblyFeatureAction
{
    List,
    Cut,
    Revolve,
    Hole,
    Mirror,
    Pattern,
    Protrusion,
    RevolvedProtrusion,
    FilletWeld,
    LabelWeld,
    Delete,
}

public enum AssemblyFamilyAction
{
    List,
    Create,
    Add,
    Activate,
    Rename,
    Delete,
    SaveMember,
    Exclude,
    Alternates,
    AddAlternate,
    RemoveAlternate,
}

public enum AssemblyFamilyKind
{
    AlternatePositions,
    Family,
}

public enum AssemblyConfigAction
{
    List,
    Save,
    Apply,
    Delete,
    Explode,
    Exploded,
    Performance,
}

public enum ExplodeLevel
{
    Part,
    Subassembly,
}

public enum InterferenceCompare
{
    Auto,
    Others,
    Visible,
}

public enum BomStructure
{
    Top,
    Flat,
    Indented,
    Exploded,
    Statistics,
}
