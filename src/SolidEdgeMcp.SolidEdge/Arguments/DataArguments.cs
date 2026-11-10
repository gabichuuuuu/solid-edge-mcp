// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum VariableAction
{
    List,
    Get,
    Set,
    Add,
    Delete,
    Rename,
    Comment,
    Expose,
    Limits,
    GoalSeek,
    Adjustable,
}

public enum VariableKind
{
    All,
    User,
    Dimension,
    Physical,
}

public enum VariableUnits
{
    Distance,
    Angle,
    Area,
    Volume,
    Mass,
    Scalar,
}

public enum PropertyAction
{
    List,
    Set,
    Delete,
}

public enum PartFamilyAction
{
    List,
    AddVariables,
    RemoveVariable,
    AddMember,
    EditMember,
    Apply,
    Populate,
    CopyMember,
    DeleteMember,
}

public enum MaterialAction
{
    Current,
    Apply,
    Libraries,
    List,
    Info,
}

public enum MaterialFolder
{
    Options,
    Installation,
}
