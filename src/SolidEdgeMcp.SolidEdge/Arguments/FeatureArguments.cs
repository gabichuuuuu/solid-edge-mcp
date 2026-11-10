// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum FeatureOperation
{
    Add,
    Cut,
}

public enum ExtrudeExtent
{
    Finite,
    ThroughAll,
    ThroughNext,
    To,
    FromTo,
}

/// <summary>Extents of holes and assembly features, which have no plane-to-plane limits.</summary>
public enum SimpleExtent
{
    Finite,
    ThroughAll,
    ThroughNext,
}

public enum FeatureDirection
{
    Normal,
    Reverse,
    Symmetric,
}

public enum DraftSide
{
    Inside,
    Outside,
}

public enum WallSide
{
    Inside,
    Outside,
    Symmetric,
}

public enum FeatureEditAction
{
    Suppress,
    Unsuppress,
    Delete,
    Rename,
    Group,
    Ungroup,
    Reorder,
    RollTo,
    RollToEnd,
    MoveToSynchronous,
}

public enum ReorderPosition
{
    Before,
    After,
}
