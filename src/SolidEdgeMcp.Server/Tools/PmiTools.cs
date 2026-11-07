// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum PmiAction
{
    List,
    Set,
    Lock,
    Unlock,
    Delete,
    Display,
    SetDisplay,
    Copy,
    Size,
    Plane,
    Axis,
    Dimension,
    Move,
}

public enum PmiDimensionType
{
    Linear,
    Diameter,
    Radius,
    Angle,
}

public enum PmiCopyKind
{
    All,
    Dimensions,
    Annotations,
}

public enum PmiDirection
{
    Measure,
    Origin,
    Symmetric,
}

/// <summary>PMI (3D) dimensions of parts and sheet metal: list, place, drive, lock, delete, copy from a feature, and the PMI display.</summary>
[McpServerToolType]
public sealed class PmiTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_pmi", Title = "PMI dimensions (3D driving dimensions)", Destructive = true, OpenWorld = false)]
    [Description("""
        3D (PMI) dimensions of a part or sheet metal document: the ones synchronous features carry (a se_primitive box: height, width, length). mm and degrees.
        list (index, handle, name, type, value, locked, status, faces) |
        set (one dimension, value: the model changes to it; direction = which end moves: measure (default), origin or symmetric (both, half each); locked = whether it stays locked, default as it was) |
        lock | unlock (dimensions: a locked dimension drives the model and keeps its value in later synchronous edits) |
        delete (dimensions; the model stays as it is) | move (one dimension, distance: its line's stand-off, as in dimension) |
        display | set_display (showDimensions, showAnnotations: show the PMI dimensions and annotations; returns the previous values).
        set works in synchronous modelling (switched for the call), checks the value the model measures and puts the old one back if it differs (value_rejected).
        copy (feature, kind = all (default) | dimensions | annotations: Copy to PMI, the dimensions of the feature's sketch become PMI dimensions; each call adds them again; returns the new ones).
        size (pixelMode: true = PMI text sized in screen pixels, pixelSize; false = at model scale) | plane (plane = '1'-'3' or a reference plane: Lock Dimension Plane for new PMI; none = unlock) | axis (edge = a straight edge that orients new PMI dimensions; none = clear). Each returns the old state.
        dimension (type: linear (points = two vertices, or one point on a straight edge: its length) | diameter | radius (a point on a circular edge) | angle (a point on each of two straight edges); plane = '1'-'3' or a reference plane it is read in, default the locked one; distance = mm its line stands off along the plane's normal, default 0): checked against the geometry; not drivable. PMI annotations: se_pmi_annotation. Dimensions of sketches and ordered features: se_variables.
        dimensions = indexes, handles or names from list.
        Example: {"action":"set","dimensions":["Linear_2"],"value":80}; {"action":"copy","feature":"feature:4"}.
        """)]
    public Task<CallToolResult> Pmi(
        PmiAction action,
        [Description("set, move: one dimension; lock, unlock, delete: the dimensions. Index, handle or name from list.")] string[]? dimensions = null,
        [Description("set: the new value in mm (degrees for an angle).")] double? value = null,
        [Description("set: which end of the dimension moves: measure, origin, or symmetric (both, half each).")] PmiDirection direction = PmiDirection.Measure,
        [Description("set: true leaves the dimension locked, false unlocked; default: as it was.")] bool? locked = null,
        [Description("set_display: show the PMI dimensions.")] bool? showDimensions = null,
        [Description("set_display: show the PMI annotations.")] bool? showAnnotations = null,
        [Description("copy: the feature whose sketch dimensions are copied.")] string? feature = null,
        [Description("copy: all (default), dimensions or annotations.")] PmiCopyKind kind = PmiCopyKind.All,
        [Description("size: true = text in screen pixels, false = model size.")] bool? pixelMode = null,
        [Description("size: the text height in pixels (pixel mode), 1-200.")] int? pixelSize = null,
        [Description("plane: '1'-'3' or a reference plane handle; omitted = unlock the plane. dimension: the plane it is drawn in (default: the locked PMI plane).")] string? plane = null,
        [Description("axis: a straight edge handle (se_geometry edges); omitted = clear the axis.")] string? edge = null,
        [Description("dimension: linear, diameter, radius or angle.")] PmiDimensionType? type = null,
        [Description("dimension: model points [x1, y1, z1, ...] in mm on the vertices or edges it measures.")] double[]? points = null,
        [Description("dimension, move: mm the dimension line stands off what it measures, along its plane's normal (either sign); dimension: default 0.")] double? distance = null,
        [Description("Part or sheet metal document handle; default: the document of the dimensions or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        string act = Snake(action);
        var spec = new PmiSpec(act, dimensions, value, Snake(direction), locked, showDimensions, showAnnotations, feature, Snake(kind), plane, edge, pixelMode, pixelSize,
            points, type is { } t ? Snake(t) : null, distance);
        return runner.RunAsync("se_pmi", act, c => PmiService.Run(c, document, spec), cancellationToken);
    }
}
