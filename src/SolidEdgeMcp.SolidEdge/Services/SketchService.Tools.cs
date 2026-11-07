// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Sketch tools of the ribbon: Clean Sketch, Convert to Curve, Reposition and Zero Origin, Image, Section Sketches, Total Length
/// and Copy Sketch (assembly layouts into parts).
/// </summary>
public static partial class SketchService
{
    /// <summary>
    /// Clean Sketch: duplicate, overlapping and very small elements removed (points, B-splines, identical and small elements as
    /// chosen). Verified in Solid Edge 2026: Profile.CleanGeometry2d(delete, …, tolerance) on two identical lines and a line
    /// shorter than the tolerance left one line; Sheet.CleanGeometry2d takes the elements to clean and did the same on a sheet.
    /// </summary>
    public static object Clean(SeContext context, string sketch, string[]? elements, double? tolerance, bool points, bool splines, bool identical, bool small)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        double limit = Units.MmToM(tolerance ?? 0.01);
        if (limit <= 0)
        {
            throw ToolException.InvalidArgument("'tolerance' must be a positive length in mm.");
        }

        object before = Summary(canvas);
        int count = ElementCounts(canvas).Sum();
        if (canvas.Profile is { } profile)
        {
            if (elements is not null)
            {
                throw ToolException.InvalidArgument("In a sketch, clean works on the whole sketch: leave 'elements' out.");
            }

            profile.CleanGeometry2d(CleanProfileOptions.igCleanProfileDelete, points, splines, identical, small, Type.Missing, limit);
        }
        else
        {
            object[] chosen = elements is null ? [.. AllElements(canvas)] : Elements(context, elements, 1, int.MaxValue, "clean");
            Array array = chosen;
            canvas.Sheet!.CleanGeometry2d(chosen.Length, ref array, SolidEdgeDraft.CleanProfileOptions.igCleanProfileDelete, points, splines, identical, small, Type.Missing, limit);
        }

