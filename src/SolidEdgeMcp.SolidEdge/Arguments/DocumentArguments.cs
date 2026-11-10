// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum DocumentAction
{
    List,
    Info,
    Activate,
    New,
    Open,
    Import,
    Save,
    SaveAs,
    SaveCopyAs,
    Close,
    Undo,
    Redo,
    Recompute,
    Templates,
    Status,
    SaveMirror,
    ModelingMode,
    Thumbnail,
}

public enum DocumentLifeStatus
{
    Available,
    InWork,
    InReview,
    Released,
    Baselined,
    Obsolete,
}

public enum DocumentModelingMode
{
    Synchronous,
    Ordered,
}

public enum DocumentKind
{
    Part,
    SheetMetal,
    Assembly,
    Draft,
    Weldment,
}
