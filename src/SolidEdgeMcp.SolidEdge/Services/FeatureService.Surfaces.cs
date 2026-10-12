// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Construction surfaces: extruded or revolved from a sketch (open or closed, optionally capped) and offset from a face. They
/// do not change the volume of the body, so each one is checked by its status and the faces it makes, and deleted if it
/// fails. Verified: a line 60 long extruded 20 gives one face of 1200 mm²; a line 40 long at 150 from the axis revolved 180°
/// gives π·150·40 mm²; a single face is offset (an array of faces gives E_NOINTERFACE), igLeft into the material.
/// Lofted and swept surfaces take the sections like the solid loft and sweep (included profiles and their start points);
/// stitching joins surfaces that share edges into one; a bounded surface fills a closed chain of edges.
/// </summary>
public static partial class FeatureService
{
    private static readonly string[] SurfaceTypes = ["extruded", "revolved", "offset", "lofted", "swept", "stitched", "bounded", "copy", "bluesurf"];

    public static object Surfaces(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object[] surfaces = AllSurfaces(model).Select(s => DescribeSurface(context, model, s.Surface, s.Type)).ToArray();
        return new { document = context.DocumentHandle(model.Document), count = surfaces.Length, surfaces };
    }

    public static object ExtrudeSurface(SeContext context, string? documentHandle, string? sketch, double? depth, string direction, bool endCaps)
    {
        string handle = sketch ?? throw ToolException.InvalidArgument("'sketch' is required: closed with se_sketch 'close' (open: true for an open profile).");
        double length = Units.MmToM(Units.RequirePositive(depth ?? throw ToolException.InvalidArgument("extrude requires 'depth' (mm)."), "depth"));
        ModelDocument model = ModelDocument.For(context, documentHandle, handle);
        Profile sketchProfile = SketchService.Resolve(context, handle);
        FeaturePropertyConstants side = Side(direction, finite: true);
        bool switched = model.EnsureOrdered();
        IncludedProfile included = Include(model, sketchProfile);
        Array profiles = new object[] { included.Profile };
        object surface = Create("extruded surface", included,
            () => ConstructionsOf(model).ExtrudedSurfaces.AddFinite(1, ref profiles, side, length, endCaps));
        return CheckedSurface(context, model, surface, "extruded", switched, [included], [sketchProfile]);
    }

