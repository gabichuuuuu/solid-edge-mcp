// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SheetContentAction
{
    List,
    Table,
    Cell,
    Rows,
    Image,
    Frame,
    Symbol,
    Watermark,
    LineUp,
    Connector,
    Delete,
}

public enum ConnectorType
{
    Line,
    Jump,
    Corner,
    Step,
    Gap,
}

public enum TextAlignment
{
    Left,
    Right,
    Center,
    Bottom,
    Top,
    Middle,
}
