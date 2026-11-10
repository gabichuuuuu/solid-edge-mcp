// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum GeometryAction
{
    Summary,
    Faces,
    Edges,
    Draft,
}

public enum MeasureAction
{
    Distance,
    Angle,
    NormalDistance,
    WallThickness,
}

public enum DistanceType
{
    Minimum,
    Maximum,
}

public enum ThicknessResolution
{
    Coarse,
    Standard,
    Fine,
}
