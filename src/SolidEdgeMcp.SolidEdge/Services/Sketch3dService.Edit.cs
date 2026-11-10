// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using KeyType = SolidEdgePart.Sketch3DKeypointType;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// The 3D Draw commands beyond lines, arcs and splines: arcs through three points and tangent to a line, rectangles, mirror
/// copies, split and trim, equation driven curves and model edges included. Verified in Solid Edge 2026 (part, ordered):
/// <list type="bullet">
/// <item>Lines3D.CreateRectangle(width, height, centre, normal, width direction, out lines) makes four lines, with no relations;
/// its out array must be passed initialised (a null array fails before the call).</item>
/// <item>Sketch3D.Mirror(ref elements, plane, out copies) adds the mirror copies (a line about the xz plane: y negated).</item>
/// <item>Sketch3D.Split3DSegment(element, point) splits a line in two at the point (0..100 at 40: 0..40 and 40..100);
/// TrimByPoint(element, point) removes the part of the element on that side of the nearest crossing element.</item>
/// <item>Sketch3D.AddEquationDrivenCurve(x, y, z, t0, t1, out code) evaluates the expressions in mm; the curve is an edge of
/// the 3D sketch but not an item of BSplineCurves3D. With some ranges it ends one sample short of t1 (0..1: 0.999; 1..50 and
/// 0..2 exact), so its ends are read back.</item>
/// <item>Sketch3D.IncludeEdge(edge) adds a copy of a model edge (a line, or a circle as an ellipse) but reports E_FAIL, so
/// the elements are counted before and after.</item>
/// </list>
/// </summary>
public static partial class Sketch3dService
{
    /// <summary>Arc from 'points' = [start, a point on the arc, end]: its centre is the circumcentre of the three.</summary>
    private static object ArcThrough(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[][] p = PointList(spec.Points, 3, "arc_through", exactly: 3);
        double[] start = p[0], through = p[1], end = p[2];
        double[] a = Geo.Minus(through, start), b = Geo.Minus(end, start);
        double[] axb = Geo.Cross(a, b);
        double n2 = Geo.Dot(axb, axb);
        if (n2 < 1e-18)
        {
            throw ToolException.InvalidArgument("The three points of arc_through are in line (or two are the same): no arc passes through them.");
        }

        double[] offset = Geo.Cross(Geo.Minus(Geo.Scale(b, Geo.Dot(a, a)), Geo.Scale(a, Geo.Dot(b, b))), axb);
        double[] center = [.. start.Zip(offset, (s, o) => s + o / (2 * n2))];
        double radius = Geo.Distance(start, center);
        double[] normal = Geo.TryUnit(axb)!;
        Arc3D arc = Make("arc", () => sketch.Arcs3D.AddByStartEndCenterNormal(start[0], start[1], start[2], end[0], end[1], end[2],
            center[0], center[1], center[2], normal[0], normal[1], normal[2], radius));
        Check(arc, Math.Abs(arc.Radius - radius) < Tolerance, $"its radius is {Geo.Mm(arc.Radius)} mm instead of {Geo.Mm(radius)} mm");
        return Changed(context, document, sketch, [arc]);
    }

    /// <summary>
    /// Arc that leaves the end (or start) of a line along it and ends at 'points' = [end]; tied to the line by connect and
    /// tangent relations, as the Tangent Arc command draws it.
    /// </summary>
    private static object TangentArc(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        object element = Elements(context, spec.Elements, "tangent_arc", 1, 1)[0];
        if (element is not Line3D line)
        {
            throw ToolException.InvalidArgument("tangent_arc starts from a 3D line: give one line handle in 'elements'.");
        }

        KeyType key = Key(spec.Keypoints, 0, KeyType.igSketch3DEndPoint);
        if (key is not (KeyType.igSketch3DEndPoint or KeyType.igSketch3DStartPoint))
        {
            throw ToolException.InvalidArgument("tangent_arc leaves the start or the end of the line ('keypoints': [start] or [end]).");
        }

        double[] lineStart = Keypoint(line, KeyType.igSketch3DStartPoint) ?? throw new ToolException("feature_failed", "Solid Edge did not give the start of the line.");
        double[] lineEnd = Keypoint(line, KeyType.igSketch3DEndPoint) ?? throw new ToolException("feature_failed", "Solid Edge did not give the end of the line.");
        double[] start = key == KeyType.igSketch3DEndPoint ? lineEnd : lineStart;
        double[] tangent = Geo.TryUnit(key == KeyType.igSketch3DEndPoint ? Geo.Minus(lineEnd, lineStart) : Geo.Minus(lineStart, lineEnd))!;
        double[] end = PointList(spec.Points, 1, "tangent_arc", exactly: 1)[0];
        double[] chord = Geo.Minus(end, start);
        double[] normal = Geo.TryUnit(Geo.Cross(tangent, chord))
            ?? throw ToolException.InvalidArgument("The end of tangent_arc lies on the line's direction: a tangent arc cannot reach it.");
        double[] inward = Geo.TryUnit(Geo.Cross(normal, tangent))!;
        double radius = Geo.Dot(chord, chord) / (2 * Geo.Dot(chord, inward));
        double[] center = [.. start.Zip(inward, (s, u) => s + radius * u)];
        Arc3D arc = Make("arc", () => sketch.Arcs3D.AddByStartEndCenterNormal(start[0], start[1], start[2], end[0], end[1], end[2],
            center[0], center[1], center[2], normal[0], normal[1], normal[2], radius));
        Check(arc, Math.Abs(arc.Radius - radius) < Tolerance, $"its radius is {Geo.Mm(arc.Radius)} mm instead of {Geo.Mm(radius)} mm");
        try
        {
            Sketch3DRelations relations = sketch.Sketch3DRelations;
            Make("connect relation", () => relations.AddConnect(line, key, arc, KeyType.igSketch3DStartPoint));
            Make("tangent relation", () => relations.AddTangent(line, arc));
        }
        catch
        {
            Late.TryDelete(arc);
            throw;
        }

        double[] arcStart = Keypoint(arc, KeyType.igSketch3DStartPoint) ?? [];
        double[] arcEnd = Keypoint(arc, KeyType.igSketch3DEndPoint) ?? [];
        Check(arc, arcStart.Length == 3 && arcEnd.Length == 3 && Geo.Distance(arcStart, start) < 1e-5 && Geo.Distance(arcEnd, end) < 1e-5,
            "the relations moved it off the line or the end point");
        return Changed(context, document, sketch, [arc]);
    }

