// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using AngularDimensionQuadrantConstants = SolidEdgeFrameworkSupport.AngularDimensionQuadrantConstants;
using DimAngularCoordnateOrientationConstants = SolidEdgeFrameworkSupport.DimAngularCoordnateOrientationConstants;
using DimChamferModeConstants = SolidEdgeFrameworkSupport.DimChamferModeConstants;
using Dimension = SolidEdgeFrameworkSupport.Dimension;
using DimensionOrientationConstants = SolidEdgeFrameworkSupport.DimensionOrientationConstants;
using DimTypeConstants = SolidEdgeFrameworkSupport.DimTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Dimensions placed on drawing views, and the elements of a view they are attached to: the edges drawn in the view are
/// found from model points, and each dimension is placed clear of the view and of the others.
/// </summary>
public static partial class DrawingService
{
    /// <summary>Retrieves the model dimensions (and centre marks) in a view and returns the ones added.</summary>
    public static object RetrieveDimensions(SeContext context, string? viewHandle)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Sheet sheet = SheetOf(view);
        var before = new HashSet<object>(DimensionsOf(sheet), ReferenceEqualityComparer.Instance);
        try
        {
            view.RetrieveDimensions();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("retrieve_failed", $"Solid Edge could not retrieve dimensions: {ex.Message.Trim()}", null, ex);
        }

