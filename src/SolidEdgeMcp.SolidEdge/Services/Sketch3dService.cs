// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using KeyType = SolidEdgePart.Sketch3DKeypointType;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What a se_sketch3d call draws or relates (model coordinates in mm).</summary>
public sealed record Sketch3dSpec(string? Sketch, double[]? Points, double[]? Normal, double? Radius, bool Closed,
    string[]? Elements, string? Relation, string[]? Keypoints, string? Plane, string? Name, double? Width = null, double? Height = null,
    double[]? Direction = null, string[]? Equations = null, double[]? Range = null, string[]? Edges = null);

/// <summary>
/// 3D sketches of a part or sheet metal part: lines, arcs, circles, B-spline curves and points in model space, fillets
/// between two elements and 3D relations. Every element is read back (keypoints, length, radius) and deleted if it does not
/// match what was asked.
/// </summary>
public static partial class Sketch3dService
{
    private const double Tolerance = 1e-6;

    private static readonly string[] RelationNames =
        ["connect", "parallel", "perpendicular", "point_on", "include", "on_plane", "concentric", "tangent", "collinear", "equal",
         "coaxial", "horizontal_vertical", "fillet", "lock", "rigid_set", "symmetric"];

    public static object Run(SeContext context, string? documentHandle, string action, Sketch3dSpec spec) => action switch
    {
        "list" => List(context, documentHandle),
        "create" => Create(context, documentHandle, spec.Name),
        "info" => Info(context, spec.Sketch),
        "line" => Lines(context, spec),
        "arc" => Arc(context, spec),
        "arc_through" => ArcThrough(context, spec),
        "tangent_arc" => TangentArc(context, spec),
        "rectangle" => Rectangle(context, spec),
        "mirror" => Mirror(context, spec),
        "split" => Split(context, spec),
        "trim" => Trim(context, spec),
        "equation" => Equation(context, spec),
        "include" => Include(context, spec),
        "circle" => Circle(context, spec),
        "spline" => Spline(context, spec),
        "point" => Points(context, spec),
        "fillet" => Fillet(context, spec),
        "relation" => Relation(context, spec),
        "delete" => Delete(context, spec),
        _ => throw ToolException.InvalidArgument($"Unknown action '{action}' for a 3D sketch."),
    };

    public static object List(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object[] sketches = Late.Items(SketchesOf(model)).OfType<Sketch3D>().Select(s => Summary(context, model.Document, s)).ToArray();
        return new { document = context.DocumentHandle(model.Document), count = sketches.Length, sketches };
    }

    public static object Create(SeContext context, string? documentHandle, string? name)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        Sketches3D sketches = SketchesOf(model);
        int before = sketches.Count;
        Sketch3D sketch = sketches.Add();
        if (sketches.Count != before + 1)
        {
            throw new ToolException("feature_failed", "Solid Edge did not add the 3D sketch.");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            sketch.Name = name.Trim();
        }

