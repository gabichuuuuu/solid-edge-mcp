// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum DesignManagerAction
{
    Links,
    Rename,
    Copy,
    Replace,
    Move,
    Revise,
    Release,
    Status,
    Relink,
    BrokenLinks,
    Duplicates,
}

public enum DocumentStatus
{
    Available,
    InWork,
    InReview,
    Released,
    Baselined,
    Obsolete,
}
