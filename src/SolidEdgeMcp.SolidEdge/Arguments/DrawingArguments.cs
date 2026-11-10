// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum DrawingAction
{
    Sheets,
    AddSheet,
    SetupSheet,
    ActivateSheet,
    DeleteSheet,
    AddBackground,
    ReplaceBackground,
    Views,
    BaseView,
    ProjectedView,
    AuxiliaryView,
    CuttingPlane,
    SectionView,
    DetailView,
    Model2dView,
    MoveView,
    ScaleView,
    RotateView,
    CropView,
    DeleteView,
    Dimension,
    RetrieveDimensions,
    Dimensions,
    PartsList,
    BendTable,
    ToleranceTable,
    FamilyTable,
    BlockTable,
    HoleTable,
    Tables,
    Update,
    Blocks,
    CreateBlock,
    PlaceBlock,
    ImportBlock,
    DeleteBlock,
    Unblock,
    UnblockAll,
}

public enum DimensionKind
{
    Length,
    Distance,
    Diameter,
    Radius,
    Angle,
    Coordinate,
    AngularCoordinate,
    Chamfer,
    SymmetricDiameter,
}

public enum MeasureDirection
{
    Aligned,
    Horizontal,
    Vertical,
    Counterclockwise,
    Clockwise,
}

public enum DrawingOrientation
{
    Front,
    Back,
    Top,
    Bottom,
    Left,
    Right,
    TopFrontRight,
    TopFrontLeft,
    TopBackRight,
    TopBackLeft,
    BottomFrontRight,
    BottomFrontLeft,
    BottomBackRight,
    BottomBackLeft,
}

public enum FoldDirection
{
    Up,
    Down,
    Left,
    Right,
    UpRight,
    UpLeft,
    DownRight,
    DownLeft,
}

public enum PaperSize
{
    A4,
    A3,
    A2,
    A1,
    A0,
    AnsiA,
    AnsiB,
    AnsiC,
    AnsiD,
    AnsiE,
}
