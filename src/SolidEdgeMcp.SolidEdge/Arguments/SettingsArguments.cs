// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum OptionAction
{
    List,
    Get,
    Set,
    Units,
    SetUnits,
    Format,
    Categories,
    Snapshot,
    Restore,
}

public enum OptionScope
{
    Application,
    Document,
    Drawing,
}

public enum UiAction
{
    Status,
    Windows,
    Activate,
    Arrange,
    State,
    Environments,
    Ribbon,
    Tab,
    Find,
    Display,
    SetDisplay,
}

public enum ArrangeStyle
{
    Tiled,
    Horizontal,
    Vertical,
    Cascade,
}

public enum WindowStateKind
{
    Normal,
    Minimized,
    Maximized,
}