    /// <summary>Rectangle centred on 'points' = [centre], in the plane of 'normal', 'width' along 'direction' and 'height' across it.</summary>
    private static object Rectangle(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[] c = PointList(spec.Points, 1, "rectangle", exactly: 1)[0];
        double[] n = Direction(spec.Normal ?? throw ToolException.InvalidArgument("rectangle requires 'normal': the axis [x,y,z] of its plane."), "normal");
        double width = Units.MmToM(Units.RequirePositive(spec.Width ?? throw ToolException.InvalidArgument("rectangle requires 'width' (mm)."), "width"));
        double height = Units.MmToM(Units.RequirePositive(spec.Height ?? throw ToolException.InvalidArgument("rectangle requires 'height' (mm)."), "height"));
        double[] along = spec.Direction is { } given ? Direction(given, "direction") : DefaultAlong(n);
        double[] u = Geo.TryUnit(Geo.Minus(along, Geo.Scale(n, Geo.Dot(along, n))))
            ?? throw ToolException.InvalidArgument("'direction' is along 'normal': the width must run in the plane of the rectangle.");
        Array lines = Array.Empty<object>();
        int before = sketch.Lines3D.Count;
        Make("rectangle", () =>
        {
            sketch.Lines3D.CreateRectangle(width, height, c[0], c[1], c[2], n[0], n[1], n[2], u[0], u[1], u[2], out lines);
            return lines;
        });
        Line3D[] made = lines.Length == 4 ? [.. lines.Cast<object?>().OfType<Line3D>()] : [.. Late.Items(sketch.Lines3D).OfType<Line3D>().Skip(before)];
        double[] sides = [.. made.Select(l => l.Length).Order()];
        if (made.Length != 4 || Math.Abs(sides[0] - Math.Min(width, height)) > Tolerance || Math.Abs(sides[3] - Math.Max(width, height)) > Tolerance)
        {
            made.ToList().ForEach(Late.TryDelete);
            throw new ToolException("feature_failed", "Solid Edge did not make the rectangle as asked; its lines were deleted.");
        }

        Sketch3DRelations relations = sketch.Sketch3DRelations;
        for (int i = 0; i < 4; i++)
        {
            Line3D previous = made[i], next = made[(i + 1) % 4];
            Make("connect relation", () => relations.AddConnect(previous, KeyType.igSketch3DEndPoint, next, KeyType.igSketch3DStartPoint));
        }

        return Changed(context, document, sketch, made);
    }

    /// <summary>Mirror copies of 'elements' about 'plane'.</summary>
    private static object Mirror(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        object[] elements = Elements(context, spec.Elements, "mirror", 1, int.MaxValue);
        object plane = PlaneOf(context, document, spec.Plane, "mirror");
        int before = ElementCount(sketch);
        Array input = elements;
        Array copies = Array.Empty<object>();
        Make("mirror copies", () =>
        {
            sketch.Mirror(ref input, plane, out copies);
            return copies;
        });
        object[] made = [.. copies.Cast<object?>().OfType<object>()];
        if (ElementCount(sketch) <= before || made.Length == 0)
        {
            throw new ToolException("feature_failed", "Solid Edge did not add the mirror copies.", "Check the elements and the plane.");
        }

        return Changed(context, document, sketch, made);
    }

