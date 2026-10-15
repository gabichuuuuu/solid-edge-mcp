// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Drawing-wide dimension settings: the dimension change tracker, alignment sets and arranging dimensions.</summary>
public static partial class DimensionService
{
    /// <summary>
    /// Track Dimension Changes: the dimensions of a drawing whose value changed, or that came loose, when the views were
    /// updated after the model changed. Verified in Solid Edge 2026: with two retrieved dimensions of 100 mm, the part made
    /// 120 mm and the view updated, DimensionTrackerNumEntries gives 2, DimensionTrackerEntry(i) the dimension, the reason
    /// (value changed), 0.1 → 0.12 m and the sheet; ClearAllDimensionTrackerEntries empties it.
    /// </summary>
    public static object Tracker(SeContext context, string? documentHandle, bool clear)
    {
        DraftDocument draft = context.GetDocument<DraftDocument>(documentHandle, "draft");
        draft.DimensionTrackerNumEntries(out int count);
        draft.DimensionTrackerStatisticsEx(out int changedAnnotations, out int changedDimensions, out int detachedAnnotations, out int detachedDimensions,
            out int reattachedAnnotations, out int reattachedDimensions);
        var entries = new List<object>();
        for (int i = 1; i <= count; i++)
        {
            draft.DimensionTrackerEntry(i, out int balloon, out object item, out DimensionTrackerReasonCode reason, out double previous, out double current, out string sheet);
            int units = item is SolidEdgeFrameworkSupport.Dimension dimension ? VariableService.UnitsOf(dimension) : (int)UnitTypeConstants.igUnitDistance;
            entries.Add(new
            {
                dimension = item is SolidEdgeFrameworkSupport.Dimension ? context.Register(item, "dimension", (SolidEdgeDocument)draft) : null,
                balloon = balloon == 0 ? (int?)null : balloon,
                reason = JsonNamingPolicy.SnakeCaseLower.ConvertName(reason.ToString().Replace("igDTRC_", "", StringComparison.Ordinal)),
                previous = Math.Round(VariableService.FromDatabase(previous, units), 6),
                current = Math.Round(VariableService.FromDatabase(current, units), 6),
                unit = VariableService.UnitLabel(units),
                sheet,
            });
        }

