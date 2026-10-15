// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Additional sketch elements (ellipse, spline, point, fillet, model edges) and 2D constraints:
/// geometric relations and dimensions that drive the sketch, as a person does with "Smart Dimension".
/// </summary>
public static partial class SketchService
{
    public static readonly IReadOnlyList<string> RelationTypes =
        ["horizontal", "vertical", "fix", "parallel", "perpendicular", "equal", "tangent", "concentric", "colinear", "connect", "symmetric"];

    public static readonly IReadOnlyList<string> DimensionTypes = ["length", "radius", "diameter", "distance", "angle"];

    /// <summary>Ellipse by center, major semi-axis, minor semi-axis and angle of the major axis (degrees from x).</summary>
    public static object Ellipse(SeContext context, string sketch, double[]? center, double? radius, double? minorRadius, double? angle)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        (double x, double y) = Geo.Point2(center, "center");
        double major = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'ellipse' requires 'radius' (semi-major axis, mm)."), "radius"));
        double minor = Units.MmToM(Units.RequirePositive(minorRadius ?? throw ToolException.InvalidArgument("Action 'ellipse' requires 'minorRadius' (semi-minor axis, mm)."), "minorRadius"));
        if (minor > major * (1 + 1e-12))
        {
            throw ToolException.InvalidArgument("'minorRadius' must not exceed 'radius' (the semi-major axis).", "Swap them and add 90 to 'angle'.");
        }