    /// <summary>Splits 'elements' = [one] in two at 'points' = [a point on it].</summary>
    private static object Split(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        object element = Elements(context, spec.Elements, "split", 1, 1)[0];
        double[] at = PointList(spec.Points, 1, "split", exactly: 1)[0];
        int before = ElementCount(sketch);
        object piece = Make("split", () => sketch.Split3DSegment(element, at[0], at[1], at[2]));
        if (ElementCount(sketch) != before + 1)
        {
            throw new ToolException("feature_failed", "Solid Edge did not split the element.", "Give a point on the element, away from its ends.");
        }

        return Changed(context, document, sketch, [element, piece]);
    }

    /// <summary>Trims 'elements' = [one]: the part on the side of 'points' = [a point on it] beyond the nearest crossing is removed.</summary>
    private static object Trim(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        object element = Elements(context, spec.Elements, "trim", 1, 1)[0];
        double[] at = PointList(spec.Points, 1, "trim", exactly: 1)[0];
        double? length = element is Line3D line ? line.Length : null;
        Make("trim", () =>
        {
            sketch.TrimByPoint(element, at[0], at[1], at[2]);
            return element;
        });
        if (length is { } was && element is Line3D trimmed && trimmed.Length >= was - Tolerance)
        {
            throw new ToolException("feature_failed", "Solid Edge did not trim the line.",
                "Give a point on the part to remove, beyond an element that crosses it.");
        }

        return Changed(context, document, sketch, [element]);
    }

    /// <summary>Curve x(t), y(t), z(t) ('equations', mm) for t from range[0] to range[1].</summary>
    private static object Equation(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        if (spec.Equations is not { Length: 3 } eq || eq.Any(string.IsNullOrWhiteSpace))
        {
            throw ToolException.InvalidArgument("equation requires 'equations': [x(t), y(t), z(t)] in mm, e.g. [\"100*t\", \"20*sin(t*pi)\", \"0\"].");
        }

        if (spec.Range is not { Length: 2 } range || !(range[1] > range[0]))
        {
            throw ToolException.InvalidArgument("equation requires 'range': [t start, t end] with end > start.");
        }

        EquationDrivenCurveErrorCode code = EquationDrivenCurveErrorCode.seEquationDrivenCurveErrorCodeUnknownError;
        BSplineCurve3D? curve;
        try
        {
            curve = sketch.AddEquationDrivenCurve(eq[0].Trim(), eq[1].Trim(), eq[2].Trim(), range[0], range[1], out code);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("feature_failed", $"Solid Edge rejected the equation driven curve: {com.Message.Trim()}",
                "Write the expressions in t with + - * / ^, parentheses, pi and functions such as sin, cos, sqrt.", ex);
        }

        if (code != EquationDrivenCurveErrorCode.seEquationDrivenCurveErrorCodeNoError || curve is null)
        {
            if (curve is not null)
            {
                Late.TryDelete(curve);
            }

            throw new ToolException("feature_failed", code switch
            {
                EquationDrivenCurveErrorCode.seEquationDrivenCurveErrorCodeInvalidExpression => "Solid Edge could not read the expressions.",
                EquationDrivenCurveErrorCode.seEquationDrivenCurveErrorCodeSelfIntersecting => "The curve crosses itself.",
                EquationDrivenCurveErrorCode.seEquationDrivenCurveErrorCodeDiscontinuous => "The curve is not continuous over the range.",
                _ => "Solid Edge did not make the curve.",
            }, "Write the expressions in t with + - * / ^, parentheses, pi and functions such as sin, cos, sqrt.");
        }

        return Changed(context, document, sketch, [curve]);
    }

    /// <summary>Copies model 'edges' into the sketch.</summary>
    private static object Include(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        if (spec.Edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("include requires 'edges': model edge handles (se_geometry edges).");
        }

        object[] edges = [.. spec.Edges.Select(h => context.Handles.Resolve(h, "edges"))];
        List<object> before = AllElements(sketch);
        foreach (object edge in edges)
        {
            try
            {
                sketch.IncludeEdge(edge);
            }
            catch (COMException ex) when (ex.HResult == ComErrors.EFail)
            {
                // IncludeEdge reports E_FAIL even when it adds the element: the count below decides.
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
            {
                throw new ToolException("feature_failed", $"Solid Edge rejected the edge: {com.Message.Trim()}", "Give edges of the model (se_geometry edges).", ex);
            }
        }

        object[] made = [.. AllElements(sketch).Where(e => !before.Any(b => Late.SameObject(b, e)))];
        if (made.Length < edges.Length)
        {
            throw new ToolException("feature_failed", $"Solid Edge included {made.Length} of the {edges.Length} edges.", "Check them with action 'info'.");
        }

        return Changed(context, document, sketch, made);
    }

    private static List<object> AllElements(Sketch3D sketch) =>
        [.. new object[] { sketch.Lines3D, sketch.Arcs3D, sketch.Ellipses3D, sketch.BSplineCurves3D }.SelectMany(c => Late.Items(c))];

    private static double[] DefaultAlong(double[] normal) =>
        Math.Abs(normal[0]) < 0.9 ? [1, 0, 0] : [0, 1, 0];
}
