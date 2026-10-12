using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// 3D section views of parts, sheet metal and assemblies (the Section command of the View tab): a sketch profile is pushed
/// along the normal of its plane and the model is shown cut by it; the model itself does not change. Verified in Solid Edge
/// 2026 (SectionViews.Add with a profile; AddByPlane and AddByPlaneEx always give E_FAIL), seen from above with the image
/// centre on the model:
/// - the profile side is the part that stays: inside or outside of a closed profile; for an open profile Solid Edge's left
///   (igLeftProfileSide) keeps the part to the right of the line's drawing direction as the sketch frame shows it (x right,
///   y up), and its right keeps the left;
/// - the cut acts only within the extent, along the normal of the sketch plane: Solid Edge's left extents go against the
///   sketch frame's normal and its right extents along it; a symmetric extent is the total width (6 mm: 3 to each side);
/// - Delete also deletes the profile and every reference plane of the document that nothing uses (no sketch or feature on
///   it), and Undo cannot bring them back (no transaction is recorded);
/// - a new section view takes the display mode and the cutting plane visibility last set in its document (not the opacity);
///   a new document starts with cut faces and cut bodies and the plane hidden;
/// - PlaneExtentType gives E_INVALIDARG for these section views.
/// </summary>
public static class SectionViewService
{
    private static readonly Dictionary<string, PMISectionDisplayModeConstants> Displays = new(StringComparer.Ordinal)
    {
        ["only_cut_faces"] = PMISectionDisplayModeConstants.sePMISectionDisplayShowOnlyCutFaces,
        ["cut_faces_and_cut_bodies"] = PMISectionDisplayModeConstants.sePMISectionDisplayShowCutFacesAndCutBodies,
        ["cut_faces_with_original_bodies"] = PMISectionDisplayModeConstants.sePMISectionDisplayShowCutFacesWithOriginalBodies,
        ["only_original_bodies"] = PMISectionDisplayModeConstants.sePMISectionDisplayShowOnlyOriginalBodies,
    };

    public static object List(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = Model(context, documentHandle);
        var sections = Late.Items(ViewsOf(document)).Cast<SectionView>().Select(v => Describe(context, document, v)).ToList();
        return new { document = context.DocumentHandle(document), count = sections.Count, sections };
    }

    /// <summary>New section view from a sketch profile of the model.</summary>
    /// <param name="keep">inside | outside (closed profile) or left | right of the drawing direction (open profile).</param>
    /// <param name="depth">Extent in mm along the sketch normal; null = without limit.</param>
    /// <param name="direction">both (through all, or symmetric with a depth) | front (along the normal) | back.</param>
    /// <param name="display">What is drawn (see <see cref="Displays"/>); null = what Solid Edge repeats from the document.</param>
    public static object Add(SeContext context, string? documentHandle, string? sketch, string keep, double? depth, string direction,
        string? caption, bool cutHardware, string? display = null, double? opacity = null, bool? showPlane = null)
    {
        if (string.IsNullOrWhiteSpace(sketch))
        {
            throw ToolException.InvalidArgument("add requires 'sketch': a sketch of the model (se_sketch create, draw, close) whose profile cuts it.");
        }

        PMISectionDisplayModeConstants? mode = Mode(display);
        RequireOpacity(opacity);

        SolidEdgeDocument document = string.IsNullOrWhiteSpace(documentHandle) && context.Handles.GetScope(sketch) is not null
            ? Require(context.DocumentOf(sketch))
            : Model(context, documentHandle);
        object profile = SketchService.Resolve(context, sketch);
        SectionViewProfileSide side = keep switch
        {
            "inside" => SectionViewProfileSide.igInsideProfileSide,
            "outside" => SectionViewProfileSide.igOutsideProfileSide,
            "left" => SectionViewProfileSide.igRightProfileSide,
            "right" => SectionViewProfileSide.igLeftProfileSide,
            _ => throw ToolException.InvalidArgument($"Unknown side '{keep}'.", "Use inside, outside, left or right."),
        };
        (SectionViewExtentSide extent, double metres) = Extent(depth, direction);
        SectionViews views = ViewsOf(document);
        int before = views.Count;
        Array profiles = new object[] { profile };
        SectionView view;
        try
        {
            view = (SectionView)views.Add(1, ref profiles, caption?.Trim() ?? "", metres, extent, side, cutHardware ? 1 : 0);
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("section_failed", $"Solid Edge did not make the section view: {ex.Message.Trim()}",
                "Close the sketch first (se_sketch close; open=true for a single open line) and check that it crosses the model.", ex);
        }

        if (views.Count != before + 1 || !view.IsShown)
        {
            throw new ToolException("section_failed", "Solid Edge did not show the new section view.", "List them with action 'list'.");
        }

        Look(view, mode, opacity, showPlane);
        return new
        {
            section = Describe(context, document, view),
            count = views.Count,
            note = "The model itself does not change; see the cut with se_view capture, hide it with 'hide'.",
        };
    }