        context.ModelChanged(model.Document, "a 3D sketch was added");
        return Summary(context, model.Document, sketch);
    }

    public static object Info(SeContext context, string? sketchHandle)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, sketchHandle);
        return Describe(context, document, sketch);
    }

    /// <summary>Lines through 'points' in order (a polyline); closed = back to the first point.</summary>
    private static object Lines(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[][] points = PointList(spec.Points, 2, "line");
        if (spec.Closed && points.Length < 3)
        {
            throw ToolException.InvalidArgument("A closed polyline needs at least three points.");
        }

        int segments = spec.Closed ? points.Length : points.Length - 1;
        var made = new List<Line3D>();
        try
        {
            for (int i = 0; i < segments; i++)
            {
                double[] a = points[i];
                double[] b = points[(i + 1) % points.Length];
                if (Geo.Distance(a, b) < Tolerance)
                {
                    throw ToolException.InvalidArgument($"Points {i + 1} and {(i + 1) % points.Length + 1} are the same.");
                }

                Line3D line = Make("line", () => sketch.Lines3D.Add(a[0], a[1], a[2], b[0], b[1], b[2]));
                made.Add(line);
                if (Math.Abs(line.Length - Geo.Distance(a, b)) > Tolerance)
                {
                    throw new ToolException("feature_failed", $"Line {i + 1} measures {Geo.Mm(line.Length)} mm instead of {Geo.Mm(Geo.Distance(a, b))} mm.");
                }
            }

            // Lines that share an end are not connected by Lines3D.Add: the chain is tied as the Line command ties it.
            Sketch3DRelations relations = sketch.Sketch3DRelations;
            for (int i = 1; i < made.Count + (spec.Closed ? 1 : 0); i++)
            {
                Line3D previous = made[i - 1];
                Line3D next = made[i % made.Count];
                Make("connect relation", () => relations.AddConnect(previous, KeyType.igSketch3DEndPoint, next, KeyType.igSketch3DStartPoint));
            }
        }
        catch
        {
            made.ForEach(TryDelete);
            throw;
        }

        return Changed(context, document, sketch, made);
    }

    /// <summary>Arc from 'points' = [start, end, center]; 'normal' picks the side it turns (default: start × end about the center).</summary>
    private static object Arc(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[][] p = PointList(spec.Points, 3, "arc", exactly: 3);
        double[] start = p[0], end = p[1], center = p[2];
        double radius = Geo.Distance(start, center);
        if (radius < Tolerance || Math.Abs(Geo.Distance(end, center) - radius) > 1e-5)
        {
            throw ToolException.InvalidArgument("The start and end of an arc must be at the same distance from its center.",
                $"Start to center: {Geo.Mm(radius)} mm; end to center: {Geo.Mm(Geo.Distance(end, center))} mm.");
        }

        double[] normal = spec.Normal is { } given ? Direction(given, "normal")
            : Unit(Geo.Cross(Sub(start, center), Sub(end, center)))
              ?? throw ToolException.InvalidArgument("Start, end and center are in line: give 'normal' to say which way the arc turns.");
        Arc3D arc = Make("arc", () => sketch.Arcs3D.AddByStartEndCenterNormal(start[0], start[1], start[2], end[0], end[1], end[2],
            center[0], center[1], center[2], normal[0], normal[1], normal[2], radius));
        Check(arc, Math.Abs(arc.Radius - radius) < Tolerance, $"its radius is {Geo.Mm(arc.Radius)} mm instead of {Geo.Mm(radius)} mm");
        return Changed(context, document, sketch, [arc]);
    }

    /// <summary>Circle by 'points' = [center], 'normal' and 'radius'.</summary>
    private static object Circle(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[] c = PointList(spec.Points, 1, "circle", exactly: 1)[0];
        double[] n = Direction(spec.Normal ?? throw ToolException.InvalidArgument("circle requires 'normal': the axis [x,y,z] of the circle."), "normal");
        double radius = Units.MmToM(Units.RequirePositive(spec.Radius ?? throw ToolException.InvalidArgument("circle requires 'radius' (mm)."), "radius"));
        Ellipse3D circle = Make("circle", () => sketch.Ellipses3D.AddByCenterRadiusNormal(c[0], c[1], c[2], n[0], n[1], n[2], radius));
        Check(circle, Math.Abs(circle.Radius - radius) < Tolerance, $"its radius is {Geo.Mm(circle.Radius)} mm instead of {Geo.Mm(radius)} mm");
        return Changed(context, document, sketch, [circle]);
    }

    /// <summary>B-spline curve through 'points'; closed = it returns smoothly to the first point.</summary>
    private static object Spline(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[][] points = PointList(spec.Points, spec.Closed ? 3 : 2, "spline");
        Array values = points.SelectMany(v => v).ToArray();
        BSplineCurve3D curve = Make("spline", () => sketch.BSplineCurves3D.AddByPoints(points.Length, ref values, spec.Closed));
        if (!spec.Closed)
        {
            double[] start = Keypoint(curve, KeyType.igSketch3DStartPoint) ?? [];
            double[] end = Keypoint(curve, KeyType.igSketch3DEndPoint) ?? [];
            Check(curve, start.Length == 3 && end.Length == 3 && Geo.Distance(start, points[0]) < 1e-5 && Geo.Distance(end, points[^1]) < 1e-5,
                "it does not run from the first point to the last one");
        }

        return Changed(context, document, sketch, [curve]);
    }

    /// <summary>One point per [x,y,z] of 'points'.</summary>
    private static object Points(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        double[][] points = PointList(spec.Points, 1, "point");
        int before = PointCount(sketch);
        // Points3D.Add takes every coordinate at once; its points have no objects of their own (Item gives E_NOTIMPL).
        Array values = points.SelectMany(v => v).ToArray();
        Make("points", () => sketch.Points3D.Add(values.Length, ref values));
        int added = PointCount(sketch) - before;
        if (added != points.Length)
        {
            throw new ToolException("feature_failed", $"Solid Edge added {added} points instead of {points.Length}.", "Check them with action 'info'.");
        }

        context.ModelChanged(document, "a 3D sketch changed");
        return new { points = points.Select(p => p.Select(Geo.Mm).ToArray()).ToArray(), sketch = Summary(context, document, sketch) };
    }

    /// <summary>Arc of 'radius' between two elements that meet ('elements'), trimming them.</summary>
    private static object Fillet(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        object[] pair = Elements(context, spec.Elements, "fillet", 2, 2);
        double radius = Units.MmToM(Units.RequirePositive(spec.Radius ?? throw ToolException.InvalidArgument("fillet requires 'radius' (mm)."), "radius"));
        Arc3D arc = Make("fillet", () => sketch.Arcs3D.AddAsFillet(pair[0], pair[1], radius, false));
        Check(arc, Math.Abs(arc.Radius - radius) < Tolerance, $"its radius is {Geo.Mm(arc.Radius)} mm instead of {Geo.Mm(radius)} mm");
        return Changed(context, document, sketch, [arc]);
    }

    /// <summary>A 3D relation between elements; the elements are read back after it.</summary>
    private static object Relation(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        string type = spec.Relation?.Trim().ToLowerInvariant() ?? throw ToolException.InvalidArgument("relation requires 'relation'.",
            "Use connect, parallel, perpendicular, collinear, equal, tangent, concentric, coaxial, horizontal_vertical, lock, on_plane, symmetric or rigid_set.");
        Sketch3DRelations relations = sketch.Sketch3DRelations;
        int before = relations.Count;
        object[] e;
        Func<Sketch3DRelation> add;
        switch (type)
        {
            case "connect":
                e = Elements(context, spec.Elements, type, 2, 2);
                (KeyType k1, KeyType k2) = (Key(spec.Keypoints, 0, KeyType.igSketch3DEndPoint), Key(spec.Keypoints, 1, KeyType.igSketch3DStartPoint));
                add = () => relations.AddConnect(e[0], k1, e[1], k2);
                break;
            case "parallel": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddParallel(e[0], e[1]); break;
            case "perpendicular": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddPerpendicular(e[0], e[1]); break;
            case "collinear": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddCollinear(e[0], e[1]); break;
            case "equal": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddEqual(e[0], e[1]); break;
            case "tangent": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddTangent(e[0], e[1]); break;
            case "concentric": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddConcentric(e[0], e[1]); break;
            case "coaxial": e = Elements(context, spec.Elements, type, 2, 2); add = () => relations.AddCoaxial(e[0], e[1]); break;
            case "horizontal_vertical": e = Elements(context, spec.Elements, type, 1, 1); add = () => relations.AddHV(e[0]); break;
            case "lock": e = Elements(context, spec.Elements, type, 1, 1); add = () => relations.AddLock(e[0]); break;
            case "on_plane":
            {
                e = Elements(context, spec.Elements, type, 1, 1);
                object plane = PlaneOf(context, document, spec.Plane, type);
                add = () => relations.AddOnPlane(e[0], plane);
                break;
            }

            case "symmetric":
            {
                e = Elements(context, spec.Elements, type, 2, 2);
                object plane = PlaneOf(context, document, spec.Plane, type);
                add = () => relations.AddSymmetric(e[0], e[1], plane);
                break;
            }

            case "rigid_set":
            {
                e = Elements(context, spec.Elements, type, 2, int.MaxValue);
                Array set = e;
                add = () => relations.AddRigidSet(ref set);
                break;
            }

            default:
                throw ToolException.InvalidArgument($"Unknown relation '{type}'.",
                    "Use connect, parallel, perpendicular, collinear, equal, tangent, concentric, coaxial, horizontal_vertical, lock, on_plane, symmetric or rigid_set.");
        }

        Sketch3DRelation relation = Make($"{type} relation", add);
        string? status = FeatureService.StatusOf(relation);
        if (relations.Count <= before || status is "failed")
        {
            TryDelete(relation);
            throw new ToolException("relation_failed", $"Solid Edge could not apply the {type} relation{(status is null ? "" : $" (status {status})")}; it was removed.",
                "Check that the elements can take it (e.g. lines for parallel, arcs or circles for concentric).");
        }

        context.ModelChanged(document, "a 3D relation was added");
        return new
        {
            relation = DescribeRelation(relation),
            elements = e.Select(x => DescribeElement(context, document, x)).ToArray(),
            sketch = Summary(context, document, sketch),
        };
    }

    /// <summary>Deletes 'elements' of the sketch, or the whole sketch when no elements are given.</summary>
    private static object Delete(SeContext context, Sketch3dSpec spec)
    {
        (Sketch3D sketch, SolidEdgeDocument document) = Resolve(context, spec.Sketch);
        if (spec.Elements is { Length: > 0 })
        {
            object[] elements = Elements(context, spec.Elements, "delete", 1, int.MaxValue);
            int before = ElementCount(sketch);
            foreach (object element in elements)
            {
                Late.Call(element, "Delete");
            }

            context.ModelChanged(document, "3D sketch elements were deleted");
            int after = ElementCount(sketch);
            return after < before
                ? new { deleted = before - after, sketch = Summary(context, document, sketch) }
                : throw new ToolException("feature_failed", "Solid Edge did not delete the elements.", "Check them with action 'info'.");
        }

        ModelDocument model = ModelDocument.From(document);
        Sketches3D sketches = SketchesOf(model);
        int count = sketches.Count;
        string name = sketch.Name;
        sketch.Delete();
        context.ModelChanged(document, "a 3D sketch was deleted");
        return sketches.Count < count
            ? new { deleted = name, count = sketches.Count }
            : throw new ToolException("feature_failed", $"Solid Edge did not delete the 3D sketch '{name}'.");
    }

    private static object Changed(SeContext context, SolidEdgeDocument document, Sketch3D sketch, IEnumerable<object> made)
    {
        context.ModelChanged(document, "a 3D sketch changed");
        object[] elements = made.Select(m => DescribeElement(context, document, m)).ToArray();
        return new { elements, sketch = Summary(context, document, sketch) };
    }

    private static object Summary(SeContext context, SolidEdgeDocument document, Sketch3D sketch) => new
    {
        handle = context.Register(sketch, "sketch3d", document),
        name = Late.TryGetString(sketch, "Name"),
        status = FeatureService.StatusOf(sketch),
        lines = sketch.Lines3D.Count,
        arcs = sketch.Arcs3D.Count,
        circles = sketch.Ellipses3D.Count,
        splines = sketch.BSplineCurves3D.Count,
        points = PointCount(sketch),
        relations = sketch.Sketch3DRelations.Count,
    };

    private static object Describe(SeContext context, SolidEdgeDocument document, Sketch3D sketch)
    {
        object[] elements = new object[] { sketch.Lines3D, sketch.Arcs3D, sketch.Ellipses3D, sketch.BSplineCurves3D }
            .SelectMany(c => Late.Items(c)).Select(e => DescribeElement(context, document, e)).ToArray();
        object[] relations = Late.Items(sketch.Sketch3DRelations).OfType<Sketch3DRelation>().Select(DescribeRelation).ToArray();
        return new { sketch = Summary(context, document, sketch), elements, relations };
    }

    private static object DescribeElement(SeContext context, SolidEdgeDocument document, object element)
    {
        string type = element switch
        {
            Line3D => "line",
            Arc3D => "arc",
            Ellipse3D e => e.IsCircle() ? "circle" : "ellipse",
            BSplineCurve3D => "spline",
            _ => "element",
        };
        double[]? start = type is "line" or "arc" or "spline" ? Keypoint(element, KeyType.igSketch3DStartPoint) : null;
        double[]? end = type is "line" or "arc" or "spline" ? Keypoint(element, KeyType.igSketch3DEndPoint) : null;
        double[]? center = type is "arc" or "circle" or "ellipse" ? Keypoint(element, KeyType.igSketch3DCenter) : null;
        return new
        {
            handle = context.Register(element, $"{type}3d", document),
            type,
            start = start?.Select(Geo.Mm).ToArray(),
            end = end?.Select(Geo.Mm).ToArray(),
            center = center?.Select(Geo.Mm).ToArray(),
            length = element is Line3D line ? Geo.Mm(line.Length) : (double?)null,
            radius = element switch { Arc3D a => Geo.Mm(a.Radius), Ellipse3D c => Geo.Mm(c.Radius), _ => (double?)null },
        };
    }

    private static object DescribeRelation(Sketch3DRelation relation)
    {
        int code = (int)relation.Type;
        return new
        {
            type = code >= 0 && code < RelationNames.Length ? RelationNames[code] : code.ToString(CultureInfo.InvariantCulture),
            status = FeatureService.StatusOf(relation),
        };
    }

    private static double[]? Keypoint(object element, KeyType type)
    {
        try
        {
            object?[] args = Late.CallByRef(element, "GetKeypointPosition", [type, new double[3]], 1);
            return args[1] is Array position ? [.. Geo.Doubles(position)] : null;
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }
    }

    private static int ElementCount(Sketch3D sketch) =>
        sketch.Lines3D.Count + sketch.Arcs3D.Count + sketch.Ellipses3D.Count + sketch.BSplineCurves3D.Count + PointCount(sketch);

    /// <summary>Points3D.Count gives E_POINTER (a NullReferenceException) while the sketch has no points.</summary>
    private static int PointCount(Sketch3D sketch)
    {
        try
        {
            return sketch.Points3D.Count;
        }
        catch (NullReferenceException)
        {
            return 0;
        }
    }

    private static Sketches3D SketchesOf(ModelDocument model) =>
        Late.Get(model.Document, "Sketches3D") as Sketches3D
        ?? throw new ToolException("not_supported", $"'{model.Document.Name}' has no 3D sketches.", "Use a part or sheet metal part.");

    private static (Sketch3D Sketch, SolidEdgeDocument Document) Resolve(SeContext context, string? handle)
    {
        string h = string.IsNullOrWhiteSpace(handle)
            ? throw ToolException.InvalidArgument("'sketch' is required: a 3D sketch handle (action 'create' or 'list').")
            : handle;
        return (context.Handles.Resolve<Sketch3D>(h, "sketch"), context.DocumentOf(h));
    }

    private static object[] Elements(SeContext context, string[]? handles, string action, int min, int max)
    {
        if (handles is null || handles.Length < min || handles.Length > max)
        {
            string count = min == max ? $"{min}" : max == int.MaxValue ? $"at least {min}" : $"{min} to {max}";
            throw ToolException.InvalidArgument($"{action} takes {count} element handle(s) in 'elements' (action 'info').");
        }

        return handles.Select(h => context.Handles.Resolve(h, "elements")).ToArray();
    }

    private static KeyType Key(string[]? keypoints, int index, KeyType fallback) =>
        keypoints is { } k && index < k.Length
            ? k[index].Trim().ToLowerInvariant() switch
            {
                "start" => KeyType.igSketch3DStartPoint,
                "end" => KeyType.igSketch3DEndPoint,
                "middle" => KeyType.igSketch3DMidPoint,
                "center" => KeyType.igSketch3DCenter,
                var other => throw ToolException.InvalidArgument($"Unknown keypoint '{other}'.", "Use start, end, middle or center."),
            }
            : fallback;

    private static object PlaneOf(SeContext context, SolidEdgeDocument document, string? plane, string action) =>
        ModelDocument.From(document).ResolvePlane(context,
            plane ?? throw ToolException.InvalidArgument($"{action} requires 'plane': '1'-'3', 'xy', 'yz', 'xz' or a reference plane handle."));

    /// <summary>Model points [x1,y1,z1,...] in mm → metres, grouped by three.</summary>
    private static double[][] PointList(double[]? values, int min, string action, int? exactly = null)
    {
        int need = exactly ?? min;
        if (values is null || values.Length % 3 != 0 || values.Length / 3 < min || (exactly is { } n && values.Length / 3 != n))
        {
            string count = exactly is not null ? $"{need}" : $"at least {need}";
            throw ToolException.InvalidArgument($"{action} takes {count} point(s) in 'points' as [x1,y1,z1,...] in mm.");
        }

        return values.Select(v => Units.MmToM(Units.RequireFinite(v, "points"))).Chunk(3).ToArray();
    }

    private static double[] Direction(double[] values, string name) =>
        values.Length == 3 && Unit(values.Select(v => Units.RequireFinite(v, name)).ToArray()) is { } unit
            ? unit
            : throw ToolException.InvalidArgument($"'{name}' must be a direction [x,y,z] that is not zero.");

    private static double[]? Unit(double[] v)
    {
        double length = Geo.Length(v);
        return length < 1e-12 ? null : v.Select(c => c / length).ToArray();
    }

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static T Make<T>(string what, Func<T> create)
    {
        try
        {
            return create() ?? throw new ToolException("feature_failed", $"Solid Edge did not add the {what}.");
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            throw new ToolException("feature_failed", $"Solid Edge rejected the {what}: {com.Message.Trim()}",
                "Check the points and directions (mm), and that the elements belong to this sketch.", ex);
        }
    }

    private static void Check(object element, bool ok, string problem)
    {
        if (!ok)
        {
            TryDelete(element);
            throw new ToolException("feature_failed", $"Solid Edge did not make the element as asked ({problem}); it was deleted.");
        }
    }

    private static void TryDelete(object element)
    {
        try
        {
            Late.Call(element, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Already gone.
        }
    }
}
