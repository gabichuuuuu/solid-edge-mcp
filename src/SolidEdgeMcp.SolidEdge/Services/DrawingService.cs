using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using AngularDimensionQuadrantConstants = SolidEdgeFrameworkSupport.AngularDimensionQuadrantConstants;
using Dimension = SolidEdgeFrameworkSupport.Dimension;
using DimensionOrientationConstants = SolidEdgeFrameworkSupport.DimensionOrientationConstants;
using DimTypeConstants = SolidEdgeFrameworkSupport.DimTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Drawings: sheets, model views (base, projected, auxiliary, sections and details), dimensions retrieved from the model,
/// parts list and updating. Positions are sheet mm (origin at the lower left corner) and refer
/// to the centre of the view; cutting lines and detail centres are given in model mm and projected onto the
/// source view.
/// </summary>
public static partial class DrawingService
{
    /// <summary>Spacing between automatically placed views (sheet m).</summary>
    private const double Gap = 0.02;

    /// <summary>Fraction of the sheet that the base view may occupy when the scale is chosen automatically.</summary>
    private const double FitFraction = 0.4;

    /// <summary>Standard scales (ISO 5455), from largest to smallest.</summary>
    private static readonly double[] StandardScales = [50, 20, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001];

    private static readonly Dictionary<string, ViewOrientationConstants> Orientations = new(StringComparer.Ordinal)
    {
        ["front"] = ViewOrientationConstants.igFrontView,
        ["back"] = ViewOrientationConstants.igBackView,
        ["top"] = ViewOrientationConstants.igTopView,
        ["bottom"] = ViewOrientationConstants.igBottomView,
        ["left"] = ViewOrientationConstants.igLeftView,
        ["right"] = ViewOrientationConstants.igRightView,
        ["top_front_right"] = ViewOrientationConstants.igTopFrontRightView,
        ["top_front_left"] = ViewOrientationConstants.igTopFrontLeftView,
        ["top_back_right"] = ViewOrientationConstants.igTopBackRightView,
        ["top_back_left"] = ViewOrientationConstants.igTopBackLeftView,
        ["bottom_front_right"] = ViewOrientationConstants.igBottomFrontRightView,
        ["bottom_front_left"] = ViewOrientationConstants.igBottomFrontLeftView,
        ["bottom_back_right"] = ViewOrientationConstants.igBottomBackRightView,
        ["bottom_back_left"] = ViewOrientationConstants.igBottomBackLeftView,
    };

    private static readonly Dictionary<string, (FoldTypeConstants Fold, int X, int Y)> Folds = new(StringComparer.Ordinal)
    {
        ["up"] = (FoldTypeConstants.igFoldUp, 0, 1),
        ["down"] = (FoldTypeConstants.igFoldDown, 0, -1),
        ["right"] = (FoldTypeConstants.igFoldRight, 1, 0),
        ["left"] = (FoldTypeConstants.igFoldLeft, -1, 0),
        ["up_right"] = (FoldTypeConstants.igFoldUpRight, 1, 1),
        ["up_left"] = (FoldTypeConstants.igFoldUpLeft, -1, 1),
        ["down_right"] = (FoldTypeConstants.igFoldDownRight, 1, -1),
        ["down_left"] = (FoldTypeConstants.igFoldDownLeft, -1, -1),
    };

    private static readonly Dictionary<string, (PaperSizeConstants Wide, PaperSizeConstants Tall)> Papers = new(StringComparer.Ordinal)
    {
        ["a4"] = (PaperSizeConstants.igIsoA4Wide, PaperSizeConstants.igIsoA4Tall),
        ["a3"] = (PaperSizeConstants.igIsoA3Wide, PaperSizeConstants.igIsoA3Tall),
        ["a2"] = (PaperSizeConstants.igIsoA2Wide, PaperSizeConstants.igIsoA2Tall),
        ["a1"] = (PaperSizeConstants.igIsoA1Wide, PaperSizeConstants.igIsoA1Tall),
        ["a0"] = (PaperSizeConstants.igIsoA0Wide, PaperSizeConstants.igIsoA0Tall),
        ["ansi_a"] = (PaperSizeConstants.igAnsiAWide, PaperSizeConstants.igAnsiATall),
        ["ansi_b"] = (PaperSizeConstants.igAnsiBWide, PaperSizeConstants.igAnsiBTall),
        ["ansi_c"] = (PaperSizeConstants.igAnsiCWide, PaperSizeConstants.igAnsiCTall),
        ["ansi_d"] = (PaperSizeConstants.igAnsiDWide, PaperSizeConstants.igAnsiDTall),
        ["ansi_e"] = (PaperSizeConstants.igAnsiEWide, PaperSizeConstants.igAnsiETall),
    };

    // ---------------------------------------------------------------- sheets

    public static object Sheets(SeContext context, string? documentHandle)
    {
        DraftDocument draft = Draft(context, documentHandle);
        string active = draft.ActiveSheet.Name;
        return new
        {
            document = DocumentHandle(context, draft),
            sheets = WorkingSheets(draft).Select(sheet => DescribeSheet(context, draft, sheet, active)).ToList(),
            backgrounds = BackgroundSheets(draft).Select(sheet => new { name = sheet.Name, size = SizeMm(sheet) }).ToList(),
        };
    }

    public static object AddSheet(SeContext context, string? documentHandle, string? name, string? paper, bool portrait,
        double? width, double? height, string? background)
    {
        DraftDocument draft = Draft(context, documentHandle);
        if (name is not null)
        {
            RequireFreeName(draft, name);
        }

        Sheet sheet = name is null
            ? draft.Sheets.AddSheet(SectionType: SheetSectionTypeConstants.igWorkingSection)
            : draft.Sheets.AddSheet(name, SheetSectionTypeConstants.igWorkingSection);
        ApplySetup(draft, sheet, paper, portrait, width, height, background);
        sheet.Activate();
        return DescribeSheet(context, draft, sheet, sheet.Name);
    }