    public static object Show(SeContext context, string? documentHandle, string? section, bool shown)
    {
        (SolidEdgeDocument document, SectionView view) = Find(context, documentHandle, section);
        view.Show(shown);
        if (view.IsShown != shown)
        {
            throw new ToolException("section_failed", $"Solid Edge kept the section view '{view.Name}' {(shown ? "hidden" : "shown")}.");
        }

        return Describe(context, document, view);
    }

    public static object Edit(SeContext context, string? documentHandle, string? section, string? caption, string? display, double? opacity,
        bool? showPlane, bool? cutHardware)
    {
        (SolidEdgeDocument document, SectionView view) = Find(context, documentHandle, section);
        if (caption is null && display is null && opacity is null && showPlane is null && cutHardware is null)
        {
            throw ToolException.InvalidArgument("edit needs at least one of 'caption', 'display', 'opacity', 'showPlane' or 'cutHardware'.");
        }

        PMISectionDisplayModeConstants? mode = Mode(display);
        RequireOpacity(opacity);
        if (caption is not null)
        {
            view.Caption = caption.Trim();
            Check(view.Caption == caption.Trim(), view, "caption");
        }

        Look(view, mode, opacity, showPlane);
        if (cutHardware is { } hardware)
        {
            view.CutHardware = hardware ? 1 : 0;
            Check(view.CutHardware != 0 == hardware, view, "cutHardware");
        }

        return Describe(context, document, view);
    }

    /// <summary>Deletes a section view; Solid Edge also deletes its profile and the unused reference planes, which are reported.</summary>
    public static object Delete(SeContext context, string? documentHandle, string? section)
    {
        (SolidEdgeDocument document, SectionView view) = Find(context, documentHandle, section);
        SectionViews views = ViewsOf(document);
        int before = views.Count;
        string name = view.Name;
        List<PlaneInfo> planesBefore = Planes(document);
        string? handle = context.Handles.TryGetId(view, out string? id) ? id : null;
        view.Delete();
        if (ViewsOf(document).Count != before - 1)
        {
            throw new ToolException("section_failed", $"Solid Edge did not delete the section view '{name}'.", "List them with action 'list'.");
        }

        if (handle is not null)
        {
            context.Handles.Remove(handle, "the section view was deleted");
        }

        List<PlaneInfo> removed = Removed(planesBefore, Planes(document));
        return new
        {
            deleted = name,
            count = before - 1,
            removedPlanes = removed.Count > 0 ? removed.Select(p => p.Describe()).ToList() : null,
            warning = removed.Count > 0
                ? $"Solid Edge also deleted {removed.Count} reference plane(s) that nothing used; make them again with se_ref_plane if they are needed."
                : null,
        };
    }

    // ---------------------------------------------------------------- helpers

    private static (SectionViewExtentSide Side, double Metres) Extent(double? depth, string direction)
    {
        if (depth is null)
        {
            return direction switch
            {
                "both" => (SectionViewExtentSide.igThroughAllExtent, 0),
                "front" => (SectionViewExtentSide.igInfiniteRightExtent, 0),
                "back" => (SectionViewExtentSide.igInfiniteLeftExtent, 0),
                _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}'.", "Use both, front or back."),
            };
        }

