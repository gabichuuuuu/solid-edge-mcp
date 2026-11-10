// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum ComponentGroupAction
{
    List,
    Create,
    Add,
    Remove,
    Rename,
    Show,
    Hide,
    Ungroup,
    Pack,
    Unpack,
    Delete,
}

public enum ComponentQueryAction
{
    Find,
    Zone,
    Run,
    List,
    Rename,
    Delete,
}

public enum ComponentProperty
{
    Name,
    Title,
    Subject,
    Author,
    Manager,
    Company,
    Category,
    Keywords,
    Comments,
    DocumentNumber,
    Revision,
    Project,
    Material,
    Custom,
    Status,
}

public enum ComponentCondition
{
    Contains,
    Is,
    IsNot,
}

public enum ComponentScope
{
    All,
    Shown,
    Hidden,
    Selected,
}

public enum ComponentDisplay
{
    Select,
    ShowOnly,
    Hide,
    Show,
}