    public static object RevolveSurface(SeContext context, string? documentHandle, string? sketch, string? axis, double angle, string direction, bool endCaps)
    {
        string handle = sketch ?? throw ToolException.InvalidArgument("'sketch' is required: closed with se_sketch 'close' (open: true for an open profile).");
        RefAxis refAxis = context.Handles.Resolve<RefAxis>(
            axis ?? throw ToolException.InvalidArgument("revolve requires 'axis'.", "Create it with se_sketch action 'axis' on a line of the sketch, before closing the sketch."),
            "axis");
        double radians = Units.DegToRad(Units.RequirePositive(angle, "angle"));
        if (radians > 2 * Math.PI + 1e-9)
        {
            throw ToolException.InvalidArgument("'angle' must be at most 360 degrees.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, handle);
        Profile sketchProfile = SketchService.Resolve(context, handle);
        FeaturePropertyConstants side = Side(direction, finite: true);
        bool switched = model.EnsureOrdered();
        IncludedProfile included = Include(model, sketchProfile);
        Array profiles = new object[] { included.Profile };
        object surface = Create("revolved surface", included,
            () => ConstructionsOf(model).RevolvedSurfaces.AddFinite(1, ref profiles, refAxis, side, radians, endCaps));
        return CheckedSurface(context, model, surface, "revolved", switched, [included], [sketchProfile]);
    }

    /// <summary>Surface at a distance (mm) from one face: positive along its normal (out of the material), negative into it.</summary>
    public static object OffsetSurface(SeContext context, string? documentHandle, string[]? faces, double? distance)
    {
        if (faces is not [string faceHandle])
        {
            throw ToolException.InvalidArgument("offset takes one face in 'faces' (se_geometry faces).");
        }

        double d = Units.MmToM(Units.RequireFinite(distance ?? throw ToolException.InvalidArgument("offset requires 'distance' (mm)."), "distance"));
        if (Math.Abs(d) < 1e-9)
        {
            throw ToolException.InvalidArgument("'distance' must not be 0: positive along the face normal (out of the material), negative into it.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, faceHandle);
        Face face = context.Handles.Resolve<Face>(faceHandle, "faces");
        bool switched = model.EnsureOrdered();
        FeaturePropertyConstants side = d > 0 ? FeaturePropertyConstants.igRight : FeaturePropertyConstants.igLeft;
        object surface = Create("offset surface", () => ConstructionsOf(model).OffsetSurfaces.Add(side, Math.Abs(d), face));
        return CheckedSurface(context, model, surface, "offset", switched, [], []);
    }

    /// <summary>Surface through two or more section sketches in order (open or closed), optionally capped.</summary>
    public static object LoftSurface(SeContext context, string? documentHandle, string[]? sketches, double[]? origins, bool endCaps)
    {
        if (sketches is not { Length: >= 2 })
        {
            throw ToolException.InvalidArgument("loft needs at least two section sketches in 'sketches', in order.");
        }

        (ModelDocument model, Profile[] sketchProfiles, bool switched, IncludedProfile[] included, object[] sections, int[] types, object[] starts) =
            Sections(context, documentHandle, sketches, origins);
        const FeaturePropertyConstants none = FeaturePropertyConstants.igNone;
        object surface;
        try
        {
            surface = Create("lofted surface", () => ConstructionsOf(model).LoftedSurfaces.Add(sections.Length, sections, types, starts,
                none, none, none, 0, none, 0, System.Reflection.Missing.Value, System.Reflection.Missing.Value, endCaps));
        }
        catch (ToolException)
        {
            DeleteAll(included);
            throw;
        }

        return CheckedSurface(context, model, surface, "lofted", switched, included, sketchProfiles);
    }

    /// <summary>Surface that sweeps one or more section sketches along a path (a sketch or a curve from se_curve).</summary>
    public static object SweepSurface(SeContext context, string? documentHandle, string[]? sketches, string? path, double[]? origins, bool endCaps)
    {
        if (sketches is not { Length: >= 1 })
        {
            throw ToolException.InvalidArgument("sweep needs the section sketches in 'sketches'.");
        }

        string pathHandle = path ?? throw ToolException.InvalidArgument("sweep requires 'path': a sketch closed with se_sketch 'close' (open: true for an open chain) or a curve from se_curve.");
        object resolvedPath = context.Handles.Resolve(pathHandle, "path");
        Profile? pathSketch = resolvedPath is Profile or Sketch ? SketchService.Resolve(context, pathHandle) : null;
        Edge[] pathEdges = pathSketch is null ? CurveEdges(resolvedPath) : [];
        if (pathSketch is null && pathEdges.Length == 0)
        {
            throw new ToolException("handle_wrong_type", $"'{pathHandle}' in 'path' is neither a sketch nor a curve.",
                "Pass a sketch closed with se_sketch 'close' (open: true for an open chain) or a curve from se_curve.");
        }

        (ModelDocument model, Profile[] sketchProfiles, bool switched, IncludedProfile[] included, object[] sections, int[] types, object[] starts) =
            Sections(context, documentHandle, sketches, origins);
        IncludedProfile? pathCopy;
        try
        {
            pathCopy = pathSketch is null ? null : Include(model, pathSketch);
        }
        catch (ToolException)
        {
            DeleteAll(included);
            throw;
        }

        Array traces = pathCopy is null ? pathEdges.Cast<object>().ToArray() : new object[] { pathCopy.Profile };
        FeaturePropertyConstants traceType = pathCopy is null ? FeaturePropertyConstants.igEdgeBasedCrossSection : FeaturePropertyConstants.igProfileBasedCrossSection;
        Array sectionArray = sections;
        int originRefs = 0;
        IncludedProfile[] copies = pathCopy is null ? included : [.. included, pathCopy];
        object surface;
        try
        {
            // Verified: Add (origins by reference) gives E_NOINTERFACE; AddEx takes them by value.
            surface = Create("swept surface", () => ConstructionsOf(model).SweptSurfaces.AddEx(traces.Length, ref traces, traceType,
                sectionArray.Length, ref sectionArray, FeaturePropertyConstants.igProfileBasedCrossSection, starts, ref originRefs,
                FeaturePropertyConstants.igNone, FeaturePropertyConstants.igNone, endCaps));
        }
        catch (ToolException)
        {
            DeleteAll(copies);
            throw;
        }

        return CheckedSurface(context, model, surface, "swept", switched, copies, pathSketch is null ? sketchProfiles : [.. sketchProfiles, pathSketch]);
    }

    /// <summary>Joins two or more surfaces that share edges into one stitched surface; its area is checked against theirs.</summary>
    public static object StitchSurface(SeContext context, string? documentHandle, string[]? surfaces, double? tolerance)
    {
        if (surfaces is not { Length: >= 2 })
        {
            throw ToolException.InvalidArgument("stitch needs at least two surface handles in 'surfaces' (action 'list').");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, surfaces[0]);
        object[] items = surfaces.Select(h => context.Handles.Resolve(h, "surfaces")).ToArray();
        if (items.Any(s => FacesOf(s).Length == 0))
        {
            throw ToolException.InvalidArgument("Every handle in 'surfaces' must be a construction surface with faces (se_surface list).");
        }

        double expected = items.Sum(s => FacesOf(s).Sum(f => f.Area));
        object tol = tolerance is { } t ? Units.MmToM(Units.RequirePositive(t, "tolerance")) : System.Reflection.Missing.Value;
        bool switched = model.EnsureOrdered();
        Array array = items;
        object surface = Create("stitched surface", () => ConstructionsOf(model).StitchSurfaces.Add(array.Length, ref array, false, tol));
        object result = CheckedSurface(context, model, surface, "stitched", switched, [], []);
        double area = FacesOf(surface).Sum(f => f.Area);
        if (Math.Abs(area - expected) > Math.Max(1e-9, expected * 1e-4))
        {
            TryDeleteSurface(surface);
            throw new ToolException("feature_failed",
                $"The stitched surface has {Geo.Mm2(area)} mm² instead of {Geo.Mm2(expected)} mm² (the sum of the surfaces); it was deleted.",
                "Stitch surfaces that meet along whole edges.");
        }

        return result;
    }

    /// <summary>Surface that fills a closed chain of edges (of the body, a surface or a curve).</summary>
    public static object BoundedSurface(SeContext context, string? documentHandle, string[]? edges)
    {
        if (edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("bounded needs 'edges': a closed chain of edge handles (se_geometry edges of the body, a surface or a curve).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, edges[0]);
        Array array = edges.Select(h => (object)context.Handles.Resolve<Edge>(h, "edges")).ToArray();
        bool switched = model.EnsureOrdered();
        object surface = Create("bounded surface", () => ConstructionsOf(model).SurfaceByBoundaries.Add(array.Length, ref array));
        return CheckedSurface(context, model, surface, "bounded", switched, [], []);
    }

    /// <summary>Included copies of the section sketches, with their types and start points (like the solid loft and sweep).</summary>
    private static (ModelDocument Model, Profile[] SketchProfiles, bool Switched, IncludedProfile[] Included, object[] Sections, int[] Types, object[] Starts)
        Sections(SeContext context, string? documentHandle, string[] sketches, double[]? origins)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketches[0]);
        Profile[] sketchProfiles = sketches.Select(s => SketchService.Resolve(context, s)).ToArray();
        (double X, double Y)[]? points = origins is null ? null : Geo.Points2(origins, "origins", sketches.Length).ToArray();
        if (points is not null && points.Length != sketches.Length)
        {
            throw ToolException.InvalidArgument("'origins' must give one point [x, y] per section.");
        }

        bool switched = model.EnsureOrdered();
        var included = new List<IncludedProfile>();
        try
        {
            foreach (Profile profile in sketchProfiles)
            {
                included.Add(Include(model, profile));
            }
        }
        catch (ToolException)
        {
            DeleteAll(included);
            throw;
        }

        object[] sections = included.Select(i => (object)i.Profile).ToArray();
        int[] types = Enumerable.Repeat(ProfileSection, sections.Length).ToArray();
        object[] starts = sections.Select((s, i) => (object)(points is null ? StartPoint((Profile)s, sketchProfiles[i]) : [points[i].X, points[i].Y])).ToArray();
        return (model, sketchProfiles, switched, [.. included], sections, types, starts);
    }

    private static void DeleteAll(IEnumerable<IncludedProfile> copies)
    {
        foreach (IncludedProfile copy in copies)
        {
            DeleteIncluded(copy);
        }
    }

    private static void TryDeleteSurface(object surface)
    {
        try
        {
            Late.Call(surface, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The error being reported is the failed surface.
        }
    }

    public static object DeleteSurface(SeContext context, string? documentHandle, string? name)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        object surface = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the surface name (action 'list').")
            : FindSurface(model, name) ?? throw ToolException.InvalidArgument($"The document has no surface '{name}'.", "List them with action 'list'.");
        Late.Call(surface, "Delete");
        context.ModelChanged(model.Document, "a surface was deleted");
        return FindSurface(model, name) is null
            ? new { deleted = name, count = AllSurfaces(model).Count() }
            : throw new ToolException("feature_failed", $"Solid Edge did not delete the surface '{name}'.", "Check it with action 'list'.");
    }

    private static object CheckedSurface(SeContext context, ModelDocument model, object surface, string type, bool switched,
        IncludedProfile[] included, Profile[] sketchProfiles)
    {
        context.ModelChanged(model.Document, "the model changed (new surface)");
        string? status = StatusOf(surface);
        Face[] faces = FacesOf(surface);
        if (status is not ("ok" or "warning") || faces.Length == 0 || faces.Sum(f => f.Area) <= 0)
        {
            TryDeleteSurface(surface);
            DeleteAll(included);

            throw new ToolException("feature_failed",
                $"Solid Edge did not make the {type} surface ({(faces.Length == 0 ? "it has no faces" : $"status {status}")}); it was deleted.",
                "Check the sketch, the axis or the face, and the distance.");
        }

        foreach (Profile profile in sketchProfiles.Concat(included.Select(i => i.Profile)))
        {
            Hide(profile);
        }

        return new { surface = DescribeSurface(context, model, surface, type), warnings = Warnings(switched, status == "warning") };
    }

    private static object DescribeSurface(SeContext context, ModelDocument model, object surface, string type)
    {
        Face[] faces = FacesOf(surface);
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (Face face in faces)
        {
            Array min = new double[3];
            Array max = new double[3];
            face.GetRange(ref min, ref max);
            double[] a = [.. Geo.Doubles(min)];
            double[] b = [.. Geo.Doubles(max)];
            for (int k = 0; k < 3; k++)
            {
                lo[k] = Math.Min(lo[k], a[k]);
                hi[k] = Math.Max(hi[k], b[k]);
            }
        }

        return new
        {
            handle = context.Register(surface, "surface", model.Document),
            name = Late.TryGetString(surface, "Name"),
            type,
            status = StatusOf(surface),
            faces = faces.Length,
            area = Geo.Mm2(faces.Sum(f => f.Area)),
            boundingBox = faces.Length == 0 ? null : new { min = lo.Select(Geo.Mm).ToArray(), max = hi.Select(Geo.Mm).ToArray() },
        };
    }

    private static Face[] FacesOf(object surface)
    {
        try
        {
            return Late.Get(surface, "Faces", TopologyQuery.igQueryAll) is { } faces ? [.. Late.Items(faces).OfType<Face>()] : [];
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return [];
        }
    }

    private static Constructions ConstructionsOf(ModelDocument model) => (Constructions)Late.Get(model.Document, "Constructions")!;

    private static IEnumerable<(object Surface, string Type)> AllSurfaces(ModelDocument model)
    {
        Constructions constructions = ConstructionsOf(model);
        object[] collections = [constructions.ExtrudedSurfaces, constructions.RevolvedSurfaces, constructions.OffsetSurfaces,
            constructions.LoftedSurfaces, constructions.SweptSurfaces, constructions.StitchSurfaces, constructions.SurfaceByBoundaries,
            constructions.CopySurfaces, constructions.BlueSurfs];
        // Surfaces closed into a solid belong to the construction model they were made on, and each model lists them all.
        var intersections = new List<object>();
        foreach (object item in Late.Items(constructions).SelectMany(c => Late.TryGet(c, "IntersectSurfaces") is { } items ? Late.Items(items) : []))
        {
            if (!intersections.Any(known => Late.SameObject(known, item)))
            {
                intersections.Add(item);
            }
        }

        return collections.SelectMany((c, i) => Late.Items(c).Select(s => (s, SurfaceTypes[i])))
            .Concat(intersections.Select(s => (s, "intersect")));
    }

    private static object? FindSurface(ModelDocument model, string name) =>
        AllSurfaces(model).Select(s => s.Surface).FirstOrDefault(s => string.Equals(Late.TryGetString(s, "Name"), name, StringComparison.OrdinalIgnoreCase));
}
