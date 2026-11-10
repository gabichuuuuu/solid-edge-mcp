// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SectionViewAction
{
    List,
    Add,
    Show,
    Hide,
    Edit,
    Delete,
}

public enum SectionKeep
{
    Inside,
    Outside,
    Left,
    Right,
}

public enum SectionDirection
{
    Both,
    Front,
    Back,
}

public enum SectionDisplay
{
    OnlyCutFaces,
    CutFacesAndCutBodies,
    CutFacesWithOriginalBodies,
    OnlyOriginalBodies,
}
