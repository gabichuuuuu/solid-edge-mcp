// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class SketchService
{
    /// <summary>
    /// Tear off, as the Tear Off command does: a new sketch on <paramref name="plane"/> with a linked copy of the elements of
    /// <paramref name="sketch"/> at the same 2D coordinates. Verified: <c>Sketches.AddByTearOff</c> takes the curves of the
    /// profile's curve body (solid or curve edges give E_NOINTERFACE) and works only associative (otherwise E_POINTER and an
    /// empty sketch, which is deleted); in an assembly, <c>Layouts.AddByTearOff</c> with layout curves fails (E_FAIL).
    /// </summary>
    public static object TearOff(SeContext context, string sketch, string? plane)
    {
        Profile profile = Resolve(context, sketch);
        if (LayoutOf(profile) is not null || profile.Parent is not Sketch)
        {
            throw new ToolException("not_supported", $"'{sketch}' is not a part sketch: Solid Edge tears off only part and sheet metal sketches through the API.",
                "Draw the elements again on a layout of the other plane (se_sketch create layout=true).");
        }

        ModelDocument model = ModelDocument.From((SolidEdgeFramework.SolidEdgeDocument)context.DocumentOf(sketch));
        RefPlane target = model.ResolvePlane(context, plane ?? throw ToolException.InvalidArgument(
            "tear_off requires 'plane': '1'-'3', a refplane handle or a planar face.", "Make an offset plane with se_ref_plane if needed."));
        object[] curves;
        try
        {
            curves = Late.Items(profile.CurveBody.Curves).ToArray();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // An empty sketch has no curve body (E_FAIL, verified).
            curves = [];
        }

        if (curves.Length == 0)
        {
            throw new ToolException("sketch_not_closed", $"'{sketch}' has no curves to tear off yet.",
                "Close it first with se_sketch 'close' (open=true for an open chain).");
        }

        Canvas2d source = Canvas2d.Of(profile);
        int count = model.Sketches.Count;
        Array array = curves;
        Sketch? created = null;
        try
        {
            created = model.Sketches.AddByTearOff(ref array, target, true, true);
        }
        catch (Exception ex) when (ex is COMException || ex.HResult == unchecked((int)0x80004003))
        {
            DeleteNewSketches(model, count);
            throw new ToolException("sketch_failed", $"Solid Edge did not tear off the sketch ({ex.Message}).",
                "Close the sketch first (se_sketch close) and try another plane.", ex);
        }

        Profile copy = created?.Profiles.Count > 0 ? created.Profiles.Item(1) : throw Failed(model, count, "it made an empty sketch");
        Canvas2d result = Canvas2d.Of(copy);
        int Elements(Canvas2d c) => c.Lines2d.Count + c.Arcs2d.Count + c.Circles2d.Count + c.Ellipses2d.Count + c.BSplineCurves2d.Count;
        if (Elements(result) != Elements(source))
        {
            throw Failed(model, count, $"the new sketch has {Elements(result)} elements instead of {Elements(source)}");
        }

        return new
        {
            sketch = context.Register(copy, "sketch", model.Document),
            plane = context.Register(target, "refplane", model.Document),
            frame = Frame(copy),
            elements = Summary(result),
        };
    }

    private static ToolException Failed(ModelDocument model, int count, string problem)
    {
        DeleteNewSketches(model, count);
        return new ToolException("sketch_failed", $"The tear off was deleted because {problem}.", "Close the sketch and try another plane.");
    }

    private static void DeleteNewSketches(ModelDocument model, int count)
    {
        while (model.Sketches.Count > count)
        {
            try
            {
                model.Sketches.Item(model.Sketches.Count).Delete();
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                return;
            }
        }
    }
}
