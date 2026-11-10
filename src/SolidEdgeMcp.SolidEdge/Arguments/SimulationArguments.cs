// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum SimulationAction
{
    List,
    Create,
    Constrain,
    Load,
    Solve,
    Results,
    CloseResults,
    Delete,
}

public enum StudyType
{
    Static,
    Modal,
    Buckling,
}

public enum SimulationConstraint
{
    Fixed,
    Pinned,
    NoRotation,
    Sliding,
    Cylindrical,
}

public enum SimulationLoad
{
    Force,
    Pressure,
}

public enum ResultQuantity
{
    Displacement,
    Stress,
}

public enum ResultComponent
{
    Magnitude,
    X,
    Y,
    Z,
    VonMises,
    Xx,
    Yy,
    Zz,
    Xy,
    Yz,
    Zx,
}
