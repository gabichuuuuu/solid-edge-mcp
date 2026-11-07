// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum DimensionAction
{
    Get,
    Tolerance,
    Text,
    Tracker,
    AlignmentSet,
    Arrange,
}

public enum DimensionTolerance
{
    Nominal,
    PlusMinus,
    Limits,
    Fit,
    FitPlusMinus,
    FitLimits,
    Basic,
    Reference,
}

/// <summary>Tolerance and texts of a dimension of a drawing, a sketch or the PMI.</summary>
[McpServerToolType]
public sealed class DimensionTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_dimension", Title = "Dimension tolerance, text, tracker and arrangement", Destructive = false, OpenWorld = false)]
    [Description("""
        Tolerance and texts of one dimension (drawing dimensions from se_drawing dimension/dimensions; sketch or PMI dimensions by handle).
        get (value in mm or degrees, tolerance kind, upper, lower, hole, shaft, prefix, suffix, override) |
        tolerance: nominal | plus_minus and limits (upper, lower: deviations in the dimension's units, e.g. "0.1", "-0.2") | fit, fit_plus_minus, fit_limits (hole e.g. H7 and/or shaft e.g. g6) | basic | reference; read back, and put back as it was if Solid Edge does not keep it |
        text: prefix, suffix, override (the text shown instead of the value); an empty text clears it |
        tracker (drawing, no dimension): the dimensions whose value changed or that came loose when the views were updated (dimension, reason, previous, current, sheet); clear=true empties the list |
        alignment_set (drawing): whether dimensions placed in line keep their alignment set; maintain=true/false sets it |
        arrange: spaces dimensions out from the outline they measure (dimensions = handles on one sheet, or PMI dimensions of one model; default every dimension of the active sheet); pitch = multiple of the style's stack pitch (default 1); result: each dimension's value and centre before and after.
        Dimensions with a fit fill the tolerance table of a view (se_drawing tolerance_table).
        Example: {"action":"tolerance","dimension":"dimension:12","tolerance":"plus_minus","upper":"0.1","lower":"-0.1"}; {"action":"tolerance","dimension":"dimension:13","tolerance":"fit","hole":"H7"}.
        """)]
    public Task<CallToolResult> Dimension(
        DimensionAction action,
        [Description("get, tolerance, text: the dimension handle.")] string? dimension = null,
        [Description("tolerance: the kind.")] DimensionTolerance? tolerance = null,
        [Description("plus_minus, limits: upper deviation as text in the dimension's units, e.g. \"0.1\".")] string? upper = null,
        [Description("plus_minus, limits: lower deviation as text, e.g. \"-0.2\".")] string? lower = null,
        [Description("fit*: ISO hole class, capital letters, e.g. H7.")] string? hole = null,
        [Description("fit*: ISO shaft class, small letters, e.g. g6.")] string? shaft = null,
        [Description("text: prefix before the value (e.g. 4x), empty to clear.")] string? prefix = null,
        [Description("text: suffix after the value, empty to clear.")] string? suffix = null,
        [Description("text: text shown instead of the value, empty to clear.")] string? @override = null,
        [Description("tracker, alignment_set, arrange: the drawing (default: the active document).")] string? document = null,
        [Description("tracker: empty the list after reading it.")] bool clear = false,
        [Description("alignment_set: true keeps the alignment sets, false lets dimensions move on their own; omit to read.")] bool? maintain = null,
        [Description("arrange: dimension handles (se_drawing dimensions, se_pmi list); default every dimension of the active sheet.")] string[]? dimensions = null,
        [Description("arrange: spacing as a multiple of the stack pitch of the dimension style (default 1).")] double? pitch = null,
        [Description("arrange: keep the arranged dimensions tied together (default true).")] bool associative = true,
        CancellationToken cancellationToken = default)
    {
        string act = Snake(action);
        if (action == DimensionAction.Tracker)
        {
            return runner.RunAsync("se_dimension", act, c => DimensionService.Tracker(c, document, clear), cancellationToken);
        }

        if (action == DimensionAction.Arrange)
        {
            return runner.RunAsync("se_dimension", act, c => DimensionService.Arrange(c, document, dimensions, pitch, associative), cancellationToken);
        }

        if (action == DimensionAction.AlignmentSet)
        {
            return runner.RunAsync("se_dimension", act, c => DimensionService.AlignmentSet(c, document, maintain), cancellationToken);
        }

        var spec = new DimensionSpec(act, dimension, tolerance is { } t ? Snake(t) : null, upper, lower, hole, shaft, prefix, suffix, @override);
        return runner.RunAsync("se_dimension", act, c => DimensionService.Run(c, spec), cancellationToken);
    }
}