        double metres = Units.MmToM(Units.RequirePositive(depth.Value, "depth"));
        return direction switch
        {
            "both" => (SectionViewExtentSide.igFiniteSymmetricExtent, metres),
            "front" => (SectionViewExtentSide.igRightExtent, metres),
            "back" => (SectionViewExtentSide.igLeftExtent, metres),
            _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}'.", "Use both, front or back."),
        };
    }

    private static SolidEdgeDocument Model(SeContext context, string? documentHandle) => Require(context.GetDocument(documentHandle));

    private static SolidEdgeDocument Require(SolidEdgeDocument document) =>
        document is SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument or SolidEdgeAssembly.AssemblyDocument
            ? document
            : throw new ToolException("not_supported", $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}: section views belong to parts, sheet metal and assemblies.",
                "Drawings have their own section views: se_drawing section_view.");

    private static SectionViews ViewsOf(SolidEdgeDocument document) => (SectionViews)Late.Get(document, "SectionViews")!;

    private static (SolidEdgeDocument Document, SectionView View) Find(SeContext context, string? documentHandle, string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            throw ToolException.InvalidArgument("'section' is required: a handle, name or caption from action 'list'.");
        }

        if (context.Handles.GetScope(section) is not null)
        {
            return (Require(context.DocumentOf(section)), context.Handles.Resolve<SectionView>(section, "section"));
        }

        SolidEdgeDocument document = Model(context, documentHandle);
        SectionView[] views = [.. Late.Items(ViewsOf(document)).Cast<SectionView>()];
        SectionView? match = views.FirstOrDefault(v => string.Equals(v.Name, section, StringComparison.OrdinalIgnoreCase))
            ?? views.FirstOrDefault(v => string.Equals(v.Caption, section, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? (document, match)
            : throw ToolException.NotFound($"There is no section view '{section}' in '{SeContext.SafeName(document)}'.",
                views.Length == 0 ? "The document has none: make one with 'add'." : $"Section views: {string.Join(", ", views.Select(v => v.Name))}.");
    }

    private static PMISectionDisplayModeConstants? Mode(string? display) =>
        display is null ? null
        : Displays.TryGetValue(display, out PMISectionDisplayModeConstants mode) ? mode
        : throw ToolException.InvalidArgument($"Unknown display '{display}'.", $"Use one of: {string.Join(", ", Displays.Keys)}.");

    private static void RequireOpacity(double? opacity)
    {
        if (opacity is { } o && (double.IsNaN(o) || o < 0 || o > 1))
        {
            throw ToolException.InvalidArgument("'opacity' goes from 0 (transparent) to 1 (opaque).");
        }
    }

    /// <summary>What is drawn and the cutting plane; Solid Edge repeats the display and the plane visibility in the next section views of the document.</summary>
    private static void Look(SectionView view, PMISectionDisplayModeConstants? mode, double? opacity, bool? showPlane)
    {
        if (mode is { } m)
        {
            view.SectionDisplayMode = m;
            Check(view.SectionDisplayMode == m, view, "display");
        }

        if (opacity is { } o)
        {
            view.Opacity = o;
            Check(Math.Abs(view.Opacity - o) < 1e-6, view, "opacity");
        }

        if (showPlane is { } plane)
        {
            view.ShowCuttingPlane = plane ? 1 : 0;
            Check(view.ShowCuttingPlane != 0 == plane, view, "showPlane");
        }
    }

    private static void Check(bool ok, SectionView view, string what)
    {
        if (!ok)
        {
            throw new ToolException("section_failed", $"Solid Edge did not keep the new '{what}' of the section view '{view.Name}'.",
                "See it with action 'list'; edit, hide or delete it.");
        }
    }

    private static object Describe(SeContext context, SolidEdgeDocument document, SectionView view) => new
    {
        handle = context.Register(view, "sectionview", document),
        name = view.Name,
        caption = view.Caption,
        shown = view.IsShown,
        display = Displays.FirstOrDefault(d => d.Value == view.SectionDisplayMode).Key ?? view.SectionDisplayMode.ToString(),
        opacity = Math.Round(view.Opacity, 4),
        showPlane = view.ShowCuttingPlane != 0,
        cutHardware = view.CutHardware != 0,
        style = view.Style,
    };

    /// <summary>A reference plane as seen before and after a delete: name, normal and root point (mm).</summary>
    private sealed record PlaneInfo(int Index, string? Name, double[]? Normal, double[]? Root)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture,
            $"{Name}|{string.Join(",", (Normal ?? []).Select(v => Math.Round(v, 6) + 0.0))}|{string.Join(",", (Root ?? []).Select(v => Math.Round(v, 3) + 0.0))}");

        public object Describe() => new { index = Index, name = Name, normal = Normal, rootPoint = Root };
    }

    private static List<PlaneInfo> Planes(SolidEdgeDocument document)
    {
        object? planes = document is SolidEdgeAssembly.AssemblyDocument assembly ? assembly.AsmRefPlanes : Late.TryGet(document, "RefPlanes");
        var list = new List<PlaneInfo>();
        if (planes is null)
        {
            return list;
        }

        int index = 0;
        foreach (object plane in Late.Items(planes))
        {
            index++;
            list.Add(new PlaneInfo(index, Late.TryGetString(plane, "Name"), Vector(plane, "GetNormal", false), Vector(plane, "GetRootPoint", true)));
        }

        return list;
    }

    private static double[]? Vector(object plane, string method, bool millimetres)
    {
        try
        {
            var values = (Array)Late.CallByRef(plane, method, [new double[3]], 0)[0]!;
            return millimetres ? Geo.Mm(values) : Geo.Plain(values);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Planes of <paramref name="before"/> that are no longer there (matched by name, normal and root point).</summary>
    private static List<PlaneInfo> Removed(List<PlaneInfo> before, List<PlaneInfo> after)
    {
        var left = after.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Count());
        var removed = new List<PlaneInfo>();
        foreach (PlaneInfo plane in before)
        {
            if (left.TryGetValue(plane.Key, out int n) && n > 0)
            {
                left[plane.Key] = n - 1;
            }
            else
            {
                removed.Add(plane);
            }
        }

        return removed;
    }
}
