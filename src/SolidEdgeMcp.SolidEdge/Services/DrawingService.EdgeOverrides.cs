// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Hide Edges and Show Edges of a drawing view: edges hidden or shown one by one over the view's display, and every such
/// override cleared. Verified in Solid Edge 2026 on the front view of a block: <c>ShowHideEdgeOverride</c> of a view line set
/// to Hide reads back Hide and the line is no longer drawn (its segment style gives E_FAIL), also after the view is updated;
/// <c>DrawingView.ClearEdgeOverrides</c> brings every edge back to None and drawn. Show draws a hidden edge again but reads back
/// Hide, so a shown edge is checked by being drawn. Edge Painter has no member: the graphics of a view have no style to set
/// (only <c>GetSegmentedStyle</c>).
/// </summary>
public static partial class DrawingService
{
    public static object EdgeOverride(SeContext context, string? viewHandle, double[]? points, string mode)
    {
        DrawingView view = ResolveView(context, viewHandle);
        if (mode == "clear")
        {
            int overridden = Graphics(view).Count(g => Override(g.Value) != DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideNone);
            view.ClearEdgeOverrides();
            List<Graphic> after = Graphics(view);
            int left = after.Count(g => Override(g.Value) != DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideNone);
            return left == 0
                ? new { view = viewHandle, cleared = overridden, edges = after.Count }
                : throw new ToolException("edges_failed", $"{left} edges of the view kept their override.", "Update the view (se_drawing update) and try again.");
        }

        DVShowHideEdgeOverrideType wanted = mode switch
        {
            "hide" => DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide,
            "show" => DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideShow,
            _ => throw ToolException.InvalidArgument($"Unknown edge override '{mode}'.", "Use hide_edges, show_edges or clear_edges."),
        };
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 1);
        List<Graphic> graphics = Graphics(view);
        if (graphics.Count == 0)
        {
            throw new ToolException("nothing_to_edit", "The view has no edges.", "Update the view (se_drawing update) and try again.");
        }

        Graphic[] picked = [.. model.Select(p => Nearest(view, graphics, ModelToView(view, p), p, _ => true, "edge"))];
        var done = new List<(Graphic Graphic, DVShowHideEdgeOverrideType Before)>();
        try
        {
            foreach (Graphic graphic in picked.Distinct())
            {
                done.Add((graphic, Override(graphic.Value)));
                SetOverride(graphic.Value, wanted);
                bool drawn = Drawn(graphic.Value);
                if (drawn != (mode == "show") || (mode == "hide" && Override(graphic.Value) != wanted))
                {
                    throw new ToolException("edges_failed", $"Solid Edge did not {mode} the edge at ({Geo.Mm(graphic.Centre.X)}, {Geo.Mm(graphic.Centre.Y)}) of the view.",
                        "Give a model point on an edge the view draws (se_geometry edges).");
                }
            }
        }
        catch (Exception ex) when (ex is ToolException || (ex is COMException com && !ComErrors.IsGone(com)))
        {
            foreach ((Graphic graphic, DVShowHideEdgeOverrideType before) in done)
            {
                TrySetOverride(graphic.Value, before);
            }

            throw ex as ToolException ?? new ToolException("edges_failed", $"Solid Edge refused the edge override ({ex.Message.Trim()}).", "Check the view and the points.");
        }

        return new
        {
            view = viewHandle,
            edges = picked.Select((g, i) => new
            {
                point = new[] { Geo.Mm(model[i].X), Geo.Mm(model[i].Y), Geo.Mm(model[i].Z) },
                kind = g.Shape.ToString().ToLowerInvariant(),
                @override = mode,
                drawn = Drawn(g.Value),
            }).ToArray(),
        };
    }

    private static DVShowHideEdgeOverrideType Override(object graphic) => graphic switch
    {
        DVLine2d line => line.get_ShowHideEdgeOverride(Type.Missing),
        DVArc2d arc => arc.get_ShowHideEdgeOverride(Type.Missing),
        DVCircle2d circle => circle.get_ShowHideEdgeOverride(Type.Missing),
        _ => DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideNone,
    };

    private static void SetOverride(object graphic, DVShowHideEdgeOverrideType value)
    {
        switch (graphic)
        {
            case DVLine2d line:
                line.set_ShowHideEdgeOverride(Type.Missing, value);
                break;
            case DVArc2d arc:
                arc.set_ShowHideEdgeOverride(Type.Missing, value);
                break;
            case DVCircle2d circle:
                circle.set_ShowHideEdgeOverride(Type.Missing, value);
                break;
        }
    }

    private static void TrySetOverride(object graphic, DVShowHideEdgeOverrideType value)
    {
        try
        {
            SetOverride(graphic, value);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the one that made the edges go back.
        }
    }

    /// <summary>Whether the view draws the element: a hidden one has no readable segment style.</summary>
    private static bool Drawn(object graphic)
    {
        foreach ((Func<DrawingView, object> _, Func<object, int> count, Func<object, int, object?> style) in StyledGraphics)
        {
            try
            {
                return count(graphic) > 0 && style(graphic, 1) is not null;
            }
            catch (InvalidCastException)
            {
                // Another kind of graphic.
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                return false;
            }
        }

        return false;
    }
}