        int removed = count - ElementCounts(canvas).Sum();
        return new { sketch, removed, before, after = Summary(canvas), note = removed == 0 ? "Nothing to clean: no duplicate, overlapping or small elements." : null };
    }

    /// <summary>
    /// Convert to Curve: lines and arcs of a sketch or a drawing sheet become B-spline curves of the same shape. Verified in
    /// Solid Edge 2026: Profile.ConvertToCurve on a line and an arc gave two B-splines of the same lengths, and
    /// Sheet.ConvertToCurve turned a line of a sheet into a B-spline.
    /// </summary>
    public static object ConvertToCurve(SeContext context, string sketch, string[]? elements)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] chosen = Elements(context, elements, 1, int.MaxValue, "convert");
        double length = chosen.Sum(LengthOf);
        int[] before = ElementCounts(canvas);
        Array array = chosen;
        object? converted = null;
        if (canvas.Profile is { } profile)
        {
            profile.ConvertToCurve(chosen.Length, ref array, out _, ref converted);
        }
        else
        {
            canvas.Sheet!.ConvertToCurve(chosen.Length, ref array, out _, ref converted);
        }

        object[] curves = [.. NewElements(canvas, before).OfType<BSplineCurve2d>()];
        if (curves.Length == 0)
        {
            throw new ToolException("convert_failed", "Solid Edge made no curve from the elements.", "Pass lines, arcs or other curves of this sketch.");
        }

        double after = curves.Sum(LengthOf);
        return new
        {
            sketch,
            curves = curves.Select(c => new { element = context.RegisterBeside(c, "bspline2d", sketch), geometry = Describe2d(c) }).ToArray(),
            length = Geo.Mm(after),
            lengthBefore = Geo.Mm(length),
        };
    }

    /// <summary>
    /// Reposition Origin and Zero Origin: the local x axis of a sketch (its origin and direction in the sketch plane), set or
    /// put back to the plane's own. Verified in Solid Edge 2026: Sketch.SetLocalXAxis(origin, direction) is read back by
    /// GetLocalXAxis.
    /// </summary>
    public static object Origin(SeContext context, string sketch, double[]? origin, double? angle)
    {
        Profile profile = Resolve(context, sketch);
        Sketch owner = profile.Parent as Sketch
            ?? throw new ToolException("not_supported", $"'{sketch}' is not a standalone sketch.", "Create the sketch with se_sketch create.");
        owner.GetLocalXAxis(out double ox, out double oy, out double ax, out double ay);
        var previous = new { origin = new[] { Geo.Mm(ox), Geo.Mm(oy) }, angle = Geo.R(Units.RadToDeg(Math.Atan2(ay, ax))) };
        (double X, double Y) at = origin is null ? (0, 0) : Geo.Points2(origin, "origin", 1)[0];
        double radians = Units.DegToRad(angle ?? 0);
        owner.SetLocalXAxis(at.X, at.Y, Math.Cos(radians), Math.Sin(radians));
        owner.GetLocalXAxis(out ox, out oy, out ax, out ay);
        double turned = Math.Atan2(ay, ax);
        if (Math.Abs(ox - at.X) > 1e-9 || Math.Abs(oy - at.Y) > 1e-9 || Math.Abs(Math.IEEERemainder(turned - radians, 2 * Math.PI)) > 1e-6)
        {
            throw new ToolException("setting_rejected", "Solid Edge did not move the origin of the sketch.");
        }

        return new
        {
            sketch,
            origin = new[] { Geo.Mm(ox), Geo.Mm(oy) },
            angle = Geo.R(Units.RadToDeg(turned)),
            previous,
            frame = Frame(profile),
        };
    }

    /// <summary>
    /// Image: a picture file placed in a sketch. Verified in Solid Edge 2026: Images2d.AddImage(link, file) places it with its
    /// aspect ratio locked; SetOrigin moves its lower-left corner and a new Width scales the height with it.
    /// </summary>
    public static object Image(SeContext context, string sketch, string? path, double[]? position, double? width, bool link)
    {
        Profile profile = Resolve(context, sketch);
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("'path' is required: the picture file (.png, .jpg, .bmp)."), "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{file}' does not exist.");
        }

        if (width is <= 0)
        {
            throw ToolException.InvalidArgument("'width' must be a positive length in mm.");
        }

        Images2d images = profile.Images2d;
        int count = images.Count;
        Image2d image = images.AddImage(link, file);
        if (images.Count != count + 1 || image is null)
        {
            throw new ToolException("image_failed", $"Solid Edge did not place '{Path.GetFileName(file)}'.", "Pass a .png, .jpg or .bmp picture.");
        }

        if (position is not null)
        {
            (double X, double Y) at = Geo.Points2(position, "position", 1)[0];
            image.SetOrigin(at.X, at.Y);
        }

        if (width is { } wanted)
        {
            image.LockAspectRatio = true;
            image.Width = Units.MmToM(wanted);
        }

        image.GetOrigin(out double x, out double y);
        return new
        {
            sketch,
            element = context.RegisterBeside(image, "image2d", sketch),
            file = image.ImageFileName,
            linked = image.IsLink,
            position = new[] { Geo.Mm(x), Geo.Mm(y) },
            width = Geo.Mm(image.Width),
            height = Geo.Mm(image.Height),
        };
    }

    /// <summary>
    /// Section Sketches: sketches of the sections of design bodies on planes parallel to a reference plane. Verified in Solid
    /// Edge 2026: Sketches.CreateSectionSketches(bodies, plane, out sketches, out count, out error, planes, side, spacing,
    /// 1, 1, 1, 1) through the interop (late bound it gives a type mismatch; a null out array a NullReferenceException) makes
    /// a sketch on the reference plane and one on each of the 'planes' planes, with the section as lines, arcs and circles
    /// (Model objects instead of bodies give "not intersecting"). The first call leaves some new sketches empty (all of them in a part never saved, the one on the reference plane in a
    /// saved part); the same call again adds the section to every sketch on those planes, without new sketches. So when a
    /// sketch is empty the call is repeated once and the copies it adds to the sketches already filled are deleted.
    /// </summary>
    public static object Sections(SeContext context, string? documentHandle, string? plane, string[]? bodies, int? count, double? spacing, string side)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        if (model.Document is not PartDocument and not SheetMetalDocument)
        {
            throw new ToolException("wrong_document_type", "Section sketches are made in parts and sheet metal parts.");
        }

        RefPlane refPlane = model.ResolvePlaneSource(context, plane ?? throw ToolException.InvalidArgument("'plane' is required: '1'-'3' or a reference plane."), "plane") as RefPlane
            ?? throw ToolException.InvalidArgument($"'{plane}' is not a reference plane.", "Pass '1'-'3' or a plane handle (se_ref_plane list).");
        IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> all = model.Bodies();
        if (all.Count == 0)
        {
            throw new ToolException("no_model", $"'{model.Document.Name}' has no solid yet.");
        }

        object[] chosen = bodies is { Length: > 0 }
            ? [.. bodies.Select(h => context.Handles.Resolve<SolidEdgeGeometry.Body>(h, "bodies"))]
            : [.. all.Select(b => (object)b.Body)];
        int planes = count ?? 1;
        if (planes is < 1 or > 100)
        {
            throw ToolException.InvalidArgument("'count' must be from 1 to 100 planes.");
        }

        SectionSketchesPlanesDirection direction = side switch
        {
            "both" => SectionSketchesPlanesDirection.seSectionSketchesPlaneBothSide,
            "reverse" => SectionSketchesPlanesDirection.seSectionSketchesPlaneReverseNormalSide,
            _ => SectionSketchesPlanesDirection.seSectionSketchesPlaneNormalSide,
        };
        Sketchs sketches = model.Sketches;
        int before = sketches.Count;
        SectionSketchesErrorCode error = SectionSketchesErrorCode.seSectionSketchesUnknownError;
        Sketch[] Cut()
        {
            Array objects = chosen;
            // The out array must not be null: the dispatch call wraps it as a safe array and fails on null.
            Array made = Array.Empty<object>();
            sketches.CreateSectionSketches(ref objects, refPlane, out made, out _, out error, planes, direction, Units.MmToM(spacing ?? 0), 1, 1, 1, 1);
            return made is null ? [] : [.. made.Cast<object?>().OfType<Sketch>()];
        }

        Sketch[] created = Cut();
        if (created.Length == 0)
        {
            throw new ToolException("section_failed", $"Solid Edge made no section sketch ({error}).",
                error == SectionSketchesErrorCode.seSectionSketchesNotPlaneIntersecting ? "No plane crosses the bodies: move the plane, change 'side' or 'spacing'." : null);
        }

        Canvas2d?[] canvases = [.. created.Select(s => s.Profiles.Count > 0 ? Canvas2d.Of(s.Profiles.Item(1)) : null)];
        int[][] counts = [.. canvases.Select(c => c is null ? [] : ElementCounts(c))];
        if (counts.Any(c => c.Sum() == 0))
        {
            _ = Cut();
            for (int i = 0; i < canvases.Length; i++)
            {
                if (canvases[i] is { } canvas && counts[i].Sum() > 0)
                {
                    foreach (object copy in NewElements(canvas, counts[i]).ToList())
                    {
                        Late.Call(copy, "Delete");
                    }
                }
            }
        }

        context.ModelChanged(model.Document, "section sketches were added");
        return new
        {
            document = context.DocumentHandle(model.Document),
            count = created.Length,
            added = sketches.Count - before,
            partial = error == SectionSketchesErrorCode.seSectionSketchesSomePlaneIntersecting,
            sketches = created.Select(s => new
            {
                sketch = context.Register(s, "sketch", model.Document),
                name = s.Name,
                frame = s.Profiles.Count > 0 ? Frame(s.Profiles.Item(1)) : null,
                elements = s.Profiles.Count > 0 ? Summary(Canvas2d.Of(s.Profiles.Item(1))) : null,
            }).ToArray(),
        };
    }

    /// <summary>Total Length: the length of the chosen curves of a sketch or sheet (all of them by default).</summary>
    public static object TotalLength(SeContext context, string sketch, string[]? elements)
    {
        Canvas2d canvas = ResolveCanvas(context, sketch);
        object[] chosen = elements is null ? [.. AllElements(canvas).Where(e => e is not Point2d)] : Elements(context, elements, 1, int.MaxValue, "length");
        return new
        {
            sketch,
            elements = chosen.Length,
            length = Geo.Mm(chosen.Sum(LengthOf)),
            byType = chosen.GroupBy(KindOf).Select(g => new { type = g.Key, count = g.Count(), length = Geo.Mm(g.Sum(LengthOf)) }).ToArray(),
        };
    }

    /// <summary>
    /// Copy Sketch: an assembly layout copied into the part of an occurrence, as a sketch of that part. Verified in Solid Edge
    /// 2026: AssemblyDocument.CopySketch(layout, occurrence, associative, out status) added a sketch to the target part.
    /// </summary>
    public static object CopyToPart(SeContext context, string sketch, string? occurrenceHandle, bool associative)
    {
        Profile profile = Resolve(context, sketch);
        object source = LayoutOf(profile)
            ?? throw ToolException.InvalidArgument($"'{sketch}' is not an assembly layout.", "Copy a layout made with se_sketch create layout=true.");
        SolidEdgeAssembly.Occurrence occurrence = context.Handles.Resolve<SolidEdgeAssembly.Occurrence>(
            occurrenceHandle ?? throw ToolException.InvalidArgument("'occurrence' is required: the part occurrence that receives the sketch."), "occurrence");
        var assembly = (SolidEdgeAssembly.AssemblyDocument)occurrence.TopLevelDocument;
        ModelDocument part = AssemblyService.PartOf(occurrence);
        Sketchs target = part.Sketches;
        int before = target.Count;
        assembly.CopySketch(source, occurrence, associative, out object status);
        if (target.Count <= before)
        {
            throw new ToolException("copy_failed", $"Solid Edge did not copy the layout into '{occurrence.Name}' (status {status}).");
        }

        context.ModelChanged(part.Document, "a sketch was copied in");
        Sketch copied = target.Item(target.Count);
        return new
        {
            occurrence = occurrence.Name,
            part = context.DocumentHandle(part.Document),
            sketch = context.Register(copied, "sketch", part.Document),
            name = copied.Name,
            associative,
            elements = copied.Profiles.Count > 0 ? Summary(Canvas2d.Of(copied.Profiles.Item(1))) : null,
        };
    }

    private static IEnumerable<object> AllElements(Canvas2d canvas) =>
        ElementCollections.SelectMany(c => Late.Items(c(canvas)));

    private static double LengthOf(object element) => element switch
    {
        Circle2d circle => circle.Circumference,
        Ellipse2d ellipse => ellipse.Circumference,
        Point2d => 0,
        _ => Late.TryGet(element, "Length") is double length ? length : 0,
    };
}