        List<object> added = DimensionsOf(sheet).Where(d => !before.Contains(d)).Select(d => DescribeDimension(context, draft, d)).ToList();
        return new
        {
            view = context.Register(view, "drawing_view", Document(draft)),
            retrieved = added.Count,
            dimensions = added,
            note = added.Count == 0
                ? "Nothing was retrieved: only model dimensions (PMI or sketch dimensions) lying parallel to this view are retrieved."
                : null,
        };
    }

    /// Manual dimension in a view, as a person would place it: each model point (mm) is projected onto the view and selects the
    /// edge, circle or vertex through it. Linear dimensions are moved outside the outline and stacked.
    /// </summary>
    public static object AddDimension(SeContext context, string? viewHandle, DimensionKind kind, double[]? points, MeasureDirection orientation, double[]? position)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 1);
        List<Graphic> graphics = DimensionGraphics(view);
        if (graphics.Count == 0)
        {
            throw new ToolException("nothing_to_dimension", "The view has no edges to dimension.", "Update the view (action 'update') and try again.");
        }

        Sheet sheet = SheetOf(view);
        var dimensions = (SolidEdgeFrameworkSupport.Dimensions)sheet.Dimensions;
        (double X, double Y)? at = position is null ? null : Geo.Point2(position, "position");
        Dimension dimension;
        switch (kind)
        {
            case DimensionKind.Length:
            {
                (double X, double Y) p = model.Count >= 2 ? Mid(ModelToView(view, model[0]), ModelToView(view, model[1])) : ModelToView(view, model[0]);
                Graphic edge = Nearest(view, graphics, p, model[0], g => g.Shape == Shape.Line, "straight edge");
                dimension = dimensions.AddLength(edge.Reference);
                PlaceLinear(view, sheet, dimension, ToSheet(view, edge.Start), ToSheet(view, edge.End), null, graphics, at);
                break;
            }

            case DimensionKind.Diameter:
            case DimensionKind.Radius:
            {
                // A section shows a hole as two parallel edges, not as a circle.
                Graphic curve = Nearest(view, graphics, ModelToView(view, model[0]), model[0], g => g.Shape is Shape.Circle or Shape.Arc, "circle or arc",
                    "A hole or shaft seen from the side (two parallel edges, e.g. in a section): draw its axis with se_annotation centerline and use kind symmetric_diameter; or use a view that looks along it.");
                dimension = kind == DimensionKind.Radius ? dimensions.AddRadius(curve.Reference)
                    : curve.Shape == Shape.Circle ? dimensions.AddCircularDiameter(curve.Reference)
                    : dimensions.AddRadialDiameter(curve.Reference);
                PlaceRadial(view, dimension, curve, graphics, at);
                break;
            }

            case DimensionKind.Distance:
            case DimensionKind.Angle:
            {
                if (model.Count != 2)
                {
                    throw ToolException.InvalidArgument($"A {ArgumentNames.Snake(kind)} dimension needs two model points in 'points' [x1, y1, z1, x2, y2, z2].");
                }

                bool angle = kind == DimensionKind.Angle;
                Pick first = PickAt(view, graphics, model[0], angle);
                Pick second = PickAt(view, graphics, model[1], angle);
                if (ReferenceEquals(first.Graphic.Value, second.Graphic.Value) && !(first.KeyPoint && second.KeyPoint))
                {
                    throw ToolException.InvalidArgument("Both points select the same edge.",
                        angle ? "Give one point on each of the two edges." : "Use kind 'length' for the length of an edge, or pick two vertices.");
                }

                if (angle)
                {
                    (double X, double Y) corner = Intersection(view, first.Graphic, second.Graphic)
                        ?? throw ToolException.InvalidArgument("The two edges are parallel: there is no angle between them.", "Use kind 'distance' for parallel edges.");
                    dimension = dimensions.AddAngleBetweenObjects(first.Graphic.Reference, first.Sheet.X, first.Sheet.Y, 0, false,
                        second.Graphic.Reference, second.Sheet.X, second.Sheet.Y, 0, false);
                    OrientAngle(view, dimension, corner, first, second);
                    break;
                }

                string? direction = orientation is MeasureDirection.Horizontal or MeasureDirection.Vertical ? ArgumentNames.Snake(orientation) : null;
                first = ToEndpoint(view, first, out string? across1);
                second = ToEndpoint(view, second, out string? across2);
                if (first.KeyPoint && second.KeyPoint && Distance(first.Sheet, second.Sheet) < 1e-9)
                {
                    throw ToolException.InvalidArgument("Both points select the same point of the view.", "Pick two different vertices, centres or edges.");
                }

                if (direction is null && (across1 ?? across2) is { } square)
                {
                    if (across1 is not null && across2 is not null && across1 != across2)
                    {
                        throw ToolException.InvalidArgument("The two edges are perpendicular: there is no distance between them.",
                            "Pick a vertex or circle centre instead of one of the edges, or set measure horizontal/vertical.");
                    }

                    direction = square;
                }

                dimension = dimensions.AddDistanceBetweenObjects(first.Graphic.Reference, first.Sheet.X, first.Sheet.Y, 0, first.KeyPoint,
                    second.Graphic.Reference, second.Sheet.X, second.Sheet.Y, 0, second.KeyPoint);
                (double X, double Y)? measure = null;
                if (direction is not null)
                {
                    bool horizontal = direction == "horizontal";
                    dimension.SetOrientation(horizontal ? DimensionOrientationConstants.igOrientationHorizontal : DimensionOrientationConstants.igOrientationVertical);
                    measure = horizontal ? (1, 0) : (0, 1);
                }

                PlaceLinear(view, sheet, dimension, first.Sheet, second.Sheet, measure, graphics, at);
                break;
            }

            case DimensionKind.Coordinate:
                return CoordinateDimensions(context, draft, view, graphics, dimensions, model, orientation == MeasureDirection.Vertical);

            case DimensionKind.AngularCoordinate:
                return AngularCoordinateDimensions(context, draft, view, graphics, dimensions, model, orientation == MeasureDirection.Clockwise);

            case DimensionKind.Chamfer:
            {
                if (model.Count != 2)
                {
                    throw ToolException.InvalidArgument("A chamfer dimension needs two model points: one on the chamfer edge and one on the edge it is measured from.");
                }

                Pick bevel = PickAt(view, graphics, model[0], true);
                Pick from = PickAt(view, graphics, model[1], true);
                if (ReferenceEquals(bevel.Graphic.Value, from.Graphic.Value) || Intersection(view, bevel.Graphic, from.Graphic) is null)
                {
                    throw ToolException.InvalidArgument("The two points must be on the chamfer edge and on an edge that meets it.", "Give one point on each of the two edges.");
                }

                // Verified in Solid Edge 2026: the edge it is measured from goes first (the other order reads nonsense), and the
                // dimension starts unplaced (TrackDistance -123456) until its track distance is set.
                dimension = dimensions.AddChamfer(from.Graphic.Reference, from.Sheet.X, from.Sheet.Y, 0, false,
                    bevel.Graphic.Reference, bevel.Sheet.X, bevel.Sheet.Y, 0, false, DimChamferModeConstants.igDimChamferModeAlongAxis);
                (double X, double Y) b1 = ToSheet(view, bevel.Graphic.Start), b2 = ToSheet(view, bevel.Graphic.End);
                (double X, double Y) f1 = ToSheet(view, from.Graphic.Start), f2 = ToSheet(view, from.Graphic.End);
                double along = Distance(f1, f2);
                double setback = Math.Abs(((b2.X - b1.X) * (f2.X - f1.X)) + ((b2.Y - b1.Y) * (f2.Y - f1.Y))) / along / view.ScaleFactor;
                RequireValue(dimension, setback, "chamfer dimension");
                PlaceOutside(view, graphics, dimension, at);
                break;
            }

            case DimensionKind.SymmetricDiameter:
                dimension = SymmetricDiameter(view, sheet, graphics, dimensions, model, at);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new { view = context.Register(view, "drawing_view", Document(draft)), dimension = DescribeDimension(context, draft, dimension) };
    }

    /// <summary>
    /// Coordinate (ordinate) dimensions: an origin at a vertex or circle centre and one dimension per further point. Verified
    /// in Solid Edge 2026: where the origin's text goes decides what they measure (below the view: horizontal distances; to
    /// its left: vertical ones), and each value is checked against the points' distance on the sheet at the view scale.
    /// </summary>
    private static object CoordinateDimensions(SeContext context, DraftDocument draft, DrawingView view, List<Graphic> graphics,
        SolidEdgeFrameworkSupport.Dimensions dimensions, List<(double X, double Y, double Z)> model, bool vertical)
    {
        if (model.Count < 2)
        {
            throw ToolException.InvalidArgument("Coordinate dimensions need 'points': the origin first (a vertex or circle centre), then each point measured from it.");
        }

        Pick origin = PickAt(view, graphics, model[0], false);
        if (!origin.KeyPoint)
        {
            throw ToolException.InvalidArgument("The origin of coordinate dimensions (the first point) must be a vertex or a circle centre of the view.",
                "Give the model point of a corner or a hole centre (se_geometry lists edges with their ends and circles with their centres).");
        }

        List<Pick> picks = [.. model.Skip(1).Select(p => ToEndpoint(view, PickAt(view, graphics, p, false), out _))];
        Box outline = Outline(view, graphics);
        (double X, double Y) Text((double X, double Y) at) => vertical ? (outline.X1 - DimensionOffset, at.Y) : (at.X, outline.Y1 - DimensionOffset);
        double scale = view.ScaleFactor;
        var created = new List<Dimension>();
        try
        {
            (double X, double Y) text = Text(origin.Sheet);
            Dimension start = dimensions.AddCoordinateOriginEx(origin.Graphic.Reference, origin.Sheet.X, origin.Sheet.Y, 0, true, text.X, text.Y, 0);
            created.Add(start);
            foreach (Pick pick in picks)
            {
                text = Text(pick.Sheet);
                Dimension dimension = dimensions.AddCoordinateEx(start, pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint, text.X, text.Y, 0);
                created.Add(dimension);
                double expected = Math.Abs(vertical ? pick.Sheet.Y - origin.Sheet.Y : pick.Sheet.X - origin.Sheet.X) / scale;
                if (Math.Abs(dimension.Value - expected) > 1e-6)
                {
                    throw new ToolException("dimension_failed",
                        $"A coordinate dimension reads {Geo.Mm(dimension.Value)} mm instead of {Geo.Mm(expected)} mm; the dimensions were removed.",
                        "Give points that are vertices, circle centres or points on edges of this view.");
                }
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex)))
        {
            foreach (Dimension dimension in created)
            {
                try
                {
                    dimension.Delete();
                }
                catch (COMException deleteError) when (!ComErrors.IsGone(deleteError))
                {
                    // The error being reported is the original one.
                }
            }

            throw ex is ToolException ? ex
                : new ToolException("dimension_failed", $"Solid Edge rejected the coordinate dimensions: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        return new
        {
            view = context.Register(view, "drawing_view", Document(draft)),
            measures = vertical ? "vertical distances from the origin" : "horizontal distances from the origin",
            dimensions = created.Select(d => DescribeDimension(context, draft, d)).ToList(),
        };
    }

    /// <summary>
    /// Sets the track distance of a dimension that Solid Edge leaves unplaced (chamfer dimensions), or moves it to
    /// <paramref name="position"/>: of the two sides, the one whose dimension ends farther from the middle of the view.
    /// </summary>
    private static void PlaceOutside(DrawingView view, List<Graphic> graphics, Dimension dimension, (double X, double Y)? position)
    {
        Box outline = Outline(view, graphics);
        double best = double.MinValue, chosen = DimensionOffset;
        foreach (double track in new[] { DimensionOffset, -DimensionOffset, 2 * DimensionOffset, -2 * DimensionOffset })
        {
            dimension.TrackDistance = track;
            Box range = RangeOf(dimension);
            double score = position is { } p
                ? -Distance((range.CentreX, range.CentreY), p)
                : Distance((range.CentreX, range.CentreY), (outline.CentreX, outline.CentreY));
            if (range.Width < 1 && range.Height < 1 && score > best)
            {
                (best, chosen) = (score, track);
            }
        }

        dimension.TrackDistance = chosen;
    }

    /// <summary>
    /// Symmetric diameter: twice the distance from an axis (a centre line of the sheet, se_annotation centerline) to a straight
    /// edge parallel to it, e.g. the diameter of a shaft seen from the side. Verified in Solid Edge 2026: AddSymmetricalDiameter
    /// with the centre line and the edge measures along the axis by default (7 mm: twice the overhang of the centre line past
    /// the shaft end). Across a horizontal axis SetOrientation(vertical) gives the diameter (40 mm for a radius of 20); with a
    /// vertical axis SetOrientation reads 0 either way and MeasurementAxisDirection = true gives it (30 mm for a radius of 15).
    /// </summary>
    private static Dimension SymmetricDiameter(DrawingView view, Sheet sheet, List<Graphic> graphics, SolidEdgeFrameworkSupport.Dimensions dimensions,
        List<(double X, double Y, double Z)> model, (double X, double Y)? at)
    {
        if (model.Count != 2)
        {
            throw ToolException.InvalidArgument("A symmetric diameter needs two model points: one on the axis (a centre line of the view) and one on the edge whose distance to it is doubled.");
        }

        (double X, double Y) onAxis = ToSheet(view, ModelToView(view, model[0]));
        SolidEdgeFrameworkSupport.CenterLine? axis = null;
        Box axisBox = default;
        double nearest = double.MaxValue;
        foreach (SolidEdgeFrameworkSupport.CenterLine line in Late.Items(sheet.CenterLines).OfType<SolidEdgeFrameworkSupport.CenterLine>())
        {
            line.Range(out double x1, out double y1, out double x2, out double y2);
            var box = new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            double dx = Math.Max(0, Math.Max(box.X1 - onAxis.X, onAxis.X - box.X2)), dy = Math.Max(0, Math.Max(box.Y1 - onAxis.Y, onAxis.Y - box.Y2));
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < nearest)
            {
                (axis, axisBox, nearest) = (line, box, distance);
            }
        }

        if (axis is null || nearest > PickTolerance * view.ScaleFactor)
        {
            throw ToolException.NotFound("No centre line of the sheet passes through the first point.",
                "Draw the axis first with se_annotation centerline (two points, one on each silhouette edge of the shaft), then give a model point on it.");
        }

        bool horizontal = axisBox.Y2 - axisBox.Y1 < 1e-7;
        if (!horizontal && axisBox.X2 - axisBox.X1 >= 1e-7)
        {
            throw new ToolException("not_supported", "The centre line is neither horizontal nor vertical on the sheet.", "Use a view where the axis is horizontal or vertical.");
        }

        Pick edge = PickAt(view, graphics, model[1], true);
        (double X, double Y) e1 = ToSheet(view, edge.Graphic.Start), e2 = ToSheet(view, edge.Graphic.End);
        double drift = horizontal ? Math.Abs(e2.Y - e1.Y) : Math.Abs(e2.X - e1.X);
        double offset = horizontal ? e1.Y - axisBox.Y1 : e1.X - axisBox.X1;
        if (drift > 1e-7)
        {
            throw ToolException.InvalidArgument("The edge at the second point is not parallel to the axis.", "Give a point on a silhouette edge of the shaft, along the centre line.");
        }

        if (Math.Abs(offset) < 1e-7)
        {
            throw ToolException.InvalidArgument("The edge at the second point lies on the axis.", "Give a point on a silhouette edge of the shaft, away from the centre line.");
        }

        Dimension dimension = dimensions.AddSymmetricalDiameter(axis, onAxis.X, onAxis.Y, 0, false, edge.Graphic.Reference, edge.Sheet.X, edge.Sheet.Y, 0, false);
        if (horizontal)
        {
            dimension.SetOrientation(DimensionOrientationConstants.igOrientationVertical);
        }
        else
        {
            dimension.MeasurementAxisDirection = !dimension.MeasurementAxisDirection;
        }

        RequireValue(dimension, 2 * Math.Abs(offset) / view.ScaleFactor, "symmetric diameter", "Give a point on the centre line and one on a silhouette edge parallel to it.");
        (double X, double Y) near = edge.Sheet;
        (double X, double Y) far = horizontal ? (near.X, 2 * axisBox.Y1 - near.Y) : (2 * axisBox.X1 - near.X, near.Y);
        PlaceLinear(view, sheet, dimension, near, far, horizontal ? (0, 1) : (1, 0), graphics, at);
        return dimension;
    }

    private static void RequireValue(Dimension dimension, double expected, string what,        string hint = "Check the points: the first on the chamfer edge, the second on the edge it is measured from.")
    {
        if (Math.Abs(dimension.Value - expected) > 1e-6)
        {
            double value = dimension.Value;
            TryDeleteDimension(dimension);
            throw new ToolException("dimension_failed", $"The {what} reads {Geo.Mm(value)} mm instead of {Geo.Mm(expected)} mm; it was removed.", hint);
        }
    }

    private static void TryDeleteDimension(Dimension dimension)
    {
        try
        {
            dimension.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }

    /// <summary>
    /// Angular coordinate dimensions: a centre (a circle or a vertex), a point that sets the zero direction, and one dimension
    /// per further point with its angle about the centre, counterclockwise unless <paramref name="clockwise"/>. Each angle is
    /// checked against the points on the sheet.
    /// </summary>
    private static object AngularCoordinateDimensions(SeContext context, DraftDocument draft, DrawingView view, List<Graphic> graphics,
        SolidEdgeFrameworkSupport.Dimensions dimensions, List<(double X, double Y, double Z)> model, bool clockwise)
    {
        if (model.Count < 3)
        {
            throw ToolException.InvalidArgument("Angular coordinate dimensions need 'points': the centre (a circle or vertex), a point that sets the zero direction, then each point measured.");
        }

        Pick centre = PickAt(view, graphics, model[0], false);
        Pick zero = PickAt(view, graphics, model[1], false);
        if (!centre.KeyPoint)
        {
            throw ToolException.InvalidArgument("The centre of angular coordinate dimensions (the first point) must be a circle centre or a vertex of the view.");
        }

        List<Pick> picks = [.. model.Skip(2).Select(p => PickAt(view, graphics, p, false))];
        double Angle((double X, double Y) p) => Math.Atan2(p.Y - centre.Sheet.Y, p.X - centre.Sheet.X);
        double reach = new[] { zero }.Concat(picks).Max(p => Distance(p.Sheet, centre.Sheet)) + DimensionOffset;
        (double X, double Y) Text((double X, double Y) p)
        {
            double a = Angle(p);
            return (centre.Sheet.X + (reach * Math.Cos(a)), centre.Sheet.Y + (reach * Math.Sin(a)));
        }

        var created = new List<Dimension>();
        try
        {
            (double X, double Y) text = Text(zero.Sheet);
            Dimension start = dimensions.AddAngularCoordinateOrigin(centre.Graphic.Reference, centre.Sheet.X, centre.Sheet.Y, 0, true,
                zero.Graphic.Reference, zero.Sheet.X, zero.Sheet.Y, 0, zero.KeyPoint, text.X, text.Y, 0);
            created.Add(start);
            foreach (Pick pick in picks)
            {
                text = Text(pick.Sheet);
                Dimension dimension = dimensions.AddAngularCoordinate(start, pick.Graphic.Reference, pick.Sheet.X, pick.Sheet.Y, 0, pick.KeyPoint,
                    text.X, text.Y, 0, clockwise ? DimAngularCoordnateOrientationConstants.igDimAngCoordOrientClockwise : DimAngularCoordnateOrientationConstants.igDimAngCoordOrientCounterClockwise);
                created.Add(dimension);
                double expected = (clockwise ? Angle(zero.Sheet) - Angle(pick.Sheet) : Angle(pick.Sheet) - Angle(zero.Sheet)) % (2 * Math.PI);
                if (expected < 0)
                {
                    expected += 2 * Math.PI;
                }

                if (Math.Abs(dimension.Value - expected) > 1e-6)
                {
                    throw new ToolException("dimension_failed",
                        $"An angular coordinate dimension reads {Units.RadToDeg(dimension.Value):0.###} degrees instead of {Units.RadToDeg(expected):0.###}; the dimensions were removed.",
                        "Give the centre, then a point for the zero direction, then the points to measure.");
                }
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex)))
        {
            foreach (Dimension dimension in created)
            {
                TryDeleteDimension(dimension);
            }

            throw ex is ToolException ? ex
                : new ToolException("dimension_failed", $"Solid Edge rejected the angular coordinate dimensions: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        return new
        {
            view = context.Register(view, "drawing_view", Document(draft)),
            measures = clockwise ? "clockwise angles from the zero direction" : "counterclockwise angles from the zero direction",
            dimensions = created.Select(d => DescribeDimension(context, draft, d)).ToList(),
        };
    }

    public static object Dimensions(SeContext context, string? documentHandle, string? sheetRef, int offset, int limit)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        List<Dimension> all = DimensionsOf(sheet).ToList();
        int start = Math.Max(offset, 0);
        List<object> page = all.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems)).Select(d => DescribeDimension(context, draft, d)).ToList();
        return new { sheet = sheet.Name, count = all.Count, offset = start, returned = page.Count, more = start + page.Count < all.Count, dimensions = page };
    }

    private static object DescribeDimension(SeContext context, DraftDocument draft, Dimension dimension)
    {
        DimTypeConstants type = dimension.DimensionType;
        bool angular = type is DimTypeConstants.igDimTypeAngular or DimTypeConstants.igDimTypeArcAngle or DimTypeConstants.igDimTypeAngularCoordinate;
        double value = dimension.Value;
        double[]? at = null;
        try
        {
            dimension.Range(out double x1, out double y1, out double x2, out double y2);
            at = [Geo.Mm((x1 + x2) / 2), Geo.Mm((y1 + y2) / 2)];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // No graphic extension (dimension not placed).
        }

        return new
        {
            handle = context.Register(dimension, "dimension", Document(draft)),
            type = DimensionTypeName(type),
            value = angular ? Geo.R(Units.RadToDeg(value)) : Geo.Mm(value),
            unit = angular ? "deg" : "mm",
            at,
        };
    }

    private static string DimensionTypeName(DimTypeConstants type) => type switch
    {
        DimTypeConstants.igDimTypeLinear => "linear",
        DimTypeConstants.igDimTypeRadial => "radius",
        DimTypeConstants.igDimTypeAngular => "angle",
        DimTypeConstants.igDimTypeRDiameter or DimTypeConstants.igDimTypeCDiameter or DimTypeConstants.igDimTypeSDiameter => "diameter",
        DimTypeConstants.igDimTypeArcLength => "arc_length",
        DimTypeConstants.igDimTypeArcAngle => "arc_angle",
        DimTypeConstants.igDimTypeCoordinate => "coordinate",
        DimTypeConstants.igDimTypeChamfer => "chamfer",
        DimTypeConstants.igDimTypeAngularCoordinate => "angular_coordinate",
        DimTypeConstants.igDimTypeCurveLength => "curve_length",
        _ => "other",
    };

    // View elements and dimension placement

    /// <summary>Maximum distance (model m) between a given point and the edge chosen with it.</summary>
    private const double PickTolerance = 0.001;

    /// <summary>Maximum distance (model m) to take a vertex or a centre as the exact point of the dimension.</summary>
    private const double KeyTolerance = 0.0002;

    /// <summary>Spacing between the view outline and the outer edge of an automatically placed dimension (sheet m).</summary>
    private const double DimensionOffset = 0.01;

    /// <summary>Step between stacked dimensions on the same side (sheet m).</summary>
    private const double DimensionStep = 0.008;

    private enum Shape
    {
        Line,
        Circle,
        Arc,
    }

    /// <summary>2D element of a view, in view coordinates (model scale, m).</summary>
    private sealed record Graphic(Shape Shape, object Value, (double X, double Y) Start, (double X, double Y) End,
        (double X, double Y) Centre, double Radius, double StartAngle, double Sweep)
    {
        /// <summary>The dependent detail view that shows this element of its source view (see DimensionGraphics).</summary>
        public DrawingView? Detail { get; init; }

        /// <summary>Sheet reference of the element: dimensions created with it stay associated with the view.</summary>
        public object Reference => Detail is { } detail ? DetailReference(detail, Value)
            : Late.Get(Value, "Reference") ?? throw new ToolException("dimension_failed", "Solid Edge returned no reference for the selected edge.");

        private static object DetailReference(DrawingView detail, object value)
        {
            detail.GetReferenceToGraphicMember(value, out SolidEdgeFramework.Reference reference);
            return reference ?? throw new ToolException("dimension_failed", "Solid Edge returned no reference for the selected edge in the detail view.");
        }
    }

    private sealed record Pick(Graphic Graphic, bool KeyPoint, (double X, double Y) Sheet);

    private static List<Graphic> Graphics(DrawingView view)
    {
        var list = new List<Graphic>();
        foreach (DVLine2d line in Late.Items(view.DVLines2d).OfType<DVLine2d>())
        {
            line.GetStartPoint(out double x1, out double y1);
            line.GetEndPoint(out double x2, out double y2);
            list.Add(new Graphic(Shape.Line, line, (x1, y1), (x2, y2), ((x1 + x2) / 2, (y1 + y2) / 2), 0, 0, 0));
        }

        foreach (DVCircle2d circle in Late.Items(view.DVCircles2d).OfType<DVCircle2d>())
        {
            circle.GetCenterPoint(out double cx, out double cy);
            list.Add(new Graphic(Shape.Circle, circle, (cx, cy), (cx, cy), (cx, cy), circle.Radius, 0, 2 * Math.PI));
        }

        foreach (DVArc2d arc in Late.Items(view.DVArcs2d).OfType<DVArc2d>())
        {
            arc.GetCenterPoint(out double cx, out double cy);
            arc.GetStartPoint(out double x1, out double y1);
            arc.GetEndPoint(out double x2, out double y2);
            list.Add(new Graphic(Shape.Arc, arc, (x1, y1), (x2, y2), (cx, cy), arc.Radius, arc.StartAngle, arc.SweepAngle));
        }

        return list;
    }

    /// <summary>
    /// Elements to dimension in a view. A dependent detail view has none of its own (DVLines2d, DVArcs2d... and
    /// GraphicMembers are empty; verified): it shows its source view's elements, in the same view coordinates, so these are
    /// the source's elements that reach into its circle, referenced through the detail view (GetReferenceToGraphicMember: a
    /// radius dimension on it reads 4 mm and stays in the detail; the source element's own reference fails).
    /// </summary>
    private static List<Graphic> DimensionGraphics(DrawingView view)
    {
        List<Graphic> own = Graphics(view);
        if (own.Count > 0 || view.DrawingViewType != DrawingViewTypeConstants.igDetailView || view.SourceDrawingView is not { } source)
        {
            return own;
        }

        List<Graphic> shown = [.. Graphics(source).Select(g => g with { Detail = view })];
        DetailEnvelope? envelope = Late.Items(source.DetailEnvelopes).OfType<DetailEnvelope>()
            .FirstOrDefault(e => e.DetailView is { } d && Late.SameObject(d, view));
        if (envelope is null || envelope.Diameter <= 0)
        {
            return shown;
        }

        // The envelope's diameter is on the sheet at the source's scale (18 mm for a radius of 9 at 1:1; DisplayAsCircle
        // reads false for it); the detail view's centre is that of its circle.
        double radius = envelope.Diameter / 2 / source.ScaleFactor;
        view.GetCenterForDetailView(out double sx, out double sy);
        view.SheetToView(sx, sy, out double cx, out double cy);
        return [.. shown.Where(g => DistanceTo(g, (cx, cy)) <= radius + 1e-6)];
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static (double X, double Y) Mid((double X, double Y) a, (double X, double Y) b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>Distance from a point to an element; for circles and arcs, the centre also counts.</summary>
    private static double DistanceTo(Graphic graphic, (double X, double Y) p)
    {
        if (graphic.Shape == Shape.Line)
        {
            double dx = graphic.End.X - graphic.Start.X, dy = graphic.End.Y - graphic.Start.Y;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared < 1e-18 ? 0 : Math.Clamp(((p.X - graphic.Start.X) * dx + (p.Y - graphic.Start.Y) * dy) / lengthSquared, 0, 1);
            return Distance(p, (graphic.Start.X + t * dx, graphic.Start.Y + t * dy));
        }

        double fromCentre = Distance(p, graphic.Centre);
        double onCurve = Math.Abs(fromCentre - graphic.Radius);
        if (graphic.Shape == Shape.Arc && !OnSweep(graphic, p))
        {
            onCurve = Math.Min(Distance(p, graphic.Start), Distance(p, graphic.End));
        }

        return Math.Min(onCurve, fromCentre);
    }

    private static bool OnSweep(Graphic arc, (double X, double Y) p)
    {
        double angle = Math.Atan2(p.Y - arc.Centre.Y, p.X - arc.Centre.X);
        double relative = (angle - arc.StartAngle) % (2 * Math.PI);
        if (relative < 0)
        {
            relative += 2 * Math.PI;
        }

        return arc.Sweep >= 0 ? relative <= arc.Sweep + 1e-9 : relative >= 2 * Math.PI + arc.Sweep - 1e-9;
    }

    private static Graphic Nearest(DrawingView view, List<Graphic> graphics, (double X, double Y) p, (double X, double Y, double Z) model,
        Func<Graphic, bool> filter, string what, string? hint = null)
    {
        Graphic? best = null;
        double bestDistance = double.MaxValue;
        foreach (Graphic graphic in graphics.Where(filter))
        {
            double distance = DistanceTo(graphic, p);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (graphic, distance);
            }
        }

        string point = $"({Geo.Mm(model.X)}, {Geo.Mm(model.Y)}, {Geo.Mm(model.Z)})";
        if (best is null)
        {
            throw ToolException.NotFound($"The view shows no {what}.", hint ?? "Choose another view.");
        }

        if (bestDistance > PickTolerance)
        {
            throw ToolException.NotFound($"No {what} of the view passes through {point}: the nearest is {Geo.Mm(bestDistance)} mm away (model units){InModel(view, best)}.",
                "Give a model point on the edge as seen in this view (se_geometry lists edges and vertices of the model; a flat pattern view shows the flat pattern, whose coordinates are not those of the folded part).");
        }

        return best;
    }

    /// <summary>
    /// Where an element of the view is in model coordinates, for an error: ModelToView is linear and drops the depth along
    /// the view direction, so its points are given on the plane through the model origin square to that direction.
    /// </summary>
    private static string InModel(DrawingView view, Graphic graphic)
    {
        (double X, double Y) o = ModelToView(view, (0, 0, 0));
        (double X, double Y) a = ModelToView(view, (1, 0, 0)), b = ModelToView(view, (0, 1, 0)), c = ModelToView(view, (0, 0, 1));
        double[] u = [a.X - o.X, b.X - o.X, c.X - o.X];
        double[] v = [a.Y - o.Y, b.Y - o.Y, c.Y - o.Y];
        double uu = u.Sum(x => x * x), vv = v.Sum(x => x * x), uv = u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
        double det = uu * vv - uv * uv;
        if (!(det > 1e-18))
        {
            return "";
        }

        // The least-norm solution of the 2 x 3 system: m = A^T (A A^T)^-1 (p - o), with u and v the rows of A.
        string Model((double X, double Y) p)
        {
            double px = p.X - o.X, py = p.Y - o.Y;
            double s = (vv * px - uv * py) / det, t = (uu * py - uv * px) / det;
            return $"({Geo.Mm(s * u[0] + t * v[0])}, {Geo.Mm(s * u[1] + t * v[1])}, {Geo.Mm(s * u[2] + t * v[2])})";
        }

        return graphic.Shape == Shape.Line
            ? $": the line from {Model(graphic.Start)} to {Model(graphic.End)}"
            : $": the {(graphic.Shape == Shape.Arc ? "arc" : "circle")} of radius {Geo.Mm(graphic.Radius)} centred at {Model(graphic.Centre)}";
    }

    /// <summary>Element pointed at by a point: an exact vertex or centre if it matches, otherwise the edge through it.</summary>
    private static Pick PickAt(DrawingView view, List<Graphic> graphics, (double X, double Y, double Z) model, bool linesOnly)
    {
        (double X, double Y) p = ModelToView(view, model);
        if (!linesOnly)
        {
            foreach (Graphic graphic in graphics.OrderBy(g => g.Shape))
            {
                (double X, double Y)? key = null;
                if (graphic.Shape != Shape.Circle && Distance(p, graphic.Start) <= KeyTolerance)
                {
                    key = graphic.Start;
                }
                else if (graphic.Shape != Shape.Circle && Distance(p, graphic.End) <= KeyTolerance)
                {
                    key = graphic.End;
                }
                else if (graphic.Shape != Shape.Line && Distance(p, graphic.Centre) <= KeyTolerance)
                {
                    key = graphic.Centre;
                }

                if (key is { } k)
                {
                    return new Pick(graphic, true, ToSheet(view, k));
                }
            }
        }

        Graphic edge = Nearest(view, graphics, p, model, g => !linesOnly || g.Shape == Shape.Line, linesOnly ? "straight edge" : "edge");
        return new Pick(edge, false, ToSheet(view, p));
    }

    /// <summary>
    /// Replaces a horizontal or vertical edge (without a key point) with its nearest endpoint to the given point, which, perpendicular to
    /// it, measures the same: Solid Edge draws the distance dimensions attached to a vertical edge on the side opposite to the one that
    /// Range and GetDisplayData report (verified in the PDF), so they could not be placed. <paramref name="across"/> is the measurement in
    /// perpendicular to the edge (horizontal or vertical).
    /// </summary>
    private static Pick ToEndpoint(DrawingView view, Pick pick, out string? across)
    {
        across = null;
        if (pick.KeyPoint || pick.Graphic.Shape != Shape.Line)
        {
            return pick;
        }

        (double X, double Y) a = ToSheet(view, pick.Graphic.Start), b = ToSheet(view, pick.Graphic.End);
        double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y), tolerance = 1e-6 * Math.Max(dx, dy);
        if (dx > tolerance && dy > tolerance)
        {
            return pick;
        }

        across = dx <= tolerance ? "horizontal" : "vertical";
        return new Pick(pick.Graphic, true, Distance(pick.Sheet, a) <= Distance(pick.Sheet, b) ? a : b);
    }

    /// <summary>Intersection (on the sheet) of the lines of two edges, or null if they are parallel.</summary>
    private static (double X, double Y)? Intersection(DrawingView view, Graphic first, Graphic second)
    {
        (double X, double Y) a = ToSheet(view, first.Start), b = ToSheet(view, first.End);
        (double X, double Y) c = ToSheet(view, second.Start), d = ToSheet(view, second.End);
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
        double cross = rx * sy - ry * sx;
        if (Math.Abs(cross) < 1e-9 * Math.Sqrt((rx * rx + ry * ry) * (sx * sx + sy * sy)))
        {
            return null;
        }

        double t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / cross;
        return (a.X + t * rx, a.Y + t * ry);
    }

    /// <summary>
    /// Quadrant of an angular dimension: the sector between the rays that go from the intersection of the edges to the given points
    /// (as when they are pointed at with the mouse). Each quadrant is tested and the one whose drawn arc falls within that sector is chosen.
    /// </summary>
    private static void OrientAngle(DrawingView view, Dimension dimension, (double X, double Y) corner, Pick first, Pick second)
    {
        (double X, double Y) Ray(Pick pick)
        {
            (double X, double Y) p = pick.Sheet;
            if (Distance(p, corner) < 1e-6)
            {
                (double X, double Y) s = ToSheet(view, pick.Graphic.Start), e = ToSheet(view, pick.Graphic.End);
                p = Distance(s, corner) > Distance(e, corner) ? s : e;
            }

            double length = Distance(p, corner);
            return ((p.X - corner.X) / length, (p.Y - corner.Y) / length);
        }

        (double X, double Y) u = Ray(first), v = Ray(second);
        (double bx, double by) = (u.X + v.X, u.Y + v.Y);
        double norm = Math.Sqrt(bx * bx + by * by);
        if (norm < 1e-9)
        {
            return;
        }

        (bx, by) = (bx / norm, by / norm);
        AngularDimensionQuadrantConstants? best = null;
        double bestScore = double.MinValue;
        foreach (AngularDimensionQuadrantConstants quadrant in new[]
                 {
                     AngularDimensionQuadrantConstants.igFirstQuadrant, AngularDimensionQuadrantConstants.igSecondQuadrant,
                     AngularDimensionQuadrantConstants.igThirdQuadrant, AngularDimensionQuadrantConstants.igFourthQuadrant,
                 })
        {
            dimension.SetQuadrant(quadrant);
            SolidEdgeFrameworkSupport.DisplayData display = dimension.GetDisplayData();
            if (display.GetArcCount() == 0)
            {
                continue;
            }

            display.GetArcAtIndex(0, out _, out _, out _, out double mx, out double my, out _, out _, out _, out _);
            double length = Distance((mx, my), corner);
            double score = length < 1e-12 ? -1 : ((mx - corner.X) * bx + (my - corner.Y) * by) / length;
            if (score > bestScore)
            {
                (best, bestScore) = (quadrant, score);
            }
        }

        if (best is { } chosen)
        {
            dimension.SetQuadrant(chosen);
        }
    }

    private static (double X, double Y) ToSheet(DrawingView view, (double X, double Y) p)
    {
        view.ViewToSheet(p.X, p.Y, out double x, out double y);
        return (x, y);
    }

    /// <summary>Outline (on the sheet) of what is drawn in the view.</summary>
    private static Box Outline(DrawingView view, List<Graphic> graphics)
    {
        var points = new List<(double X, double Y)>();
        foreach (Graphic graphic in graphics)
        {
            if (graphic.Shape == Shape.Line)
            {
                points.Add(ToSheet(view, graphic.Start));
                points.Add(ToSheet(view, graphic.End));
            }
            else
            {
                (double cx, double cy, double r) = (graphic.Centre.X, graphic.Centre.Y, graphic.Radius);
                points.Add(ToSheet(view, (cx - r, cy - r)));
                points.Add(ToSheet(view, (cx + r, cy + r)));
            }
        }

        return new Box(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    private static Box RangeOf(Dimension dimension)
    {
        dimension.Range(out double x1, out double y1, out double x2, out double y2);
        return new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    /// <summary>
    /// Moves a linear dimension outside the view outline (or to <paramref name="position"/>) by shifting its line
    /// perpendicular to what is measured. The sign of TrackDistance depends on the direction of the edge, so it is calibrated by testing; if
    /// it collides with another dimension on the same side, it moves one more step away.
    /// </summary>
    private static void PlaceLinear(DrawingView view, Sheet sheet, Dimension dimension, (double X, double Y) a, (double X, double Y) b,
        (double X, double Y)? measure, List<Graphic> graphics, (double X, double Y)? position)
    {
        double length = Distance(a, b);
        (double mx, double my) = measure ?? (length < 1e-12 ? (1.0, 0.0) : ((b.X - a.X) / length, (b.Y - a.Y) / length));
        double nx = -my, ny = mx;
        Box outline = Outline(view, graphics);
        (double X, double Y) middle = Mid(a, b);
        double target;
        if (position is { } p)
        {
            if ((p.X - middle.X) * nx + (p.Y - middle.Y) * ny < 0)
            {
                (nx, ny) = (-nx, -ny);
            }

            target = p.X * nx + p.Y * ny;
        }
        else
        {
            if ((middle.X - outline.CentreX) * nx + (middle.Y - outline.CentreY) * ny < 0)
            {
                (nx, ny) = (-nx, -ny);
            }

            target = Project(outline, nx, ny, max: true) + DimensionOffset;
        }

        double Far() => Project(RangeOf(dimension), nx, ny, max: true);

        // Which sign of TrackDistance moves the dimension toward n, and how much per metre.
        double sign = 0, slope = 0;
        foreach (double s in new[] { 1.0, -1.0 })
        {
            dimension.TrackDistance = s * 0.01;
            double first = Far();
            dimension.TrackDistance = s * 0.02;
            double rate = (Far() - first) / 0.01;
            if (rate > 0.5)
            {
                (sign, slope) = (s, rate);
                break;
            }
        }

        if (sign == 0)
        {
            return;
        }

        double track = Math.Max(0.002, 0.02 + (target - Far()) / slope);
        dimension.TrackDistance = sign * track;
        if (position is not null)
        {
            return;
        }

        // Dimensions already placed on the same side whose line and extension band along the length coincide with the new one.
        double outlineFar = Project(outline, nx, ny, max: true);
        List<Box> others = DimensionsOf(sheet).Where(d => !ReferenceEquals(d, dimension)).Select(RangeOf)
            .Where(r => Project(r, nx, ny, max: true) > outlineFar).ToList();
        for (int i = 0; i < 8 && others.Any(o => Collides(o, RangeOf(dimension), nx, ny)); i++)
        {
            track += DimensionStep / slope;
            dimension.TrackDistance = sign * track;
        }
    }

    /// <summary>
    /// Moves a diameter or radius dimension so that its text is outside the circle. Verified (r240-r243): a circular diameter
    /// is laid along TrackAngle + 90° (sheet angle) with its text at TrackDistance from the centre (by default 7 mm, inside a
    /// circle of radius 10), so it is turned towards <paramref name="position"/> or away from the centre of the view outline;
    /// a radius (or radial diameter) ignores TrackAngle and TrackDistance, and TerminatorPosition puts its arrow and text outside.
    /// </summary>
    private static void PlaceRadial(DrawingView view, Dimension dimension, Graphic curve, List<Graphic> graphics, (double X, double Y)? position)
    {
        try
        {
            (double X, double Y) centre = ToSheet(view, curve.Centre);
            double radius = Distance(centre, ToSheet(view, (curve.Centre.X + curve.Radius, curve.Centre.Y)));
            if (dimension.DimensionType != DimTypeConstants.igDimTypeCDiameter)
            {
                Box range = RangeOf(dimension);
                double reach = Math.Max(Math.Max(Math.Abs(range.X1 - centre.X), Math.Abs(range.X2 - centre.X)),
                    Math.Max(Math.Abs(range.Y1 - centre.Y), Math.Abs(range.Y2 - centre.Y)));
                if (reach < radius + 0.001)
                {
                    dimension.TerminatorPosition = true;
                }

                return;
            }

            double ux, uy, distance;
            if (position is { } p && Distance(p, centre) > radius)
            {
                distance = Distance(p, centre);
                (ux, uy) = ((p.X - centre.X) / distance, (p.Y - centre.Y) / distance);
            }
            else
            {
                Box outline = Outline(view, graphics);
                (double vx, double vy) = (centre.X - outline.CentreX, centre.Y - outline.CentreY);
                double length = Math.Sqrt(vx * vx + vy * vy);
                (ux, uy) = length < 1e-6 ? (Math.Sqrt(0.5), Math.Sqrt(0.5)) : (vx / length, vy / length);
                distance = radius + DimensionOffset;
            }

            dimension.TrackAngle = Math.Atan2(uy, ux) - Math.PI / 2;
            dimension.TrackDistance = distance;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Left where Solid Edge put it.
        }
    }

    /// <summary>Two parallel dimensions collide if their outer bands (line and text) overlap along and across.</summary>
    private static bool Collides(Box other, Box mine, double nx, double ny)
    {
        const double band = 0.006;
        double otherFar = Project(other, nx, ny, max: true), mineFar = Project(mine, nx, ny, max: true);
        bool across = otherFar > mineFar - band - 0.001 && mineFar > otherFar - band - 0.001;
        double ox1 = Project(other, ny, -nx, max: false), ox2 = Project(other, ny, -nx, max: true);
        double mx1 = Project(mine, ny, -nx, max: false), mx2 = Project(mine, ny, -nx, max: true);
        bool along = ox1 < mx2 && mx1 < ox2;
        return across && along;
    }
}
