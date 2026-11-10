// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Arguments;

public enum HoleType
{
    Simple,
    Counterbore,
    Countersink,
    Tapped,
}

public enum HoleDirection
{
    Auto,
    Normal,
    Reverse,
}

public enum PatternType
{
    Rectangular,
    Circular,
    Mirror,
    Curve,
    Fill,
    Table,
}

public enum ShellSide
{
    Inside,
    Outside,
}

public enum DraftDirection
{
    Normal,
    Reverse,
}

public enum SweepAction
{
    Loft,
    Sweep,
    Helix,
    Tube,
    Solid,
}

public enum HelixHand
{
    Right,
    Left,
}

public enum PartFeatureAction
{
    Rib,
    Thicken,
    MirrorBody,
    DeleteFaces,
    DeleteHoles,
    DeleteRounds,
    DeleteRegion,
    OffsetFaces,
    ReplaceFaces,
    RotateFaces,
    RedefineFaces,
    ResizeHoles,
    ResizeRounds,
    Radiate,
    NormalProtrusion,
    NormalCutout,
    Split,
    CutByPlane,
    Unite,
    Slot,
    WebNetwork,
    Lip,
    ScaleBody,
    CopyPart,
    FilletWeld,
    LabelWeld,
}

public enum PartFeatureSide
{
    Auto,
    Normal,
    Reverse,
    Symmetric,
}

public enum BodyAction
{
    List,
    Subtract,
    Intersect,
    Unite,
    Emboss,
    ToConstruction,
    ToDesign,
    Divide,
    Heal,
    Publish,
}

public enum BodySide
{
    Auto,
    Normal,
    Reverse,
}

public enum PrimitiveShape
{
    Box,
    Cylinder,
    Sphere,
}

public enum CurveAction
{
    List,
    Points,
    Intersection,
    Project,
    Cross,
    Derived,
    Contour,
    Wrap,
    Isocline,
    Helix,
    Split,
    Delete,
}

public enum SurfaceAction
{
    List,
    Extrude,
    Revolve,
    Offset,
    Loft,
    Sweep,
    Stitch,
    Bounded,
    Copy,
    Trim,
    Split,
    Extend,
    Intersect,
    Bluesurf,
    Blend,
    Ruled,
    Delete,
}

public enum SurfaceExtension
{
    Natural,
    Linear,
    Reflective,
}
