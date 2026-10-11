using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Face = SolidEdgeGeometry.Face;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Construction surfaces: extruded or revolved from a sketch (open or closed, optionally capped) and offset from a face. They
/// do not change the volume of the body, so each one is checked by its status and the faces it makes, and deleted if it
/// fails. Verified: a line 60 long extruded 20 gives one face of 1200 mm²; a line 40 long at 150 from the axis revolved 180°
/// gives π·150·40 mm²; a single face is offset (an array of faces gives E_NOINTERFACE), igLeft into the material.
/// </summary>
public static partial class FeatureService
{
    private static readonly string[] SurfaceTypes = ["extruded", "revolved", "offset"];

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
        return CheckedSurface(context, model, surface, "extruded", switched, included, sketchProfile);
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
        return CheckedSurface(context, model, surface, "revolved", switched, included, sketchProfile);
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
        return CheckedSurface(context, model, surface, "offset", switched, null, null);
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
        IncludedProfile? included, Profile? sketchProfile)
    {
        context.ModelChanged(model.Document, "the model changed (new surface)");
        string? status = StatusOf(surface);
        Face[] faces = FacesOf(surface);
        if (status is not ("ok" or "warning") || faces.Length == 0 || faces.Sum(f => f.Area) <= 0)
        {
            try
            {
                Late.Call(surface, "Delete");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                // The error being reported is the failed surface.
            }

            if (included is not null)
            {
                DeleteIncluded(included);
            }

            throw new ToolException("feature_failed",
                $"Solid Edge did not make the {type} surface ({(faces.Length == 0 ? "it has no faces" : $"status {status}")}); it was deleted.",
                "Check the sketch, the axis or the face, and the distance.");
        }

        if (sketchProfile is not null)
        {
            Hide(sketchProfile);
            Hide(included!.Profile);
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
        object[] collections = [constructions.ExtrudedSurfaces, constructions.RevolvedSurfaces, constructions.OffsetSurfaces];
        return collections.SelectMany((c, i) => Late.Items(c).Select(s => (s, SurfaceTypes[i])));
    }

    private static object? FindSurface(ModelDocument model, string name) =>
        AllSurfaces(model).Select(s => s.Surface).FirstOrDefault(s => string.Equals(Late.TryGetString(s, "Name"), name, StringComparison.OrdinalIgnoreCase));
}
