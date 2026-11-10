// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum AnnotationAction
{
    List,
    Text,
    Requirements,
    Notes,
    Balloon,
    Callout,
    CenterMark,
    Centerline,
    Leader,
    SurfaceFinish,
    Datum,
    FeatureControlFrame,
    Weld,
    BoltCircle,
    DatumTarget,
    DatumPoint,
    Corner,
    GostWeld,
    Delete,
}

public enum AnnotationKind
{
    Text,
    Balloon,
    CenterMark,
    Centerline,
    Leader,
    SurfaceFinish,
    Datum,
    FeatureControlFrame,
    Weld,
    BoltCircle,
    DatumTarget,
    DatumPoint,
    Corner,
    GostWeld,
}

public enum SurfaceFinishType
{
    Basic,
    Machined,
    NoMaterialRemoval,
}

public enum GeometricCharacteristic
{
    Straightness,
    Flatness,
    Circularity,
    Cylindricity,
    LineProfile,
    SurfaceProfile,
    Angularity,
    Perpendicularity,
    Parallelism,
    Position,
    Concentricity,
    Symmetry,
    CircularRunout,
    TotalRunout,
}

public enum WeldType
{
    Fillet,
    Square,
    VGroove,
    Bevel,
    UGroove,
    JGroove,
    Spot,
    Seam,
    Plug,
    Backing,
    Surfacing,
    FlareV,
    FlareBevel,
    Edge,
}

public enum WeldSide
{
    Arrow,
    Other,
    Both,
}
