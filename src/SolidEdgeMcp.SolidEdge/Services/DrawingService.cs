// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Dimension = SolidEdgeFrameworkSupport.Dimension;

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

    private static readonly Dictionary<DrawingOrientation, ViewOrientationConstants> Orientations = new()
    {
        [DrawingOrientation.Front] = ViewOrientationConstants.igFrontView,
        [DrawingOrientation.Back] = ViewOrientationConstants.igBackView,
        [DrawingOrientation.Top] = ViewOrientationConstants.igTopView,
        [DrawingOrientation.Bottom] = ViewOrientationConstants.igBottomView,
        [DrawingOrientation.Left] = ViewOrientationConstants.igLeftView,
        [DrawingOrientation.Right] = ViewOrientationConstants.igRightView,
        [DrawingOrientation.TopFrontRight] = ViewOrientationConstants.igTopFrontRightView,
        [DrawingOrientation.TopFrontLeft] = ViewOrientationConstants.igTopFrontLeftView,
        [DrawingOrientation.TopBackRight] = ViewOrientationConstants.igTopBackRightView,
        [DrawingOrientation.TopBackLeft] = ViewOrientationConstants.igTopBackLeftView,
        [DrawingOrientation.BottomFrontRight] = ViewOrientationConstants.igBottomFrontRightView,
        [DrawingOrientation.BottomFrontLeft] = ViewOrientationConstants.igBottomFrontLeftView,
        [DrawingOrientation.BottomBackRight] = ViewOrientationConstants.igBottomBackRightView,
        [DrawingOrientation.BottomBackLeft] = ViewOrientationConstants.igBottomBackLeftView,
    };

    private static readonly Dictionary<FoldDirection, (FoldTypeConstants Fold, int X, int Y)> Folds = new()
    {
        [FoldDirection.Up] = (FoldTypeConstants.igFoldUp, 0, 1),
        [FoldDirection.Down] = (FoldTypeConstants.igFoldDown, 0, -1),
        [FoldDirection.Right] = (FoldTypeConstants.igFoldRight, 1, 0),
        [FoldDirection.Left] = (FoldTypeConstants.igFoldLeft, -1, 0),
        [FoldDirection.UpRight] = (FoldTypeConstants.igFoldUpRight, 1, 1),
        [FoldDirection.UpLeft] = (FoldTypeConstants.igFoldUpLeft, -1, 1),
        [FoldDirection.DownRight] = (FoldTypeConstants.igFoldDownRight, 1, -1),
        [FoldDirection.DownLeft] = (FoldTypeConstants.igFoldDownLeft, -1, -1),
    };

    private static readonly Dictionary<PaperSize, (PaperSizeConstants Wide, PaperSizeConstants Tall)> Papers = new()
    {
        [PaperSize.A4] = (PaperSizeConstants.igIsoA4Wide, PaperSizeConstants.igIsoA4Tall),
        [PaperSize.A3] = (PaperSizeConstants.igIsoA3Wide, PaperSizeConstants.igIsoA3Tall),
        [PaperSize.A2] = (PaperSizeConstants.igIsoA2Wide, PaperSizeConstants.igIsoA2Tall),
        [PaperSize.A1] = (PaperSizeConstants.igIsoA1Wide, PaperSizeConstants.igIsoA1Tall),
        [PaperSize.A0] = (PaperSizeConstants.igIsoA0Wide, PaperSizeConstants.igIsoA0Tall),
        [PaperSize.AnsiA] = (PaperSizeConstants.igAnsiAWide, PaperSizeConstants.igAnsiATall),
        [PaperSize.AnsiB] = (PaperSizeConstants.igAnsiBWide, PaperSizeConstants.igAnsiBTall),
        [PaperSize.AnsiC] = (PaperSizeConstants.igAnsiCWide, PaperSizeConstants.igAnsiCTall),
        [PaperSize.AnsiD] = (PaperSizeConstants.igAnsiDWide, PaperSizeConstants.igAnsiDTall),
        [PaperSize.AnsiE] = (PaperSizeConstants.igAnsiEWide, PaperSizeConstants.igAnsiETall),
    };

    // Sheets

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

    public static object AddSheet(SeContext context, string? documentHandle, string? name, PaperSize? paper, bool portrait,
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
    public static object SetupSheet(SeContext context, string? documentHandle, string? sheetRef, string? name, PaperSize? paper, bool portrait,
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

    // Views

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
        DrawingOrientation? orientation, double? scale, double[]? position, bool flat, string? configuration = null, string? modelView = null, string? member = null)
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

        DrawingOrientation orientationName = orientation ?? (flat ? DrawingOrientation.Top : DrawingOrientation.Front);
        ViewOrientationConstants orient = Orientations[orientationName];

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
                    Late.TryDelete(configured);
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
    private static string? StoredOrientation(DrawingView view, ViewOrientationConstants wanted, DrawingOrientation wantedName, string configuration)
    {
        view.ViewOrientation(out _, out _, out _, out _, out _, out _, out ViewOrientationConstants shown);
        if (shown == wanted)
        {
            return null;
        }

        string name = Orientations.Where(pair => pair.Value == shown).Select(pair => ArgumentNames.Snake(pair.Key)).FirstOrDefault()
            ?? string.Concat(shown.ToString().Replace("ig", "", StringComparison.Ordinal).Replace("View", "", StringComparison.Ordinal)
                .Select((ch, i) => i > 0 && char.IsUpper(ch) ? $" {char.ToLowerInvariant(ch)}" : char.ToLowerInvariant(ch).ToString()));
        return $"The view keeps the orientation stored with the configuration '{configuration}' ({name}), not '{ArgumentNames.Snake(wantedName)}': an explode configuration keeps the orientation the assembly window had when it was exploded. To change it, explode the assembly again with se_assembly_config explode and 'orientation', save it and make the view in a drawing without a view of that configuration (a drawing reads it once).";
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
    public static object ProjectedView(SeContext context, string? viewHandle, FoldDirection direction, double[]? position)
    {
        DrawingView source = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        var fold = Folds[direction];

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
        if (section is not null && context.Handles.TryGetId(section, out string? sectionHandle))
        {
            context.Handles.Remove(sectionHandle, "its cutting plane was deleted with it");
        }

        return new { deleted = $"cutting plane {caption}", sectionView = sectionName, count = source.CuttingPlanes.Count };
    }

    // Dimensions retrieved, view activation and updating

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

    // Description

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

    private static string? OrientationOf(DrawingView view)
    {
        try
        {
            view.ViewOrientation(out _, out _, out _, out _, out _, out _, out ViewOrientationConstants orientation);
            return Orientations.Where(pair => pair.Value == orientation).Select(pair => ArgumentNames.Snake(pair.Key)).FirstOrDefault() ?? "custom";
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
        foreach ((PaperSize size, (PaperSizeConstants wide, PaperSizeConstants tall)) in Papers)
        {
            if (option == wide)
            {
                return ArgumentNames.Snake(size);
            }

            if (option == tall)
            {
                return ArgumentNames.Snake(size) + "_portrait";
            }
        }

        return option == PaperSizeConstants.igCustomSheetSize ? "custom" : "other";
    }

    // Resolution

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
            for (int i = 1, count = documents.Count; i <= count; i++)
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
        catch (COMException ex) when (ex.HResult == ComErrors.StgEFileNotFound)
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
    private static void ApplySetup(DraftDocument draft, Sheet sheet, PaperSize? paper, bool portrait, double? width, double? height, string? background,
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
        if (paper is { } size)
        {
            var option = Papers[size];
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

    // Sheet geometry

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
        catch (Exception ex) when (ex is ToolException || (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex)))
        {
            // If it cannot be deleted, the empty cutting plane remains: it does not affect the model.
        }
    }
}
