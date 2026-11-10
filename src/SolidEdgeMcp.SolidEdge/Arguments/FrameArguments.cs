// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum FrameAction
{
    Library,
    List,
    Frame,
    Pipe,
    Delete,
    Repair,
}

public enum FrameLibrary
{
    Frames,
    Pipes,
}

public enum FrameEndCondition
{
    Miter,
    Butt1,
    Butt2,
    None,
}