        if (clear)
        {
            draft.ClearAllDimensionTrackerEntries();
            draft.DimensionTrackerNumEntries(out int left);
            if (left != 0)
            {
                throw new ToolException("setting_rejected", $"Solid Edge kept {left} entries in the dimension tracker.");
            }
        }

        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)draft),
            count,
            changed = new { dimensions = changedDimensions, annotations = changedAnnotations },
            detached = new { dimensions = detachedDimensions, annotations = detachedAnnotations },
            reattached = new { dimensions = reattachedDimensions, annotations = reattachedAnnotations },
            entries,
            cleared = clear,
        };
    }

    /// <summary>
    /// Arrange Dimensions: spaces the given dimensions (default every dimension of the sheet) out from the outline they measure,
    /// <paramref name="pitch"/> times the stack pitch of their style apart. Verified in Solid Edge 2026 with
    /// Sheet.ArrangeDimensionsInSelectSet on three dimensions of a drawing view (two stacked on one side, one on the right):
    /// they move and keep their values. PMI dimensions of a model go through the document's ArrangeDimensionsInSelectSet.
    /// </summary>
    public static object Arrange(SeContext context, string? documentHandle, string[]? dimensions, double? pitch, bool associative)
    {
        double multiplier = pitch is { } p ? Units.RequirePositive(p, "pitch") : 1;
        List<(string Handle, SolidEdgeFrameworkSupport.Dimension Dimension)> chosen;
        SolidEdgeDocument owner;
        if (dimensions is { Length: > 0 })
        {
            chosen = [.. dimensions.Distinct(StringComparer.Ordinal).Select(h => (h, context.Handles.Resolve<SolidEdgeFrameworkSupport.Dimension>(h, "dimensions")))];
            owner = context.DocumentOf(dimensions[0]);
            if (dimensions.Any(h => !ReferenceEquals(context.DocumentOf(h), owner)))
            {
                throw ToolException.InvalidArgument("'dimensions' must all be in one document.");
            }
        }
        else
        {
            DraftDocument draft = context.GetDocument<DraftDocument>(documentHandle, "draft");
            owner = (SolidEdgeDocument)draft;
            chosen = [.. Late.Items(draft.ActiveSheet.Dimensions).OfType<SolidEdgeFrameworkSupport.Dimension>()
                .Select(d => (context.Register(d, "dimension", owner), d))];
            if (chosen.Count == 0)
            {
                throw ToolException.NotFound($"The active sheet '{draft.ActiveSheet.Name}' has no dimensions.", "Add some with se_drawing dimension or retrieve_dimensions.");
            }
        }

        object target = owner is DraftDocument
            ? Late.TryGet(chosen[0].Dimension, "Parent") is Sheet sheet && chosen.All(c => Late.TryGet(c.Dimension, "Parent") is Sheet s && s.Name == sheet.Name)
                ? sheet
                : throw ToolException.InvalidArgument("'dimensions' must all be on one sheet of the drawing.")
            : owner;
        double[]?[] before = [.. chosen.Select(c => RangeOf(c.Dimension))];
        SelectSet selection = context.Application.ActiveSelectSet;
        selection.RemoveAll();
        try
        {
            foreach ((_, SolidEdgeFrameworkSupport.Dimension dimension) in chosen)
            {
                selection.Add(dimension);
            }

            Late.Call(target, "ArrangeDimensionsInSelectSet", multiplier, associative);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("arrange_failed", $"Solid Edge did not arrange the dimensions: {com.Message.Trim()}", "Pass dimensions placed on one sheet.", ex);
        }
        finally
        {
            selection.RemoveAll();
        }

        var arranged = chosen.Select((c, i) =>
        {
            double[]? after = RangeOf(c.Dimension);
            bool moved = before[i] is { } b && after is { } a && b.Zip(a, (x, y) => Math.Abs(x - y)).Any(d => d > 1e-7);
            return new
            {
                dimension = c.Handle,
                value = Math.Round(VariableService.FromDatabase(c.Dimension.Value, VariableService.UnitsOf(c.Dimension)), 6),
                moved,
                from = Centre(before[i]),
                to = Centre(after),
            };
        }).ToList();
        return new { count = arranged.Count, moved = arranged.Count(a => a.moved), pitch = multiplier, associative, dimensions = arranged };
    }

    private static double[]? RangeOf(SolidEdgeFrameworkSupport.Dimension dimension)
    {
        try
        {
            dimension.Range(out double x1, out double y1, out double x2, out double y2);
            return [x1, y1, x2, y2];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // A dimension that is not drawn has no range.
            return null;
        }
    }

    private static double[]? Centre(double[]? range) => range is null ? null : [Geo.Mm((range[0] + range[2]) / 2), Geo.Mm((range[1] + range[3]) / 2)];

    /// <summary>
    /// Maintain Alignment Set: whether dimensions placed in line keep their alignment set when one moves. Verified in Solid
    /// Edge 2026: DraftDocument.MaintainAlignmentSet reads true by default and takes false and true.
    /// </summary>
    public static object AlignmentSet(SeContext context, string? documentHandle, bool? maintain)
    {
        DraftDocument draft = context.GetDocument<DraftDocument>(documentHandle, "draft");
        bool previous = draft.MaintainAlignmentSet;
        if (maintain is { } wanted && wanted != previous)
        {
            draft.MaintainAlignmentSet = wanted;
            if (draft.MaintainAlignmentSet != wanted)
            {
                throw new ToolException("setting_rejected", "Solid Edge did not change Maintain Alignment Set.");
            }
        }

        return new { document = context.DocumentHandle((SolidEdgeDocument)draft), maintain = draft.MaintainAlignmentSet, previous };
    }
}
