// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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
}

public enum PmiDirection
{
    Measure,
    Origin,
    Symmetric,
}

/// <summary>PMI (3D) dimensions of parts and sheet metal: list, drive, lock, delete, and the PMI display.</summary>
[McpServerToolType]
public sealed class PmiTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_pmi", Title = "PMI dimensions (3D driving dimensions)", Destructive = true, OpenWorld = false)]
    [Description("""
        3D (PMI) dimensions of a part or sheet metal document: the ones synchronous features carry (a se_primitive box: height, width, length; a cylinder: height, diameter). mm and degrees.
        list (index, handle, name, type, value, locked, status, faces) |
        set (one dimension, value: the model changes to it; direction = which end moves: measure (default), origin or symmetric (both, half each); locked = whether it stays locked, default as it was) |
        lock | unlock (dimensions: a locked dimension drives the model and keeps its value in later synchronous edits) |
        delete (dimensions; the model stays as it is) |
        display | set_display (showDimensions, showAnnotations: whether the window shows the PMI dimensions and annotations; returns the previous values).
        set works in synchronous modelling (the document switches for the call and back), then checks the value the model measures; if it differs, the old value is put back (value_rejected).
        New PMI dimensions cannot be placed through the API: to hold faces at a distance use se_face_relation offset. Dimensions of sketches and ordered features: se_variables.
        dimensions = indexes, handles or names from list.
        Example: {"action":"set","dimensions":["Linear_2"],"value":80}; {"action":"lock","dimensions":["3"]}.
        """)]
    public Task<CallToolResult> Pmi(
        PmiAction action,
        [Description("set: one dimension; lock, unlock, delete: the dimensions. Index, handle or name from list.")] string[]? dimensions = null,
        [Description("set: the new value in mm (degrees for an angle).")] double? value = null,
        [Description("set: which end of the dimension moves: measure, origin, or symmetric (both, half each).")] PmiDirection direction = PmiDirection.Measure,
        [Description("set: true leaves the dimension locked, false unlocked; default: as it was.")] bool? locked = null,
        [Description("set_display: show the PMI dimensions.")] bool? showDimensions = null,
        [Description("set_display: show the PMI annotations.")] bool? showAnnotations = null,
        [Description("Part or sheet metal document handle; default: the document of the dimensions or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        string act = Snake(action);
        var spec = new PmiSpec(act, dimensions, value, Snake(direction), locked, showDimensions, showAnnotations);
        return runner.RunAsync("se_pmi", act, c => PmiService.Run(c, document, spec), cancellationToken);
    }
}