    public static object SetupSheet(SeContext context, string? documentHandle, string? sheetRef, string? name, string? paper, bool portrait,
        double? width, double? height, string? background)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        if (name is not null && !string.Equals(name, sheet.Name, StringComparison.Ordinal))
        {
            RequireFreeName(draft, name);
            sheet.Name = name;
        }

        ApplySetup(draft, sheet, paper, portrait, width, height, background);
        return DescribeSheet(context, draft, sheet, draft.ActiveSheet.Name);
    }

    public static object ActivateSheet(SeContext context, string? documentHandle, string? sheetRef)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        sheet.Activate();
        return DescribeSheet(context, draft, sheet, sheet.Name);
    }

    public static object DeleteSheet(SeContext context, string? documentHandle, string? sheetRef)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        if (string.IsNullOrWhiteSpace(sheetRef))
        {
            throw ToolException.InvalidArgument("Action 'delete_sheet' requires 'sheet' (handle or name).");
        }

        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        if (WorkingSheets(draft).Count() <= 1)
        {
            throw new ToolException("last_sheet", "A drawing needs at least one sheet.", "Add another sheet first.");
        }

        string name = sheet.Name;
        string handle = context.Register(sheet, "sheet", Document(draft));
        sheet.Delete();
        context.Handles.Remove(handle, "the sheet was deleted");
        return new { deleted = name };
    }

    // ---------------------------------------------------------------- views

    public static object Views(SeContext context, string? documentHandle, string? sheetRef)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        return new
        {
            document = DocumentHandle(context, draft),
            sheet = sheet.Name,
            size = SizeMm(sheet),
            views = ViewsOf(sheet).Select(view => DescribeView(context, draft, view)).ToList(),
        };
    }

    /// <summary>Base view of a saved model; without a scale, the largest standard scale that leaves room for the projections.</summary>
    public static object BaseView(SeContext context, string? documentHandle, string? sheetRef, string? path, string? modelHandle,
        string? orientation, double? scale, double[]? position, bool flat)
    {
        DraftDocument draft = Draft(context, documentHandle);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        string file = ModelFile(context, path, modelHandle, out string? warning);
        string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (flat && extension is not (".psm" or ".par"))
        {
            throw ToolException.InvalidArgument("'flat' needs a sheet metal model (.psm, or a .par with a flat pattern).");
        }

        string orientationName = orientation ?? (flat ? "top" : "front");
        if (!Orientations.TryGetValue(orientationName, out ViewOrientationConstants orient))
        {
            throw ToolException.InvalidArgument($"Unknown orientation '{orientationName}'.", $"Use one of: {string.Join(", ", Orientations.Keys)}.");
        }

        ModelLink link = LinkTo(draft, file);
        double factor = scale is { } s ? RequireScale(s) : FitScale(link, orient, sheet);
        (double x, double y) = position is null ? Centre(sheet) : Geo.Point2(position, "position");
        sheet.Activate();
        DrawingView view = Create(() => flat
                ? sheet.DrawingViews.AddSheetMetalView(link, orient, factor, x, y, SheetMetalDrawingViewTypeConstants.seSheetMetalFlatView)
                : sheet.DrawingViews.Add(link, orient, factor, x, y),
            "base view", flat ? "Create the flat pattern in the model first (se_sheetmetal 'flatten') and save it." : "Check that the model has geometry and is saved.");
        CentreAt(view, x, y);
        return Result(context, draft, view, warning);
    }

    /// <summary>Projected (folded) view from another one; the diagonals give a pictorial view.</summary>
    public static object ProjectedView(SeContext context, string? viewHandle, string direction, double[]? position)
    {
        DrawingView source = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (!Folds.TryGetValue(direction, out var fold))
        {
            throw ToolException.InvalidArgument($"Unknown direction '{direction}'.", $"Use one of: {string.Join(", ", Folds.Keys)}.");
        }

        Box from = BoxOf(source);
        (double x, double y) = position is null
            ? (from.CentreX + fold.X * (from.Width + Gap), from.CentreY + fold.Y * (from.Height + Gap))
            : Geo.Point2(position, "position");
        Sheet sheet = SheetOf(source);
        DrawingView view = Create(() => sheet.DrawingViews.AddByFold(source, fold.Fold, x, y), "projected view",
            "Project from a principal (orthographic) view.");
        if (position is not null)
        {
            CentreAt(view, x, y);
        }
        else if (fold.X != 0 && fold.Y != 0)
        {
            PlacePictorial(sheet, view, from, fold.X, fold.Y);
        }
        else
        {
            PlaceAway(sheet, view, from, fold.X, fold.Y);
        }

        return Result(context, draft, view, null);
    }

    /// <summary>Auxiliary view: looks perpendicular to a line of the model seen in the source view (an inclined edge).</summary>
    public static object AuxiliaryView(SeContext context, string? viewHandle, double[]? line, double[]? position)
    {
        DrawingView source = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        ((double X, double Y) start, (double X, double Y) end) = SheetLine(source, line);
        Box from = BoxOf(source);
        Sheet sheet = SheetOf(source);
        (double nx, double ny) = RoomierNormal(start, end, from, sheet);
        double mx = (start.X + end.X) / 2, my = (start.Y + end.Y) / 2;
        double reach = Math.Max(from.Width, from.Height) / 2 + Gap;
        (double x, double y) = position is null ? (mx + nx * reach, my + ny * reach) : Geo.Point2(position, "position");
        DrawingView view = Create(() => sheet.DrawingViews.AddByAuxiliaryFold(source, start.X, start.Y, end.X, end.Y, x, y),
            "auxiliary view", "Give two model points that project apart in the source view (along the edge to look square at).");
        if (position is null)
        {
            PlaceAlong(sheet, view, from, nx, ny);
        }

        return Result(context, draft, view, null);
    }

    /// <summary>
    /// Section by a plane perpendicular to the source view passing through the given line (two model points). The line
    /// is extended until it crosses the whole view.
    /// </summary>
    public static object SectionView(SeContext context, string? viewHandle, double[]? line, bool flip, double[]? position)
    {
        DrawingView source = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        ((double X, double Y) a, (double X, double Y) b) = SheetLine(source, line);

        // The profile of the cutting plane is in sheet coordinates and extends beyond the view to cut the whole part.
        Box from = BoxOf(source);
        double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
        double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
        double reach = Math.Max(0.55 * Math.Sqrt(from.Width * from.Width + from.Height * from.Height)
            + Math.Sqrt((mx - from.CentreX) * (mx - from.CentreX) + (my - from.CentreY) * (my - from.CentreY)), length / 2);

        CuttingPlane plane = source.CuttingPlanes.Add();
        DrawingView view;
        double lookX = -uy, lookY = ux;
        try
        {
            plane.Profile.Lines2d.AddBy2Points(mx - ux * reach, my - uy * reach, mx + ux * reach, my + uy * reach);
            plane.Profile.EndEdit();
            plane.ViewSide = flip ? SectionViewProfileSide.igRightProfileSide : SectionViewProfileSide.igLeftProfileSide;
            view = Create(() => plane.CreateView(DraftSectionViewType.seDraftSectionViewTypeStandard), "section view",
                "Check that the cutting line crosses the part in the source view.");
            plane.GetFoldLineWithViewDirection(out _, out _, out _, out _, out lookX, out lookY);
            view.Update();
        }
        catch (Exception ex) when (ex is ToolException || (ex is COMException com && !ComErrors.IsDisconnected(com)))
        {
            TryDelete(plane);
            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("view_failed", $"Solid Edge could not create the section view: {ex.Message.Trim()}",
                "Check that the cutting line crosses the part in the source view.", ex);
        }

        if (position is not null)
        {
            (double x, double y) = Geo.Point2(position, "position");
            CentreAt(view, x, y);
        }
        else
        {
            // Aligned with its origin, toward where the cutting arrows point; if it does not fit there, on the other side.
            Sheet sheet = SheetOf(source);
            double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
            double norm = Math.Sqrt(lookX * lookX + lookY * lookY);
            (double px, double py) = norm > 1e-9 ? (lookX / norm, lookY / norm) : (-uy, ux);
            PlaceAlong(sheet, view, from, px, py);
            if (!BoxOf(view).Inside(width, height))
            {
                PlaceAlong(sheet, view, from, -px, -py);
                if (!BoxOf(view).Inside(width, height))
                {
                    PlaceAlong(sheet, view, from, px, py);
                }
            }
        }

        return Result(context, draft, view, null);
    }

    /// <summary>Circular detail around a model point (radius in model mm); by default at twice the scale.</summary>
    public static object DetailView(SeContext context, string? viewHandle, double[]? centre, double? radius, double? scale, double[]? position)
    {
        DrawingView source = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> points = Geo.Points3(centre, "center", 1);
        if (points.Count != 1)
        {
            throw ToolException.InvalidArgument("'center' must be one model point [x, y, z] in mm.");
        }

        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("Action 'detail_view' requires 'radius' (mm of the model)."), "radius"));
        double sourceScale = source.ScaleFactor;
        double factor = scale is { } s ? RequireScale(s) : sourceScale * 2;
        (double X, double Y) c = ModelToSheet(source, points[0]);
        Box from = BoxOf(source);
        double half = r * factor;
        (double x, double y) = position is null ? (from.X2 + Gap + half, c.Y) : Geo.Point2(position, "position");
        Sheet sheet = SheetOf(source);
        DrawingView view = Create(() => sheet.DrawingViews.AddDetailView(source, c.X, c.Y, r * sourceScale, factor, x, y, false),
            "detail view", "Give a centre point on the part as seen in the source view.");
        if (position is null)
        {
            PlaceFree(sheet, view, from);
        }
        else
        {
            CentreAt(view, x, y);
        }

        return Result(context, draft, view, null);
    }

    public static object MoveView(SeContext context, string? viewHandle, double[]? position, double[]? delta)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        Box before = BoxOf(view);
        double tx, ty;
        if (position is not null)
        {
            (double x, double y) = Geo.Point2(position, "position");
            (tx, ty) = (x - before.CentreX, y - before.CentreY);
        }
        else
        {
            (tx, ty) = Geo.Point2(delta ?? throw ToolException.InvalidArgument("Action 'move_view' requires 'position' or 'delta'."), "delta");
        }

        Shift(view, tx, ty);
        Box after = BoxOf(view);
        double movedX = after.CentreX - before.CentreX, movedY = after.CentreY - before.CentreY;
        string? note = Math.Abs(movedX - tx) > 1e-6 || Math.Abs(movedY - ty) > 1e-6
            ? $"The view moved {Geo.Mm(movedX)}, {Geo.Mm(movedY)} mm instead of {Geo.Mm(tx)}, {Geo.Mm(ty)} mm: it stays aligned with its source view."
            : null;
        return Result(context, draft, view, note);
    }

    public static object ScaleView(SeContext context, string? viewHandle, double? scale)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        view.ScaleFactor = RequireScale(scale ?? throw ToolException.InvalidArgument("Action 'scale_view' requires 'scale'."));
        return Result(context, draft, view, null);
    }

    public static object DeleteView(SeContext context, string? viewHandle)
    {
        DrawingView view = ResolveView(context, viewHandle);
        string? name = Late.TryGetString(view, "Name");
        view.Delete();
        context.Handles.Remove(viewHandle!, "the drawing view was deleted");
        return new { deleted = name ?? viewHandle };
    }

    // ---------------------------------------------------------------- dimensions, parts list and updating

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
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
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
    public static object AddDimension(SeContext context, string? viewHandle, string kind, double[]? points, string? orientation, double[]? position)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 1);
        List<Graphic> graphics = Graphics(view);
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
            case "length":
            {
                (double X, double Y) p = model.Count >= 2 ? Mid(ModelToView(view, model[0]), ModelToView(view, model[1])) : ModelToView(view, model[0]);
                Graphic edge = Nearest(graphics, p, model[0], g => g.Shape == Shape.Line, "straight edge");
                dimension = dimensions.AddLength(edge.Reference);
                PlaceLinear(view, sheet, dimension, ToSheet(view, edge.Start), ToSheet(view, edge.End), null, graphics, at);
                break;
            }

            case "diameter":
            case "radius":
            {
                Graphic curve = Nearest(graphics, ModelToView(view, model[0]), model[0], g => g.Shape is Shape.Circle or Shape.Arc, "circle or arc");
                dimension = kind == "radius" ? dimensions.AddRadius(curve.Reference)
                    : curve.Shape == Shape.Circle ? dimensions.AddCircularDiameter(curve.Reference)
                    : dimensions.AddRadialDiameter(curve.Reference);
                break;
            }

            case "distance":
            case "angle":
            {
                if (model.Count != 2)
                {
                    throw ToolException.InvalidArgument($"A {kind} dimension needs two model points in 'points' [x1, y1, z1, x2, y2, z2].");
                }

                bool angle = kind == "angle";
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

                string? direction = orientation is "horizontal" or "vertical" ? orientation : null;
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

            default:
                throw ToolException.InvalidArgument($"Unknown dimension kind '{kind}'.", "Use length, distance, diameter, radius or angle.");
        }

        return new { view = context.Register(view, "drawing_view", Document(draft)), dimension = DescribeDimension(context, draft, dimension) };
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

    /// <summary>Parts list of an assembly view (with balloons if requested).</summary>
    public static object PartsList(SeContext context, string? viewHandle, double[]? position, bool balloons, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (view.ModelLink is not ModelLink link || link.ModelType != ModelLinkTypeConstants.igAssemblyLink)
        {
            throw new ToolException("wrong_view", "A parts list needs a view of an assembly.", "Pass a drawing view created from an .asm file.");
        }

        PartsList list;
        try
        {
            list = draft.PartsLists.Add(view, style ?? "", balloons ? 1 : 0, 1);
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("parts_list_failed", $"Solid Edge could not create the parts list: {ex.Message.Trim()}",
                style is null ? "Pass 'style' with the name of a saved parts list setting (e.g. 'Normal')." : "Check the saved setting name in 'style'.", ex);
        }

        if (position is not null)
        {
            (double x, double y) = Geo.Point2(position, "position");
            list.AnchorPoint = TableAnchorPoint.igUpperLeft;
            list.SetOrigin(x, y);
        }
        else
        {
            // Solid Edge leaves it outside the frame; as a person would place it, above the title block and right-aligned.
            (double right, double top) = AboveTitleBlock(SheetOf(view));
            list.AnchorPoint = TableAnchorPoint.igLowerRight;
            list.SetOrigin(right, top);
        }

        return DescribePartsList(context, draft, list);
    }

    /// <summary>
    /// Lower right corner of the gap above the title block, deduced from the lines of the sheet background: the right edge of the frame
    /// (the longest vertical line in the right half) and the highest horizontal line in the lower half that reaches that edge
    /// without crossing the whole frame. Without a background, at a margin from the lower right corner of the sheet.
    /// </summary>
    private static (double X, double Y) AboveTitleBlock(Sheet sheet)
    {
        double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
        var lines = new List<(double X1, double Y1, double X2, double Y2)>();
        try
        {
            if (sheet.Background is Sheet back)
            {
                foreach (SolidEdgeFrameworkSupport.Line2d line in Late.Items(back.Lines2d).OfType<SolidEdgeFrameworkSupport.Line2d>())
                {
                    line.GetStartPoint(out double x1, out double y1);
                    line.GetEndPoint(out double x2, out double y2);
                    lines.Add((x1, y1, x2, y2));
                }
            }
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // Background without readable lines: the sheet corner is used.
        }

        var vertical = lines.Where(l => Math.Abs(l.X1 - l.X2) < 1e-6 && l.X1 > width / 2).ToList();
        if (vertical.Count == 0)
        {
            return (width - Margin, Margin);
        }

        double right = vertical.MaxBy(l => Math.Abs(l.Y2 - l.Y1)).X1;
        var horizontal = lines.Where(l => Math.Abs(l.Y1 - l.Y2) < 1e-6 && l.Y1 < height / 2).ToList();
        double frameWidth = horizontal.Count == 0 ? width : horizontal.Max(l => Math.Abs(l.X2 - l.X1));
        double top = horizontal.Where(l => Math.Abs(Math.Max(l.X1, l.X2) - right) < 1e-4 && Math.Abs(l.X2 - l.X1) < 0.8 * frameWidth)
            .Select(l => l.Y1).DefaultIfEmpty(horizontal.Select(l => l.Y1).DefaultIfEmpty(Margin).Min()).Max();
        return (right, top);
    }

    /// <summary>Updates a view or the whole drawing (out-of-date views and title block property texts).</summary>
    public static object Update(SeContext context, string? documentHandle, string? viewHandle, bool force)
    {
        if (!string.IsNullOrWhiteSpace(viewHandle))
        {
            DrawingView view = ResolveView(context, viewHandle);
            DraftDocument owner = DraftFor(context, null, viewHandle);
            if (force)
            {
                view.ForceUpdate();
            }
            else
            {
                view.Update();
            }

            return Result(context, owner, view, null);
        }

        DraftDocument draft = Draft(context, documentHandle);
        draft.UpdateAll(force);
        try
        {
            draft.UpdatePropertyTextCacheAndDisplay();
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // Without property texts there is nothing to refresh.
        }

        var views = new List<object>();
        int outOfDate = 0;
        foreach (Sheet sheet in WorkingSheets(draft))
        {
            foreach (DrawingView view in ViewsOf(sheet))
            {
                bool upToDate = view.IsUpToDate;
                outOfDate += upToDate ? 0 : 1;
                views.Add(new { handle = context.Register(view, "drawing_view", Document(draft)), name = Late.TryGetString(view, "Name"), sheet = sheet.Name, upToDate });
            }
        }

        return new { document = DocumentHandle(context, draft), outOfDate, views };
    }

    // ---------------------------------------------------------------- description

    private static object DescribeSheet(SeContext context, DraftDocument draft, Sheet sheet, string activeName)
    {
        string? background = null;
        try
        {
            background = sheet.Background?.Name;
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // Sheet without background.
        }

        return new
        {
            handle = context.Register(sheet, "sheet", Document(draft)),
            name = sheet.Name,
            number = sheet.Number,
            active = string.Equals(sheet.Name, activeName, StringComparison.Ordinal),
            size = SizeMm(sheet),
            paper = PaperName(sheet.SheetSetup.SheetSizeOption),
            background,
            views = sheet.DrawingViews.Count,
        };
    }

    private static object Result(SeContext context, DraftDocument draft, DrawingView view, string? note)
    {
        if (note is null && view.Parent is Sheet sheet)
        {
            double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
            if (!BoxOf(view).Within(width, height))
            {
                note = $"The view does not fit inside the {Geo.Mm(width)} x {Geo.Mm(height)} mm sheet: move it (move_view), "
                    + "reduce its scale (scale_view) or use a larger sheet (setup_sheet).";
            }
        }

        return new { view = DescribeView(context, draft, view), note };
    }

    private static object DescribeView(SeContext context, DraftDocument draft, DrawingView view)
    {
        Box box = BoxOf(view);
        string? model = null;
        try
        {
            if (view.ModelLink is ModelLink link)
            {
                model = System.IO.Path.GetFileName(link.FileName);
            }
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // View without a model (2D drawing).
        }

        return new
        {
            handle = context.Register(view, "drawing_view", Document(draft)),
            name = Late.TryGetString(view, "Name"),
            type = TypeName(view.DrawingViewType),
            orientation = OrientationOf(view),
            scale = Geo.R(view.ScaleFactor),
            center = new[] { Geo.Mm(box.CentreX), Geo.Mm(box.CentreY) },
            size = new[] { Geo.Mm(box.Width), Geo.Mm(box.Height) },
            sheet = (view.Parent as Sheet)?.Name,
            model,
            upToDate = view.IsUpToDate,
        };
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
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
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

    private static object DescribePartsList(SeContext context, DraftDocument draft, PartsList list)
    {
        list.GetOrigin(out double x, out double y);
        TableColumns columns = list.Columns;
        var shown = new List<int>();
        var headers = new List<string?>();
        for (int c = 1; c <= columns.Count; c++)
        {
            TableColumn column = columns.Item(c);
            if (!column.Show)
            {
                continue;
            }

            shown.Add(c);
            headers.Add(Late.TryGetString(column, "HeaderRowValue") ?? column.Header);
        }

        int count = list.Rows.Count;
        var rows = new List<string?[]>();
        for (int r = 1; r <= count && r <= context.MaxListItems; r++)
        {
            int row = r;
            rows.Add(shown.Select(c => (Late.TryGet(list, "Cell", row, c) as TableCell)?.value).ToArray());
        }

        return new
        {
            handle = context.Register(list, "parts_list", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            columns = headers,
            count,
            rows,
        };
    }

    private static string TypeName(DrawingViewTypeConstants type) => type switch
    {
        DrawingViewTypeConstants.igPrincipleView => "principal",
        DrawingViewTypeConstants.igIsometricView => "pictorial",
        DrawingViewTypeConstants.igAuxiliaryView => "auxiliary",
        DrawingViewTypeConstants.igXSectionView => "section",
        DrawingViewTypeConstants.igDetailView => "detail",
        DrawingViewTypeConstants.igIsoXSectionView => "pictorial_section",
        DrawingViewTypeConstants.igRevolvedSectionView => "revolved_section",
        DrawingViewTypeConstants.igBrokenView => "broken",
        DrawingViewTypeConstants.igUserView => "user",
        DrawingViewTypeConstants.igNailboardView => "nailboard",
        _ => "other",
    };

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

    private static string? OrientationOf(DrawingView view)
    {
        try
        {
            view.ViewOrientation(out _, out _, out _, out _, out _, out _, out ViewOrientationConstants orientation);
            return Orientations.FirstOrDefault(pair => pair.Value == orientation).Key ?? "custom";
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return null;
        }
    }

    private static string PaperName(PaperSizeConstants option)
    {
        foreach ((string name, (PaperSizeConstants wide, PaperSizeConstants tall)) in Papers)
        {
            if (option == wide)
            {
                return name;
            }

            if (option == tall)
            {
                return name + "_portrait";
            }
        }

        return option == PaperSizeConstants.igCustomSheetSize ? "custom" : "other";
    }

    // ---------------------------------------------------------------- resolution

    private static DraftDocument Draft(SeContext context, string? documentHandle) =>
        context.GetDocument<DraftDocument>(documentHandle, "draft");

    /// <summary>Drawing of the given document or, if none is given, of the related handle (sheet or view) or the active one.</summary>
    private static DraftDocument DraftFor(SeContext context, string? documentHandle, string? related)
    {
        if (string.IsNullOrWhiteSpace(documentHandle) && !string.IsNullOrWhiteSpace(related) && context.Handles.GetScope(related) is not null)
        {
            return context.DocumentOf(related) as DraftDocument ?? Draft(context, null);
        }

        return Draft(context, documentHandle);
    }

    private static SolidEdgeDocument Document(DraftDocument draft) => (SolidEdgeDocument)draft;

    private static string DocumentHandle(SeContext context, DraftDocument draft) => context.DocumentHandle(Document(draft));

    private static DrawingView ResolveView(SeContext context, string? handle) =>
        string.IsNullOrWhiteSpace(handle)
            ? throw ToolException.InvalidArgument("'view' is required: a drawing view handle from se_drawing 'views' or from the action that created it.")
            : context.Handles.Resolve<DrawingView>(handle, "view");

    /// <summary>Sheet by handle or name; if none is given, the active one.</summary>
    private static Sheet ResolveSheet(SeContext context, DraftDocument draft, string? sheet)
    {
        if (string.IsNullOrWhiteSpace(sheet))
        {
            return draft.ActiveSheet;
        }

        if (context.Handles.GetScope(sheet) is not null)
        {
            return context.Handles.Resolve<Sheet>(sheet, "sheet");
        }

        return WorkingSheets(draft).FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"There is no sheet '{sheet}' in '{SeContext.SafeName(Document(draft))}'.", "Action 'sheets' lists them.");
    }

    /// <summary>Sheet of the drawing that contains the view (DrawingView.Sheet is the view's own internal sheet).</summary>
    private static Sheet SheetOf(DrawingView view) =>
        view.Parent as Sheet ?? throw new ToolException("view_failed", "The drawing view is not on a sheet.");

    private static IEnumerable<Sheet> WorkingSheets(DraftDocument draft) => Late.Items(draft.Sections.WorkingSection.Sheets).OfType<Sheet>();

    private static IEnumerable<Sheet> BackgroundSheets(DraftDocument draft) => Late.Items(draft.Sections.BackgroundSection.Sheets).OfType<Sheet>();

    private static IEnumerable<DrawingView> ViewsOf(Sheet sheet) => Late.Items(sheet.DrawingViews).OfType<DrawingView>();

    private static IEnumerable<Dimension> DimensionsOf(Sheet sheet) => Late.Items(sheet.Dimensions).OfType<Dimension>();

    private static void RequireFreeName(DraftDocument draft, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' cannot be empty.");
        }

        if (WorkingSheets(draft).Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException("name_in_use", $"There is already a sheet named '{name}'.", "Choose another name.");
        }
    }

    /// <summary>Model file: saved path (allowed folders) or that of an open, already saved document.</summary>
    private static string ModelFile(SeContext context, string? path, string? modelHandle, out string? warning)
    {
        warning = null;
        string file;
        if (!string.IsNullOrWhiteSpace(modelHandle))
        {
            SolidEdgeDocument model = context.GetDocument(modelHandle);
            file = DocumentService.PathOf(model) ?? throw new ToolException("never_saved",
                $"'{model.Name}' has never been saved; a drawing shows a model file.", "Save it first (se_documents save_as).");
            if (model.Dirty)
            {
                warning = $"'{model.Name}' has unsaved changes; the drawing shows the saved file. Save it, then run action 'update'.";
            }
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            file = path;
        }
        else
        {
            throw ToolException.InvalidArgument("Action 'base_view' requires 'path' (a saved .par, .psm or .asm) or 'model' (handle of an open, saved model).");
        }

        file = context.Paths.Check(file, "model path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The model file '{file}' does not exist.", "Save the model first (se_documents save_as).");
        }

        if (System.IO.Path.GetExtension(file).ToLowerInvariant() is not (".par" or ".psm" or ".asm" or ".pwd"))
        {
            throw ToolException.InvalidArgument($"'{file}' is not a part, sheet metal, assembly or weldment file.");
        }

        return file;
    }

    private static ModelLink LinkTo(DraftDocument draft, string file)
    {
        foreach (ModelLink link in Late.Items(draft.ModelLinks).OfType<ModelLink>())
        {
            if (string.Equals(Late.TryGetString(link, "FileName"), file, StringComparison.OrdinalIgnoreCase))
            {
                return link;
            }
        }

        return draft.ModelLinks.Add(file);
    }

    private static void ApplySetup(DraftDocument draft, Sheet sheet, string? paper, bool portrait, double? width, double? height, string? background)
    {
        SheetSetup setup = sheet.SheetSetup;
        bool resized = false;
        if (paper is not null)
        {
            if (!Papers.TryGetValue(paper, out var option))
            {
                throw ToolException.InvalidArgument($"Unknown paper size '{paper}'.", $"Use one of: {string.Join(", ", Papers.Keys)}, or width and height in mm.");
            }

            setup.SheetSizeOption = portrait ? option.Tall : option.Wide;
            resized = true;
        }
        else if (width is not null || height is not null)
        {
            double w = Units.RequirePositive(width ?? throw ToolException.InvalidArgument("A custom sheet size needs both 'width' and 'height'."), "width");
            double h = Units.RequirePositive(height ?? throw ToolException.InvalidArgument("A custom sheet size needs both 'width' and 'height'."), "height");
            setup.SheetSizeOption = PaperSizeConstants.igCustomSheetSize;
            setup.SheetWidth = Units.MmToM(w);
            setup.SheetHeight = Units.MmToM(h);
            resized = true;
        }

        Sheet? back = null;
        if (background is not null)
        {
            back = BackgroundSheets(draft).FirstOrDefault(b => string.Equals(b.Name, background, StringComparison.OrdinalIgnoreCase))
                ?? throw ToolException.NotFound($"There is no background sheet '{background}'.", "Action 'sheets' lists the backgrounds of the template.");
        }
        else if (resized)
        {
            // The background (frame and title block) of the same paper size, if the template has one.
            back = BackgroundSheets(draft).FirstOrDefault(b =>
                Math.Abs(b.SheetSetup.SheetWidth - setup.SheetWidth) < 5e-4 && Math.Abs(b.SheetSetup.SheetHeight - setup.SheetHeight) < 5e-4);
        }

        if (back is not null)
        {
            sheet.Background = back;
        }
    }

    // ---------------------------------------------------------------- sheet geometry

    private readonly record struct Box(double X1, double Y1, double X2, double Y2)
    {
        public double Width => X2 - X1;

        public double Height => Y2 - Y1;

        public double CentreX => (X1 + X2) / 2;

        public double CentreY => (Y1 + Y2) / 2;

        public Box Shift(double dx, double dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy);

        public bool Overlaps(Box other, double margin) =>
            X1 < other.X2 + margin && other.X1 < X2 + margin && Y1 < other.Y2 + margin && other.Y1 < Y2 + margin;

        public bool Inside(double width, double height) =>
            X1 >= Margin && Y1 >= Margin && X2 <= width - Margin && Y2 <= height - Margin;

        public bool Within(double width, double height) => X1 >= 0 && Y1 >= 0 && X2 <= width && Y2 <= height;
    }

    /// <summary>Minimum distance of an automatically placed view to the edge of the sheet (m).</summary>
    private const double Margin = 0.01;

    private static Box BoxOf(DrawingView view)
    {
        view.Range(out double x1, out double y1, out double x2, out double y2);
        return new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    private static double[] SizeMm(Sheet sheet) => [Geo.Mm(sheet.SheetSetup.SheetWidth), Geo.Mm(sheet.SheetSetup.SheetHeight)];

    private static (double X, double Y) Centre(Sheet sheet) => (sheet.SheetSetup.SheetWidth / 2, sheet.SheetSetup.SheetHeight / 2);

    private static void Shift(DrawingView view, double dx, double dy)
    {
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            return;
        }

        view.GetOrigin(out double x, out double y);
        view.SetOrigin(x + dx, y + dy);
    }

    private static void CentreAt(DrawingView view, double x, double y)
    {
        Box box = BoxOf(view);
        Shift(view, x - box.CentreX, y - box.CentreY);
    }

    /// <summary>
    /// Moves a view away from its origin in the given direction (−1, 0 or 1 per axis), with the standard spacing. If
    /// <paramref name="sheet"/> is given, the view keeps moving away in that direction (without losing alignment) until it no longer overlaps others.
    /// </summary>
    private static void PlaceAway(Sheet? sheet, DrawingView view, Box from, int dirX, int dirY)
    {
        Box box = BoxOf(view);
        double dx = dirX > 0 ? from.X2 + Gap - box.X1 : dirX < 0 ? from.X1 - Gap - box.X2 : 0;
        double dy = dirY > 0 ? from.Y2 + Gap - box.Y1 : dirY < 0 ? from.Y1 - Gap - box.Y2 : 0;
        Shift(view, dx, dy);
        if (sheet is not null)
        {
            Clear(sheet, view, dirX, dirY);
        }
    }

    /// <summary>
    /// Moves a view along a unit direction until it is at the standard spacing from its origin, and beyond
    /// the views in that direction.
    /// </summary>
    private static void PlaceAlong(Sheet sheet, DrawingView view, Box from, double ux, double uy)
    {
        Box box = BoxOf(view);
        double fromFar = Project(from, ux, uy, max: true);
        double boxNear = Project(box, ux, uy, max: false);
        double move = fromFar + Gap - boxNear;
        Shift(view, ux * move, uy * move);
        Clear(sheet, view, ux, uy);
    }

    /// <summary>Moves a view away along (ux, uy), its alignment direction, until it no longer overlaps other views on the sheet.</summary>
    private static void Clear(Sheet sheet, DrawingView view, double ux, double uy)
    {
        List<Box> others = ViewsOf(sheet).Where(v => !ReferenceEquals(v, view)).Select(BoxOf).ToList();
        Box box = BoxOf(view);
        double move = 0;
        for (int i = 0; i < others.Count; i++)
        {
            Box current = box.Shift(ux * move, uy * move);
            List<Box> blocking = others.Where(o => o.Overlaps(current, Gap / 2)).ToList();
            if (blocking.Count == 0)
            {
                break;
            }

            move += blocking.Max(o => Project(o, ux, uy, max: true)) + Gap - Project(current, ux, uy, max: false);
        }

        Shift(view, ux * move, uy * move);
    }

    private static double Project(Box box, double ux, double uy, bool max)
    {
        double[] values = [box.X1 * ux + box.Y1 * uy, box.X2 * ux + box.Y1 * uy, box.X1 * ux + box.Y2 * uy, box.X2 * ux + box.Y2 * uy];
        return max ? values.Max() : values.Min();
    }

    /// <summary>Pictorial view (not aligned): diagonally from its origin, fitted into the sheet; if that overlaps, the first free gap.</summary>
    private static void PlacePictorial(Sheet sheet, DrawingView view, Box from, int dirX, int dirY)
    {
        PlaceAway(null, view, from, dirX, dirY);
        Box box = BoxOf(view);
        double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
        double dx = Math.Min(0, width - Margin - box.X2) + Math.Max(0, Margin - box.X1);
        double dy = Math.Min(0, height - Margin - box.Y2) + Math.Max(0, Margin - box.Y1);
        Box moved = box.Shift(dx, dy);
        if (moved.Inside(width, height) && !ViewsOf(sheet).Where(v => !ReferenceEquals(v, view)).Select(BoxOf).Any(o => o.Overlaps(moved, 0)))
        {
            Shift(view, dx, dy);
            return;
        }

        PlaceFree(sheet, view, from);
    }

    /// <summary>Places an independent view next to its origin, in the first gap of the sheet that does not overlap others.</summary>
    private static void PlaceFree(Sheet sheet, DrawingView view, Box from)
    {
        Box box = BoxOf(view);
        List<Box> others = ViewsOf(sheet).Where(v => !ReferenceEquals(v, view)).Select(BoxOf).ToList();
        double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
        foreach ((int dx, int dy) in new[] { (1, 0), (0, -1), (0, 1), (-1, 0) })
        {
            double cx = dx > 0 ? from.X2 + Gap + box.Width / 2 : dx < 0 ? from.X1 - Gap - box.Width / 2 : from.CentreX;
            double cy = dy > 0 ? from.Y2 + Gap + box.Height / 2 : dy < 0 ? from.Y1 - Gap - box.Height / 2 : from.CentreY;
            Box candidate = new(cx - box.Width / 2, cy - box.Height / 2, cx + box.Width / 2, cy + box.Height / 2);
            for (int step = 0; step < others.Count && candidate.Inside(width, height); step++)
            {
                Box? blocking = others.Where(o => o.Overlaps(candidate, Gap / 2)).Select(o => (Box?)o).FirstOrDefault();
                if (blocking is not { } b)
                {
                    break;
                }

                candidate = dx > 0 ? candidate.Shift(b.X2 + Gap - candidate.X1, 0)
                    : dx < 0 ? candidate.Shift(b.X1 - Gap - candidate.X2, 0)
                    : dy > 0 ? candidate.Shift(0, b.Y2 + Gap - candidate.Y1)
                    : candidate.Shift(0, b.Y1 - Gap - candidate.Y2);
            }

            if (candidate.Inside(width, height) && !others.Any(o => o.Overlaps(candidate, 0)))
            {
                Shift(view, candidate.X1 - box.X1, candidate.Y1 - box.Y1);
                return;
            }
        }
    }

    /// <summary>Unit normal (on the sheet) to a line, toward the side of the view with more free sheet.</summary>
    private static (double X, double Y) RoomierNormal((double X, double Y) start, (double X, double Y) end, Box from, Sheet sheet)
    {
        double length = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));
        double nx = -(end.Y - start.Y) / length, ny = (end.X - start.X) / length;
        var paper = new Box(0, 0, sheet.SheetSetup.SheetWidth, sheet.SheetSetup.SheetHeight);
        double roomPlus = Project(paper, nx, ny, max: true) - Project(from, nx, ny, max: true);
        double roomMinus = Project(paper, -nx, -ny, max: true) - Project(from, -nx, -ny, max: true);
        return roomMinus > roomPlus ? (-nx, -ny) : (nx, ny);
    }

    private static ((double X, double Y) Start, (double X, double Y) End) SheetLine(DrawingView view, double[]? line)
    {
        List<(double X, double Y, double Z)> points = Geo.Points3(line, "line", 2);
        if (points.Count != 2)
        {
            throw ToolException.InvalidArgument("'line' must be two model points [x1, y1, z1, x2, y2, z2] in mm.");
        }

        (double X, double Y) start = ModelToSheet(view, points[0]);
        (double X, double Y) end = ModelToSheet(view, points[1]);
        if (Math.Abs(end.X - start.X) < 1e-9 && Math.Abs(end.Y - start.Y) < 1e-9)
        {
            throw ToolException.InvalidArgument("The two points of 'line' coincide in this view.",
                "Give two model points that project apart in the source view.");
        }

        return (start, end);
    }

    private static (double X, double Y) ModelToView(DrawingView view, (double X, double Y, double Z) point)
    {
        view.ModelToView(point.X, point.Y, point.Z, out double x, out double y);
        return (x, y);
    }

    private static (double X, double Y) ModelToSheet(DrawingView view, (double X, double Y, double Z) point)
    {
        (double vx, double vy) = ModelToView(view, point);
        view.ViewToSheet(vx, vy, out double x, out double y);
        return (x, y);
    }

    private static double RequireScale(double scale)
    {
        Units.RequirePositive(scale, "scale");
        if (scale is < 1e-4 or > 1e4)
        {
            throw ToolException.InvalidArgument($"'scale' must be between 0.0001 and 10000 (1 = 1:1, 0.5 = 1:2); got {scale}.");
        }

        return scale;
    }

    /// <summary>Largest standard scale at which the view occupies at most <see cref="FitFraction"/> of the sheet.</summary>
    private static double FitScale(ModelLink link, ViewOrientationConstants orientation, Sheet sheet)
    {
        double width, height;
        try
        {
            if (orientation is >= ViewOrientationConstants.igTopView and <= ViewOrientationConstants.igBackView)
            {
                link.Range2d(orientation, out double x1, out double y1, out double x2, out double y2);
                (width, height) = (Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            }
            else
            {
                link.Range3d(out double x1, out double y1, out double z1, out double x2, out double y2, out double z2);
                double diagonal = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1) + (z2 - z1) * (z2 - z1));
                (width, height) = (diagonal, diagonal);
            }
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return 1;
        }

        if (width <= 0 && height <= 0)
        {
            return 1;
        }

        double maxWidth = sheet.SheetSetup.SheetWidth * FitFraction, maxHeight = sheet.SheetSetup.SheetHeight * FitFraction;
        foreach (double scale in StandardScales)
        {
            if (width * scale <= maxWidth && height * scale <= maxHeight)
            {
                return scale;
            }
        }

        return StandardScales[^1];
    }

    // ---------------------------------------------------------------- view elements and dimension placement

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
        /// <summary>Sheet reference of the element: dimensions created with it stay associated with the view.</summary>
        public object Reference => Late.Get(Value, "Reference")
            ?? throw new ToolException("dimension_failed", "Solid Edge returned no reference for the selected edge.");
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

    private static Graphic Nearest(List<Graphic> graphics, (double X, double Y) p, (double X, double Y, double Z) model, Func<Graphic, bool> filter, string what)
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
            throw ToolException.NotFound($"The view shows no {what}.", "Choose another view.");
        }

        if (bestDistance > PickTolerance)
        {
            throw ToolException.NotFound($"No {what} of the view passes through {point}: the nearest is {Geo.Mm(bestDistance)} mm away (model units).",
                "Give a model point on the edge as seen in this view (se_geometry lists edges and vertices of the model).");
        }

        return best;
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

        Graphic edge = Nearest(graphics, p, model, g => !linesOnly || g.Shape == Shape.Line, linesOnly ? "straight edge" : "edge");
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

    private static DrawingView Create(Func<DrawingView> create, string what, string hint)
    {
        try
        {
            return create() ?? throw new ToolException("view_failed", $"Solid Edge did not create the {what}.", hint);
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("view_failed", $"Solid Edge could not create the {what}: {ex.Message.Trim()}", hint, ex);
        }
    }

    private static void TryDelete(object target)
    {
        try
        {
            Late.Call(target, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // If it cannot be deleted, the empty cutting plane remains: it does not affect the model.
        }
    }
}
