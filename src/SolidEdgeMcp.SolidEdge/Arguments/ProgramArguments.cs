// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum AppAction
{
    Info,
    Addins,
    SetAddin,
    Templates,
    SetTemplate,
    Recent,
    SuspendRecent,
    ResumeRecent,
    ClearRecent,
    AiDrawingData,
    Numbering,
    ResetDxfSettings,
}

public enum CustomizeAction
{
    RibbonThemes,
    CreateRibbonTheme,
    ActivateRibbonTheme,
    RemoveRibbonTheme,
    SetRibbonTheme,
    AddTab,
    AddGroup,
    AddCommand,
    SetItem,
    RemoveItem,
    Shortcuts,
    AddShortcut,
    RemoveShortcut,
    ResetShortcuts,
}