        double a = Units.DegToRad(Units.RequireFinite(angle ?? 0, "angle"));
        // The major axis is given as a vector from the center (verified: it is not an absolute point).
        Ellipse2d ellipse = canvas.Ellipses2d.AddByCenter(x, y, major * Math.Cos(a), major * Math.Sin(a), minor / major,
            Geom2dOrientationConstants.igGeom2dOrientCounterClockwise);
        return new { element = context.RegisterBeside(ellipse, "ellipse2d", sketch), geometry = Describe2d(ellipse) };
    }

    /// <summary>Cubic spline through the points; closed (continuous tangent) if <paramref name="closed"/>.</summary>
    public static object Spline(SeContext context, string sketch, double[]? points, bool closed)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", 3);
        Array flat = p.SelectMany(q => new[] { q.X, q.Y }).ToArray();
        int order = Math.Min(4, p.Count);
        BSplineCurve2d curve = closed
            ? canvas.BSplineCurves2d.AddByPointsWithCloseOption(order, p.Count, ref flat, true)
            : canvas.BSplineCurves2d.AddByPoints(order, p.Count, ref flat);
        return new { element = context.RegisterBeside(curve, "bspline2d", sketch), geometry = Describe2d(curve) };
    }

    /// <summary>
    /// A conic from the start to the end point whose end tangents meet at the control point; rho (between 0 and 1) sets the
    /// shape: 0.5 a parabola, less an ellipse, more a hyperbola. Verified: (0,0) to (50,0) with the control point at (25,30)
    /// and rho 0.5 peaks at 15 mm and closes a profile with the line under it (extruded 10 mm: 5002.29 mm³).
    /// </summary>
    public static object Conic(SeContext context, string sketch, double[]? points, double? rho)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        Profile profile = canvas.Profile ?? throw ToolException.InvalidArgument("conic draws in model sketches only (drawing sheets have no conics).");
        List<(double X, double Y)> p = Geo.Points2(points, "points", 3);
        if (p.Count != 3)
        {
            throw ToolException.InvalidArgument("'points' for conic: [xStart, yStart, xEnd, yEnd, xControl, yControl] in mm (the control point is where the end tangents meet).");
        }

        double r = rho ?? 0.5;
        if (!(r > 0 && r < 1))
        {
            throw ToolException.InvalidArgument("'rho' must be between 0 and 1: 0.5 is a parabola, less an ellipse, more a hyperbola.");
        }

        Conic2d conic = profile.Conics2d.AddByThreePointsAndRhoValue(p[0].X, p[0].Y, p[1].X, p[1].Y, p[2].X, p[2].Y, r);
        return new { element = context.RegisterBeside(conic, "conic2d", sketch), geometry = new { type = "conic", rho = r } };
    }

    public static object Point(SeContext context, string sketch, double[]? points)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        List<(double X, double Y)> p = Geo.Points2(points, "points", 1);
        if (p.Count != 1)
        {
            throw ToolException.InvalidArgument("'points' must be one point [x, y] for action 'point'.");
        }

        Point2d point = canvas.Points2d.Add(p[0].X, p[0].Y);
        return new { element = context.RegisterBeside(point, "point2d", sketch), geometry = Describe2d(point) };
    }

    /// <summary>
    /// Fillet between two elements that trims both. <paramref name="side"/> is a point inside the corner that
    /// is rounded; with two lines, by default, on the bisector of their common corner.
    /// </summary>
    public static object Fillet(SeContext context, string sketch, string[]? elements, double? radius, double[]? side)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] e = Elements(context, elements, 2, 2, "fillet");
        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'fillet' requires 'radius' (mm)."), "radius"));
        (double X, double Y) pick = side is not null ? Geo.Point2(side, "points")
            : e is [Line2d a, Line2d b] ? InsideCorner(a, b, r)
            : throw ToolException.InvalidArgument("A fillet between curves needs 'points' = [x, y], a point inside the corner to round.");
        try
        {
            // With no possible corner (e.g. parallel lines) Solid Edge does not throw an error: it returns null.
            Arc2d arc = canvas.Arcs2d.AddAsFillet(e[0], e[1], r, pick.X, pick.Y)
                ?? throw new ToolException("fillet_failed", "Solid Edge could not round the corner: the elements do not form a corner for that radius.",
                    "The two elements must meet (or cross) and both be long enough for the radius; pass 'points' inside the corner to round.");
            return new
            {
                element = context.RegisterBeside(arc, "arc2d", sketch),
                geometry = Describe2d(arc),
                trimmed = e.Select(Describe2d).ToArray(),
            };
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("fillet_failed", $"Solid Edge could not round the corner: {ex.Message}",
                "The two elements must meet (or cross) and both be long enough for the radius; pass 'points' inside the corner to round.", ex);
        }
    }

    /// <summary>
    /// Chamfer between two lines that trims both: <paramref name="setbacks"/> = one distance (equal) or two, measured from the
    /// corner along the first and the second line. <paramref name="side"/> is a point inside the corner, by default on the
    /// bisector of the common corner.
    /// </summary>
    public static object Chamfer(SeContext context, string sketch, string[]? elements, double[]? setbacks, double[]? side)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] e = Elements(context, elements, 2, 2, "chamfer");
        (double a, double b) = setbacks switch
        {
            [double equal] => (equal, equal),
            [double first, double second] => (first, second),
            _ => throw ToolException.InvalidArgument("Action 'chamfer' requires 'setbacks': one distance in mm (equal) or two (along the first and the second line)."),
        };
        a = Units.MmToM(Units.RequirePositive(a, "setbacks"));
        b = Units.MmToM(Units.RequirePositive(b, "setbacks"));
        (double X, double Y) pick = side is not null ? Geo.Point2(side, "points")
            : e is [Line2d l1, Line2d l2] ? InsideCorner(l1, l2, Math.Max(a, b))
            : throw ToolException.InvalidArgument("A chamfer needs two lines; pass 'points' = [x, y], a point inside the corner.");
        try
        {
            // Verified: SetBackA is measured along the second element and SetBackB along the first.
            Line2d line = canvas.Lines2d.AddAsChamfer(e[0], e[1], pick.X, pick.Y, b, a)
                ?? throw new ToolException("chamfer_failed", "Solid Edge could not chamfer the corner: the elements do not form a corner for those setbacks.",
                    "The two lines must meet (or cross) and be longer than the setbacks; pass 'points' inside the corner.");
            return new
            {
                element = context.RegisterBeside(line, "line2d", sketch),
                geometry = Describe2d(line),
                trimmed = e.Select(Describe2d).ToArray(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException || ex is COMException com && !ComErrors.IsGone(com))
        {
            // Lines without a corner (parallel) give E_INVALIDARG, which .NET raises as ArgumentException.
            throw new ToolException("chamfer_failed", $"Solid Edge could not chamfer the corner: {ex.Message}",
                "The two lines must meet (or cross) and be longer than the setbacks; pass 'points' inside the corner.", ex);
        }
    }

    /// <summary>Includes model edges in the sketch (projected and linked, like the "Include" command of the UI).</summary>
    public static object Project(SeContext context, string sketch, string[]? edges)
    {
        Profile profile = Resolve(context, sketch);
        if (edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("Action 'project' requires 'elements' with model edge handles.", "Get them with se_geometry action 'edges'.");
        }

        var created = new List<object>();
        foreach (string handle in edges)
        {
            object edge = context.Handles.Resolve<SolidEdgeGeometry.Edge>(handle, "elements");
            try
            {
                // The Geometry2d output parameter comes back empty (verified): the new elements are searched for in the collections.
                int[] before = ElementCounts(Canvas2d.Of(profile));
                profile.IncludeEdge(edge, out _);
                foreach (object geometry in NewElements(Canvas2d.Of(profile), before))
                {
                    created.Add(new { edge = handle, element = context.RegisterBeside(geometry, KindOf(geometry), sketch), geometry = Describe2d(geometry) });
                }
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                throw new ToolException("project_failed", $"Solid Edge could not include edge '{handle}' in the sketch: {ex.Message}",
                    "The edge must belong to this part; edges lying off the sketch plane are projected onto it.", ex);
            }
        }

        return new { elements = created };
    }

    /// <summary>Geometric relation between sketch elements; Solid Edge repositions the geometry to satisfy it.</summary>
    public static object Relate(SeContext context, string sketch, string type, string[]? elements, int[]? keypoints)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        Relations2d relations = canvas.Relations2d;
        (int count, string usage) = type switch
        {
            "horizontal" or "vertical" or "fix" => (1, "one line (fix: any element)"),
            "connect" => (2, "two elements plus 'keypoints' [i1, i2]"),
            "symmetric" => (3, "the symmetry line followed by two elements"),
            _ when RelationTypes.Contains(type) => (2, "two elements"),
            _ => throw ToolException.InvalidArgument($"Unknown relation '{type}'.", $"Use one of: {string.Join(", ", RelationTypes)}."),
        };
        object[] e = Elements(context, elements, count, count, $"relation '{type}' ({usage})");
        int[] k = type == "connect" ? Keypoints(keypoints, 2) : [];
        try
        {
            Relation2d relation = type switch
            {
                "horizontal" => relations.AddHorizontal(e[0]),
                "vertical" => relations.AddVertical(e[0]),
                "fix" => relations.AddFix(e[0]),
                "parallel" => relations.AddParallel(e[0], e[1]),
                "perpendicular" => relations.AddPerpendicular(e[0], e[1]),
                "equal" => relations.AddEqual(e[0], e[1]),
                "tangent" => relations.AddTangent(e[0], e[1]),
                "concentric" => relations.AddConcentric(e[0], e[1]),
                "colinear" => relations.AddColinear(e[0], e[1]),
                "connect" => relations.AddKeypoint(e[0], k[0], e[1], k[1]),
                _ => relations.AddSymmetric(e[0], e[1], e[2]),
            };
            return new
            {
                relation = context.RegisterBeside(relation, "relation2d", sketch),
                type,
                elements = e.Select(Describe2d).ToArray(),
            };
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // 0x80040210 comes with a misleading text (PerUser subscription); it appears when repeating a relation that already exists.
            string reason = ex.HResult == unchecked((int)0x80040210)
                ? "it repeats or contradicts a relation the elements already have (HRESULT 0x80040210)"
                : ex.Message;
            throw new ToolException("relation_failed", $"Solid Edge rejected the {type} relation: {reason}",
                "Check the element types (e.g. parallel needs lines, concentric circles or arcs) and the existing relations (se_sketch_constraint 'list'): rectangle lines are already horizontal/vertical and connected.", ex);
        }
    }

    /// <summary>
    /// Sketch dimension that drives the geometry (like the ones in the UI). With <paramref name="value"/> it sets that measure
    /// (mm or degrees); without it, it locks the current one. Its variable can be changed later with se_variables.
    /// </summary>
    public static object Dimension(SeContext context, string sketch, string type, string[]? elements, int[]? keypoints, double? value)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        Dimensions dimensions = canvas.Dimensions;
        int count = type is "distance" or "angle" ? 2 : 1;
        object[] e = Elements(context, elements, count, count, $"dimension '{type}'");
        int[]? k = keypoints is null ? null : Keypoints(keypoints, count);
        double? target = value is null ? null
            : type == "angle" ? Units.DegToRad(Units.RequirePositive(value.Value, "value"))
            : Units.MmToM(Units.RequirePositive(value.Value, "value"));
        if (type == "angle" && target >= Math.PI - 1e-12)
        {
            throw ToolException.InvalidArgument("'value' of an angle must be between 0 and 180 degrees (the angle inside the corner of the two lines).");
        }

        // The interior angle is converted to the one Solid Edge stores (see AngleFrame).
        AngleFrame? frame = type == "angle" ? AngleFrame.Of(e) : null;
        Dimension dimension;
        try
        {
            dimension = type switch
            {
                "length" => dimensions.AddLength(e[0] as Line2d ?? throw ToolException.InvalidArgument("A 'length' dimension takes one line.", "For arcs and circles use 'radius' or 'diameter'.")),
                "radius" => dimensions.AddRadius(e[0]),
                "diameter" => dimensions.AddCircularDiameter(e[0]),
                "distance" => Distance(dimensions, e, k),
                "angle" => Angle(dimensions, e),
                _ => throw ToolException.InvalidArgument($"Unknown dimension '{type}'.", $"Use one of: {string.Join(", ", DimensionTypes)}."),
            };
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("dimension_failed", $"Solid Edge could not dimension the elements: {ex.Message}",
                "Check the element types: length (line), radius/diameter (circle or arc), distance (two elements or keypoints), angle (two lines).", ex);
        }

        double measured = dimension.Value;
        dimension.Constraint = true;
        if (target is { } wanted)
        {
            double raw = frame is { } f ? f.ToSolidEdge(wanted) : wanted;
            dimension.Value = raw;
            if (Math.Abs(dimension.Value - raw) > 1e-9)
            {
                double got = frame is { } g ? g.ToInterior(dimension.Value) : dimension.Value;
                Late.Call(dimension, "Delete");
                throw new ToolException("dimension_failed",
                    $"Solid Edge kept the {type} at {Format(type, got)} instead of {Format(type, wanted)}; the dimension was deleted.",
                    "The sketch is probably over-constrained: remove a conflicting relation or dimension (se_sketch_constraint 'delete').");
            }
        }

        double current = dimension.Value;
        return new
        {
            dimension = context.RegisterBeside(dimension, "dimension", sketch),
            variable = dimension.DisplayName,
            type,
            measured = Num(type, frame is { } m ? m.ToInterior(measured) : measured),
            value = Num(type, frame is { } v ? v.ToInterior(current) : current),
            variableValue = frame is null ? (double?)null : Num(type, current),
            unit = type == "angle" ? "deg" : "mm",
            elements = e.Select(Describe2d).ToArray(),
        };
    }

    /// <summary>Relations and dimensions of the sketch.</summary>
    public static object Constraints(SeContext context, string sketch)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        Dimensions dimensions = canvas.Dimensions;
        var items = new List<object>();
        for (int i = 1; i <= dimensions.Count; i++)
        {
            Dimension d = dimensions.Item(i);
            string kind = d.DimensionType switch
            {
                DimTypeConstants.igDimTypeAngular or DimTypeConstants.igDimTypeArcAngle => "angle",
                DimTypeConstants.igDimTypeRadial => "radius",
                DimTypeConstants.igDimTypeRDiameter or DimTypeConstants.igDimTypeCDiameter or DimTypeConstants.igDimTypeSDiameter => "diameter",
                _ => "linear",
            };
            items.Add(new
            {
                dimension = context.RegisterBeside(d, "dimension", sketch),
                variable = d.DisplayName,
                type = kind,
                value = Num(kind, d.Value),
                unit = kind == "angle" ? "deg" : "mm",
                driving = d.Constraint,
            });
        }

        Relations2d relations = canvas.Relations2d;
        var related = new List<object>();
        for (int i = 1; i <= relations.Count && related.Count < context.MaxListItems; i++)
        {
            Relation2d relation = relations.Item(i);
            related.Add(new
            {
                relation = context.RegisterBeside(relation, "relation2d", sketch),
                type = RelationName(relation.Type),
                elements = RelatedElements(context, relation, sketch),
            });
        }

        return new
        {
            sketch,
            relationCount = relations.Count,
            relations = related,
            dimensions = items,
            truncated = related.Count < relations.Count ? true : (bool?)null,
        };
    }

    /// <summary>2D elements of the sketch with their handle and geometry (mm), up to the list limit.</summary>
    private static (List<object> Items, bool Truncated) ElementList(SeContext context, Canvas2d canvas, string sketch)
    {
        var items = new List<object>();
        System.Collections.IEnumerable[] groups =
        [
            canvas.Lines2d, canvas.Circles2d, canvas.Arcs2d, canvas.Ellipses2d,
            canvas.EllipticalArcs2d, canvas.BSplineCurves2d, canvas.Points2d,
        ];
        foreach (object element in groups.SelectMany(g => g.Cast<object>()))
        {
            if (items.Count >= context.MaxListItems)
            {
                return (items, true);
            }

            items.Add(new { element = context.RegisterBeside(element, KindOf(element), sketch), geometry = Describe2d(element) });
        }

        return (items, false);
    }

    /// <summary>
    /// Elements joined by a relation, with the key point when the relation is on a point. GetRelatedObject
    /// starts at 1; a negative key point index means the whole element.
    /// </summary>
    private static object[] RelatedElements(SeContext context, Relation2d relation, string sketch)
    {
        relation.GetRelatedObjectCount(out int count);
        var result = new object[count];
        for (int j = 1; j <= count; j++)
        {
            relation.GetRelatedObject(j, out object element, out int keypoint);
            double[]? point = null;
            if (keypoint >= 0)
            {
                try
                {
                    (double x, double y) = KeyPoint(element, keypoint);
                    point = [Geo.Mm(x), Geo.Mm(y)];
                }
                catch (ToolException)
                {
                    // Element without that queryable key point (e.g. a reference axis): only the index is given.
                }
            }

            result[j - 1] = new
            {
                element = context.RegisterBeside(element, KindOf(element), sketch),
                keypoint = keypoint >= 0 ? keypoint : (int?)null,
                point,
            };
        }

        return result;
    }

    /// <summary>Name of the 2D relation from its type (SolidEdgeFramework.ObjectType).</summary>
    private static string RelationName(int type) => (SolidEdgeFramework.ObjectType)type switch
    {
        SolidEdgeFramework.ObjectType.igKeyPointRelation2d => "connect",
        SolidEdgeFramework.ObjectType.igHorizontalRelation2d => "horizontal",
        SolidEdgeFramework.ObjectType.igVerticalRelation2d => "vertical",
        SolidEdgeFramework.ObjectType.igFixRelation2d => "fix",
        SolidEdgeFramework.ObjectType.igParallelRelation2d => "parallel",
        SolidEdgeFramework.ObjectType.igPerpendicularRelation2d => "perpendicular",
        SolidEdgeFramework.ObjectType.igEqualRelation2d => "equal",
        SolidEdgeFramework.ObjectType.igTangentRelation2d => "tangent",
        SolidEdgeFramework.ObjectType.igConcentricRelation2d => "concentric",
        SolidEdgeFramework.ObjectType.igColinearRelation2d => "colinear",
        SolidEdgeFramework.ObjectType.igSymmetricRelation2d => "symmetric",
        SolidEdgeFramework.ObjectType.igPointOnRelation2d => "point_on",
        SolidEdgeFramework.ObjectType.igIntersectRelation2d => "intersect",
        SolidEdgeFramework.ObjectType.igHorizontalAlignRelation2d => "horizontal_align",
        SolidEdgeFramework.ObjectType.igVerticalAlignRelation2d => "vertical_align",
        SolidEdgeFramework.ObjectType.igOffsetRelation2d => "offset",
        SolidEdgeFramework.ObjectType.igChamferRelation2d => "chamfer",
        SolidEdgeFramework.ObjectType.igFilletRelation2d => "fillet",
        SolidEdgeFramework.ObjectType.igSetRelation2d => "set",
        SolidEdgeFramework.ObjectType.igLinkRelation2d => "link",
        _ => $"type_{type}",
    };

    /// <summary>Deletes a relation or dimension of the sketch ('relation' or 'dimension' handle).</summary>
    public static object DeleteConstraint(SeContext context, string handle)
    {
        object item = context.Handles.Resolve(handle, "constraint");
        if (item is not (Relation2d or SolidEdgeFrameworkSupport.Dimension))
        {
            throw new ToolException("handle_wrong_type", $"'{handle}' is not a sketch relation or dimension.",
                "Pass a handle returned by se_sketch_constraint 'relation', 'dimension' or 'list'.");
        }

        Late.Call(item, "Delete");
        context.Handles.Remove(handle, "the constraint was deleted");
        return new { deleted = handle };
    }

    /// <summary>
    /// Auto-Scale Sketch: the whole sketch is scaled so that one driving dimension takes a value. Verified in Solid Edge
    /// 2026: Dimension.AutoScaleSketch(value m, out ok) on a driving dimension of 50 mm with value 0.1 scaled the sketch by 2
    /// (a 30 mm line became 60); on a driven dimension, or when the sketch has a second driving dimension (which the scale
    /// would change), it returns false and changes nothing.
    /// </summary>
    public static object AutoScale(SeContext context, string handle, double? value)
    {
        if (context.Handles.Resolve(handle, "constraint") is not SolidEdgeFrameworkSupport.Dimension dimension)
        {
            throw new ToolException("handle_wrong_type", $"'{handle}' is not a sketch dimension.", "Pass a dimension handle from se_sketch_constraint 'dimension' or 'list'.");
        }

        if (dimension.DimensionType is SolidEdgeFrameworkSupport.DimTypeConstants.igDimTypeAngular or SolidEdgeFrameworkSupport.DimTypeConstants.igDimTypeArcAngle)
        {
            throw ToolException.InvalidArgument("Auto-scale needs a length dimension; an angle does not change with the scale.");
        }

        if (!dimension.Constraint)
        {
            throw ToolException.InvalidArgument($"'{handle}' is a driven dimension.", "Auto-scale works with a driving dimension (se_sketch_constraint dimension).");
        }

        double target = Units.MmToM(Units.RequirePositive(value ?? throw ToolException.InvalidArgument("autoscale needs 'value': the new size in mm."), "value"));
        double before = dimension.Value;
        dimension.AutoScaleSketch(target, out bool scaled);
        if (!scaled || Math.Abs(dimension.Value - target) > 1e-9)
        {
            throw new ToolException("constraint_failed", $"Solid Edge did not scale the sketch (the dimension is {Geo.Mm(dimension.Value)} mm).",
                "Auto-scale works while this is the only driving dimension of the sketch: scale it before dimensioning the rest, or set the values (se_variables set).");
        }

        return new { dimension = handle, before = Geo.Mm(before), value = Geo.Mm(dimension.Value), scale = Math.Round(target / before, 9) };
    }

    /// <summary>Geometry of a 2D element in mm (and degrees), in sketch coordinates.</summary>
    internal static object Describe2d(object element)
    {
        static double[] P(double x, double y) => [Geo.Mm(x), Geo.Mm(y)];
        switch (element)
        {
            case Line2d line:
            {
                line.GetStartPoint(out double x1, out double y1);
                line.GetEndPoint(out double x2, out double y2);
                return new { type = "line", start = P(x1, y1), end = P(x2, y2), length = Geo.Mm(line.Length) };
            }

            case Circle2d circle:
            {
                circle.GetCenterPoint(out double x, out double y);
                return new { type = "circle", center = P(x, y), radius = Geo.Mm(circle.Radius) };
            }

            case Arc2d arc:
            {
                arc.GetCenterPoint(out double x, out double y);
                arc.GetStartPoint(out double x1, out double y1);
                arc.GetEndPoint(out double x2, out double y2);
                return new { type = "arc", center = P(x, y), start = P(x1, y1), end = P(x2, y2), radius = Geo.Mm(arc.Radius) };
            }

            case Ellipse2d ellipse:
            {
                ellipse.GetCenterPoint(out double x, out double y);
                return new
                {
                    type = "ellipse",
                    center = P(x, y),
                    radius = Geo.Mm(ellipse.MajorRadius),
                    minorRadius = Geo.Mm(ellipse.MinorRadius),
                    angle = Geo.R(Units.RadToDeg(ellipse.RotationAngle)),
                };
            }

            case Point2d point:
            {
                point.GetPoint(out double x, out double y);
                return new { type = "point", point = P(x, y) };
            }

            case BSplineCurve2d spline:
            {
                // Verified: the nodes (points it passes through) start at 1; key point 1 is the second node, not the end.
                spline.GetNode(1, out double x1, out double y1);
                spline.GetNode(spline.NodeCount, out double x2, out double y2);
                return new { type = "spline", start = P(x1, y1), end = P(x2, y2), nodes = spline.NodeCount, length = Geo.Mm(spline.Length) };
            }

            default:
                return new { type = KindOf(element) };
        }
    }

    /// <summary>Handle kind of a 2D element (line2d, circle2d…), for elements registered by other services.</summary>
    internal static string ElementKind(object element) => KindOf(element);

    private static string KindOf(object element) => element switch
    {
        Line2d => "line2d",
        Circle2d => "circle2d",
        Arc2d => "arc2d",
        Ellipse2d => "ellipse2d",
        Point2d => "point2d",
        BSplineCurve2d => "bspline2d",
        _ => InteropCatalog.GetTypeName(element)?.Split('.')[^1].ToLowerInvariant() ?? "element2d",
    };

    private static object[] Elements(SeContext context, string[]? handles, int min, int max, string what)
    {
        if (handles is null || handles.Length < min || handles.Length > max)
        {
            string count = min == max ? $"{min}" : $"{min} to {max}";
            throw ToolException.InvalidArgument($"'elements' must list {count} sketch element handle(s) for {what}.",
                "Use the handles returned when drawing (line2d:…, circle2d:…, arc2d:…).");
        }

        return handles.Select(h => context.Handles.Resolve(h, "elements")).ToArray();
    }

    private static int[] Keypoints(int[]? keypoints, int count) =>
        keypoints is { } k && k.Length == count && k.All(i => i >= 0)
            ? k
            : throw ToolException.InvalidArgument($"'keypoints' must give {count} keypoint index(es), one per element.",
                "Lines: 0 start, 1 end, 2 middle. Circles: 0 centre. Arcs: 0 centre, 1 start, 2 end. Ellipses: 0 centre.");

    /// <summary>
    /// Distance between two elements. Without key points the line is taken by its midpoint (distance between lines or from
    /// point to line), and circles, arcs, ellipses and points by their center.
    /// </summary>
    private static Dimension Distance(Dimensions dimensions, object[] e, int[]? k)
    {
        (double X, double Y, bool Key)[] picks = e.Select((element, i) => k is null ? DefaultPick(element) : (KeyPoint(element, k[i]).X, KeyPoint(element, k[i]).Y, true)).ToArray();
        Dimension dimension = dimensions.AddDistanceBetweenObjects(e[0], picks[0].X, picks[0].Y, 0, picks[0].Key, e[1], picks[1].X, picks[1].Y, 0, picks[1].Key);
        if (ExpectedDistance(e, picks) is not { } expected || Math.Abs(dimension.Value - expected) <= DistanceTolerance)
        {
            return dimension;
        }

        // Verified: between lines with opposite directions (the top side of a rectangle runs from right to left)
        // Solid Edge measures in another direction; reversing the direction of the measurement axis gives the real distance.
        dimension.MeasurementAxisDirection = !dimension.MeasurementAxisDirection;
        if (Math.Abs(dimension.Value - expected) <= DistanceTolerance)
        {
            return dimension;
        }

        double got = dimension.Value;
        Late.Call(dimension, "Delete");
        throw new ToolException("dimension_failed",
            $"Solid Edge measured {Format("distance", got)} instead of the true distance {Format("distance", expected)}; the dimension was deleted.",
            "Dimension between keypoints instead ('keypoints' [i1, i2]), or use se_sketch_constraint 'length' on a line.");
    }

    private const double DistanceTolerance = 1e-7;

    /// <summary>
    /// Real distance that the dimension must measure, in m: between points, from point to line or between parallel lines. Null if there is no
    /// single obvious one (intersecting lines, curves).
    /// </summary>
    private static double? ExpectedDistance(object[] e, (double X, double Y, bool Key)[] picks)
    {
        (double X, double Y)? point0 = picks[0].Key ? (picks[0].X, picks[0].Y) : null;
        (double X, double Y)? point1 = picks[1].Key ? (picks[1].X, picks[1].Y) : null;
        Line2d? line0 = picks[0].Key ? null : e[0] as Line2d;
        Line2d? line1 = picks[1].Key ? null : e[1] as Line2d;
        return (point0, point1, line0, line1) switch
        {
            ({ } a, { } b, _, _) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)),
            ({ } a, _, _, { } m) => PointToLine(a, m),
            (_, { } b, { } m, _) => PointToLine(b, m),
            (_, _, { } m0, { } m1) when Parallel(m0, m1) => PointToLine(Ends(m1).Start, m0),
            _ => null,
        };
    }

    private static ((double X, double Y) Start, (double X, double Y) End) Ends(Line2d line)
    {
        line.GetStartPoint(out double x1, out double y1);
        line.GetEndPoint(out double x2, out double y2);
        return ((x1, y1), (x2, y2));
    }

    private static double PointToLine((double X, double Y) p, Line2d line)
    {
        ((double X, double Y) a, (double X, double Y) b) = Ends(line);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool Parallel(Line2d a, Line2d b)
    {
        ((double X, double Y) a1, (double X, double Y) a2) = Ends(a);
        ((double X, double Y) b1, (double X, double Y) b2) = Ends(b);
        (double X, double Y) u = Unit(a2.X - a1.X, a2.Y - a1.Y);
        (double X, double Y) v = Unit(b2.X - b1.X, b2.Y - b1.Y);
        return Math.Abs(u.X * v.Y - u.Y * v.X) < 1e-9;
    }

    /// <summary>
    /// Angle between two lines. Solid Edge stores the counter-clockwise turn of the direction (start→end) from the first to the
    /// second, between 0 and 360°, ignoring the selection points (verified); the tool works with the
    /// interior angle of the corner they form (0–180°), measured between the segments seen from their intersection.
    /// </summary>
    private readonly record struct AngleFrame(int Sense, double Offset)
    {
        public static AngleFrame Of(object[] e)
        {
            if (e is not [Line2d a, Line2d b])
            {
                throw ToolException.InvalidArgument("An 'angle' dimension takes two lines.");
            }

            ((double X, double Y) a1, (double X, double Y) a2) = Ends(a);
            ((double X, double Y) b1, (double X, double Y) b2) = Ends(b);
            (double X, double Y) da = (a2.X - a1.X, a2.Y - a1.Y);
            (double X, double Y) db = (b2.X - b1.X, b2.Y - b1.Y);
            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) < 1e-12 * Math.Sqrt((da.X * da.X + da.Y * da.Y) * (db.X * db.X + db.Y * db.Y)))
            {
                throw ToolException.InvalidArgument("The lines are parallel: there is no angle between them.", "Use a 'distance' dimension instead.");
            }

            // Intersection of the lines and, from it, the direction toward the center of each segment.
            double t = ((b1.X - a1.X) * db.Y - (b1.Y - a1.Y) * db.X) / cross;
            (double X, double Y) corner = (a1.X + t * da.X, a1.Y + t * da.Y);
            (double X, double Y) ua = Toward(corner, a1, a2, da);
            (double X, double Y) ub = Toward(corner, b1, b2, db);
            double psi = Mod(Math.Atan2(ua.X * ub.Y - ua.Y * ub.X, ua.X * ub.X + ua.Y * ub.Y));
            int sense = psi <= Math.PI ? 1 : -1;
            // Each line whose direction points toward the corner has its direction turned by 180°.
            double flips = (Dot(db, ub) < 0 ? Math.PI : 0) - (Dot(da, ua) < 0 ? Math.PI : 0);
            return new AngleFrame(sense, flips);
        }

        public double ToSolidEdge(double interior) => Mod(Sense * interior + Offset);

        public double ToInterior(double raw)
        {
            double angle = Mod(Sense * (raw - Offset));
            return angle > Math.PI ? 2 * Math.PI - angle : angle;
        }

        private static (double X, double Y) Toward((double X, double Y) corner, (double X, double Y) start, (double X, double Y) end, (double X, double Y) direction)
        {
            (double X, double Y) middle = ((start.X + end.X) / 2 - corner.X, (start.Y + end.Y) / 2 - corner.Y);
            double length = Math.Sqrt(middle.X * middle.X + middle.Y * middle.Y);
            double scale = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            // If the intersection is the center of the segment (crossed lines), its direction is used.
            return length < 1e-9 * scale ? (direction.X / scale, direction.Y / scale) : (middle.X / length, middle.Y / length);
        }

        private static double Dot((double X, double Y) a, (double X, double Y) b) => a.X * b.X + a.Y * b.Y;

        private static double Mod(double angle) => ((angle % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
    }

    private static readonly Func<Canvas2d, object>[] ElementCollections =
        [c => c.Lines2d, c => c.Arcs2d, c => c.Circles2d, c => c.Ellipses2d, c => c.EllipticalArcs2d, c => c.BSplineCurves2d, c => c.Points2d];

    private static int[] ElementCounts(Canvas2d canvas) => ElementCollections.Select(c => Late.Count(c(canvas))).ToArray();

    /// <summary>Counts of the 2D element collections of a drawing sheet, to find what an operation adds to it.</summary>
    internal static int[] SheetElementCounts(SolidEdgeDraft.Sheet sheet) => ElementCounts(Canvas2d.Of(sheet));

    /// <summary>The 2D elements added to a sheet since <paramref name="before"/>, registered beside <paramref name="relatedHandle"/>.</summary>
    internal static object[] AddedOnSheet(SeContext context, SolidEdgeDraft.Sheet sheet, int[] before, string relatedHandle) =>
        Registered(context, relatedHandle, NewElements(Canvas2d.Of(sheet), before).ToList());

    /// <summary>2D elements added to the sketch since <paramref name="before"/> (the collections grow at the end).</summary>
    private static IEnumerable<object> NewElements(Canvas2d canvas, int[] before)
    {
        for (int i = 0; i < ElementCollections.Length; i++)
        {
            object collection = ElementCollections[i](canvas);
            int count = Late.Count(collection);
            for (int j = before[i] + 1; j <= count; j++)
            {
                yield return Late.Call(collection, "Item", j)!;
            }
        }
    }

    private static Dimension Angle(Dimensions dimensions, object[] e)
    {
        if (e is not [Line2d a, Line2d b])
        {
            throw ToolException.InvalidArgument("An 'angle' dimension takes two lines.");
        }

        (double X, double Y, bool _) pa = DefaultPick(a);
        (double X, double Y, bool _) pb = DefaultPick(b);
        return dimensions.AddAngleBetweenObjects(a, pa.X, pa.Y, 0, false, b, pb.X, pb.Y, 0, false);
    }

    private static (double X, double Y, bool Key) DefaultPick(object element)
    {
        switch (element)
        {
            case Line2d line:
                line.GetStartPoint(out double x1, out double y1);
                line.GetEndPoint(out double x2, out double y2);
                return ((x1 + x2) / 2, (y1 + y2) / 2, false);
            case Point2d point:
                point.GetPoint(out double px, out double py);
                return (px, py, true);
            default:
                (double X, double Y) c = KeyPoint(element, 0);
                return (c.X, c.Y, true);
        }
    }

    /// <summary>Key point of a 2D element (KeypointIndexConstants indices), through IDispatch: it works for any type.</summary>
    private static (double X, double Y) KeyPoint(object element, int index)
    {
        try
        {
            object?[] r = Late.CallByRef(element, "GetKeyPoint", [index, 0.0, 0.0, 0.0, 0, 0], 1, 2, 3, 4, 5);
            return (Convert.ToDouble(r[1], CultureInfo.InvariantCulture), Convert.ToDouble(r[2], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw ToolException.InvalidArgument($"The element has no keypoint {index}.",
                "Lines: 0 start, 1 end, 2 middle. Circles: 0 centre. Arcs: 0 centre, 1 start, 2 end.");
        }
    }

    /// <summary>Point inside the common corner of two lines, on the bisector, at a distance of the radius.</summary>
    private static (double X, double Y) InsideCorner(Line2d a, Line2d b, double radius)
    {
        a.GetStartPoint(out double a1x, out double a1y);
        a.GetEndPoint(out double a2x, out double a2y);
        b.GetStartPoint(out double b1x, out double b1y);
        b.GetEndPoint(out double b2x, out double b2y);
        (double X, double Y)[] ea = [(a1x, a1y), (a2x, a2y)];
        (double X, double Y)[] eb = [(b1x, b1y), (b2x, b2y)];
        (int i, int j) = (from p in new[] { 0, 1 } from q in new[] { 0, 1 }
                          orderby Math.Pow(ea[p].X - eb[q].X, 2) + Math.Pow(ea[p].Y - eb[q].Y, 2)
                          select (p, q)).First();
        (double X, double Y) corner = ea[i];
        (double X, double Y) ua = Unit(ea[1 - i].X - corner.X, ea[1 - i].Y - corner.Y);
        (double X, double Y) ub = Unit(eb[1 - j].X - corner.X, eb[1 - j].Y - corner.Y);
        if (Math.Abs(ua.X * ub.Y - ua.Y * ub.X) < 1e-9)
        {
            throw ToolException.InvalidArgument("The lines are parallel or colinear: there is no corner to round.");
        }

        (double X, double Y) bisector = Unit(ua.X + ub.X, ua.Y + ub.Y);
        return (corner.X + bisector.X * radius, corner.Y + bisector.Y * radius);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt(x * x + y * y);
        return length < 1e-15
            ? throw ToolException.InvalidArgument("The lines are parallel or degenerate: there is no corner to round.")
            : (x / length, y / length);
    }

    private static double Num(string type, double value) => type == "angle" ? Geo.R(Units.RadToDeg(value)) : Geo.Mm(value);

    private static string Format(string type, double value) => type == "angle"
        ? $"{Geo.R(Units.RadToDeg(value)).ToString(CultureInfo.InvariantCulture)} deg"
        : $"{Geo.Mm(value).ToString(CultureInfo.InvariantCulture)} mm";
}
