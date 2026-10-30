// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
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
            section = IsBackground(draft.ActiveSheet) ? "background" : "working",
            sheets = WorkingSheets(draft).Select(sheet => DescribeSheet(context, draft, sheet, active)).ToList(),
            backgrounds = BackgroundSheets(draft).Select(sheet => DescribeSheet(context, draft, sheet, active)).ToList(),
            model2d = Model2d(context, draft),
        };
    }

    /// <summary>The 2D model sheet: drawn on with se_sketch like any sheet, and shown on working sheets by 2D model views.</summary>
    private static object Model2d(SeContext context, DraftDocument draft)
    {
        Sheet sheet = draft.Sections.Get2DModelSheet;
        return new { handle = context.Register(sheet, "sheet", Document(draft)), name = sheet.Name, objects = Late.Count(sheet.DrawingObjects) };
    }

    /// <summary>
    /// A 2D model view: the content of the 2D model sheet at a scale, centred at <paramref name="position"/> (default the centre of
    /// the sheet). Verified: Add2dModelView shows everything drawn on the 2D model sheet; with nothing on it the view is an empty
    /// 25.4 mm square, so an empty 2D model is refused.
    /// </summary>
    public static object Model2dView(SeContext context, string? documentHandle, string? sheetRef, double? scale, double[]? position)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        Sheet model = draft.Sections.Get2DModelSheet;
        if (Late.Count(model.DrawingObjects) == 0)
        {
            throw ToolException.InvalidArgument("The 2D model sheet is empty, so a 2D model view would show nothing.",
                "Draw on it first: se_sketch with the 'model2d' sheet handle from 'sheets'.");
        }

        if (IsBackground(sheet))
        {
            throw ToolException.InvalidArgument("A 2D model view goes on a working sheet, not on a background.");
        }

        double factor = scale is { } s ? RequireScale(s) : 1;
        (double x, double y) = position is null ? Centre(sheet) : Geo.Point2(position, "position");
        sheet.Activate();
        DrawingView view = Create(() => sheet.DrawingViews.Add2dModelView(factor, x, y), "2D model view", "Check the scale.");
        CentreAt(view, x, y);
        return Result(context, draft, view, null);
    }

    public static object AddSheet(SeContext context, string? documentHandle, string? name, string? paper, bool portrait,
        double? width, double? height, string? background, bool? showBackground = null)
    {
        DraftDocument draft = Draft(context, documentHandle);
        if (name is not null)
        {
            RequireFreeName(draft, name);
        }

        Sheet sheet = name is null
            ? draft.Sheets.AddSheet(SectionType: SheetSectionTypeConstants.igWorkingSection)
            : draft.Sheets.AddSheet(name, SheetSectionTypeConstants.igWorkingSection);
        ApplySetup(draft, sheet, paper, portrait, width, height, background, showBackground);
        sheet.Activate();
        return DescribeSheet(context, draft, sheet, sheet.Name);
    }

    /// <summary>Renames, resizes or changes the background of a working sheet, or renames or resizes a background sheet.</summary>
    public static object SetupSheet(SeContext context, string? documentHandle, string? sheetRef, string? name, string? paper, bool portrait,
        double? width, double? height, string? background, bool? showBackground = null)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        if (name is not null && !string.Equals(name, sheet.Name, StringComparison.Ordinal))
        {
            RequireFreeName(draft, name);
            sheet.Name = name;
        }

        ApplySetup(draft, sheet, paper, portrait, width, height, background, showBackground);
        return DescribeSheet(context, draft, sheet, draft.ActiveSheet.Name);
    }

    /// <summary>
    /// Shows a sheet. Activating a background sheet switches the window to the background sheets, as the Background command
    /// does (verified: ActiveSectionType becomes igBackgroundSection); activating a working sheet switches back.
    /// </summary>
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
        if (IsBackground(sheet))
        {
            // Verified: Solid Edge deletes a background in use and leaves its sheets without one.
            if (UsersOf(draft, sheet) is { Count: > 0 } users)
            {
                throw new ToolException("background_in_use", $"The background sheet '{sheet.Name}' is used by {string.Join(", ", users)}.",
                    "Give those sheets another background first (setup_sheet with 'background').");
            }
        }
        else if (WorkingSheets(draft).Count() <= 1)
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
            cuttingPlanes = ViewsOf(sheet).SelectMany(CuttingPlanesOf).Select(plane => DescribeCuttingPlane(context, draft, plane)).ToList(),
        };
    }

    /// <summary>Base view of a saved model; without a scale, the largest standard scale that leaves room for the projections.</summary>
    public static object BaseView(SeContext context, string? documentHandle, string? sheetRef, string? path, string? modelHandle,
        string? orientation, double? scale, double[]? position, bool flat, string? configuration = null, string? modelView = null, string? member = null)
    {
        DraftDocument draft = Draft(context, documentHandle);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        string file = ModelFile(context, path, modelHandle, out SolidEdgeDocument? unsaved);
        string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (flat && extension is not (".psm" or ".par"))
        {
            throw ToolException.InvalidArgument("'flat' needs a sheet metal model (.psm, or a .par with a flat pattern).");
        }

        if (!string.IsNullOrWhiteSpace(modelView) && (flat || !string.IsNullOrWhiteSpace(configuration) || orientation is not null))
        {
            throw ToolException.InvalidArgument("'modelView' shows a PMI model view as it was saved: leave out 'flat', 'configuration' and 'orientation'.");
        }

        string orientationName = orientation ?? (flat ? "top" : "front");
        if (!Orientations.TryGetValue(orientationName, out ViewOrientationConstants orient))
        {
            throw ToolException.InvalidArgument($"Unknown orientation '{orientationName}'.", $"Use one of: {string.Join(", ", Orientations.Keys)}.");
        }

        // A display configuration of an assembly lives in the .cfg file next to it (see AssemblyConfigService).
        string? configFile = null;
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            configFile = System.IO.Path.ChangeExtension(file, ".cfg");
            if (extension != ".asm" || flat)
            {
                throw ToolException.InvalidArgument("'configuration' needs an assembly (.asm): its display configurations are saved with se_assembly_config.");
            }

            if (!File.Exists(configFile))
            {
                throw ToolException.NotFound($"'{System.IO.Path.GetFileName(file)}' has no display configurations (no {System.IO.Path.GetFileName(configFile)} next to it).",
                    "Save one with se_assembly_config 'save' and save the assembly.");
            }
        }

        if (!string.IsNullOrWhiteSpace(member) && extension != ".asm")
        {
            throw ToolException.InvalidArgument("'member' picks a member of an assembly with alternate positions or a family (.asm).");
        }

        // Making a view clears the model's changed mark (see UnsavedNote): set it again on every path, errors included.
        try
        {
            ModelLink link = LinkTo(draft, file, string.IsNullOrWhiteSpace(member) ? null : member.Trim());
            if (!string.IsNullOrWhiteSpace(modelView))
            {
                RequireModelView(link, file, modelView);
            }

            double factor = scale is { } s ? RequireScale(s) : FitScale(link, orient, sheet);
            (double x, double y) = position is null ? Centre(sheet) : Geo.Point2(position, "position");
            sheet.Activate();
            if (!string.IsNullOrWhiteSpace(modelView))
            {
                DrawingView pmi = Create(() => sheet.DrawingViews.AddPMIModelView(link, modelView, factor, x, y), "PMI model view",
                    "Check the name of the PMI model view in the model.");
                CentreAt(pmi, x, y);
                return Result(context, draft, pmi, UnsavedNote(unsaved));
            }

            if (configFile is not null)
            {
                DrawingView configured = Create(() => sheet.DrawingViews.AddWithConfiguration(link, orient, factor, x, y, configFile, configuration!),
                    "base view", "Check the configuration name with se_assembly_config 'list'.");
                if (!string.Equals(configured.Configuration, configuration, StringComparison.OrdinalIgnoreCase))
                {
                    string kept = configured.Configuration;
                    TryDelete(configured);
                    throw ToolException.NotFound($"The assembly has no display configuration '{configuration}' (the view showed '{kept}').",
                        "List them with se_assembly_config 'list'.");
                }

                CentreAt(configured, x, y);
                string?[] notes = [UnsavedNote(unsaved), orientation is null ? null : StoredOrientation(configured, orient, orientationName, configuration!)];
                return Result(context, draft, configured, notes.Any(n => n is not null) ? string.Join(" ", notes.Where(n => n is not null)) : null);
            }

            DrawingView view = Create(() => flat
                    ? sheet.DrawingViews.AddSheetMetalView(link, orient, factor, x, y, SheetMetalDrawingViewTypeConstants.seSheetMetalFlatView)
                    : sheet.DrawingViews.Add(link, orient, factor, x, y),
                "base view", flat ? "Create the flat pattern in the model first (se_sheetmetal 'flatten') and save it." : "Check that the model has geometry and is saved.");
            CentreAt(view, x, y);
            return Result(context, draft, view, UnsavedNote(unsaved));
        }
        finally
        {
            if (unsaved is { Dirty: false })
            {
                unsaved.Dirty = true;
            }
        }
    }

    /// <summary>
    /// The note for a view of a configuration that kept its own orientation. Verified in Solid Edge 2026: an explode
    /// configuration keeps the orientation the assembly window had when it was exploded, AddWithConfiguration ignores the one
    /// given and SetViewOrientationStandard does not turn the view; a drawing that already has a view of the configuration
    /// keeps reading it as first loaded.
    /// </summary>
    private static string? StoredOrientation(DrawingView view, ViewOrientationConstants wanted, string wantedName, string configuration)
    {
        view.ViewOrientation(out _, out _, out _, out _, out _, out _, out ViewOrientationConstants shown);
        if (shown == wanted)
        {
            return null;
        }

        string name = Orientations.FirstOrDefault(pair => pair.Value == shown).Key
            ?? string.Concat(shown.ToString().Replace("ig", "", StringComparison.Ordinal).Replace("View", "", StringComparison.Ordinal)
                .Select((ch, i) => i > 0 && char.IsUpper(ch) ? $" {char.ToLowerInvariant(ch)}" : char.ToLowerInvariant(ch).ToString()));
        return $"The view keeps the orientation stored with the configuration '{configuration}' ({name}), not '{wantedName}': an explode configuration keeps the orientation the assembly window had when it was exploded. To change it, explode the assembly again with se_assembly_config explode and 'orientation', save it and make the view in a drawing without a view of that configuration (a drawing reads it once).";
    }

    /// <summary>
    /// The note for an open model whose unsaved changes the new view shows: a drawing reads the open document, not its file,
    /// and making the view clears the model's changed mark, so closing it with saveChanges did not write them (verified in
    /// Solid Edge 2026: a part saved as a disc, then given a bar, drawn with the bar, closed with saveChanges=true, and its
    /// file still held the disc). BaseView sets the mark again.
    /// </summary>
    private static string? UnsavedNote(SolidEdgeDocument? model) => model is null
        ? null
        : $"'{model.Name}' has unsaved changes: the drawing shows them, but its file does not have them until it is saved (se_documents save).";

    /// <summary>
    /// Checks that the linked model has a PMI model view of that name (exact case, as AddPMIModelView needs it). Verified: an
    /// unknown name makes AddPMIModelView fail with DISP_E_MEMBERNOTFOUND, which says nothing about the name.
    /// </summary>
    private static void RequireModelView(ModelLink link, string file, string modelView)
    {
        string[] names = [];
        if (Late.Get(link.ModelDocument, "PMI") is { } pmi && Late.Get(pmi, "PMIModelViews") is { } views)
        {
            names = [.. Late.Items(views).Select(v => Late.Get(v, "Name") as string ?? "")];
        }

        if (!names.Contains(modelView, StringComparer.Ordinal))
        {
            throw ToolException.NotFound(
                names.Length == 0 ? $"'{System.IO.Path.GetFileName(file)}' has no PMI model views." : $"'{System.IO.Path.GetFileName(file)}' has no PMI model view '{modelView}'.",
                names.Length == 0 ? "Add one in the model (PMI model views) and save it." : $"Its PMI model views: {string.Join(", ", names)}.");
        }
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

        return Result(context, draft, view, null, fold.X == 0 || fold.Y == 0 ? ProjectionOf(draft) : null);
    }

    /// <summary>
    /// Projection angle of the drawing: it decides which side of the model a view placed beside another shows (first angle:
    /// the view to the right of a front view is the left view; third angle: the right view).
    /// </summary>
    private static string? ProjectionOf(DraftDocument draft)
    {
        try
        {
            return draft.FilePreferences.DVProjectionAngle == DrawingViewProjectionAngleConstants.ProjectionAngleFirst
                ? "first_angle"
                : "third_angle";
        }
        catch (COMException)
        {
            return null;
        }
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
    /// Cutting plane without its view (the Cutting Plane command): the cutting line through two model points, extended
    /// across the source view, and the side it looks at. section_view makes its view later.
    /// </summary>
    public static object AddCuttingPlane(SeContext context, string? viewHandle, double[]? line, bool flip, string? caption)
    {
        DrawingView source = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        CuttingPlane plane = DrawCuttingPlane(draft, source, line, flip);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            try
            {
                plane.Caption = caption;
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                TryDeleteCuttingPlane(draft, plane);
                throw new ToolException("view_failed", $"Solid Edge did not take the caption '{caption}': {ex.Message.Trim()}",
                    "Give a short caption such as B, or leave it out for the next letter.", ex);
            }
        }

        return new { cuttingPlane = DescribeCuttingPlane(context, draft, plane), count = source.CuttingPlanes.Count };
    }

    /// <summary>
    /// Section by a plane perpendicular to the source view passing through the given line (two model points), extended
    /// until it crosses the whole view; or the view of an existing cutting plane (cutting_plane, views).
    /// </summary>
    public static object SectionView(SeContext context, string? viewHandle, double[]? line, bool flip, double[]? position)
    {
        CuttingPlane? existing = CuttingPlaneOf(context, viewHandle);
        DrawingView source = existing?.SourceDrawingView ?? ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (existing is not null)
        {
            if (line is not null || flip)
            {
                throw ToolException.InvalidArgument("A cutting plane already has its line and side: give it alone as 'view', or give a view with 'line'.");
            }

            if (existing.SectionView is DrawingView made)
            {
                throw ToolException.InvalidArgument(
                    $"The cutting plane {existing.Caption} already has its section view ({Late.TryGetString(made, "Name")}): add another cutting_plane.");
            }
        }

        Box from = BoxOf(source);
        CuttingPlane plane = existing ?? DrawCuttingPlane(draft, source, line, flip);
        DrawingView view;
        double lookX = 0, lookY = 0, ux = 1, uy = 0;
        try
        {
            view = Create(() => plane.CreateView(DraftSectionViewType.seDraftSectionViewTypeStandard), "section view",
                "Check that the cutting line crosses the part in the source view.");
            plane.GetFoldLineWithViewDirection(out double x1, out double y1, out double x2, out double y2, out lookX, out lookY);
            double length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            (ux, uy) = length > 1e-9 ? ((x2 - x1) / length, (y2 - y1) / length) : (1, 0);
            view.Update();
        }
        catch (Exception ex) when (ex is ToolException || (ex is COMException com && !ComErrors.IsGone(com)))
        {
            if (existing is null)
            {
                TryDeleteCuttingPlane(draft, plane);
            }

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

    /// <summary>
    /// Turns a view on the sheet to an absolute angle, counter-clockwise (View Properties). Verified in Solid Edge 2026:
    /// SetRotationAngle takes radians, GetRotationAngle reads them back from 0 to 2 pi, and turning a cropped view resets
    /// its crop, so a cropped view is refused.
    /// </summary>
    public static object RotateView(SeContext context, string? viewHandle, double? angle)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        double degrees = angle is { } a && double.IsFinite(a)
            ? a
            : throw ToolException.InvalidArgument("Action 'rotate_view' requires 'angle': the view's angle on the sheet in degrees, counter-clockwise.");
        if (view.Crop)
        {
            throw ToolException.InvalidArgument("The view is cropped, and Solid Edge resets the crop when it turns a view.",
                "Show it whole first (crop_view without 'area'), turn it, then crop it in View Properties if needed.");
        }

        view.GetRotationAngle(out double previous);
        view.SetRotationAngle(Units.DegToRad(degrees));
        view.GetRotationAngle(out double current);
        if (Math.Abs(Math.IEEERemainder(current - Units.DegToRad(degrees), 2 * Math.PI)) > 1e-6)
        {
            throw new ToolException("view_failed", $"Solid Edge turned the view to {Geo.R(Units.RadToDeg(current))} degrees instead of {Geo.R(degrees)}.",
                "Turn it in View Properties.");
        }

        return new
        {
            view = DescribeView(context, draft, view),
            angle = Geo.R(Units.RadToDeg(current)),
            previous = Geo.R(Units.RadToDeg(previous)),
            note = SheetNote(view),
        };
    }

    /// <summary>
    /// Crops a view to a rectangle of the sheet (View Properties > Crop), or shows it whole again. Verified in Solid Edge 2026:
    /// CropLeft, CropRight, CropBottom and CropTop are sheet distances in metres, at any scale, from the centre of the whole
    /// view to each side; the view's box becomes that rectangle, even past its edges. On a turned view the crop follows the
    /// view's own axes, so a turned view is refused.
    /// </summary>
    public static object CropView(SeContext context, string? viewHandle, double[]? area)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (area is null)
        {
            view.Crop = false;
            if (view.Crop)
            {
                throw new ToolException("view_failed", "Solid Edge kept the view cropped.", "Clear Crop in View Properties.");
            }

            return Result(context, draft, view, null);
        }

        if (area.Length != 4 || !area.All(double.IsFinite))
        {
            throw ToolException.InvalidArgument("'area' takes the rectangle of the sheet to keep, [x1, y1, x2, y2] in mm.");
        }

        view.GetRotationAngle(out double turned);
        if (Math.Abs(Math.IEEERemainder(turned, 2 * Math.PI)) > 1e-9)
        {
            throw ToolException.InvalidArgument("The view is turned, and Solid Edge crops a turned view in its own axes, not the sheet's.",
                "Crop it in View Properties, or set rotate_view angle 0 first.");
        }

        double x1 = Units.MmToM(Math.Min(area[0], area[2])), x2 = Units.MmToM(Math.Max(area[0], area[2]));
        double y1 = Units.MmToM(Math.Min(area[1], area[3])), y2 = Units.MmToM(Math.Max(area[1], area[3]));
        if (x2 - x1 < 1e-4 || y2 - y1 < 1e-4)
        {
            throw ToolException.InvalidArgument("'area' must be a rectangle at least 0.1 mm wide and high.");
        }

        Box box = BoxOf(view);
        double cx = box.CentreX, cy = box.CentreY;
        if (view.Crop)
        {
            cx -= (view.CropRight - view.CropLeft) / 2;
            cy -= (view.CropTop - view.CropBottom) / 2;
        }

        view.CropLeft = cx - x1;
        view.CropRight = x2 - cx;
        view.CropBottom = cy - y1;
        view.CropTop = y2 - cy;
        view.Crop = true;
        Box after = BoxOf(view);
        if (!view.Crop || Math.Abs(after.X1 - x1) > 1e-6 || Math.Abs(after.X2 - x2) > 1e-6 || Math.Abs(after.Y1 - y1) > 1e-6
            || Math.Abs(after.Y2 - y2) > 1e-6)
        {
            throw new ToolException("view_failed",
                $"The cropped view spans {Geo.Mm(after.X1)}, {Geo.Mm(after.Y1)} to {Geo.Mm(after.X2)}, {Geo.Mm(after.Y2)} mm instead of the area.",
                "Check the view with 'views' and crop it in View Properties.");
        }

        return Result(context, draft, view, null);
    }

    public static object DeleteView(SeContext context, string? viewHandle)
    {
        if (CuttingPlaneOf(context, viewHandle) is { } plane)
        {
            return DeleteCuttingPlane(context, viewHandle!, plane);
        }

        DrawingView view = ResolveView(context, viewHandle);
        string? name = Late.TryGetString(view, "Name");
        view.Delete();
        context.Handles.Remove(viewHandle!, "the drawing view was deleted");
        return new { deleted = name ?? viewHandle };
    }

    /// <summary>A cutting plane goes with its section view, as in Solid Edge.</summary>
    private static object DeleteCuttingPlane(SeContext context, string handle, CuttingPlane plane)
    {
        DrawingView source = plane.SourceDrawingView;
        DraftDocument draft = DraftFor(context, null, handle);
        DrawingView? section = plane.SectionView;
        string caption = plane.Caption;
        string? sectionName = section is null ? null : Late.TryGetString(section, "Name");
        int before = source.CuttingPlanes.Count;
        DeleteBySelection(draft, plane);
        if (source.CuttingPlanes.Count >= before)
        {
            throw new ToolException("view_failed", $"Solid Edge did not delete the cutting plane {caption}.",
                "Delete it in Solid Edge (select its line, then Delete).");
        }

        context.Handles.Remove(handle, "the cutting plane was deleted");
        if (section is not null && context.Handles.TryGetId(section, out string? sectionHandle) && sectionHandle is not null)
        {
            context.Handles.Remove(sectionHandle, "its cutting plane was deleted with it");
        }

        return new { deleted = $"cutting plane {caption}", sectionView = sectionName, count = source.CuttingPlanes.Count };
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

    /// <summary>
    /// Activate / Inactivate Drawing Views: whether the drawing views of the drawing are active (DraftDocument.InactiveDrawingViewMode,
    /// which the two commands set; verified). Without <paramref name="active"/> the mode is only read.
    /// </summary>
    public static object ViewActivation(SeContext context, string? documentHandle, string? viewHandle, bool? active)
    {
        DraftDocument draft = DraftFor(context, documentHandle, viewHandle);
        bool previous = !draft.InactiveDrawingViewMode;
        if (active is bool wanted && wanted != previous)
        {
            draft.InactiveDrawingViewMode = !wanted;
            if (draft.InactiveDrawingViewMode == wanted)
            {
                throw new ToolException("setting_rejected", $"Solid Edge left the drawing views {(previous ? "active" : "inactive")}.", "Check the drawing with se_drawing views.");
            }
        }

        return new { document = DocumentHandle(context, draft), active = !draft.InactiveDrawingViewMode, previous = active is null ? (bool?)null : previous };
    }

    /// Manual dimension in a view, as a person would place it: each model point (mm) is projected onto the view and selects the
    /// edge, circle or vertex through it. Linear dimensions are moved outside the outline and stacked.
    /// </summary>
    public static object AddDimension(SeContext context, string? viewHandle, string kind, double[]? points, string? orientation, double[]? position)
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
            case "length":
            {
                (double X, double Y) p = model.Count >= 2 ? Mid(ModelToView(view, model[0]), ModelToView(view, model[1])) : ModelToView(view, model[0]);
                Graphic edge = Nearest(view, graphics, p, model[0], g => g.Shape == Shape.Line, "straight edge");
                dimension = dimensions.AddLength(edge.Reference);
                PlaceLinear(view, sheet, dimension, ToSheet(view, edge.Start), ToSheet(view, edge.End), null, graphics, at);
                break;
            }

            case "diameter":
            case "radius":
            {
                // A section shows a hole as two parallel edges, not as a circle.
                Graphic curve = Nearest(view, graphics, ModelToView(view, model[0]), model[0], g => g.Shape is Shape.Circle or Shape.Arc, "circle or arc",
                    "A hole or shaft seen from the side (two parallel edges, e.g. in a section): draw its axis with se_annotation centerline and use kind symmetric_diameter; or use a view that looks along it.");
                dimension = kind == "radius" ? dimensions.AddRadius(curve.Reference)
                    : curve.Shape == Shape.Circle ? dimensions.AddCircularDiameter(curve.Reference)
                    : dimensions.AddRadialDiameter(curve.Reference);
                PlaceRadial(view, dimension, curve, graphics, at);
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

            case "coordinate":
                return CoordinateDimensions(context, draft, view, graphics, dimensions, model, orientation == "vertical");

            case "angular_coordinate":
                return AngularCoordinateDimensions(context, draft, view, graphics, dimensions, model, orientation == "clockwise");

            case "chamfer":
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

            case "symmetric_diameter":
                dimension = SymmetricDiameter(view, sheet, graphics, dimensions, model, at);
                break;

            default:
                throw ToolException.InvalidArgument($"Unknown dimension kind '{kind}'.", "Use length, distance, diameter, radius, angle, coordinate, angular_coordinate, chamfer or symmetric_diameter.");
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

    /// <summary>
    /// Parts list of an assembly view (with balloons if requested). With <paramref name="family"/> or
    /// <paramref name="members"/>, a family of assemblies parts list: one quantity column per chosen member of the assembly
    /// family. Verified: a family with members NoPin and WithPin gives the columns "Quantity (WithPin)" and "Quantity (NoPin)"
    /// once both are chosen and the list updated (block 1 and 1, pin 1 and 0); one member alone gives its column and rows.
    /// </summary>
    public static object PartsList(SeContext context, string? viewHandle, double[]? position, bool balloons, string? style,
        bool family = false, string[]? members = null, string[]? columns = null)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (LinkOf(view) is not { } link || link.ModelType != ModelLinkTypeConstants.igAssemblyLink)
        {
            throw new ToolException("wrong_view", "A parts list needs a view of an assembly.", "Pass a drawing view created from an .asm file.");
        }

        string[]? properties = columns is null ? null : [.. columns.Select(c => c?.Trim() ?? "")];
        if (properties is not null && (properties.Length == 0 || properties.Any(p => p.Length == 0)))
        {
            throw ToolException.InvalidArgument("'columns' needs one or more property names, none empty.", "E.g. [\"Quantity\", \"File Name (no extension)\", \"Material\"].");
        }

        family |= members is { Length: > 0 };
        PartsList list;
        try
        {
            list = family
                ? draft.PartsLists.AddFamilyOfAssemblyPartsList(view, 0, style ?? "", balloons ? 1 : 0, 1)
                : draft.PartsLists.Add(view, style ?? "", balloons ? 1 : 0, 1);
        }
        catch (NullReferenceException ex) when (family)
        {
            // Verified: a view of an assembly without a family gives E_POINTER, which .NET raises as NullReferenceException.
            throw new ToolException("not_family", "The assembly of the view has no family members.",
                "Make a family of assemblies (se_assembly_family create), save it and place a view of a member; or leave out 'family'.", ex);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("parts_list_failed", $"Solid Edge could not create the parts list: {ex.Message.Trim()}",
                style is null ? "Pass 'style' with the name of a saved parts list setting (e.g. 'Normal')." : "Check the saved setting name in 'style'.", ex);
        }

        // Verified with Solid Edge 2026: a saved setting that does not exist gives no error, the list takes the default one.
        if (!string.IsNullOrWhiteSpace(style) && SavedSettings(list) is { } saved && !saved.Contains(style.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            list.Delete();
            throw ToolException.NotFound($"The drawing has no saved parts list setting '{style.Trim()}'.",
                $"Use one of: {string.Join(", ", saved)}; or leave out 'style' for the default one.");
        }

        string[] chosen = family ? ChooseFamilyMembers(view, list, members) : [];
        if (properties is not null)
        {
            ShowColumns(list, properties, chosen);
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        list.AnchorPoint = anchor;
        list.SetOrigin(x, y);
        return DescribePartsList(context, draft, list);
    }

    /// <summary>Chooses the members of a family of assemblies parts list (default all), or deletes the list if that fails.</summary>
    private static string[] ChooseFamilyMembers(DrawingView view, PartsList list, string[]? members)
    {
        try
        {
            Array all = Array.Empty<object>();
            int count = 0;
            if (list.IsFamilyOfAssemblyPartsList())
            {
                list.GetAllFamilyOfAssemblyPartsListMembers(view, out count, ref all);
            }

            string[] names = [.. all.Cast<object?>().Take(count).Select(o => o?.ToString() ?? "")];
            if (names.Length == 0)
            {
                throw new ToolException("not_family", "The assembly of the view has no family members.",
                    "Make a family of assemblies (se_assembly_family create), save it and place a view of a member; or leave out 'family'.");
            }

            string[] chosen = members is { Length: > 0 }
                ? [.. members.Select(m => names.FirstOrDefault(n => string.Equals(n, m?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw ToolException.InvalidArgument($"The assembly family has no member '{m}'.", $"Use one of: {string.Join(", ", names)}."))
                    .Distinct(StringComparer.OrdinalIgnoreCase)]
                : names;
            Array wanted = chosen.Cast<object>().ToArray();
            int number = chosen.Length;
            list.SetSelectedFamilyOfAssemblyPartsListMembers(ref number, ref wanted);
            list.Update();
            Array selected = Array.Empty<object>();
            list.GetSelectedFamilyOfAssemblyPartsListMembers(out int got, ref selected);
            // The array keeps its old length: only the first 'got' entries are the members chosen.
            string[] kept = [.. selected.Cast<object?>().Take(got).Select(o => o?.ToString() ?? "")];
            if (!kept.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(chosen))
            {
                throw new ToolException("parts_list_failed", $"Solid Edge kept the members {string.Join(", ", kept)} instead of {string.Join(", ", chosen)}.");
            }

            return chosen;
        }
        catch (Exception ex) when (ex is ToolException || ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex))
        {
            list.Delete();
            throw ex is ToolException ? ex : new ToolException("parts_list_failed",
                $"Solid Edge could not choose the family members: {ComErrors.Unwrap(ex).Message.Trim()}", "Check the member names (se_assembly_family list).", ex);
        }
    }

    /// <summary>
    /// Shows the given properties as the columns of a parts list, in place of the columns of its saved setting (each
    /// installation has its own), or deletes the list if that fails. A name is a property of the document of each row as
    /// Solid Edge names it (e.g. "Quantity", "File Name (no extension)", "Material"; a custom property followed by "/CP"), or
    /// a property text as given ("%{…}"). Verified with Solid Edge 2026: saved settings write "%{Quantity|G}" and
    /// "%{Name/CP|G}"; without "|G" a column shows the property of the drawing instead. The quantity column of each member of
    /// a family ("Quantity (WithPin)") stays, after the given ones: verified, without them the list has no rows.
    /// </summary>
    private static void ShowColumns(PartsList list, string[] properties, string[] familyMembers)
    {
        try
        {
            TableColumns all = list.Columns;
            int saved = all.Count;
            bool[] quantity = [.. Enumerable.Range(1, saved).Select(i => familyMembers.Any(m => (all.Item(i).Header ?? "").EndsWith($"({m})", StringComparison.OrdinalIgnoreCase)))];
            int firstQuantity = Array.IndexOf(quantity, true);
            int before = firstQuantity > 0 ? firstQuantity : saved;
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                bool text = property.StartsWith("%{", StringComparison.Ordinal);
                TableColumn column = all.Add(before + i, true);
                column.PropertyText = text ? property : $"%{{{property}|G}}";
                column.Header = text ? property.TrimStart('%', '{').Split('|', '}')[0].Replace("/CP", "", StringComparison.Ordinal) : property;
            }

            for (int i = saved; i >= 1; i--)
            {
                if (!quantity[i - 1])
                {
                    all.Item(i > before ? i + properties.Length : i).Delete();
                }
            }

            list.Update();
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            list.Delete();
            throw new ToolException("parts_list_failed", $"Solid Edge could not set the columns of the parts list: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Give property names as Solid Edge shows them in its property text dialog.", ex);
        }
    }

    /// <summary>
    /// Family of parts table of a view of a part with a family (se_part_family): one row per member with the value of each
    /// family variable (verified: members Thick and Thin of a block whose depth is the family variable give two rows,
    /// "Thick | 30.000 mm"). A part without members gives an empty table, which is deleted.
    /// </summary>
    public static object FamilyTable(SeContext context, string? viewHandle, double[]? position)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        FOPTable table;
        try
        {
            table = draft.FOPTables.Add(view);
        }
        catch (NullReferenceException ex)
        {
            // Verified: a view of a part without a family of parts gives E_POINTER, which .NET raises as NullReferenceException.
            throw new ToolException("family_table_failed", "The part of the view has no family of parts.",
                "Give a view of a part with family members (se_part_family add_member, then save the part).", ex);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("family_table_failed", $"Solid Edge could not create the family of parts table: {ex.Message.Trim()}",
                "Give a view of a part that has a family of parts (se_part_family).", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("family_table_failed", "The family of parts table came out empty: the part of the view has no family members.",
                "Add members with se_part_family add_member and save the part, then make the table.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        return TableResult(context, draft, table, "family_table", table.Columns, table.Rows.Count, anchor, x, y);
    }

    /// <summary>
    /// Block table of a sheet: one row per block placed on it (item, name, quantity…), as the saved setting lists them
    /// (verified: a block placed twice gives one row "Frame"). A sheet without blocks gives an empty table, which is deleted.
    /// </summary>
    public static object BlockTable(SeContext context, string? documentHandle, string? sheetRef, double[]? position, string? style)
    {
        DraftDocument draft = DraftFor(context, documentHandle, sheetRef);
        Sheet sheet = ResolveSheet(context, draft, sheetRef);
        if (!Late.SameObject(sheet, draft.ActiveSheet))
        {
            sheet.Activate();
        }

        BlockTable? table;
        try
        {
            // Verified: with no block on the sheet Solid Edge returns nothing; an unknown saved setting falls back to the default.
            table = draft.BlockTables.AddByActiveSheet(style ?? "", 0);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("block_table_failed", $"Solid Edge could not create the block table: {ex.Message.Trim()}",
                style is null ? "Place blocks on the sheet first (se_drawing place_block)." : "Check the saved setting name in 'style'.", ex);
        }

        if (table is null || table.Rows.Count == 0)
        {
            table?.Delete();
            throw new ToolException("block_table_failed", "The block table came out empty: no block is placed on the sheet.",
                "Place blocks with se_drawing place_block, then make the table.");
        }

        (TableAnchorPoint anchor, double x, double y) = position is null
            ? (TableAnchorPoint.igLowerRight, AboveTitleBlock(sheet).X, AboveTitleBlock(sheet).Y)
            : (TableAnchorPoint.igUpperLeft, Geo.Point2(position, "position").X, Geo.Point2(position, "position").Y);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        return TableResult(context, draft, table, "block_table", table.Columns, table.Rows.Count, anchor, x, y);
    }

    /// <summary>
    /// The tables already on a drawing (parts lists, bend, tolerance, family of parts, block and hole tables) with their shown columns
    /// and cells, so that a drawing opened from a file can be read.
    /// </summary>
    public static object Tables(SeContext context, string? documentHandle)
    {
        DraftDocument draft = DraftFor(context, documentHandle, null);
        var tables = new List<object>();
        void Add(string kind, object table, TableColumns columns, int count)
        {
            (List<string?> headers, List<string?[]> rows) = TableContent(context, table, columns, count);
            double[]? origin = null;
            try
            {
                object?[] args = Late.CallByRef(table, "GetOrigin", [0.0, 0.0], 0, 1);
                origin = [Geo.Mm(Convert.ToDouble(args[0], System.Globalization.CultureInfo.InvariantCulture)), Geo.Mm(Convert.ToDouble(args[1], System.Globalization.CultureInfo.InvariantCulture))];
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                // Bend and family of parts tables have no GetOrigin.
            }

            tables.Add(new { handle = context.Register(table, kind, Document(draft)), kind, origin, columns = headers, count, rows });
        }

        foreach (PartsList list in Late.Items(draft.PartsLists).OfType<PartsList>())
        {
            Add("parts_list", list, list.Columns, list.Rows.Count);
        }

        foreach (DraftBendTable table in Late.Items(draft.DraftBendTables).OfType<DraftBendTable>())
        {
            Add("bend_table", table, table.Columns, table.Rows.Count);
        }

        foreach (ToleranceTable table in Late.Items(draft.ToleranceTables).OfType<ToleranceTable>())
        {
            Add("tolerance_table", table, table.Columns, table.Rows.Count);
        }

        foreach (FOPTable table in Late.Items(draft.FOPTables).OfType<FOPTable>())
        {
            Add("family_table", table, table.Columns, table.Rows.Count);
        }

        foreach (BlockTable table in Late.Items(draft.BlockTables).OfType<BlockTable>())
        {
            Add("block_table", table, table.Columns, table.Rows.Count);
        }

        foreach (HoleTable2 table in Late.Items(draft.HoleTables2).OfType<HoleTable2>())
        {
            Add("hole_table", table, table.Columns, table.Rows.Count);
        }

        return new { document = Document(draft).Name, count = tables.Count, tables };
    }

    private static object TableResult(SeContext context, DraftDocument draft, object table, string kind, TableColumns columns, int count,
        TableAnchorPoint anchor, double x, double y)
    {
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, columns, count);
        return new
        {
            handle = context.Register(table, kind, Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            columns = headers,
            count,
            rows,
        };
    }

    /// <summary>
    /// Tolerance table of a view: one row per dimension of the view with a fit (nominal, class, deviations), as the ISO saved
    /// setting lists them (verified: a 60 mm dimension with H7 gives one row 60 | H7). A view whose dimensions have no fit gives
    /// an empty table, which is deleted.
    /// </summary>
    public static object ToleranceTable(SeContext context, string? viewHandle, double[]? position, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        ToleranceTable table;
        try
        {
            table = draft.ToleranceTables.AddByDrawingView(view, style ?? "", 1);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("tolerance_table_failed", $"Solid Edge could not create the tolerance table: {ex.Message.Trim()}",
                style is null ? "Give a view with dimensions that have a fit (se_dimension tolerance fit)." : "Check the saved setting name in 'style' (e.g. ISO).", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("tolerance_table_failed", "The tolerance table came out empty: no dimension of the view has a fit.",
                "Give dimensions of the view a fit with se_dimension tolerance (fit, e.g. hole H7), then make the table.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, table.Columns, table.Rows.Count);
        return new
        {
            handle = context.Register(table, "tolerance_table", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            columns = headers,
            count = table.Rows.Count,
            rows,
        };
    }

    /// <summary>
    /// Bend table of a view of a sheet metal part (its flat pattern or the bent part; verified: both list the same bends): one
    /// row per bend (sequence, feature, radius, angle, direction, included angle), placed like a parts list. A view without
    /// bends gives an empty table, which is deleted.
    /// </summary>
    public static object BendTable(SeContext context, string? viewHandle, double[]? position, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (LinkOf(view) is not { } link || link.ModelType != ModelLinkTypeConstants.igPartLink)
        {
            throw new ToolException("wrong_view", "A bend table needs a view of a sheet metal part.", "Use the flat pattern view (se_drawing base_view flat=true) of the part.");
        }

        DraftBendTable table;
        try
        {
            table = draft.DraftBendTables.Add(view, style ?? "", 0, 1);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("bend_table_failed", $"Solid Edge could not create the bend table: {ex.Message.Trim()}",
                style is null ? "Give the flat pattern view (base_view flat) of a sheet metal part with bends." : "Check the saved setting name in 'style'.", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("bend_table_failed", "The bend table came out empty: the view shows no bends.",
                "Give the flat pattern view (base_view flat) of a sheet metal part with bends.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.SetOrigin(x, y);
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, table.Columns, table.Rows.Count);
        return new
        {
            handle = context.Register(table, "bend_table", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            columns = headers,
            count = table.Rows.Count,
            rows,
        };
    }

    /// <summary>
    /// Hole table of a view (HoleTables2.Add by user selection, verified in Solid Edge 2026): an origin at a vertex or circle
    /// centre of the view (AddNewXYoriginEx with its sheet reference and key point) and one row per circle (AddHole with the
    /// circle's reference and origin 1; origin 0 makes Solid Edge throw a server fault, and attaching the whole view gives
    /// E_UNEXPECTED). Each row gives the hole label, X and Y from the origin and the size: the top view of a plate with holes of
    /// 6.6 at (15, 15) and (85, 15) and 12 at (50, 40), with the origin at its corner (100, 0), lists 1.1 | -85 | 15 | 6.6,
    /// 1.2 | -15 | 15 | 6.6 and 1.3 | -50 | 40 | 12 (mm). The table stays after saving and reopening.
    /// </summary>
    public static object HoleTable(SeContext context, string? viewHandle, double[]? points, double[]? position, string? style)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = points is null ? [] : Geo.Points3(points, "points", 1);
        List<Graphic> graphics = Graphics(view);
        Pick origin;
        if (model.Count > 0)
        {
            origin = PickAt(view, graphics, model[0], false);
            if (!origin.KeyPoint)
            {
                throw ToolException.InvalidArgument("The origin of a hole table (the first point) must be a vertex or a circle centre of the view.",
                    "Give the model point of a corner or a hole centre (se_geometry lists edges with their ends and circles with their centres).");
            }
        }
        else
        {
            // As a person would place it: the lower left corner of the outline.
            (Graphic Graphic, (double X, double Y) Point)[] ends = [.. graphics.Where(g => g.Shape == Shape.Line).SelectMany(g => new[] { (g, g.Start), (g, g.End) })];
            if (ends.Length == 0)
            {
                throw new ToolException("hole_table_failed", "The view has no straight edges to put the origin of the hole table on.",
                    "Give the origin in 'points' (a vertex or a circle centre of the model).");
            }

            (Graphic corner, (double X, double Y) at) = ends.MinBy(e => e.Point.X + e.Point.Y);
            origin = new Pick(corner, true, ToSheet(view, at));
        }

        List<Graphic> shown = [.. graphics.Where(g => g.Shape == Shape.Circle)];
        bool stale = model.Count <= 1 && shown.Count > 0 && !view.IsUpToDate;
        List<Graphic>? holes = model.Count > 1 || stale ? null : HoleCircles(view, shown);
        List<Graphic> circles = model.Count > 1
            ? [.. model.Skip(1).Select(p => Nearest(view, graphics, ModelToView(view, p), p, g => g.Shape == Shape.Circle, "circle"))]
            : holes ?? shown;
        if (circles.Count == 0)
        {
            throw ToolException.NotFound(shown.Count == 0 ? "The view shows no circles (holes)." : $"None of the {shown.Count} circles of the view is on a hole (a concave cylindrical or conical face of the part along the view): they are the outline, bosses or edges of chamfers and rounds.",
                shown.Count == 0 ? "Use a view that looks along the holes (e.g. the top view of a plate)." : "Give the holes in 'points' (after the origin) if they should be listed.");
        }

        string? note = stale
            ? "The view is out of date with its model, so every circle it shows was listed; update it (se_drawing update) to list only the holes."
            : holes is not null && holes.Count < shown.Count
            ? $"{shown.Count - holes.Count} circle(s) of the view were left out: not on a hole (the outline, a boss, the edge of a chamfer or a round) or a second circle of a hole listed by its narrowest one (a counterbore, countersink or taper); give the holes in 'points' to choose them."
            : null;

        if (circles.Distinct().Count() != circles.Count)
        {
            throw ToolException.InvalidArgument("Two points pick the same circle of the view.", "Give one point per hole.");
        }

        HoleTable2 table;
        try
        {
            table = ((HoleTables2)draft.HoleTables2).Add(style ?? "", HoleTableType.igByUserSelection);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("hole_table_failed", $"Solid Edge could not create the hole table: {ex.Message.Trim()}",
                style is null ? null : "Check the saved setting name in 'style'.", ex);
        }

        try
        {
            table.AddNewXYoriginEx(origin.Graphic.Reference, KeyPointOf(view, origin));
            foreach (Graphic circle in circles)
            {
                table.AddHole(circle.Reference, 1);
            }
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex))
        {
            table.Delete();
            throw new ToolException("hole_table_failed", $"Solid Edge rejected the hole table: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Give a vertex or circle centre of the view as the origin and circles of the same view as the holes.", ex);
        }

        if (table.Rows.Count == 0)
        {
            table.Delete();
            throw new ToolException("hole_table_failed", "The hole table came out empty.", "Give circles of the view as the holes.");
        }

        (TableAnchorPoint anchor, double x, double y) = TableCorner(view, position);
        table.AnchorPoint = anchor;
        table.Pages.Item(1).SetOrigin(x, y);
        (List<string?> headers, List<string?[]> rows) = TableContent(context, table, table.Columns, table.Rows.Count);
        return new
        {
            handle = context.Register(table, "hole_table", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            anchor = anchor == TableAnchorPoint.igUpperLeft ? "upper_left" : "lower_right",
            holeOrigin = new[] { Geo.Mm(origin.Sheet.X), Geo.Mm(origin.Sheet.Y) },
            columns = headers,
            count = table.Rows.Count,
            rows,
            note,
        };
    }

    /// <summary>
    /// The circles of a part view that are holes, one per hole: centred on the axis of a cylindrical or conical face of the
    /// model whose outward normal points to the axis (the material is outside it), the axis along the view direction, with a
    /// radius the face has; of the circles of one hole, the narrowest. The outline of a round part, a boss and the edge of a
    /// chamfer are left out (verified: every circle of the top view of a disc of 80 with a 1 mm chamfer made rows of 78 and
    /// 80), and so are the second circles of a counterbore, a countersink and a tapered hole (verified: a counterbore of 11
    /// over 6.6 made two rows). Null when the model cannot be read this way (an assembly, a flat pattern view, a body that is
    /// a mesh): every circle is then kept.
    /// </summary>
    private static List<Graphic>? HoleCircles(DrawingView view, List<Graphic> circles)
    {
        if (circles.Count == 0 || circles.Any(c => Late.TryGet(c.Value, "ModelMember") is { } member && Late.TryGet(member, "IsFlat") is true)
            || LinkOf(view) is not { ModelType: ModelLinkTypeConstants.igPartLink } link
            || Late.TryGet(link, "ModelDocument") is not SolidEdgeDocument document
            || ModelDocument.TryGet(document)?.TryBodies() is not { Count: > 0 } bodies)
        {
            return null;
        }

        const double tolerance = 1e-5;
        var walls = new List<((double X, double Y) Centre, double Least, double Most)>();
        foreach ((_, SolidEdgeGeometry.Body body) in bodies)
        {
            IEnumerable<SolidEdgeGeometry.Face> faces = new[] { SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryCylinder, SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryCone }
                .SelectMany(query => Late.Items(body.Faces[query]).OfType<SolidEdgeGeometry.Face>());
            foreach (SolidEdgeGeometry.Face face in faces)
            {
                Array axisPoint = new double[3];
                Array axisDirection = new double[3];
                switch (face.Geometry)
                {
                    case SolidEdgeGeometry.Cylinder cylinder:
                        cylinder.GetCylinderData(ref axisPoint, ref axisDirection, out _);
                        break;
                    case SolidEdgeGeometry.Cone cone:
                        cone.GetConeData(ref axisPoint, ref axisDirection, out _, out _, out _);
                        break;
                    default:
                        continue;
                }

                double[] a = [.. Geo.Doubles(axisPoint)];
                double[] d = [.. Geo.Doubles(axisDirection)];
                (double X, double Y) centre = ModelToView(view, (a[0], a[1], a[2]));
                (double X, double Y) further = ModelToView(view, (a[0] + d[0], a[1] + d[1], a[2] + d[2]));
                if (Distance(centre, further) >= tolerance)
                {
                    continue;
                }

                // The radius at the middle and at the corners of the face's parameter range: a cone's two ends, a cylinder's one.
                Array min = new double[2];
                Array max = new double[2];
                face.GetParamRange(ref min, ref max);
                double[] lo = [.. Geo.Doubles(min)];
                double[] hi = [.. Geo.Doubles(max)];
                double Radial(double[] p, out double[] off)
                {
                    double along = (p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1] + (p[2] - a[2]) * d[2];
                    off = [p[0] - a[0] - along * d[0], p[1] - a[1] - along * d[1], p[2] - a[2] - along * d[2]];
                    return Math.Sqrt(off.Sum(x => x * x));
                }

                Array param = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2 };
                Array point = new double[3];
                Array normal = new double[3];
                face.GetPointAtParam(1, ref param, ref point);
                face.GetNormal(1, ref param, ref normal);
                double[] n = [.. Geo.Doubles(normal)];
                Radial([.. Geo.Doubles(point)], out double[] radial);
                if (Enumerable.Range(0, 3).Sum(i => n[i] * radial[i]) >= 0)
                {
                    continue;
                }

                var radii = new List<double>();
                double[][] corners = [[lo[0], lo[1]], [lo[0], hi[1]], [hi[0], lo[1]], [hi[0], hi[1]]];
                foreach (double[] corner in corners)
                {
                    Array at = corner;
                    face.GetPointAtParam(1, ref at, ref point);
                    radii.Add(Radial([.. Geo.Doubles(point)], out _));
                }

                walls.Add((centre, radii.Min(), radii.Max()));
            }
        }

        return [.. circles
            .Where(c => walls.Any(w => Distance(c.Centre, w.Centre) < tolerance && c.Radius > w.Least - tolerance && c.Radius < w.Most + tolerance))
            .GroupBy(c => walls.First(w => Distance(c.Centre, w.Centre) < tolerance).Centre)
            .Select(hole => hole.MinBy(c => c.Radius)!)];
    }

    /// <summary>Key point index of a picked vertex or centre for the Solid Edge calls that take an element and a key point.</summary>
    private static KeypointIndexConstants KeyPointOf(DrawingView view, Pick pick)
    {
        double Off((double X, double Y) p) => Distance(ToSheet(view, p), pick.Sheet);
        return pick.Graphic.Shape switch
        {
            Shape.Line => Off(pick.Graphic.Start) <= Off(pick.Graphic.End) ? KeypointIndexConstants.igLineStart : KeypointIndexConstants.igLineEnd,
            Shape.Circle => KeypointIndexConstants.igCircleCenter,
            _ => new[] { (KeypointIndexConstants.igArcCenter, Off(pick.Graphic.Centre)), (KeypointIndexConstants.igArcStart, Off(pick.Graphic.Start)),
                (KeypointIndexConstants.igArcEnd, Off(pick.Graphic.End)) }.MinBy(k => k.Item2).Item1,
        };
    }

    /// <summary>
    /// Corner of a table: its upper left corner at the position, or, as a person would place it (Solid Edge leaves it outside
    /// the frame), its lower right corner above the title block.
    /// </summary>
    private static (TableAnchorPoint Anchor, double X, double Y) TableCorner(DrawingView view, double[]? position)
    {
        if (position is not null)
        {
            (double x, double y) = Geo.Point2(position, "position");
            return (TableAnchorPoint.igUpperLeft, x, y);
        }

        (double right, double top) = AboveTitleBlock(SheetOf(view));
        return (TableAnchorPoint.igLowerRight, right, top);
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
        catch (COMException ex) when (!ComErrors.IsGone(ex))
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
        catch (COMException ex) when (!ComErrors.IsGone(ex))
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
        string handle = context.Register(sheet, "sheet", Document(draft));
        bool active = string.Equals(sheet.Name, activeName, StringComparison.Ordinal);
        if (IsBackground(sheet))
        {
            return new
            {
                handle,
                name = sheet.Name,
                kind = "background",
                active,
                size = SizeMm(sheet),
                paper = PaperName(sheet.SheetSetup.SheetSizeOption),
                objects = Late.Count(sheet.DrawingObjects),
                usedBy = UsersOf(draft, sheet),
            };
        }

        return new
        {
            handle,
            name = sheet.Name,
            number = sheet.Number,
            active,
            size = SizeMm(sheet),
            paper = PaperName(sheet.SheetSetup.SheetSizeOption),
            background = BackgroundOf(sheet)?.Name,
            backgroundShown = sheet.BackgroundVisible,
            views = sheet.DrawingViews.Count,
        };
    }

    private static object Result(SeContext context, DraftDocument draft, DrawingView view, string? note, string? projection = null) =>
        new { view = DescribeView(context, draft, view), projection, note = SheetNote(view) is { } fit ? (note is null ? fit : $"{note} {fit}") : note };

    private static string? SheetNote(DrawingView view)
    {
        if (view.Parent is Sheet sheet)
        {
            double width = sheet.SheetSetup.SheetWidth, height = sheet.SheetSetup.SheetHeight;
            if (!BoxOf(view).Within(width, height))
            {
                return $"The view does not fit inside the {Geo.Mm(width)} x {Geo.Mm(height)} mm sheet: move it (move_view), "
                    + "reduce its scale (scale_view) or use a larger sheet (setup_sheet).";
            }
        }

        return null;
    }

    private static object DescribeView(SeContext context, DraftDocument draft, DrawingView view)
    {
        Box box = BoxOf(view);
        string? model = LinkOf(view) is { } link ? System.IO.Path.GetFileName(link.FileName) : null;
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

    private static object DescribePartsList(SeContext context, DraftDocument draft, PartsList list)
    {
        list.GetOrigin(out double x, out double y);
        int count = list.Rows.Count;
        (List<string?> headers, List<string?[]> rows) = TableContent(context, list, list.Columns, count);
        return new
        {
            handle = context.Register(list, "parts_list", Document(draft)),
            origin = new[] { Geo.Mm(x), Geo.Mm(y) },
            columns = headers,
            count,
            rows,
            savedSettings = SavedSettings(list),
        };
    }

    /// <summary>
    /// Names of the saved settings of the parts list ('style' of parts_list), which each template and installation defines:
    /// the columns and their properties come from the one used. Null when Solid Edge gives none.
    /// </summary>
    private static string[]? SavedSettings(PartsList list)
    {
        try
        {
            Array names = Array.Empty<object>();
            list.GetListOfSavedSettings(out int count, ref names);
            string[] found = [.. names.Cast<object?>().Take(count).Select(o => o?.ToString() ?? "").Where(n => n.Length > 0)];
            return found.Length > 0 ? found : null;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Headers and every row of the shown columns of a table of a drawing: a parts list, a hole, bend, tolerance, family or
    /// block table of se_drawing, or a table of se_sheet_content.
    /// </summary>
    internal static (List<string?> Headers, List<string?[]> Rows) TableCells(SeContext context, string handle)
    {
        object item = context.Handles.Resolve(handle, "table");
        if (item is not (SolidEdgeDraft.PartsList or DraftBendTable or SolidEdgeDraft.ToleranceTable or FOPTable or SolidEdgeDraft.BlockTable or HoleTable2 or Table)
            || Late.Get(item, "Columns") is not TableColumns columns || Late.Get(item, "Rows") is not { } rows)
        {
            throw ToolException.InvalidArgument($"'{handle}' is not a table of a drawing.", "List the tables with se_drawing tables or se_sheet_content list.");
        }

        return TableContent(context, item, columns, Late.Count(rows), int.MaxValue);
    }

    /// <summary>Headers and cell values of the shown columns of a table (parts list, bend table), the first rows up to the list limit or 'max'.</summary>
    private static (List<string?> Headers, List<string?[]> Rows) TableContent(SeContext context, object table, TableColumns columns, int count, int? max = null)
    {
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

        var rows = new List<string?[]>();
        for (int r = 1; r <= count && r <= (max ?? context.MaxListItems); r++)
        {
            int row = r;
            rows.Add(shown.Select(c => (Late.TryGet(table, "Cell", row, c) as TableCell)?.value).ToArray());
        }

        return (headers, rows);
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
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Model shown by a view, or null for a view without one. Verified in Solid Edge 2026: a view turned into 2D (Drop)
    /// rejects ModelLink with E_INVALIDARG and ViewOrientation with E_POINTER.
    /// </summary>
    private static ModelLink? LinkOf(DrawingView view)
    {
        try
        {
            return view.ModelLink as ModelLink;
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
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

    /// <summary>Sheet given by handle or name (default: the active sheet of the drawing).</summary>
    internal static Sheet SheetFor(SeContext context, string? documentHandle, string? sheet) =>
        ResolveSheet(context, DraftFor(context, documentHandle, sheet), sheet);

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
            ?? BackgroundSheets(draft).FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase))
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

        if (WorkingSheets(draft).Concat(BackgroundSheets(draft)).Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException("name_in_use", $"There is already a sheet or background sheet named '{name}'.", "Choose another name.");
        }
    }

    /// <summary>Model file: saved path (allowed folders) or that of an open, already saved document.</summary>
    private static string ModelFile(SeContext context, string? path, string? modelHandle, out SolidEdgeDocument? unsaved)
    {
        unsaved = null;
        string file;
        if (!string.IsNullOrWhiteSpace(modelHandle))
        {
            SolidEdgeDocument model = context.GetDocument(modelHandle);
            file = DocumentService.PathOf(model) ?? throw new ToolException("never_saved",
                $"'{model.Name}' has never been saved; a drawing shows a model file.", "Save it first (se_documents save_as).");
            unsaved = model.Dirty ? model : null;
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

        // A model given by its path may be open too: the view shows the open document.
        if (unsaved is null && string.IsNullOrWhiteSpace(modelHandle))
        {
            Documents documents = context.Application.Documents;
            for (int i = 1; i <= documents.Count; i++)
            {
                if (documents.Item(i) is SolidEdgeDocument open && open.Dirty
                    && string.Equals(DocumentService.PathOf(open), file, StringComparison.OrdinalIgnoreCase))
                {
                    unsaved = open;
                }
            }
        }

        return file;
    }

    /// <summary>
    /// The link to a model file, or to one member of an assembly with alternate positions or a family ("file.asm!Member").
    /// Verified: a drawing links such an assembly through its first member, and a link names it "file.asm!Member".
    /// </summary>
    private static ModelLink LinkTo(DraftDocument draft, string file, string? member = null)
    {
        foreach (ModelLink link in Late.Items(draft.ModelLinks).OfType<ModelLink>())
        {
            string? name = Late.TryGetString(link, "FileName");
            if (string.Equals(DocumentService.FileOf(name), file, StringComparison.OrdinalIgnoreCase)
                && (member is null || string.Equals(DocumentService.MemberOf(name), member, StringComparison.OrdinalIgnoreCase)))
            {
                return link;
            }
        }

        if (member is null)
        {
            return draft.ModelLinks.Add(file);
        }

        ModelLink added;
        try
        {
            added = draft.ModelLinks.Add($"{file}!{member}");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80030002))
        {
            // Verified: an unknown member gives STG_E_FILENOTFOUND.
            throw ToolException.NotFound($"'{System.IO.Path.GetFileName(file)}' has no member '{member}'.",
                "List the members with se_assembly_family list (on the open assembly).");
        }

        if (!added.IsAssemblyFamilyMember || !string.Equals(added.AssemblyFamilyMemberName, member, StringComparison.OrdinalIgnoreCase))
        {
            added.Delete();
            throw ToolException.NotFound($"'{System.IO.Path.GetFileName(file)}' has no member '{member}'.",
                "List the members with se_assembly_family list (on the open assembly).");
        }

        return added;
    }

    /// <summary>The alternate positions a view of an assembly with alternate positions can show, and those it shows.</summary>
    public static object Alternates(SeContext context, string? viewHandle)
    {
        DrawingView view = ResolveView(context, viewHandle);
        return new { view = viewHandle, shown = AlternateMembers(view, shown: true), candidates = AlternateMembers(view, shown: false) };
    }

    /// <summary>
    /// Shows (or stops showing) another alternate position of the assembly in a view, drawn as a phantom. Verified:
    /// GetAlternatePositionMemberCandidates lists every member of the assembly; the view grows to hold the other position.
    /// </summary>
    public static object SetAlternate(SeContext context, string? viewHandle, string? member, bool add)
    {
        DrawingView view = ResolveView(context, viewHandle);
        string name = string.IsNullOrWhiteSpace(member) ? throw ToolException.InvalidArgument("'member' is required: an alternate position from 'alternates'.") : member.Trim();
        string[] candidates = AlternateMembers(view, shown: false);
        string match = candidates.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"The view has no alternate position '{name}'.",
                candidates.Length == 0 ? "The view must show an assembly with alternate positions (se_assembly_family)." : $"Alternate positions: {string.Join(", ", candidates)}.");
        bool shown = AlternateMembers(view, shown: true).Contains(match, StringComparer.Ordinal);
        if (add && !shown)
        {
            view.AddAlternateMember(match);
        }
        else if (!add && shown)
        {
            view.RemoveAlternateMember(match);
        }

        view.Update();
        if (AlternateMembers(view, shown: true).Contains(match, StringComparer.Ordinal) != add)
        {
            throw new ToolException("view_failed", $"Solid Edge did not {(add ? "add" : "remove")} the alternate position '{match}'.");
        }

        DraftDocument draft = DraftFor(context, null, viewHandle);
        return new { view = DescribeView(context, draft, view), shown = AlternateMembers(view, shown: true) };
    }

    /// <summary>
    /// Verified: GetAlternateMembers needs a sized array and fills it from the second slot, so the positions shown are read from
    /// the model members of the view instead (IsAlternatePosition, AlternatePositionMemberName).
    /// </summary>
    private static string[] AlternateMembers(DrawingView view, bool shown)
    {
        if (shown)
        {
            return [.. Late.Items(view.ModelMembers).OfType<ModelMember>().Where(m => m.IsAlternatePosition).Select(m => m.AlternatePositionMemberName)];
        }

        Array members = Array.Empty<object>();
        int count;
        try
        {
            view.GetAlternatePositionMemberCandidates(out count, ref members);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }

        return count == 0 || members is null ? [] : [.. members.Cast<object?>().Select(m => m?.ToString() ?? "").Where(m => m.Length > 0)];
    }
    private static void ApplySetup(DraftDocument draft, Sheet sheet, string? paper, bool portrait, double? width, double? height, string? background,
        bool? showBackground)
    {
        bool isBackground = IsBackground(sheet);
        if (isBackground && (background is not null || showBackground is not null))
        {
            throw ToolException.InvalidArgument($"'{sheet.Name}' is a background sheet: it has no background of its own.",
                "Use 'background' and 'showBackground' with a working sheet.");
        }

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
        else if (resized && !isBackground)
        {
            // The background (frame and title block) of the same paper size, if the template has one.
            back = BackgroundSheets(draft).FirstOrDefault(b =>
                Math.Abs(b.SheetSetup.SheetWidth - setup.SheetWidth) < 5e-4 && Math.Abs(b.SheetSetup.SheetHeight - setup.SheetHeight) < 5e-4);
        }

        if (back is not null)
        {
            sheet.Background = back;
        }

        if (showBackground is { } shown)
        {
            sheet.BackgroundVisible = shown;
            if (sheet.BackgroundVisible != shown)
            {
                throw new ToolException("setup_failed", $"Solid Edge kept the background of '{sheet.Name}' {(shown ? "hidden" : "shown")}.");
            }
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
        catch (COMException ex) when (!ComErrors.IsGone(ex))
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

    private static DrawingView Create(Func<DrawingView> create, string what, string hint)
    {
        try
        {
            return create() ?? throw new ToolException("view_failed", $"Solid Edge did not create the {what}.", hint);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
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
            // If it cannot be deleted, it remains: the error being reported is the original one.
        }
    }

    /// <summary>
    /// The profile of a cutting plane is in sheet coordinates and extends beyond the source view to cut the whole part;
    /// left of the line unless flipped.
    /// </summary>
    private static CuttingPlane DrawCuttingPlane(DraftDocument draft, DrawingView source, double[]? line, bool flip)
    {
        ((double X, double Y) a, (double X, double Y) b) = SheetLine(source, line);
        Box from = BoxOf(source);
        double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
        double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
        double reach = Math.Max(0.55 * Math.Sqrt(from.Width * from.Width + from.Height * from.Height)
            + Math.Sqrt((mx - from.CentreX) * (mx - from.CentreX) + (my - from.CentreY) * (my - from.CentreY)), length / 2);

        CuttingPlane plane = source.CuttingPlanes.Add();
        try
        {
            plane.Profile.Lines2d.AddBy2Points(mx - ux * reach, my - uy * reach, mx + ux * reach, my + uy * reach);
            plane.Profile.EndEdit();
            plane.ViewSide = flip ? SectionViewProfileSide.igRightProfileSide : SectionViewProfileSide.igLeftProfileSide;
            return plane;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            TryDeleteCuttingPlane(draft, plane);
            throw new ToolException("view_failed", $"Solid Edge could not draw the cutting plane: {ex.Message.Trim()}",
                "Check that the cutting line crosses the part in the source view.", ex);
        }
    }

    private static IEnumerable<CuttingPlane> CuttingPlanesOf(DrawingView view)
    {
        CuttingPlanes planes = view.CuttingPlanes;
        for (int i = 1; i <= planes.Count; i++)
        {
            yield return planes.Item(i);
        }
    }

    /// <summary>The cutting plane a handle names, or null when it names something else (a drawing view).</summary>
    private static CuttingPlane? CuttingPlaneOf(SeContext context, string? handle) =>
        string.IsNullOrWhiteSpace(handle) ? null : context.Handles.Resolve(handle, "view") as CuttingPlane;

    private static object DescribeCuttingPlane(SeContext context, DraftDocument draft, CuttingPlane plane)
    {
        plane.GetFoldLineWithViewDirection(out double x1, out double y1, out double x2, out double y2, out double lookX, out double lookY);
        DrawingView? section = plane.SectionView;
        return new
        {
            handle = context.Register(plane, "cutting_plane", Document(draft)),
            caption = plane.Caption,
            view = context.Register(plane.SourceDrawingView, "drawing_view", Document(draft)),
            line = new[] { Geo.Mm(x1), Geo.Mm(y1), Geo.Mm(x2), Geo.Mm(y2) },
            looking = new[] { Geo.R(lookX), Geo.R(lookY) },
            sectionView = section is null ? null : context.Register(section, "drawing_view", Document(draft)),
        };
    }

    /// <summary>A cutting plane has no Delete (verified): it is deleted through the selection, with its section view.</summary>
    private static void TryDeleteCuttingPlane(DraftDocument draft, CuttingPlane plane)
    {
        try
        {
            DeleteBySelection(draft, plane);
        }
        catch (Exception ex) when (ex is ToolException || (Late.IsMemberFailure(ex) && !(ex is COMException com && ComErrors.IsGone(com))))
        {
            // If it cannot be deleted, the empty cutting plane remains: it does not affect the model.
        }
    }
}
